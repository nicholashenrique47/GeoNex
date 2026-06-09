using System;
using System.IO;
using System.Runtime.InteropServices;
using OSGeo.GDAL;
using MaxRev.Gdal.Core;
using OSGeo.OSR;
using OSGeo.OGR;



// 1. ISOLAMENTO DE PLATAFORMA: Só importa o motor gráfico se for Windows


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

            int largura = dataset.RasterXSize;
            int altura = dataset.RasterYSize;
            double[] geoTransform = new double[6];
            dataset.GetGeoTransform(geoTransform);

            // 1. Pegamos as coordenadas originais da imagem (Em Metros / UTM)
            double minX = geoTransform[0];
            double maxY = geoTransform[3];
            double maxX = minX + (largura * geoTransform[1]);
            double minY = maxY + (altura * geoTransform[5]);

            // 2. MAGIA DA REPROJEÇÃO: Convertendo Metros para Graus (WGS84) para o Leaflet entender
            double minLat = minY, minLng = minX, maxLat = maxY, maxLng = maxX;

            string wkt = dataset.GetProjection();
            if (!string.IsNullOrEmpty(wkt))
            {
                using (SpatialReference projOrigem = new SpatialReference(wkt))
                using (SpatialReference projDestino = new SpatialReference(""))
                {
                    projDestino.ImportFromEPSG(4326); // EPSG do GPS/Leaflet (WGS84)
                    projDestino.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);

                    using (CoordinateTransformation transform = new CoordinateTransformation(projOrigem, projDestino))
                    {
                        double[] ptMin = new double[] { minX, minY, 0 };
                        double[] ptMax = new double[] { maxX, maxY, 0 };

                        transform.TransformPoint(ptMin);
                        transform.TransformPoint(ptMax);

                        // Agora sim, essas variáveis recebem graus (ex: -25.88 e -48.57)
                        minLng = ptMin[0];
                        minLat = ptMin[1];
                        maxLng = ptMax[0];
                        maxLat = ptMax[1];
                    }
                }
            }

            // 3. Processamento da Imagem (Suporte a RGB e Opacidade = 255)


            // 3. Processamento da Imagem (Libertando a qualidade 4K para a RAM!)
            int larguraRender = Math.Min(largura, 4096);
            int alturaRender = (int)((double)altura / largura * larguraRender);

            // Garante que a imagem tem pelo menos 3 bandas (RGB). Se for P&B, repete a banda 1.

            int qtBandas = dataset.RasterCount;
            Band bandR = dataset.GetRasterBand(1);
            Band bandG = qtBandas >= 2 ? dataset.GetRasterBand(2) : bandR;
            Band bandB = qtBandas >= 3 ? dataset.GetRasterBand(3) : bandR;

            int[] bufferR = new int[larguraRender * alturaRender];
            int[] bufferG = new int[larguraRender * alturaRender];
            int[] bufferB = new int[larguraRender * alturaRender];

            // GDAL lê as fatias de cor (Pode demorar alguns segundos por causa dos 42GB)
            bandR.ReadRaster(0, 0, largura, altura, bufferR, larguraRender, alturaRender, 0, 0);
            bandG.ReadRaster(0, 0, largura, altura, bufferG, larguraRender, alturaRender, 0, 0);
            bandB.ReadRaster(0, 0, largura, altura, bufferB, larguraRender, alturaRender, 0, 0);

            // Junta as 3 cores num único pixel e FORÇA a opacidade (Alpha = 255)
            // Junta as 3 cores num único pixel e trata o "NoData" (Borda Preta)
            int[] pixelsARGB = new int[larguraRender * alturaRender];
            for (int i = 0; i < pixelsARGB.Length; i++)
            {
                int r = bufferR[i] & 0xFF;
                int g = bufferG[i] & 0xFF;
                int b = bufferB[i] & 0xFF;

                // Se for preto absoluto (área sem foto do drone), Alpha = 0 (Transparente)
                // Caso contrário, Alpha = 255 (Totalmente Opaco)
                int a = (r == 0 && g == 0 && b == 0) ? 0 : 255;

                pixelsARGB[i] = (a << 24) | (r << 16) | (g << 8) | b;
            }

            // 4. ISOLAMENTO DE PLATAFORMA (Com as tags nas linhas corretas)
