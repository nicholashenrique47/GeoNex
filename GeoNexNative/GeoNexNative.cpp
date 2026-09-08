#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstring>
#include "NativeCpu.h"
#include <limits>
#include <memory>
#include <mutex>
#include <thread>
#include <utility>
#include <vector>

namespace {
    struct RenderCancelled {};
    struct InvalidShape {};
    thread_local const std::atomic<bool>* g_renderCancellation = nullptr;

    inline void CheckRenderCancellation() {
        if (g_renderCancellation && g_renderCancellation->load(std::memory_order_relaxed))
            throw RenderCancelled{};
    }

    struct PointF {
        float x;
        float y;
    };

    thread_local std::vector<PointF> g_ringPoints;
    thread_local std::vector<PointF> g_rotatedRing;
    thread_local std::vector<unsigned char> g_keepPoints;
    thread_local std::vector<std::pair<int, int>> g_rdpStack;
    thread_local std::vector<float> g_featureCommands;

    struct NativeShapeSpatialIndex {
        std::vector<double> minX;
        std::vector<double> minY;
        std::vector<double> maxX;
        std::vector<double> maxY;
        std::vector<unsigned char> kinds;
        std::vector<int> cellStarts;
        std::vector<int> cellEntries;
        std::vector<int> oversized;
        // Minimum occupied cell per feature (x in low 16 bits, y in high 16).
        // A query emits a feature only in its first overlapping cell. Unlike
        // generation stamps this ownership rule is immutable and needs no lock.
        std::vector<uint32_t> firstCells;
        double worldMinX = 0.0;
        double worldMinY = 0.0;
        double worldMaxX = 0.0;
        double worldMaxY = 0.0;
        double inverseCellWidth = 0.0;
        double inverseCellHeight = 0.0;
        int columns = 1;
        int rows = 1;

        int CellX(double value) const noexcept {
            if (columns <= 1 || inverseCellWidth <= 0.0) return 0;
            if (value <= worldMinX) return 0;
            if (value >= worldMaxX) return columns - 1;
            const int cell = static_cast<int>((value - worldMinX) * inverseCellWidth);
            return std::clamp(cell, 0, columns - 1);
        }

        int CellY(double value) const noexcept {
            if (rows <= 1 || inverseCellHeight <= 0.0) return 0;
            if (value <= worldMinY) return 0;
            if (value >= worldMaxY) return rows - 1;
            const int cell = static_cast<int>((value - worldMinY) * inverseCellHeight);
            return std::clamp(cell, 0, rows - 1);
        }
    };

    inline int ReadInt32Unaligned(const unsigned char* source) noexcept {
        int value;
        std::memcpy(&value, source, sizeof(value));
        return value;
    }

    inline bool IsFiniteEnvelope(double minX, double minY, double maxX, double maxY) noexcept {
        return std::isfinite(minX) && std::isfinite(minY) &&
               std::isfinite(maxX) && std::isfinite(maxY) &&
               minX <= maxX && minY <= maxY;
    }

    inline float DistanceToSegmentSquared(const PointF& p, const PointF& a, const PointF& b) noexcept {
        const float dx = b.x - a.x;
        const float dy = b.y - a.y;
        const float lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= std::numeric_limits<float>::epsilon()) {
            const float px = p.x - a.x;
            const float py = p.y - a.y;
            return px * px + py * py;
        }

