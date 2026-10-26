using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using NetTopologySuite;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Prepared;
using Point = NetTopologySuite.Geometries.Point;

namespace GeoNex.Services
{
    /// <summary>
    /// Algoritmos de geração de malhas espaciais e amostragem cartográfica:
    /// Grade Regular Cartesiana, Grade Hexagonal Tesselada (Hexbins), Poisson Disk Sampling e Amostragem Regular de Pontos.
    /// </summary>
    public static class SpatialGridSamplingAlgorithms
    {
        public static List<IFeature> ExecutarGradeCartesiana(
            Envelope extensao,
            double larguraM,
            double alturaM,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (extensao == null || extensao.IsNull || larguraM <= 0.001 || alturaM <= 0.001)
                return resultado;

            var geomFactory = NtsGeometryServices.Instance.CreateGeometryFactory();
            int totalCols = (int)Math.Ceiling(extensao.Width / larguraM);
            int totalRows = (int)Math.Ceiling(extensao.Height / alturaM);
            long totalCells = (long)totalCols * totalRows;

            int col = 0, cellId = 1;
            var sw = Stopwatch.StartNew();

            for (double x = extensao.MinX; x < extensao.MaxX; x += larguraM, col++)
            {
                int row = 0;
                for (double y = extensao.MinY; y < extensao.MaxY; y += alturaM, row++)
                {
                    ct.ThrowIfCancellationRequested();

                    double x2 = Math.Min(x + larguraM, extensao.MaxX);
                    double y2 = Math.Min(y + alturaM, extensao.MaxY);

                    var coords = new[]
                    {
                        new Coordinate(x, y),
                        new Coordinate(x2, y),
                        new Coordinate(x2, y2),
                        new Coordinate(x, y2),
                        new Coordinate(x, y)
                    };

                    var poly = geomFactory.CreatePolygon(coords);
                    var attrs = new AttributesTable
                    {
                        { "CELL_ID", cellId++ },
                        { "COL", col },
                        { "ROW", row },
                        { "X_MIN", Math.Round(x, 4) },
                        { "Y_MIN", Math.Round(y, 4) },
                        { "X_MAX", Math.Round(x2, 4) },
                        { "Y_MAX", Math.Round(y2, 4) },
                        { "AREA_M2", Math.Round(poly.Area, 4) }
                    };

                    resultado.Add(new Feature(poly, attrs));

                    if (cellId % 500 == 0 || cellId == totalCells)
                    {
                        double taxa = sw.Elapsed.TotalSeconds > 0 ? cellId / sw.Elapsed.TotalSeconds : 0;
                        progresso?.Report(new GeoprocessingProgress(
                            Math.Min(1.0, cellId / (double)totalCells),
                            $"Grade Cartesiana: célula {cellId}/{totalCells}",
                            cellId,
                            (int)Math.Min(int.MaxValue, totalCells),
                            taxa));
                    }
                }
            }

            return resultado;
        }

        public static List<IFeature> ExecutarGradeHexagonal(
            Envelope extensao,
            double raioMetros,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (extensao == null || extensao.IsNull || raioMetros <= 0.001)
                return resultado;

            var geomFactory = NtsGeometryServices.Instance.CreateGeometryFactory();
            double w = Math.Sqrt(3.0) * raioMetros;
            double hPasso = 1.5 * raioMetros;

            int row = 0, hexId = 1;
            var sw = Stopwatch.StartNew();

            double yMin = extensao.MinY - raioMetros;
            double yMax = extensao.MaxY + raioMetros;
            double xMin = extensao.MinX - w;
            double xMax = extensao.MaxX + w;

            for (double y = yMin; y <= yMax; y += hPasso, row++)
            {
                double offsetX = (row % 2 == 1) ? w / 2.0 : 0.0;
                int col = 0;

                for (double x = xMin + offsetX; x <= xMax; x += w, col++)
                {
                    ct.ThrowIfCancellationRequested();

                    var coords = new Coordinate[7];
                    for (int k = 0; k < 6; k++)
                    {
                        double angRad = Math.PI / 180.0 * (60.0 * k + 30.0);
                        coords[k] = new Coordinate(
                            x + raioMetros * Math.Cos(angRad),
                            y + raioMetros * Math.Sin(angRad)
                        );
                    }
                    coords[6] = coords[0];

                    var hex = geomFactory.CreatePolygon(coords);
                    if (hex.EnvelopeInternal.Intersects(extensao))
                    {
                        var attrs = new AttributesTable
                        {
                            { "HEX_ID", hexId++ },
                            { "ROW", row },
                            { "COL", col },
                            { "CENTER_X", Math.Round(x, 4) },
                            { "CENTER_Y", Math.Round(y, 4) },
                            { "AREA_M2", Math.Round(hex.Area, 4) }
                        };
                        resultado.Add(new Feature(hex, attrs));
                    }

                    if (hexId % 500 == 0)
                    {
                        double taxa = sw.Elapsed.TotalSeconds > 0 ? hexId / sw.Elapsed.TotalSeconds : 0;
                        progresso?.Report(new GeoprocessingProgress(
                            0.5,
                            $"Grade Hexagonal: hexágono {hexId}",
                            hexId,
                            0,
                            taxa));
                    }
                }
            }

            progresso?.Report(new GeoprocessingProgress(1.0, $"Grade Hexagonal concluída: {resultado.Count} células", resultado.Count, resultado.Count));
            return resultado;
        }

