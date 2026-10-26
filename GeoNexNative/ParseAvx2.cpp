#include "NativeCpu.h"
#include <cstddef>
#include <immintrin.h>

namespace geonex {
    // This translation unit alone is compiled with /arch:AVX2. Never enable
    // whole-program optimization across this boundary: the baseline must remain
    // executable on x64 processors without AVX and on OSes without YMM support.
    void ParsePointsAvx2(const unsigned char* __restrict input, float* __restrict output, int count,
        double offsetX, double offsetY) noexcept {
        if (!input || !output || count <= 0) return;
        const __m256d offsets = _mm256_setr_pd(offsetX, offsetY, offsetX, offsetY);
        const __m256d flipY = _mm256_setr_pd(1.0, -1.0, 1.0, -1.0);
        const auto pointCount = static_cast<std::size_t>(count);
        std::size_t i = 0;
        // Loop unrolling 4x (8 pontos = 16 doubles = 128 bytes = 2 linhas de Cache L1 por iteração)
        for (; i + 7 < pointCount; i += 8) {
            const __m256d xy0 = _mm256_loadu_pd(reinterpret_cast<const double*>(input + i * 16));
            const __m256d xy1 = _mm256_loadu_pd(reinterpret_cast<const double*>(input + (i + 2) * 16));
            const __m256d xy2 = _mm256_loadu_pd(reinterpret_cast<const double*>(input + (i + 4) * 16));
            const __m256d xy3 = _mm256_loadu_pd(reinterpret_cast<const double*>(input + (i + 6) * 16));
            const __m256d local0 = _mm256_mul_pd(_mm256_sub_pd(xy0, offsets), flipY);
            const __m256d local1 = _mm256_mul_pd(_mm256_sub_pd(xy1, offsets), flipY);
            const __m256d local2 = _mm256_mul_pd(_mm256_sub_pd(xy2, offsets), flipY);
            const __m256d local3 = _mm256_mul_pd(_mm256_sub_pd(xy3, offsets), flipY);
            const __m128 ps0 = _mm256_cvtpd_ps(local0);
            const __m128 ps1 = _mm256_cvtpd_ps(local1);
            const __m128 ps2 = _mm256_cvtpd_ps(local2);
            const __m128 ps3 = _mm256_cvtpd_ps(local3);
            const __m256 combined0 = _mm256_insertf128_ps(_mm256_castps128_ps256(ps0), ps1, 1);
            const __m256 combined1 = _mm256_insertf128_ps(_mm256_castps128_ps256(ps2), ps3, 1);
            _mm256_storeu_ps(output + i * 2, combined0);
            _mm256_storeu_ps(output + (i + 4) * 2, combined1);
        }
        // Loop unrolling 2x (4 pontos = 8 doubles = 64 bytes = 1 linha inteira de Cache L1 por iteração)
        for (; i + 3 < pointCount; i += 4) {
            const __m256d xy0 = _mm256_loadu_pd(reinterpret_cast<const double*>(input + i * 16));
            const __m256d xy1 = _mm256_loadu_pd(reinterpret_cast<const double*>(input + (i + 2) * 16));
            const __m256d local0 = _mm256_mul_pd(_mm256_sub_pd(xy0, offsets), flipY);
            const __m256d local1 = _mm256_mul_pd(_mm256_sub_pd(xy1, offsets), flipY);
            const __m128 ps0 = _mm256_cvtpd_ps(local0);
            const __m128 ps1 = _mm256_cvtpd_ps(local1);
            const __m256 combined = _mm256_insertf128_ps(_mm256_castps128_ps256(ps0), ps1, 1);
            _mm256_storeu_ps(output + i * 2, combined);
        }
        for (; i + 1 < pointCount; i += 2) {
            const __m256d xy = _mm256_loadu_pd(reinterpret_cast<const double*>(input + i * 16));
            const __m256d local = _mm256_mul_pd(_mm256_sub_pd(xy, offsets), flipY);
            _mm_storeu_ps(output + i * 2, _mm256_cvtpd_ps(local));
        }
        if (i < pointCount)
            ParsePointsScalar(input + i * 16, output + i * 2, 1, offsetX, offsetY);
    }
}
