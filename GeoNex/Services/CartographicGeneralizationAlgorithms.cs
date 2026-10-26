using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NetTopologySuite;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Simplify;

namespace GeoNex.Services
{
    /// <summary>
    /// Algoritmos de generalização e suavização cartográfica:
    /// Douglas-Peucker com preservação topológica, Visvalingam-Whyatt (área triangular efetiva) e Chaikin Corner-Cutting.
    /// </summary>
    public static class CartographicGeneralizationAlgorithms
    {
        public static List<IFeature> ExecutarSimplificacaoDouglasPeucker(
            IReadOnlyList<IFeature> feicoes,
            double toleranciaMetros,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (feicoes == null || feicoes.Count == 0) return resultado;

            int total = feicoes.Count;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = feicoes[i];
                if (f.Geometry == null || f.Geometry.IsEmpty) continue;

                var geomSimplificada = TopologyPreservingSimplifier.Simplify(f.Geometry, Math.Max(0.0001, toleranciaMetros));
                if (geomSimplificada != null && !geomSimplificada.IsEmpty)
                {
                    resultado.Add(new Feature(geomSimplificada, f.Attributes));
                }

                if (i % 200 == 0 || i == total - 1)
                {
                    double taxa = sw.Elapsed.TotalSeconds > 0 ? (i + 1) / sw.Elapsed.TotalSeconds : 0;
                    progresso?.Report(new GeoprocessingProgress(
                        (i + 1) / (double)total,
                        $"Douglas-Peucker: feição {i + 1}/{total}",
                        i + 1,
                        total,
                        taxa));
                }
            }

            return resultado;
        }

        public static List<IFeature> ExecutarSimplificacaoVisvalingam(
            IReadOnlyList<IFeature> feicoes,
            double areaMinimaM2,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (feicoes == null || feicoes.Count == 0) return resultado;

            var geomFactory = NtsGeometryServices.Instance.CreateGeometryFactory();
            int total = feicoes.Count;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = feicoes[i];
                if (f.Geometry == null || f.Geometry.IsEmpty) continue;

                Geometry? novaGeom = SimplificarGeometriaVisvalingam(f.Geometry, areaMinimaM2, geomFactory);
                if (novaGeom != null && !novaGeom.IsEmpty)
                {
                    resultado.Add(new Feature(novaGeom, f.Attributes));
                }

                if (i % 100 == 0 || i == total - 1)
                {
                    double taxa = sw.Elapsed.TotalSeconds > 0 ? (i + 1) / sw.Elapsed.TotalSeconds : 0;
                    progresso?.Report(new GeoprocessingProgress(
                        (i + 1) / (double)total,
                        $"Visvalingam: feição {i + 1}/{total}",
                        i + 1,
                        total,
                        taxa));
                }
            }

