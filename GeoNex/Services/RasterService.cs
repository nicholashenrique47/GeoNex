using System;
using System.IO;
using OSGeo.GDAL;
using MaxRev.Gdal.Core;
using OSGeo.OSR;
using OSGeo.OGR;
using System.Collections.Generic;

namespace GeoNex.Services;

public class RasterService
{
    public RasterService()
    {
        GdalBase.ConfigureAll();
        Gdal.AllRegister();
    }

    public string AnalisarArquivoRaster(string caminhoArquivo)
    {
        try
        {
            Dataset dataset = Gdal.Open(caminhoArquivo, Access.GA_ReadOnly);
            if (dataset == null) return "Erro: O GDAL não conseguiu ler este formato.";

            int largura = dataset.RasterXSize;
            int altura = dataset.RasterYSize;

            double[] geoTransform = new double[6];
            dataset.GetGeoTransform(geoTransform);

            double xMin = geoTransform[0];
            double yMax = geoTransform[3];
            double pixelWidth = geoTransform[1];
            double pixelHeight = geoTransform[5];

            double xMax = xMin + (largura * pixelWidth);
            double yMin = yMax + (altura * pixelHeight);

            dataset.Dispose();

            return $"Raster Lido com Sucesso!\nResolução: {largura}x{altura} pixels\nCoordenadas:\nX Mínimo: {Math.Round(xMin, 2)}\nY Máximo: {Math.Round(yMax, 2)}";
        }
        catch (Exception ex)
        {
            return $"Falha crítica ao ler o Raster: {ex.Message}";
        }
    }