        float t = ((p.x - a.x) * dx + (p.y - a.y) * dy) / lengthSquared;
        t = std::max(0.0f, std::min(1.0f, t));
        const float px = p.x - (a.x + t * dx);
        const float py = p.y - (a.y + t * dy);
        return px * px + py * py;
    }

    void MarkRdpSegment(const std::vector<PointF>& points, int first, int last, float toleranceSquared) {
        if (last <= first + 1) return;

        g_rdpStack.clear();
        g_rdpStack.emplace_back(first, last);

        while (!g_rdpStack.empty()) {
            CheckRenderCancellation();
            const auto segment = g_rdpStack.back();
            g_rdpStack.pop_back();

            const PointF& a = points[segment.first];
            const PointF& b = points[segment.second];
            float maximumDistance = toleranceSquared;
            int split = -1;

            for (int i = segment.first + 1; i < segment.second; ++i) {
                if ((i & 1023) == 0) CheckRenderCancellation();
                const float distance = DistanceToSegmentSquared(points[i], a, b);
                if (distance > maximumDistance) {
                    maximumDistance = distance;
                    split = i;
                }
            }

            if (split >= 0) {
                g_keepPoints[split] = 1;
                if (split > segment.first + 1) g_rdpStack.emplace_back(segment.first, split);
                if (segment.second > split + 1) g_rdpStack.emplace_back(split, segment.second);
            }
        }
    }

    // RDP para anel fechado. Dois pontos-ancora geometricamente distantes evitam o
    // caso degenerado primeiro==ultimo e tornam o resultado estavel nos dois sentidos.
    void SimplifyClosedRing(std::vector<PointF>& points, float tolerance, std::vector<float>& commands) {
        if (points.size() < 3) return;

        if (points.size() > 3) {
            const PointF& first = points.front();
            const PointF& last = points.back();
            const float dx = first.x - last.x;
            const float dy = first.y - last.y;
            if ((dx * dx + dy * dy) <= 1.0e-12f) points.pop_back();
        }

        const int uniqueCount = static_cast<int>(points.size());
        if (uniqueCount < 3) return;

        int minX = 0, maxX = 0, minY = 0, maxY = 0;
        for (int i = 1; i < uniqueCount; ++i) {
            if ((i & 1023) == 0) CheckRenderCancellation();
            if (points[i].x < points[minX].x) minX = i;
            if (points[i].x > points[maxX].x) maxX = i;
            if (points[i].y < points[minY].y) minY = i;
            if (points[i].y > points[maxY].y) maxY = i;
        }

        const int extrema[4] = { minX, maxX, minY, maxY };
        int anchorA = extrema[0];
        int anchorB = extrema[1];
        float greatestDistance = -1.0f;
        for (int a = 0; a < 4; ++a) {
            for (int b = a + 1; b < 4; ++b) {
                const float dx = points[extrema[a]].x - points[extrema[b]].x;
                const float dy = points[extrema[a]].y - points[extrema[b]].y;
                const float distance = dx * dx + dy * dy;
                if (distance > greatestDistance) {
                    greatestDistance = distance;
                    anchorA = extrema[a];
                    anchorB = extrema[b];
                }
            }
        }

        g_rotatedRing.resize(static_cast<size_t>(uniqueCount) + 1);
        const int split = (anchorB - anchorA + uniqueCount) % uniqueCount;
        // Two contiguous copies replace a division/modulo for every vertex.
        std::copy(points.begin() + anchorA, points.end(), g_rotatedRing.begin());
        std::copy(points.begin(), points.begin() + anchorA, g_rotatedRing.begin() + uniqueCount - anchorA);
        g_rotatedRing[uniqueCount] = g_rotatedRing[0];

        g_keepPoints.assign(static_cast<size_t>(uniqueCount) + 1, 0);
        g_keepPoints[0] = 1;
        g_keepPoints[split] = 1;
        g_keepPoints[uniqueCount] = 1;

        const float toleranceSquared = tolerance * tolerance;
        MarkRdpSegment(g_rotatedRing, 0, split, toleranceSquared);
        MarkRdpSegment(g_rotatedRing, split, uniqueCount, toleranceSquared);

        int kept = 0;
        for (int i = 0; i < uniqueCount; ++i) kept += g_keepPoints[i] != 0;

        // Um poligono nunca pode ser reduzido para menos de tres vertices.
        if (kept < 3) {
            g_keepPoints.assign(static_cast<size_t>(uniqueCount) + 1, 0);
            g_keepPoints[0] = 1;
            g_keepPoints[split] = 1;
            int third = 0;
            float greatestArea = -1.0f;
            const PointF& a = g_rotatedRing[0];
            const PointF& b = g_rotatedRing[split];
            for (int i = 1; i < uniqueCount; ++i) {
                if (i == split) continue;
                const float area = std::abs((b.x - a.x) * (g_rotatedRing[i].y - a.y) -
                                            (b.y - a.y) * (g_rotatedRing[i].x - a.x));
                if (area > greatestArea) {
                    greatestArea = area;
                    third = i;
                }
            }
            g_keepPoints[third] = 1;
            kept = 3;
        }

        commands.push_back(static_cast<float>(kept));
        for (int i = 0; i < uniqueCount; ++i) {
            if (g_keepPoints[i]) {
                commands.push_back(g_rotatedRing[i].x);
                commands.push_back(g_rotatedRing[i].y);
            }
        }
    }

    void SimplifyOpenPart(const std::vector<PointF>& points, float tolerance, std::vector<float>& commands) {
        const int count = static_cast<int>(points.size());
        if (count < 2) return;

        g_keepPoints.assign(points.size(), 0);
        g_keepPoints[0] = 1;
        g_keepPoints[count - 1] = 1;
        MarkRdpSegment(points, 0, count - 1, tolerance * tolerance);

        int kept = 0;
        for (unsigned char value : g_keepPoints) kept += value != 0;
        if (kept < 2) return;

        commands.push_back(static_cast<float>(kept));
        for (int i = 0; i < count; ++i) {
            if (g_keepPoints[i]) {
                commands.push_back(points[i].x);
                commands.push_back(points[i].y);
            }
        }
    }

    inline bool RectanglesIntersect(float leftA, float topA, float rightA, float bottomA,
                                    float leftB, float topB, float rightB, float bottomB) noexcept {
        return leftA <= rightB && rightA >= leftB && topA <= bottomB && bottomA >= topB;
    }

    inline void MarkMicroLodPixels(
        unsigned char* grid,
        int gridWidth,
        int gridHeight,
        float viewportLeft,
        float viewportTop,
        float zoom,
        float left,
        float top,
        float right,
        float bottom) noexcept
    {
        if (!grid || gridWidth <= 0 || gridHeight <= 0) return;

        int x0 = static_cast<int>(std::floor((left - viewportLeft) * zoom));
        int y0 = static_cast<int>(std::floor((top - viewportTop) * zoom));
        int x1 = static_cast<int>(std::ceil((right - viewportLeft) * zoom));
        int y1 = static_cast<int>(std::ceil((bottom - viewportTop) * zoom));

        // Culling has a guard band, but outside geometry must not paint the edge pixel.
        if (x1 <= 0 || y1 <= 0 || x0 >= gridWidth || y0 >= gridHeight) return;

        x0 = std::clamp(x0, 0, gridWidth - 1);
        y0 = std::clamp(y0, 0, gridHeight - 1);
        x1 = std::clamp(x1, x0 + 1, gridWidth);
        y1 = std::clamp(y1, y0 + 1, gridHeight);

        for (int y = y0; y < y1; ++y) {
            std::memset(grid + static_cast<size_t>(y) * gridWidth + x0, 1,
                        static_cast<size_t>(x1 - x0));
        }
    }
}