#if WINDOWS
            using (Bitmap bitmap = new Bitmap(larguraRender, alturaRender, PixelFormat.Format32bppArgb))
            {
                BitmapData bitmapData = bitmap.LockBits(new Rectangle(0, 0, larguraRender, alturaRender), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                Marshal.Copy(pixelsARGB, 0, bitmapData.Scan0, pixelsARGB.Length);
                bitmap.UnlockBits(bitmapData);

                using (MemoryStream ms = new MemoryStream())
                {
                    bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    byte[] byteImage = ms.ToArray();
                    string base64String = Convert.ToBase64String(byteImage);

                    dataset.Dispose();
                    return (base64String, minLat, minLng, maxLat, maxLng);
                }
            }
#else
            // Se tentar compilar para iOS/Android, ele passa direto sem quebrar o sistema
            dataset.Dispose();
            return ("", minLat, minLng, maxLat, maxLng);
#endif
        }
        catch (Exception ex)
        {
            Console.WriteLine("Erro no RasterService: " + ex.Message);
            return ("", 0, 0, 0, 0);
        }
    }
    /// <summary>
    /// Motor WMS Local: Corta um pedaço exato de um raster gigante (ex: 42 GB) baseado no zoom atual do Leaflet.
    /// </summary>
    public string ObterRecorteDinamico(string caminhoArquivo, double minLat, double minLng, double maxLat, double maxLng, int larguraTela, int alturaTela)
    {
        try
        {
            using Dataset dataset = Gdal.Open(caminhoArquivo, Access.GA_ReadOnly);
            if (dataset == null) return "";

            // A MÁGICA: O GDAL Warp corta, reprojeta e redimensiona a imagem tudo num só comando!
            string[] warpArgs = {
                "-te", minLng.ToString(System.Globalization.CultureInfo.InvariantCulture),
                       minLat.ToString(System.Globalization.CultureInfo.InvariantCulture),
                       maxLng.ToString(System.Globalization.CultureInfo.InvariantCulture),
                       maxLat.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-t_srs", "EPSG:4326", // Garante que a saída vai direto em Lat/Lng para o Leaflet
                "-ts", larguraTela.ToString(), alturaTela.ToString(), // Força a usar 100% da resolução do seu monitor
                "-of", "MEM" // Executa a operação inteira na Memória RAM (Ultra rápido)
            };

            using GDALWarpAppOptions warpOptions = new GDALWarpAppOptions(warpArgs);
            using Dataset memDs = Gdal.Warp("", new[] { dataset }, warpOptions, null, null);

            if (memDs == null) return "";

            // Lê as cores do recorte processado
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

            // Monta os Pixels e remove o fundo preto (NoData)
            // --- OTIMIZAÇÃO EXTREMA: Injeção Direta na Memória SkiaSharp ---

            // 1. Matriz plana de cores (Zero conversões Bit a Bit complexas)
            var pixels = new SkiaSharp.SKColor[larguraTela * alturaTela];

            for (int i = 0; i < pixels.Length; i++)
            {
                byte r = (byte)(bufferR[i] & 0xFF);
                byte g = (byte)(bufferG[i] & 0xFF);
                byte b = (byte)(bufferB[i] & 0xFF);
                byte a = (r == 0 && g == 0 && b == 0) ? (byte)0 : (byte)255;

                pixels[i] = new SkiaSharp.SKColor(r, g, b, a);
            }

            // 2. Aloca a imagem diretamente no motor C++ 
            var info = new SkiaSharp.SKImageInfo(larguraTela, alturaTela);
            using var bitmap = new SkiaSharp.SKBitmap(info);
            bitmap.Pixels = pixels; // Injeta todos os milhares de pixeis num milissegundo

            // 3. Codifica para PNG nativamente (ignora o Windows Forms/Drawing)
            using var image = SkiaSharp.SKImage.FromBitmap(bitmap);

            // DICA PRO: Pode mudar SKEncodedImageFormat.Png para .Webp (qualidade 80) se quiser ainda mais velocidade!
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);

            return Convert.ToBase64String(data.ToArray());
        }
        catch (Exception ex)
        {
            Console.WriteLine("Erro Crítico no Recorte GDAL: " + ex.Message);
            return "";
        }
    }
    /// <summary>
    /// Lê um arquivo vetorial (SHP, GeoJSON), reprojeta para WGS84 e exporta como uma string GeoJSON.
    /// </summary>
    /// <summary>
    /// Lê um arquivo vetorial (SHP, GeoJSON), reprojeta para WGS84 e exporta como uma string GeoJSON completa (Geometria + Atributos).
    /// </summary>
    /// <summary>
    /// Lê um arquivo vetorial (SHP, GeoJSON), reprojeta para WGS84 e exporta como uma string GeoJSON.
    /// Constrói dinamicamente os atributos da tabela DBF para suportar rótulos e inspeção.
    /// </summary>
    public string ProcessarVetorParaGeoJson(string caminhoArquivo)
    {
        try
        {
            using OSGeo.OGR.DataSource ds = Ogr.Open(caminhoArquivo, 0);
            if (ds == null) throw new Exception("Falha ao ler o arquivo vetorial.");

            using Layer layer = ds.GetLayerByIndex(0);

            // 1. Lógica de Reprojeção (Metros para Graus)
            using SpatialReference srOrigem = layer.GetSpatialRef();
            using SpatialReference srDestino = new SpatialReference("");
            srDestino.ImportFromEPSG(4326); // WGS84
            srDestino.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);

            CoordinateTransformation transform = null;

            if (srOrigem != null && srOrigem.IsSame(srDestino, null) != 1)
            {
                transform = new CoordinateTransformation(srOrigem, srDestino);
            }

            // 2. Extrai a estrutura das colunas do arquivo (Schema)
            using FeatureDefn defn = layer.GetLayerDefn();
            int fieldCount = defn.GetFieldCount();

            // 3. Constrói o GeoJSON na memória
            System.Text.StringBuilder geoJson = new System.Text.StringBuilder();
            geoJson.Append("{\"type\": \"FeatureCollection\", \"features\": [");

            Feature feat;
            bool primeiro = true;
            layer.ResetReading();

            // Lê cada lote/rua/ponto do Shapefile
            while ((feat = layer.GetNextFeature()) != null)
            {
                using Geometry geom = feat.GetGeometryRef();
                if (geom != null)
                {
                    if (transform != null) geom.Transform(transform);

                    if (!primeiro) geoJson.Append(",");

                    // Monta o bloco "properties" varrendo todas as colunas do DBF
                    System.Text.StringBuilder props = new System.Text.StringBuilder();
                    props.Append("{");

                    // Adiciona o FID (ID interno do OGR) como primeira propriedade
                    props.Append($"\"FID\": \"{feat.GetFID()}\"");

                    for (int i = 0; i < fieldCount; i++)
                    {
                        props.Append(",");

                        string fName = defn.GetFieldDefn(i).GetName();
                        string fValue = feat.GetFieldAsString(i) ?? "";

                        // Tratamento de segurança contra caracteres que quebram a sintaxe JSON
                        fValue = fValue.Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");

                        props.Append($"\"{fName}\": \"{fValue}\"");
                    }
                    props.Append("}");

                    // Exporta apenas a geometria usando o método nativo que existe
                    string geometriaJson = geom.ExportToJson(null);

                    // Une a Geometria com os Atributos processados
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
    /// <summary>
    /// EXTRAÇÃO NATIVA: Lê o Shapefile e converte as geometrias diretamente em matrizes numéricas (Floats)
    /// Saltando completamente a geração de texto (GeoJSON) para maximizar o desempenho no SkiaSharp e C++.
    /// </summary>
    public List<float[]> ExtrairPoligonosNativos(string caminhoArquivo)
    {
        // Uma lista onde cada item é um array de coordenadas de um lote: [X1, Y1, X2, Y2, X3, Y3...]
        var listaPoligonos = new List<float[]>();

        try
        {
            using OSGeo.OGR.DataSource ds = Ogr.Open(caminhoArquivo, 0);
            if (ds == null) throw new Exception("Falha ao ler o arquivo vetorial.");

            using Layer layer = ds.GetLayerByIndex(0);

            // 1. Matriz de Reprojeção (De UTM/Metros para Graus WGS84)
            using SpatialReference srOrigem = layer.GetSpatialRef();
            using SpatialReference srDestino = new SpatialReference("");
            srDestino.ImportFromEPSG(4326); // WGS84
            srDestino.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);

            CoordinateTransformation transform = null;
            if (srOrigem != null && srOrigem.IsSame(srDestino, null) != 1)
            {
                transform = new CoordinateTransformation(srOrigem, srDestino);
            }

            Feature feat;
            layer.ResetReading();

            // 2. Loop de Extração Bruta
            while ((feat = layer.GetNextFeature()) != null)
            {
                using Geometry geom = feat.GetGeometryRef();
                if (geom != null)
                {
                    // Aplica a reprojeção espacial diretamente na memória não gerenciada
                    if (transform != null) geom.Transform(transform);

                    wkbGeometryType tipo = geom.GetGeometryType();

                    // Processa Polígonos Simples
                    if (tipo == wkbGeometryType.wkbPolygon)
                    {
                        ExtrairAnelDeCoordenadas(geom, listaPoligonos);
                    }
                    // Processa Polígonos Complexos (Lotes com divisões internas ou ilhas)
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

    /// <summary>
    /// Função auxiliar de alto desempenho para fatiar os vértices do OGR e injetá-los num Array.
    /// </summary>
    private void ExtrairAnelDeCoordenadas(Geometry poligono, List<float[]> listaPoligonos)
    {
        // Pega no anel principal (borda externa do lote). Ignoramos furos internos (índices > 0) por enquanto para manter leveza.
        using Geometry anel = poligono.GetGeometryRef(0);
        if (anel != null)
        {
            int totalPontos = anel.GetPointCount();

            // Cria um array flat para otimizar alocação de memória (o dobro do tamanho porque guarda X e Y)
            float[] coordenadas = new float[totalPontos * 2];

            for (int i = 0; i < totalPontos; i++)
            {
                // Guarda Longitude (X) nos índices pares e Latitude (Y) nos ímpares
                coordenadas[i * 2] = (float)anel.GetX(i);
                coordenadas[(i * 2) + 1] = (float)anel.GetY(i);
            }

            listaPoligonos.Add(coordenadas);
        }
    }
    /// <summary>
    /// Lê a tabela de atributos de um vetor usando OGR e devolve as colunas e os dados.
    /// </summary>
    public (List<string> Colunas, List<Dictionary<string, object>> Dados) ObterAtributosVetor(string caminhoArquivo)
    {
        var colunas = new List<string>();
        var dados = new List<Dictionary<string, object>>();

        try
        {
            using OSGeo.OGR.DataSource ds = Ogr.Open(caminhoArquivo, 0); // 0 = Modo de Leitura
            if (ds == null) return (colunas, dados);

            using Layer layer = ds.GetLayerByIndex(0);
            using FeatureDefn defn = layer.GetLayerDefn();

            // 1. Extrai o nome das colunas
            colunas.Add("FID"); // Coluna interna do OGR (ID da Feição)
            for (int i = 0; i < defn.GetFieldCount(); i++)
            {
                colunas.Add(defn.GetFieldDefn(i).GetName());
            }

            // 2. Extrai os dados linha por linha
            Feature feat;
            layer.ResetReading();

            // Limitador de segurança: Evita travar o DOM do navegador se o SHP tiver 100.000 lotes
            int contador = 0;
            int limiteSeguranca = 2000;

            while ((feat = layer.GetNextFeature()) != null && contador < limiteSeguranca)
            {
                var linha = new Dictionary<string, object>();
                linha["FID"] = feat.GetFID().ToString();

                for (int i = 0; i < defn.GetFieldCount(); i++)
                {
                    string fieldName = defn.GetFieldDefn(i).GetName();
                    // Lemos como string para garantir compatibilidade imediata com a UI do Blazor
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