            return resultado;
        }

        public static List<IFeature> ExecutarSuavizacaoChaikin(
            IReadOnlyList<IFeature> feicoes,
            int iteracoes = 2,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (feicoes == null || feicoes.Count == 0) return resultado;

            iteracoes = Math.Clamp(iteracoes, 1, 5);
            var geomFactory = NtsGeometryServices.Instance.CreateGeometryFactory();
            int total = feicoes.Count;
            var sw = Stopwatch.StartNew();

            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = feicoes[i];
                if (f.Geometry == null || f.Geometry.IsEmpty) continue;

                Geometry? geomSuave = SuavizarGeometriaChaikin(f.Geometry, iteracoes, geomFactory);
                if (geomSuave != null && !geomSuave.IsEmpty)
                {
                    resultado.Add(new Feature(geomSuave, f.Attributes));
                }

                if (i % 100 == 0 || i == total - 1)
                {
                    double taxa = sw.Elapsed.TotalSeconds > 0 ? (i + 1) / sw.Elapsed.TotalSeconds : 0;
                    progresso?.Report(new GeoprocessingProgress(
                        (i + 1) / (double)total,
                        $"Chaikin: feição {i + 1}/{total}",
                        i + 1,
                        total,
                        taxa));
                }
            }

            return resultado;
        }

        private static Geometry? SimplificarGeometriaVisvalingam(Geometry geom, double areaMinM2, GeometryFactory factory)
        {
            if (geom is LineString line)
            {
                var coords = SimplificarCoordsVisvalingam(line.Coordinates, areaMinM2, false);
                return factory.CreateLineString(coords);
            }
            if (geom is Polygon poly)
            {
                var shellCoords = SimplificarCoordsVisvalingam(poly.ExteriorRing.Coordinates, areaMinM2, true);
                if (shellCoords.Length < 4) return poly; // Conserva original se colapsar

                var holes = new LinearRing[poly.NumInteriorRings];
                for (int h = 0; h < poly.NumInteriorRings; h++)
                {
                    var holeCoords = SimplificarCoordsVisvalingam(poly.GetInteriorRingN(h).Coordinates, areaMinM2, true);
                    holes[h] = holeCoords.Length >= 4 ? factory.CreateLinearRing(holeCoords) : factory.CreateLinearRing(poly.GetInteriorRingN(h).Coordinates);
                }
                return factory.CreatePolygon(factory.CreateLinearRing(shellCoords), holes);
            }
            if (geom is MultiLineString mls)
            {
                var lines = new LineString[mls.NumGeometries];
                for (int m = 0; m < mls.NumGeometries; m++)
                {
                    lines[m] = (LineString)SimplificarGeometriaVisvalingam(mls.GetGeometryN(m), areaMinM2, factory)!;
                }
                return factory.CreateMultiLineString(lines);
            }
            if (geom is MultiPolygon mp)
            {
                var polys = new Polygon[mp.NumGeometries];
                for (int m = 0; m < mp.NumGeometries; m++)
                {
                    polys[m] = (Polygon)SimplificarGeometriaVisvalingam(mp.GetGeometryN(m), areaMinM2, factory)!;
                }
                return factory.CreateMultiPolygon(polys);
            }
            return geom;
        }

        public static Coordinate[] SimplificarCoordsVisvalingam(Coordinate[] coords, double areaMinM2, bool ehFechado)
        {
            int minPts = ehFechado ? 4 : 2;
            if (coords.Length <= minPts) return coords;

            var lista = new List<Coordinate>(coords);
            bool houveRemocao = true;

            while (houveRemocao && lista.Count > minPts)
            {
                houveRemocao = false;
                double menorArea = double.MaxValue;
                int idxRemover = -1;

                int limiteSuperior = ehFechado ? lista.Count - 2 : lista.Count - 2;
                for (int i = 1; i <= limiteSuperior; i++)
                {
                    var a = lista[i - 1];
                    var b = lista[i];
                    var c = lista[i + 1];
                    double area = 0.5 * Math.Abs(a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
                    if (area < menorArea)
                    {
                        menorArea = area;
                        idxRemover = i;
                    }
                }

                if (idxRemover != -1 && menorArea < areaMinM2)
                {
                    lista.RemoveAt(idxRemover);
                    houveRemocao = true;
                }
            }

            if (ehFechado && !lista[0].Equals2D(lista[^1]))
            {
                lista.Add(new Coordinate(lista[0].X, lista[0].Y));
            }

            return lista.ToArray();
        }

        private static Geometry? SuavizarGeometriaChaikin(Geometry geom, int iteracoes, GeometryFactory factory)
        {
            if (geom is LineString line)
            {
                var coords = SuavizarCoordsChaikin(line.Coordinates, iteracoes, false);
                return factory.CreateLineString(coords);
            }
            if (geom is Polygon poly)
            {
                var shellCoords = SuavizarCoordsChaikin(poly.ExteriorRing.Coordinates, iteracoes, true);
                var holes = new LinearRing[poly.NumInteriorRings];
                for (int h = 0; h < poly.NumInteriorRings; h++)
                {
                    var holeCoords = SuavizarCoordsChaikin(poly.GetInteriorRingN(h).Coordinates, iteracoes, true);
                    holes[h] = factory.CreateLinearRing(holeCoords);
                }
                return factory.CreatePolygon(factory.CreateLinearRing(shellCoords), holes);
            }
            if (geom is MultiLineString mls)
            {
                var lines = new LineString[mls.NumGeometries];
                for (int m = 0; m < mls.NumGeometries; m++)
                {
                    lines[m] = (LineString)SuavizarGeometriaChaikin(mls.GetGeometryN(m), iteracoes, factory)!;
                }
                return factory.CreateMultiLineString(lines);
            }
            if (geom is MultiPolygon mp)
            {
                var polys = new Polygon[mp.NumGeometries];
                for (int m = 0; m < mp.NumGeometries; m++)
                {
                    polys[m] = (Polygon)SuavizarGeometriaChaikin(mp.GetGeometryN(m), iteracoes, factory)!;
                }
                return factory.CreateMultiPolygon(polys);
            }
            return geom;
        }

        public static Coordinate[] SuavizarCoordsChaikin(Coordinate[] pts, int iteracoes, bool ehFechado)
        {
            if (pts.Length < 3) return pts;
            var atual = pts;

            for (int it = 0; it < iteracoes; it++)
            {
                var prox = new List<Coordinate>((atual.Length - 1) * 2 + 2);
                if (!ehFechado) prox.Add(atual[0]);

                int n = ehFechado ? atual.Length - 1 : atual.Length - 1;
                for (int i = 0; i < n; i++)
                {
                    var p0 = atual[i];
                    var p1 = atual[i + 1];
                    var q = new Coordinate(0.75 * p0.X + 0.25 * p1.X, 0.75 * p0.Y + 0.25 * p1.Y);
                    var r = new Coordinate(0.25 * p0.X + 0.75 * p1.X, 0.25 * p0.Y + 0.75 * p1.Y);
                    prox.Add(q);
                    prox.Add(r);
                }

                if (!ehFechado)
                {
                    prox.Add(atual[^1]);
                }
                else
                {
                    prox.Add(new Coordinate(prox[0].X, prox[0].Y));
                }
                atual = prox.ToArray();
            }

            return atual;
        }
    }
}