extern "C" {

    // Bump only when the managed/native contract changes incompatibly. The
    // managed loader rejects any other value before rendering a frame.
    __declspec(dllexport) uint32_t GetGeoNexNativeAbiVersion() noexcept {
        return 4u;
    }

    // Bitcodes para o Cohen-Sutherland
    const int INSIDE = 0; // 0000
    const int LEFT = 1;   // 0001
    const int RIGHT = 2;  // 0010
    const int BOTTOM = 4; // 0100
    const int TOP = 8;    // 1000

    // Calcula o código de bit de uma coordenada
    int ComputeOutCode(double x, double y, double minX, double minY, double maxX, double maxY) {
        int code = INSIDE;
        if (x < minX) code |= LEFT;
        else if (x > maxX) code |= RIGHT;
        if (y < minY) code |= BOTTOM;
        else if (y > maxY) code |= TOP;
        return code;
    }

    // Processa uma série de pontos e clipa contra um Bounding Box
    // Retorna o número de pontos resultantes após o clip
    // inputPoints é [x0, y0, x1, y1, ...]
    __declspec(dllexport) int ClipPolylineCohenSutherland(
        double* inputPoints, int numPoints, 
        double* outputPoints, 
        double minX, double minY, double maxX, double maxY) 
    {
        if (numPoints < 2) return 0;

        int outCount = 0;

        for (int i = 0; i < numPoints - 1; ++i) {
            double x0 = inputPoints[i * 2];
            double y0 = inputPoints[i * 2 + 1];
            double x1 = inputPoints[(i + 1) * 2];
            double y1 = inputPoints[(i + 1) * 2 + 1];

            int outcode0 = ComputeOutCode(x0, y0, minX, minY, maxX, maxY);
            int outcode1 = ComputeOutCode(x1, y1, minX, minY, maxX, maxY);
            bool accept = false;

            while (true) {
                if (!(outcode0 | outcode1)) {
                    // Ambos dentro
                    accept = true;
                    break;
                } else if (outcode0 & outcode1) {
                    // Ambos fora da mesma borda (trivially reject)
                    break;
                } else {
                    double x = 0, y = 0;
                    int outcodeOut = outcode0 ? outcode0 : outcode1;

                    if (outcodeOut & TOP) {
                        x = x0 + (x1 - x0) * (maxY - y0) / (y1 - y0);
                        y = maxY;
                    } else if (outcodeOut & BOTTOM) {
                        x = x0 + (x1 - x0) * (minY - y0) / (y1 - y0);
                        y = minY;
                    } else if (outcodeOut & RIGHT) {
                        y = y0 + (y1 - y0) * (maxX - x0) / (x1 - x0);
                        x = maxX;
                    } else if (outcodeOut & LEFT) {
                        y = y0 + (y1 - y0) * (minX - x0) / (x1 - x0);
                        x = minX;
                    }

                    if (outcodeOut == outcode0) {
                        x0 = x; y0 = y;
                        outcode0 = ComputeOutCode(x0, y0, minX, minY, maxX, maxY);
                    } else {
                        x1 = x; y1 = y;
                        outcode1 = ComputeOutCode(x1, y1, minX, minY, maxX, maxY);
                    }
                }
            }

            if (accept) {
                outputPoints[outCount * 2] = x0;
                outputPoints[outCount * 2 + 1] = y0;
                outCount++;
                outputPoints[outCount * 2] = x1;
                outputPoints[outCount * 2 + 1] = y1;
                outCount++;
            }
        }
        return outCount;
    }

    void DouglasPeuckerRecursiveF(float* points, int first, int last, float epsilon_sq, bool* keep) {
        float dmax_sq = 0.0f;
        int index = first;

        float p1x = points[first * 2];
        float p1y = points[first * 2 + 1];
        float p2x = points[last * 2];
        float p2y = points[last * 2 + 1];

        float dx = p2x - p1x;
        float dy = p2y - p1y;
        float sq_len = dx * dx + dy * dy;
        float inv_sq_len = (sq_len == 0.0f) ? 0.0f : 1.0f / sq_len;

        // Loop intensivo: Otimizado para branchless e pre-calculado (sem sqrt)
        // O MSVC com /arch:AVX2 consegue vetorizar (SIMD) este loop automaticamente.
        for (int i = first + 1; i < last; ++i) {
            float px = points[i * 2];
            float py = points[i * 2 + 1];

            float t = ((px - p1x) * dx + (py - p1y) * dy) * inv_sq_len;
            
            // Clamp t between 0 and 1 (branchless)
            t = (t < 0.0f) ? 0.0f : ((t > 1.0f) ? 1.0f : t);

            float projX = p1x + t * dx;
            float projY = p1y + t * dy;
            
            float diffX = px - projX;
            float diffY = py - projY;
            
            float d_sq = diffX * diffX + diffY * diffY;
            
            if (d_sq > dmax_sq) {
                index = i;
                dmax_sq = d_sq;
            }
        }

        if (dmax_sq > epsilon_sq) {
            DouglasPeuckerRecursiveF(points, first, index, epsilon_sq, keep);
            DouglasPeuckerRecursiveF(points, index, last, epsilon_sq, keep);
        } else {
            for (int i = first + 1; i < last; ++i) {
                keep[i] = false;
            }
        }
    }

    __declspec(dllexport) int SimplifyDouglasPeucker(
        float* inputPoints, int numPoints, 
        float* outputPoints, 
        float epsilon) 
    {
        if (numPoints <= 2) {
            for (int i = 0; i < numPoints * 2; ++i) {
                outputPoints[i] = inputPoints[i];
            }
            return numPoints;
        }

        bool* keep = new bool[numPoints];
        for (int i = 0; i < numPoints; ++i) {
            keep[i] = true;
        }

        float epsilon_sq = epsilon * epsilon;
        DouglasPeuckerRecursiveF(inputPoints, 0, numPoints - 1, epsilon_sq, keep);

        int outCount = 0;
        for (int i = 0; i < numPoints; ++i) {
            if (keep[i]) {
                outputPoints[outCount * 2] = inputPoints[i * 2];
                outputPoints[outCount * 2 + 1] = inputPoints[i * 2 + 1];
                outCount++;
            }
        }

        delete[] keep;
        return outCount;
    }

    // Processador ultrarrápido com AVX2 (SIMD) para parsing de geometria binária.
    // Converte de double para float, e aplica offsets da câmera/mundo, tudo nos registradores YMM (256-bits).
    // Zero Alocações de Heap (GC).
    __declspec(dllexport) void ParseShapefilePartAVX2(
        const unsigned char* inBytes, 
        float* outPoints, 
        int numPoints, 
        double offsetX, 
        double offsetY)
    {
        geonex::GetPointParser()(inBytes, outPoints, numPoints, offsetX, offsetY);
    }
    
    // =========================================================================
    // OTIMIZAÇÃO EXTREMA: Screen-Space Pixel Quantization
    // Transforma Coordenadas Reais numa grelha de Pixeis e descarta
    // todos os pontos que caiam no exato mesmo pixel consecutivo.
    // Reduz um polígono de 1 milhão de pontos para 2 mil sem perda visual.
    // =========================================================================
    __declspec(dllexport) int QuantizeGeometry(
        float* inputPoints, int numPoints,
        float* outputPoints,
        float resolution)
    {
        if (numPoints <= 0) return 0;

        float invRes = (resolution <= 0.0f) ? 1.0f : (1.0f / resolution);
        int outCount = 0;

        // Manter o primeiro ponto garantidamente
        float firstX = inputPoints[0];
        float firstY = inputPoints[1];
        outputPoints[0] = firstX;
        outputPoints[1] = firstY;
        outCount++;

        long long lastGridX = (long long)(firstX * invRes);
        long long lastGridY = (long long)(firstY * invRes);

        for (int i = 1; i < numPoints - 1; ++i) {
            float x = inputPoints[i * 2];
            float y = inputPoints[i * 2 + 1];

            long long gridX = (long long)(x * invRes);
            long long gridY = (long long)(y * invRes);

            // Se o ponto cair no mesmo pixel (grelha matemática) do ponto anterior, descartamo-lo.
            if (gridX != lastGridX || gridY != lastGridY) {
                outputPoints[outCount * 2] = x;
                outputPoints[outCount * 2 + 1] = y;
                outCount++;
                lastGridX = gridX;
                lastGridY = gridY;
            }
        }

        // Manter o último ponto garantidamente para fechar o polígono corretamente
        if (numPoints > 1) {
            outputPoints[outCount * 2] = inputPoints[(numPoints - 1) * 2];
            outputPoints[outCount * 2 + 1] = inputPoints[(numPoints - 1) * 2 + 1];
            outCount++;
        }

        return outCount;
    }
    __declspec(dllexport) int ProcessPolygonBatchAVX2(
        const unsigned char* basePtr,
        const int* offsets,
        int count,
        float* outBuffer, int maxFloats,
        double offsetX,
        double offsetY,
        float zoomReal,
        float resolution)
    {
        int outIdx = 0;
        float invRes = (resolution <= 0.0f) ? 1.0f : (1.0f / resolution);

        // OTIMIZAÇÃO QGIS: Micro-LOD adaptativo ao zoom
        // Zoom alto (perto) = threshold 3px (detalhes importam)
        // Zoom baixo (longe) = threshold 6px (mais rects = mais rápido)
        float lodThreshold = 3.0f;
        if (zoomReal < 0.5f) lodThreshold = 6.0f;
        else if (zoomReal < 2.0f) lodThreshold = 4.0f;

        // Limite máximo de vértices por parte (QGIS usa ~4000, nós usamos 2000 para WebSIG)
        const int MAX_VERTS_PER_PART = 2000;

        for (int i = 0; i < count; i++) {
            const unsigned char* dataPtr = basePtr + offsets[i] + 8;
            int shapeType = *(const int*)dataPtr;

            bool isPolygon = (shapeType == 5 || shapeType == 15 || shapeType == 25);
            bool isLine = (shapeType == 3 || shapeType == 13 || shapeType == 23);

            if (!isPolygon && !isLine) continue;

            const double* env = (const double*)(dataPtr + 4);
            double eMinX = env[0];
            double eMinY = env[1];
            double eMaxX = env[2];
            double eMaxY = env[3];

            float wEcra = (float)((eMaxX - eMinX) * zoomReal);
            float hEcra = (float)((eMaxY - eMinY) * zoomReal);

            // OTIMIZAÇÃO: Micro-LOD adaptativo — feição pequena → retângulo instantâneo
            if (isPolygon && wEcra < lodThreshold && hEcra < lodThreshold) {
                if (outIdx + 5 > maxFloats) return outIdx;
                outBuffer[outIdx++] = -4.0f; // Flag para rect
                outBuffer[outIdx++] = (float)(eMinX - offsetX);
                outBuffer[outIdx++] = -(float)(eMaxY - offsetY);
                outBuffer[outIdx++] = (float)(eMaxX - offsetX);
                outBuffer[outIdx++] = -(float)(eMinY - offsetY);
                continue;
            }

            int numParts = *(const int*)(dataPtr + 36);
            int numPoints = *(const int*)(dataPtr + 40);

            if (numParts <= 0 || numPoints <= 0) continue;

            const int* parts = (const int*)(dataPtr + 44);
            const unsigned char* pointsBase = dataPtr + 44 + (numParts * 4);

            for (int p = 0; p < numParts; p++) {
                int startIdx = parts[p];
                int endIdx = (p == numParts - 1) ? numPoints : parts[p + 1];
                int ptCount = endIdx - startIdx;

                if (ptCount <= 0) continue;

                if (outIdx + 1 > maxFloats) return outIdx;
                int countIdx = outIdx++;
                int validPts = 0;

                const double* ptArray = (const double*)(pointsBase + (startIdx * 16));
                
                long long lastGridX = -999999999LL;
                long long lastGridY = -999999999LL;

                // OTIMIZAÇÃO: Stride adaptativo — para polígonos enormes, pula vértices
                // Um polígono de 50.000 pts num zoom distante é idêntico com 2.000 pts
                int stride = 1;
                if (ptCount > MAX_VERTS_PER_PART) {
                    stride = ptCount / MAX_VERTS_PER_PART;
                    if (stride < 1) stride = 1;
                }
                
                for (int j = 0; j < ptCount; j += stride) {
                    // Sempre inclui primeiro e último ponto
                    int idx = j;
                    if (j > 0 && j + stride >= ptCount) idx = ptCount - 1;

                    float px = (float)(ptArray[idx * 2] - offsetX);
                    float py = -(float)(ptArray[idx * 2 + 1] - offsetY);

                    long long gridX = (long long)(px * invRes);
                    long long gridY = (long long)(py * invRes);

                    if (validPts == 0 || idx == ptCount - 1 || gridX != lastGridX || gridY != lastGridY) {
                        if (outIdx + 2 > maxFloats) return outIdx;
                        outBuffer[outIdx++] = px;
                        outBuffer[outIdx++] = py;
                        validPts++;
                        lastGridX = gridX;
                        lastGridY = gridY;
                    }
                }

                // Garante último ponto se stride pulou
                if (stride > 1 && ptCount > 1) {
                    float lastPx = (float)(ptArray[(ptCount - 1) * 2] - offsetX);
                    float lastPy = -(float)(ptArray[(ptCount - 1) * 2 + 1] - offsetY);
                    long long gx = (long long)(lastPx * invRes);
                    long long gy = (long long)(lastPy * invRes);
                    if (gx != lastGridX || gy != lastGridY) {
                        if (outIdx + 2 > maxFloats) return outIdx;
                        outBuffer[outIdx++] = lastPx;
                        outBuffer[outIdx++] = lastPy;
                        validPts++;
                    }
                }
                
                outBuffer[countIdx] = (float)validPts;
            }
        }

        return outIdx;
    }

}