    public (string Base64, double MinLat, double MinLng, double MaxLat, double MaxLng) PrepararRasterParaMapa(string caminhoArquivo)
    {
        try
        {
            Dataset dataset = Gdal.Open(caminhoArquivo, Access.GA_ReadOnly);
            if (dataset == null) throw new Exception("Não foi possível abrir o arquivo raster.");

            // ENGRENAGEM 1: Pirâmides de Performance para arquivos grandes
            if (dataset.GetRasterBand(1).GetOverviewCount() == 0)
            {
                dataset.BuildOverviews("AVERAGE", new int[] { 2, 4, 8, 16, 32, 64, 128 });
            }

            int largura = dataset.RasterXSize;
            int altura = dataset.RasterYSize;
            double[] geoTransform = new double[6];
            dataset.GetGeoTransform(geoTransform);

            double minX = geoTransform[0];
            double maxY = geoTransform[3];
            double maxX = minX + (largura * geoTransform[1]);
            double minY = maxY + (altura * geoTransform[5]);

            double minLat = minY, minLng = minX, maxLat = maxY, maxLng = maxX;

            string wkt = dataset.GetProjection();
            if (!string.IsNullOrEmpty(wkt))
            {
                using (SpatialReference projOrigem = new SpatialReference(wkt))
                using (SpatialReference projDestino = new SpatialReference(""))
                {
                    projDestino.ImportFromEPSG(4326);
                    projDestino.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);

                    using (CoordinateTransformation transform = new CoordinateTransformation(projOrigem, projDestino))
                    {
                        double[] ptMin = new double[] { minX, minY, 0 };
                        double[] ptMax = new double[] { maxX, maxY, 0 };

                        transform.TransformPoint(ptMin);
                        transform.TransformPoint(ptMax);

                        minLng = ptMin[0];
                        minLat = ptMin[1];
                        maxLng = ptMax[0];
                        maxLat = ptMax[1];
                    }
                }
            }

            int larguraRender = Math.Min(largura, 4096);
            int alturaRender = (int)((double)altura / largura * larguraRender);

            int qtBandas = dataset.RasterCount;
            Band bandR = dataset.GetRasterBand(1);
            Band bandG = qtBandas >= 2 ? dataset.GetRasterBand(2) : bandR;
            Band bandB = qtBandas >= 3 ? dataset.GetRasterBand(3) : bandR;

            int[] bufferR = new int[larguraRender * alturaRender];
            int[] bufferG = new int[larguraRender * alturaRender];
            int[] bufferB = new int[larguraRender * alturaRender];

            bandR.ReadRaster(0, 0, largura, altura, bufferR, larguraRender, alturaRender, 0, 0);
            bandG.ReadRaster(0, 0, largura, altura, bufferG, larguraRender, alturaRender, 0, 0);
            bandB.ReadRaster(0, 0, largura, altura, bufferB, larguraRender, alturaRender, 0, 0);

            // CORREÇÃO: Declaração das estruturas ANTES do loop de processamento
            var info = new SkiaSharp.SKImageInfo(larguraRender, alturaRender, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
            byte[] rawPixels = new byte[larguraRender * alturaRender * 4];

            int index = 0;
            for (int i = 0; i < bufferR.Length; i++)
            {
                rawPixels[index++] = (byte)(bufferR[i] & 0xFF); // R
                rawPixels[index++] = (byte)(bufferG[i] & 0xFF); // G
                rawPixels[index++] = (byte)(bufferB[i] & 0xFF); // B
                rawPixels[index++] = (bufferR[i] == 0 && bufferG[i] == 0 && bufferB[i] == 0) ? (byte)0 : (byte)255; // A
            }

            // CORREÇÃO: Escopo único e limpo para image e data em formato JPEG otimizado
            using var image = SkiaSharp.SKImage.FromPixelCopy(info, rawPixels);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90);
            string base64String = Convert.ToBase64String(data.ToArray());

            dataset.Dispose();
            return (base64String, minLat, minLng, maxLat, maxLng);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Erro no RasterService: " + ex.Message);
            return ("", 0, 0, 0, 0);
        }
    }

    public string ObterRecorteDinamico(string caminhoArquivo, double minLat, double minLng, double maxLat, double maxLng, int larguraTela, int alturaTela)
    {
        try
        {
            using Dataset dataset = Gdal.Open(caminhoArquivo, Access.GA_ReadOnly);
            if (dataset == null) return "";

            // ENGRENAGEM 2: Interpolação cúbica para manter a qualidade nativa da foto
            string[] warpArgs = {
                "-te", minLng.ToString(System.Globalization.CultureInfo.InvariantCulture),
                       minLat.ToString(System.Globalization.CultureInfo.InvariantCulture),
                       maxLng.ToString(System.Globalization.CultureInfo.InvariantCulture),
                       maxLat.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-t_srs", "EPSG:4326",
                "-ts", larguraTela.ToString(), alturaTela.ToString(),
                "-r", "cubic",
                "-of", "MEM"
            };

            using GDALWarpAppOptions warpOptions = new GDALWarpAppOptions(warpArgs);
            using Dataset memDs = Gdal.Warp("", new[] { dataset }, warpOptions, null, null);

            if (memDs == null) return "";

            int qtBandas = memDs.RasterCount;
            Band bandR = memDs.GetRasterBand(1);
            Band bandG = qtBandas >= 2 ? memDs.GetRasterBand(2) : bandR;
            Band bandB = qtBandas >= 3 ? memDs.GetRasterBand(3) : bandR;

            int[] bufferR = new int[larguraTela * alturaTela];
            int[] bufferG = new int[larguraTela * alturaTela];
            int[] bufferB = new int[larguraTela * alturaTela];

            bandR.ReadRaster(0, 0, larguraTela, alturaTela, bufferR, larguraTela, alturaTela, 0, 0);
            bandG.ReadRaster(0, 0, larguraTela, alturaTela, bufferG, larguraTela, alturaTela, 0, 0);
            bandB.ReadRaster(0, 0, larguraTela, alturaTela, bufferB, larguraTela, alturaTela, 0, 0);

            // CORREÇÃO: Declaração correta antes do processamento
            var info = new SkiaSharp.SKImageInfo(larguraTela, alturaTela, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
            byte[] rawPixels = new byte[larguraTela * alturaTela * 4];

            int index = 0;
            for (int i = 0; i < bufferR.Length; i++)
            {
                rawPixels[index++] = (byte)(bufferR[i] & 0xFF); // R
                rawPixels[index++] = (byte)(bufferG[i] & 0xFF); // G
                rawPixels[index++] = (byte)(bufferB[i] & 0xFF); // B
                rawPixels[index++] = (bufferR[i] == 0 && bufferG[i] == 0 && bufferB[i] == 0) ? (byte)0 : (byte)255; // A
            }

            using var image = SkiaSharp.SKImage.FromPixelCopy(info, rawPixels);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90);

            return Convert.ToBase64String(data.ToArray());
        }
        catch (Exception ex)
        {
            Console.WriteLine("Erro Crítico no Recorte GDAL: " + ex.Message);
            return "";
        }
    }

    public string ProcessarVetorParaGeoJson(string caminhoArquivo)
    {
        try
        {
            using OSGeo.OGR.DataSource ds = Ogr.Open(caminhoArquivo, 0);
            if (ds == null) throw new Exception("Falha ao ler o arquivo vetorial.");

            using Layer layer = ds.GetLayerByIndex(0);

            using SpatialReference srOrigem = layer.GetSpatialRef();
            using SpatialReference srDestino = new SpatialReference("");
            srDestino.ImportFromEPSG(4326);
            srDestino.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);

            CoordinateTransformation transform = null;

            if (srOrigem != null && srOrigem.IsSame(srDestino, null) != 1)
            {
                transform = new CoordinateTransformation(srOrigem, srDestino);
            }

            using FeatureDefn defn = layer.GetLayerDefn();
            int fieldCount = defn.GetFieldCount();

            System.Text.StringBuilder geoJson = new System.Text.StringBuilder();
            geoJson.Append("{\"type\": \"FeatureCollection\", \"features\": [");

            Feature feat;
            bool primeiro = true;
            layer.ResetReading();

            while ((feat = layer.GetNextFeature()) != null)
            {
                using Geometry geom = feat.GetGeometryRef();
                if (geom != null)
                {
                    if (transform != null) geom.Transform(transform);

                    if (!primeiro) geoJson.Append(",");

                    System.Text.StringBuilder props = new System.Text.StringBuilder();
                    props.Append("{");
                    props.Append($"\"FID\": \"{feat.GetFID()}\"");

                    for (int i = 0; i < fieldCount; i++)
                    {
                        props.Append(",");

                        string fName = defn.GetFieldDefn(i).GetName();
                        string fValue = feat.GetFieldAsString(i) ?? "";

                        fValue = fValue.Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");
                        props.Append($"\"{fName}\": \"{fValue}\"");
                    }
                    props.Append("}");

                    string geometriaJson = geom.ExportToJson(null);
                    geoJson.Append("{\"type\": \"Feature\", \"geometry\": " + geometriaJson + ", \"properties\": " + props.ToString() + "}");

                    primeiro = false;
                }
                feat.Dispose();
            }

            geoJson.Append("]}");
            return geoJson.ToString();
        }
        catch (Exception ex)
        {
            Console.WriteLine("Erro ao processar Vetor: " + ex.Message);
            return "";
        }
    }

    public List<float[]> ExtrairPoligonosNativos(string caminhoArquivo)
    {
        var listaPoligonos = new List<float[]>();

        try
        {
            using OSGeo.OGR.DataSource ds = Ogr.Open(caminhoArquivo, 0);
            if (ds == null) throw new Exception("Falha ao ler o arquivo vetorial.");

            using Layer layer = ds.GetLayerByIndex(0);

            using SpatialReference srOrigem = layer.GetSpatialRef();
            using SpatialReference srDestino = new SpatialReference("");
            srDestino.ImportFromEPSG(4326);
            srDestino.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);

            CoordinateTransformation transform = null;
            if (srOrigem != null && srOrigem.IsSame(srDestino, null) != 1)
            {
                transform = new CoordinateTransformation(srOrigem, srDestino);
            }

            Feature feat;
            layer.ResetReading();

            while ((feat = layer.GetNextFeature()) != null)
            {
                using Geometry geom = feat.GetGeometryRef();
                if (geom != null)
                {
                    if (transform != null) geom.Transform(transform);

                    wkbGeometryType tipo = geom.GetGeometryType();

                    if (tipo == wkbGeometryType.wkbPolygon)
                    {
                        ExtrairAnelDeCoordenadas(geom, listaPoligonos);
                    }
                    else if (tipo == wkbGeometryType.wkbMultiPolygon)
                    {
                        int subGeomCount = geom.GetGeometryCount();
                        for (int i = 0; i < subGeomCount; i++)
                        {
                            using Geometry subGeom = geom.GetGeometryRef(i);
                            ExtrairAnelDeCoordenadas(subGeom, listaPoligonos);
                        }
                    }
                }
                feat.Dispose();
            }

            return listaPoligonos;
        }
        catch (Exception ex)
        {
            Console.WriteLine("[MOTOR NATIVO] Erro Crítico na extração: " + ex.Message);
            return listaPoligonos;
        }
    }

    private void ExtrairAnelDeCoordenadas(Geometry poligono, List<float[]> listaPoligonos)
    {
        using Geometry anel = poligono.GetGeometryRef(0);
        if (anel != null)
        {
            int totalPontos = anel.GetPointCount();
            float[] coordenadas = new float[totalPontos * 2];

            for (int i = 0; i < totalPontos; i++)
            {
                coordenadas[i * 2] = (float)anel.GetX(i);
                coordenadas[(i * 2) + 1] = (float)anel.GetY(i);
            }

            listaPoligonos.Add(coordenadas);
        }
    }

    public (List<string> Colunas, List<Dictionary<string, object>> Dados) ObterAtributosVetor(string caminhoArquivo)
    {
        var colunas = new List<string>();
        var dados = new List<Dictionary<string, object>>();

        try
        {
            using OSGeo.OGR.DataSource ds = Ogr.Open(caminhoArquivo, 0);
            if (ds == null) return (colunas, dados);

            using Layer layer = ds.GetLayerByIndex(0);
            using FeatureDefn defn = layer.GetLayerDefn();

            colunas.Add("FID");
            for (int i = 0; i < defn.GetFieldCount(); i++)
            {
                colunas.Add(defn.GetFieldDefn(i).GetName());
            }

            Feature feat;
            layer.ResetReading();

            int contador = 0;
            int limiteSeguranca = 2000;

            while ((feat = layer.GetNextFeature()) != null && contador < limiteSeguranca)
            {
                var linha = new Dictionary<string, object>();
                linha["FID"] = feat.GetFID().ToString();

                for (int i = 0; i < defn.GetFieldCount(); i++)
                {
                    string fieldName = defn.GetFieldDefn(i).GetName();
                    linha[fieldName] = feat.GetFieldAsString(i);
                }

                dados.Add(linha);
                feat.Dispose();
                contador++;
            }

            return (colunas, dados);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erro ao ler tabela de atributos: {ex.Message}");
            return (colunas, dados);
        }
    }
}