        public static List<IFeature> ExecutarAmostragemPoissonDisk(
            Geometry poligono,
            double distanciaMinimaM,
            int maxPontos = 500,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (poligono == null || poligono.IsEmpty || distanciaMinimaM <= 0.001 || maxPontos <= 0)
                return resultado;

            var geomFactory = NtsGeometryServices.Instance.CreateGeometryFactory();
            var prep = PreparedGeometryFactory.Prepare(poligono);
            var env = poligono.EnvelopeInternal;

            var rnd = new Random(1337);
            var amostras = new List<Coordinate>();

            int maxTentativas = maxPontos * 40;
            int tentativas = 0;
            var sw = Stopwatch.StartNew();

            while (amostras.Count < maxPontos && tentativas < maxTentativas)
            {
                ct.ThrowIfCancellationRequested();
                tentativas++;

                double rx = env.MinX + rnd.NextDouble() * env.Width;
                double ry = env.MinY + rnd.NextDouble() * env.Height;
                var cand = new Coordinate(rx, ry);

                var ptGeom = geomFactory.CreatePoint(cand);
                if (!prep.Contains(ptGeom))
                    continue;

                bool conflito = false;
                for (int i = 0; i < amostras.Count; i++)
                {
                    if (cand.Distance(amostras[i]) < distanciaMinimaM)
                    {
                        conflito = true;
                        break;
                    }
                }

                if (!conflito)
                {
                    amostras.Add(cand);
                    var attrs = new AttributesTable
                    {
                        { "SAMPLE_ID", amostras.Count },
                        { "X", Math.Round(cand.X, 4) },
                        { "Y", Math.Round(cand.Y, 4) }
                    };
                    resultado.Add(new Feature(ptGeom, attrs));

                    if (amostras.Count % 50 == 0 || amostras.Count == maxPontos)
                    {
                        double taxa = sw.Elapsed.TotalSeconds > 0 ? amostras.Count / sw.Elapsed.TotalSeconds : 0;
                        progresso?.Report(new GeoprocessingProgress(
                            amostras.Count / (double)maxPontos,
                            $"Poisson Disk: amostra {amostras.Count}/{maxPontos}",
                            amostras.Count,
                            maxPontos,
                            taxa));
                    }
                }
            }

            return resultado;
        }

        public static List<IFeature> ExecutarAmostragemRegularPontos(
            Geometry poligono,
            double espacamentoM,
            IProgress<GeoprocessingProgress>? progresso = null,
            CancellationToken ct = default)
        {
            var resultado = new List<IFeature>();
            if (poligono == null || poligono.IsEmpty || espacamentoM <= 0.001)
                return resultado;

            var geomFactory = NtsGeometryServices.Instance.CreateGeometryFactory();
            var prep = PreparedGeometryFactory.Prepare(poligono);
            var env = poligono.EnvelopeInternal;

            int id = 1;
            var sw = Stopwatch.StartNew();

            for (double x = env.MinX; x <= env.MaxX; x += espacamentoM)
            {
                for (double y = env.MinY; y <= env.MaxY; y += espacamentoM)
                {
                    ct.ThrowIfCancellationRequested();
                    var pt = geomFactory.CreatePoint(new Coordinate(x, y));
                    if (prep.Covers(pt) || prep.Intersects(pt))
                    {
                        var attrs = new AttributesTable
                        {
                            { "PT_ID", id++ },
                            { "X", Math.Round(x, 4) },
                            { "Y", Math.Round(y, 4) }
                        };
                        resultado.Add(new Feature(pt, attrs));
                    }
                }
            }

            progresso?.Report(new GeoprocessingProgress(1.0, $"Amostragem Regular concluída: {resultado.Count} pontos", resultado.Count, resultado.Count));
            return resultado;
        }
    }
}