namespace {

    // ========================================================================
    // Pipeline vetorial V2
    // - memoria limitada: o chamador pode enviar blocos e retomar por processed
    // - culling de envelope antes de tocar nos vertices
    // - micro-LOD em espaco de tela
    // - Douglas-Peucker iterativo em unidades de pixel (sem recursao/stack overflow)
    // - ancoragem especial para aneis fechados, preservando furos/multipartes
    //
    // Protocolo de saida:
    //   -4, left, top, right, bottom : retangulo de micro-LOD
    //   N, x1, y1, ... xN, yN       : parte com N vertices
    // ========================================================================
    int ProcessShapeBatchCore(
        const unsigned char* basePtr,
        const int64_t* offsets,
        int count,
        float* outBuffer,
        int maxFloats,
        double offsetX,
        double offsetY,
        float zoomReal,
        float tolerancePixels,
        float microLodPixels,
        float viewportLeft,
        float viewportTop,
        float viewportRight,
        float viewportBottom,
        int* processedFeatures,
        int* requiredFloats,
        int* emittedParts,
        int* emittedVertices,
        unsigned char* microGrid,
        int microGridWidth,
        int microGridHeight,
        int* emittedMicroFeatures,
        int64_t fileLength = 0)
    {
        if (processedFeatures) *processedFeatures = 0;
        if (requiredFloats) *requiredFloats = 0;
        if (emittedParts) *emittedParts = 0;
        if (emittedVertices) *emittedVertices = 0;
        if (emittedMicroFeatures) *emittedMicroFeatures = 0;

        if (!basePtr || !offsets || !outBuffer || count <= 0 || maxFloats <= 0 || zoomReal <= 0.0f)
            return 0;

        const float toleranceWorld = std::max(0.0f, tolerancePixels) / zoomReal;
        const float viewportMargin = 2.0f / zoomReal;
        const float gridOriginX = viewportLeft;
        const float gridOriginY = viewportTop;
        viewportLeft -= viewportMargin;
        viewportTop -= viewportMargin;
        viewportRight += viewportMargin;
        viewportBottom += viewportMargin;

        int outIndex = 0;
        int localParts = 0;
        int localVertices = 0;
        int localMicroFeatures = 0;

        for (int featureIndex = 0; featureIndex < count; ++featureIndex) {
            CheckRenderCancellation();
            const int64_t recordOffset = offsets[featureIndex];
            if (fileLength && (recordOffset < 100 || recordOffset > fileLength - 12))
                throw InvalidShape{};
            const unsigned char* dataPtr = basePtr + offsets[featureIndex] + 8;
            const int shapeType = *reinterpret_cast<const int*>(dataPtr);
            const bool isPolygon = shapeType == 5 || shapeType == 15 || shapeType == 25;
            const bool isLine = shapeType == 3 || shapeType == 13 || shapeType == 23;

            if (!isPolygon && !isLine) {
                if (processedFeatures) *processedFeatures = featureIndex + 1;
                continue;
            }

            // V4 validates both the mapped file and the record's declared length.
            if (fileLength) {
                if (recordOffset > fileLength - 52) throw InvalidShape{};
                const unsigned char* lengthBytes = basePtr + recordOffset + 4;
                const uint32_t words = (uint32_t(lengthBytes[0]) << 24) |
                    (uint32_t(lengthBytes[1]) << 16) | (uint32_t(lengthBytes[2]) << 8) | lengthBytes[3];
                const int64_t recordBytes = int64_t(words) * 2;
                const int partsCount = ReadInt32Unaligned(dataPtr + 36);
                const int pointsCount = ReadInt32Unaligned(dataPtr + 40);
                if (partsCount <= 0 || pointsCount <= 0 || partsCount > pointsCount ||
                    recordBytes > fileLength - recordOffset - 8 ||
                    44LL + int64_t(partsCount) * 4 + int64_t(pointsCount) * 16 > recordBytes)
                    throw InvalidShape{};
                int previous = -1;
                for (int p = 0; p < partsCount; ++p) {
                    if ((p & 1023) == 0) CheckRenderCancellation();
                    const int start = ReadInt32Unaligned(dataPtr + 44 + int64_t(p) * 4);
                    if ((p == 0 && start != 0) || start <= previous || start >= pointsCount)
                        throw InvalidShape{};
                    previous = start;
                }
            }

            double envelope[4];
            std::memcpy(envelope, dataPtr + 4, sizeof(envelope));
            if (fileLength && !IsFiniteEnvelope(envelope[0], envelope[1], envelope[2], envelope[3]))
                throw InvalidShape{};
            const float localLeft = static_cast<float>(envelope[0] - offsetX);
            const float localTop = -static_cast<float>(envelope[3] - offsetY);
            const float localRight = static_cast<float>(envelope[2] - offsetX);
            const float localBottom = -static_cast<float>(envelope[1] - offsetY);

            if (!RectanglesIntersect(localLeft, localTop, localRight, localBottom,
                                     viewportLeft, viewportTop, viewportRight, viewportBottom)) {
                if (processedFeatures) *processedFeatures = featureIndex + 1;
                continue;
            }

            const float screenWidth = std::max(0.0f, localRight - localLeft) * zoomReal;
            const float screenHeight = std::max(0.0f, localBottom - localTop) * zoomReal;

            g_featureCommands.clear();
            int featureParts = 0;
            int featureVertices = 0;

            if (isPolygon && screenWidth <= microLodPixels && screenHeight <= microLodPixels) {
                if (microGrid) {
                    MarkMicroLodPixels(microGrid, microGridWidth, microGridHeight,
                                       gridOriginX, gridOriginY, zoomReal,
                                       localLeft, localTop, localRight, localBottom);
                    ++localMicroFeatures;
                }
                else {
                    g_featureCommands.push_back(-4.0f);
                    g_featureCommands.push_back(localLeft);
                    g_featureCommands.push_back(localTop);
                    g_featureCommands.push_back(localRight);
                    g_featureCommands.push_back(localBottom);
                }
                featureParts = 1;
                featureVertices = 4;
            }
            else {
                const int numParts = *reinterpret_cast<const int*>(dataPtr + 36);
                const int numPoints = *reinterpret_cast<const int*>(dataPtr + 40);
                if (numParts <= 0 || numPoints <= 0 || numParts > numPoints) {
                    if (processedFeatures) *processedFeatures = featureIndex + 1;
                    continue;
                }

                const int* parts = reinterpret_cast<const int*>(dataPtr + 44);
                const unsigned char* pointsBase = dataPtr + 44 + int64_t(numParts) * 4;

                for (int partIndex = 0; partIndex < numParts; ++partIndex) {
                    CheckRenderCancellation();
                    const int start = parts[partIndex];
                    const int end = partIndex == numParts - 1 ? numPoints : parts[partIndex + 1];
                    const int pointCount = end - start;
                    if (start < 0 || end > numPoints || pointCount < (isPolygon ? 3 : 2)) continue;

                    g_ringPoints.resize(pointCount);
                    for (int first = 0; first < pointCount;) {
                        CheckRenderCancellation();
                        const int chunk = std::min(4096, pointCount - first);
                        ParseShapefilePartAVX2(
                            pointsBase + (static_cast<size_t>(start) + first) * 16,
                            reinterpret_cast<float*>(g_ringPoints.data() + first),
                            chunk, offsetX, offsetY);
                        first += chunk;
                    }

                    const size_t commandStart = g_featureCommands.size();
                    if (toleranceWorld > 0.0f && pointCount > (isPolygon ? 4 : 2)) {
                        if (isPolygon) SimplifyClosedRing(g_ringPoints, toleranceWorld, g_featureCommands);
                        else SimplifyOpenPart(g_ringPoints, toleranceWorld, g_featureCommands);
                    }
                    else {
                        int outputCount = pointCount;
                        if (isPolygon && pointCount > 3) {
                            const float dx = g_ringPoints.front().x - g_ringPoints.back().x;
                            const float dy = g_ringPoints.front().y - g_ringPoints.back().y;
                            if (dx * dx + dy * dy <= 1.0e-12f) --outputCount;
                        }

                        if (outputCount >= (isPolygon ? 3 : 2)) {
                            g_featureCommands.push_back(static_cast<float>(outputCount));
                            for (int pointIndex = 0; pointIndex < outputCount; ++pointIndex) {
                                g_featureCommands.push_back(g_ringPoints[pointIndex].x);
                                g_featureCommands.push_back(g_ringPoints[pointIndex].y);
                            }
                        }
                    }

                    if (g_featureCommands.size() > commandStart) {
                        ++featureParts;
                        featureVertices += static_cast<int>(g_featureCommands[commandStart]);
                    }
                }
            }

            const int featureFloatCount = static_cast<int>(g_featureCommands.size());
            if (featureFloatCount > maxFloats - outIndex) {
                if (requiredFloats) *requiredFloats = featureFloatCount;
                break;
            }

            if (featureFloatCount > 0) {
                std::memcpy(outBuffer + outIndex, g_featureCommands.data(),
                            static_cast<size_t>(featureFloatCount) * sizeof(float));
                outIndex += featureFloatCount;
                localParts += featureParts;
                localVertices += featureVertices;
            }

            if (processedFeatures) *processedFeatures = featureIndex + 1;
        }

        if (emittedParts) *emittedParts = localParts;
        if (emittedVertices) *emittedVertices = localVertices;
        if (emittedMicroFeatures) *emittedMicroFeatures = localMicroFeatures;
        return outIndex;
    }
}

extern "C" {

    __declspec(dllexport) int ProcessShapeBatchV2(
        const unsigned char* basePtr,
        const int64_t* offsets,
        int count,
        float* outBuffer,
        int maxFloats,
        double offsetX,
        double offsetY,
        float zoomReal,
        float tolerancePixels,
        float microLodPixels,
        float viewportLeft,
        float viewportTop,
        float viewportRight,
        float viewportBottom,
        int* processedFeatures,
        int* requiredFloats,
        int* emittedParts,
        int* emittedVertices)
    {
        return ProcessShapeBatchCore(
            basePtr, offsets, count, outBuffer, maxFloats,
            offsetX, offsetY, zoomReal, tolerancePixels, microLodPixels,
            viewportLeft, viewportTop, viewportRight, viewportBottom,
            processedFeatures, requiredFloats, emittedParts, emittedVertices,
            nullptr, 0, 0, nullptr);
    }

    // V3 agrega polígonos subpixel numa máscara de ocupação do viewport. O custo
    // máximo passa a ser proporcional ao número de pixels visíveis, não ao número
    // total de lotes, sem alterar a geometria original usada em zoom próximo.
    __declspec(dllexport) int ProcessShapeBatchV3(
        const unsigned char* basePtr,
        const int64_t* offsets,
        int count,
        float* outBuffer,
        int maxFloats,
        double offsetX,
        double offsetY,
        float zoomReal,
        float tolerancePixels,
        float microLodPixels,
        float viewportLeft,
        float viewportTop,
        float viewportRight,
        float viewportBottom,
        unsigned char* microGrid,
        int microGridWidth,
        int microGridHeight,
        int* processedFeatures,
        int* requiredFloats,
        int* emittedParts,
        int* emittedVertices,
        int* emittedMicroFeatures)
    {
        return ProcessShapeBatchCore(
            basePtr, offsets, count, outBuffer, maxFloats,
            offsetX, offsetY, zoomReal, tolerancePixels, microLodPixels,
            viewportLeft, viewportTop, viewportRight, viewportBottom,
            processedFeatures, requiredFloats, emittedParts, emittedVertices,
            microGrid, microGridWidth, microGridHeight, emittedMicroFeatures);
    }

    // Owned by the caller; registration must be stopped before DestroyRenderCancellation.
    __declspec(dllexport) void* CreateRenderCancellation() noexcept {
        try { return new std::atomic<bool>(false); } catch (...) { return nullptr; }
    }
    __declspec(dllexport) void CancelRender(void* handle) noexcept {
        if (handle) static_cast<std::atomic<bool>*>(handle)->store(true, std::memory_order_relaxed);
    }
    __declspec(dllexport) void DestroyRenderCancellation(void* handle) noexcept {
        delete static_cast<std::atomic<bool>*>(handle);
    }

    // V4 preserves the V3 command protocol, adds record bounds and cancellable RDP.
    // Negative return: -1 cancelled, -2 invalid input/record, -3 native failure.
    // On a negative result the caller must discard both commands and occupancy.
    __declspec(dllexport) int ProcessShapeBatchV4(
        const unsigned char* basePtr, int64_t fileLength, void* cancellation,
        const int64_t* offsets, int count, float* outBuffer, int maxFloats,
        double offsetX, double offsetY, float zoomReal, float tolerancePixels,
        float microLodPixels, float viewportLeft, float viewportTop,
        float viewportRight, float viewportBottom, unsigned char* microGrid,
        int microGridWidth, int microGridHeight, int* processedFeatures,
        int* requiredFloats, int* emittedParts, int* emittedVertices, int* emittedMicroFeatures) noexcept
    {
        if (processedFeatures) *processedFeatures = 0;
        if (requiredFloats) *requiredFloats = 0;
        if (emittedParts) *emittedParts = 0;
        if (emittedVertices) *emittedVertices = 0;
        if (emittedMicroFeatures) *emittedMicroFeatures = 0;
        if (!basePtr || !offsets || !outBuffer || count < 0 || maxFloats <= 0 || fileLength < 100 ||
            !std::isfinite(offsetX) || !std::isfinite(offsetY) ||
            !std::isfinite(zoomReal) || zoomReal <= 0 ||
            !std::isfinite(tolerancePixels) || tolerancePixels < 0 ||
            !std::isfinite(microLodPixels) || microLodPixels < 0 ||
            !IsFiniteEnvelope(viewportLeft, viewportTop, viewportRight, viewportBottom) ||
            (microGrid && (microGridWidth <= 0 || microGridHeight <= 0 ||
                          int64_t(microGridWidth) * microGridHeight > std::numeric_limits<int>::max())))
            return -2;
        struct CancellationScope {
            const std::atomic<bool>* previous = g_renderCancellation;
            explicit CancellationScope(void* current) { g_renderCancellation = static_cast<std::atomic<bool>*>(current); }
            ~CancellationScope() { g_renderCancellation = previous; }
        } scope(cancellation);
        try {
            CheckRenderCancellation();
            return ProcessShapeBatchCore(basePtr, offsets, count, outBuffer, maxFloats,
                offsetX, offsetY, zoomReal, tolerancePixels, microLodPixels,
                viewportLeft, viewportTop, viewportRight, viewportBottom,
                processedFeatures, requiredFloats, emittedParts, emittedVertices,
                microGrid, microGridWidth, microGridHeight, emittedMicroFeatures, fileLength);
        }
        catch (const RenderCancelled&) { return -1; }
        catch (const InvalidShape&) { return -2; }
        catch (...) { return -3; }
    }

    __declspec(dllexport) void* CreateShapeSpatialIndex(
        const unsigned char* basePtr,
        int64_t fileLength,
        const int64_t* sourceOffsets,
        const double* sourceBounds,
        const unsigned char* sourceKinds,
        int count,
        int workerCount)
    {
        if (!sourceOffsets || count <= 0 ||
            (!sourceBounds && (!basePtr || fileLength < 100))) return nullptr;

        try {
            auto index = std::make_unique<NativeShapeSpatialIndex>();
            index->minX.assign(count, std::numeric_limits<double>::infinity());
            index->minY.assign(count, std::numeric_limits<double>::infinity());
            index->maxX.assign(count, -std::numeric_limits<double>::infinity());
            index->maxY.assign(count, -std::numeric_limits<double>::infinity());
            index->kinds.assign(count, 0);

            const int threadsToUse = std::clamp(workerCount, 1, std::min(count, 32));
            NativeShapeSpatialIndex* indexPtr = index.get();
            auto parseRange = [=](int first, int last) {
                for (int feature = first; feature < last; ++feature) {
                    if (sourceBounds) {
                        const double* bounds = sourceBounds + static_cast<int64_t>(feature) * 4;
                        if (!IsFiniteEnvelope(bounds[0], bounds[1], bounds[2], bounds[3])) continue;
                        indexPtr->minX[feature] = bounds[0];
                        indexPtr->minY[feature] = bounds[1];
                        indexPtr->maxX[feature] = bounds[2];
                        indexPtr->maxY[feature] = bounds[3];
                        indexPtr->kinds[feature] = sourceKinds ? sourceKinds[feature] : 3;
                        continue;
                    }

                    const int64_t recordOffset = sourceOffsets[feature];
                    if (recordOffset < 100 || recordOffset > fileLength - 20) continue;

                    const unsigned char* data = basePtr + recordOffset + 8;
                    const int shapeType = ReadInt32Unaligned(data);
                    if (shapeType == 1 || shapeType == 11 || shapeType == 21) {
                        if (recordOffset > fileLength - 28) continue;
                        double point[2];
                        std::memcpy(point, data + 4, sizeof(point));
                        if (!IsFiniteEnvelope(point[0], point[1], point[0], point[1])) continue;
                        indexPtr->minX[feature] = indexPtr->maxX[feature] = point[0];
                        indexPtr->minY[feature] = indexPtr->maxY[feature] = point[1];
                        indexPtr->kinds[feature] = 1;
                    }
                    else if (shapeType == 3 || shapeType == 13 || shapeType == 23 ||
                             shapeType == 5 || shapeType == 15 || shapeType == 25) {
                        if (recordOffset > fileLength - 48) continue;
                        double envelope[4];
                        std::memcpy(envelope, data + 4, sizeof(envelope));
                        if (!IsFiniteEnvelope(envelope[0], envelope[1], envelope[2], envelope[3])) continue;
                        indexPtr->minX[feature] = envelope[0];
                        indexPtr->minY[feature] = envelope[1];
                        indexPtr->maxX[feature] = envelope[2];
                        indexPtr->maxY[feature] = envelope[3];
                        indexPtr->kinds[feature] =
                            (shapeType == 5 || shapeType == 15 || shapeType == 25) ? 3 : 2;
                    }
                }
            };

            if (threadsToUse == 1) {
                parseRange(0, count);
            }
            else {
                std::vector<std::thread> workers;
                workers.reserve(threadsToUse);
                for (int worker = 0; worker < threadsToUse; ++worker) {
                    const int first = static_cast<int>((static_cast<int64_t>(count) * worker) / threadsToUse);
                    const int last = static_cast<int>((static_cast<int64_t>(count) * (worker + 1)) / threadsToUse);
                    workers.emplace_back(parseRange, first, last);
                }
                for (auto& worker : workers) worker.join();
            }

            double globalMinX = std::numeric_limits<double>::infinity();
            double globalMinY = std::numeric_limits<double>::infinity();
            double globalMaxX = -std::numeric_limits<double>::infinity();
            double globalMaxY = -std::numeric_limits<double>::infinity();
            int validCount = 0;
            for (int feature = 0; feature < count; ++feature) {
                if (index->kinds[feature] == 0) continue;
                globalMinX = std::min(globalMinX, index->minX[feature]);
                globalMinY = std::min(globalMinY, index->minY[feature]);
                globalMaxX = std::max(globalMaxX, index->maxX[feature]);
                globalMaxY = std::max(globalMaxY, index->maxY[feature]);
                ++validCount;
            }

            if (validCount == 0) return index.release();
            index->worldMinX = globalMinX;
            index->worldMinY = globalMinY;
            index->worldMaxX = globalMaxX;
            index->worldMaxY = globalMaxY;

            const double width = std::max(globalMaxX - globalMinX, 1.0e-12);
            const double height = std::max(globalMaxY - globalMinY, 1.0e-12);
            const double aspect = std::clamp(width / height, 0.0625, 16.0);
            const int targetCells = std::max(1, validCount / 64);
            index->columns = std::clamp(
                static_cast<int>(std::sqrt(targetCells * aspect) + 0.5), 1, 2048);
            index->rows = std::clamp(
                static_cast<int>((static_cast<double>(targetCells) / index->columns) + 0.5), 1, 2048);
            index->inverseCellWidth = index->columns / width;
            index->inverseCellHeight = index->rows / height;

            const int cellCount = index->columns * index->rows;
            std::vector<int> counts(cellCount, 0);
            index->oversized.reserve(std::max(16, validCount / 1000));
            index->firstCells.assign(count, 0);

            for (int feature = 0; feature < count; ++feature) {
                if (index->kinds[feature] == 0) continue;
                const int firstX = index->CellX(index->minX[feature]);
                const int lastX = index->CellX(index->maxX[feature]);
                const int firstY = index->CellY(index->minY[feature]);
                const int lastY = index->CellY(index->maxY[feature]);
                index->firstCells[feature] = static_cast<uint32_t>(firstX) |
                    (static_cast<uint32_t>(firstY) << 16);
                const int touchedCells = (lastX - firstX + 1) * (lastY - firstY + 1);
                if (touchedCells > 16) {
                    index->oversized.push_back(feature);
                    continue;
                }
                for (int y = firstY; y <= lastY; ++y) {
                    const int row = y * index->columns;
                    for (int x = firstX; x <= lastX; ++x) ++counts[row + x];
                }
            }

            index->cellStarts.resize(static_cast<size_t>(cellCount) + 1);
            int64_t entryCount = 0;
            for (int cell = 0; cell < cellCount; ++cell) {
                index->cellStarts[cell] = static_cast<int>(entryCount);
                entryCount += counts[cell];
                if (entryCount > std::numeric_limits<int>::max()) {
                    return nullptr;
                }
            }
            index->cellStarts[cellCount] = static_cast<int>(entryCount);
            index->cellEntries.resize(static_cast<size_t>(entryCount));
            std::vector<int> cursors(index->cellStarts.begin(), index->cellStarts.end() - 1);

            for (int feature = 0; feature < count; ++feature) {
                if (index->kinds[feature] == 0) continue;
                const int firstX = index->CellX(index->minX[feature]);
                const int lastX = index->CellX(index->maxX[feature]);
                const int firstY = index->CellY(index->minY[feature]);
                const int lastY = index->CellY(index->maxY[feature]);
                if ((lastX - firstX + 1) * (lastY - firstY + 1) > 16) continue;
                for (int y = firstY; y <= lastY; ++y) {
                    const int row = y * index->columns;
                    for (int x = firstX; x <= lastX; ++x) {
                        const int cell = row + x;
                        index->cellEntries[cursors[cell]++] = feature;
                    }
                }
            }

            return index.release();
        }
        catch (...) {
            return nullptr;
        }
    }

    __declspec(dllexport) void DestroyShapeSpatialIndex(void* handle)
    {
        delete static_cast<NativeShapeSpatialIndex*>(handle);
    }

    __declspec(dllexport) int QueryShapeSpatialIndex(
        void* handle,
        double queryMinX,
        double queryMinY,
        double queryMaxX,
        double queryMaxY,
        int* results,
        int resultCapacity,
        int* requiredCount)
    {
        if (requiredCount) *requiredCount = 0;
        const auto* index = static_cast<const NativeShapeSpatialIndex*>(handle);
        if (!index || !IsFiniteEnvelope(queryMinX, queryMinY, queryMaxX, queryMaxY)) return 0;

        if (index->cellStarts.empty() ||
            queryMaxX < index->worldMinX || queryMinX > index->worldMaxX ||
            queryMaxY < index->worldMinY || queryMinY > index->worldMaxY) return 0;

        const int capacity = std::max(resultCapacity, 0);
        int total = 0;
        auto addIfVisible = [&](int feature) {
            if (index->minX[feature] <= queryMaxX && index->maxX[feature] >= queryMinX &&
                index->minY[feature] <= queryMaxY && index->maxY[feature] >= queryMinY) {
                if (results && total < capacity) results[total] = feature;
                ++total;
            }
        };

        const int firstX = index->CellX(queryMinX);
        const int lastX = index->CellX(queryMaxX);
        const int firstY = index->CellY(queryMinY);
        const int lastY = index->CellY(queryMaxY);
        for (int y = firstY; y <= lastY; ++y) {
            for (int x = firstX; x <= lastX; ++x) {
                const int cell = y * index->columns + x;
                for (int cursor = index->cellStarts[cell]; cursor < index->cellStarts[cell + 1]; ++cursor) {
                    const int feature = index->cellEntries[cursor];
                    const uint32_t firstCell = index->firstCells[feature];
                    const int featureFirstX = static_cast<int>(firstCell & 0xffffu);
                    const int featureFirstY = static_cast<int>(firstCell >> 16);
                    if (x == std::max(firstX, featureFirstX) &&
                        y == std::max(firstY, featureFirstY))
                        addIfVisible(feature);
                }
            }
        }
        for (int feature : index->oversized) addIfVisible(feature);

        // A ordem CSR é estável e determinística. Ordenar novamente por FID custa
        // O(k log k) em todo pan e não altera o resultado visual de um único estilo.
        if (requiredCount) *requiredCount = total;
        return std::min(total, capacity);
    }

    __declspec(dllexport) int64_t GetShapeSpatialIndexBytes(
        void* handle,
        int* gridCells,
        int* gridEntries,
        int* oversizedFeatures)
    {
        auto* index = static_cast<NativeShapeSpatialIndex*>(handle);
        if (!index) return 0;
        if (gridCells) *gridCells = index->columns * index->rows;
        if (gridEntries) *gridEntries = static_cast<int>(index->cellEntries.size());
        if (oversizedFeatures) *oversizedFeatures = static_cast<int>(index->oversized.size());
        return static_cast<int64_t>(index->minX.capacity() + index->minY.capacity() +
                                    index->maxX.capacity() + index->maxY.capacity()) * sizeof(double) +
               static_cast<int64_t>(index->kinds.capacity()) * sizeof(unsigned char) +
               static_cast<int64_t>(index->cellStarts.capacity() + index->cellEntries.capacity() +
                                    index->oversized.capacity()) * sizeof(int) +
               static_cast<int64_t>(index->firstCells.capacity()) * sizeof(uint32_t);
    }

}
