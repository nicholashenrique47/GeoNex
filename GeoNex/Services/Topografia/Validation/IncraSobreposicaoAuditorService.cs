namespace GeoNex.Services.Topografia.Validation;

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using GeoNex.Topografia.Domain;

public sealed class IncraSobreposicaoAuditorService
{
    private readonly HttpClient _httpClient;
    private readonly GeometryFactory _geometryFactory = new(new PrecisionModel(), 4674);

    public IncraSobreposicaoAuditorService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<ConflitoSobreposicaoItem>> VerificarSobreposicaoIncraAsync(
        Geometry poligonoGlebaSirgas2000, 
        IProgress<string>? progresso = null)
    {
        var conflitos = new List<ConflitoSobreposicaoItem>();
        if (poligonoGlebaSirgas2000 is null || poligonoGlebaSirgas2000.IsEmpty) return conflitos;

        progresso?.Report("Consultando acervo fundiário georreferenciado do INCRA...");

        var env = poligonoGlebaSirgas2000.EnvelopeInternal;
        // Expande o envelope em 0.01 graus (~1.1 km) para busca regional
        double minX = env.MinX - 0.01;
        double minY = env.MinY - 0.01;
        double maxX = env.MaxX + 0.01;
        double maxY = env.MaxY + 0.01;

        string urlWfs = $"https://geoservicos.incra.gov.br/geoserver/wfs?service=WFS&version=2.0.0&request=GetFeature&typeNames=certificada_sigef_particular&outputFormat=application/json&srsName=EPSG:4674&bbox={minY:F6},{minX:F6},{maxY:F6},{maxX:F6},EPSG:4674";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, urlWfs);
            request.Headers.Add("User-Agent", "GeoNex-Desktop-GIS/2026.1 (Windows NT 10.0; Win64; x64)");

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                progresso?.Report($"Aviso: Servidor WFS INCRA retornou status {response.StatusCode}. Verificação offline preservada.");
                return conflitos;
            }

            var jsonString = await response.Content.ReadAsStringAsync();
            var reader = new GeoJsonReader();
            var featureCollection = reader.Read<FeatureCollection>(jsonString);

            if (featureCollection == null || featureCollection.Count == 0)
            {
                progresso?.Report("Nenhuma parcela certificada vizinha encontrada na região.");
                return conflitos;
            }

            progresso?.Report($"Cruzando topologicamente contra {featureCollection.Count} parcelas cadastradas...");

            foreach (var feat in featureCollection)
            {
                if (feat.Geometry == null || feat.Geometry.IsEmpty) continue;

                if (poligonoGlebaSirgas2000.Intersects(feat.Geometry))
                {
                    var intersecao = poligonoGlebaSirgas2000.Intersection(feat.Geometry);

                    if (intersecao is Polygon or MultiPolygon && intersecao.Area > 1e-8)
                    {
                        double latMedia = (env.MinY + env.MaxY) / 2.0;
                        double fatorM2 = 111139.0 * (111139.0 * Math.Cos(latMedia * Math.PI / 180.0));
                        double areaM2 = intersecao.Area * fatorM2;

                        if (areaM2 >= 1.0)
                        {
                            string codImovel = feat.Attributes.Exists("codigo_imovel") ? feat.Attributes["codigo_imovel"]?.ToString() ?? "N/D" : "N/D";
                            string nomeImovel = feat.Attributes.Exists("nome_imovel") ? feat.Attributes["nome_imovel"]?.ToString() ?? "Gleba Certificada" : "Gleba Certificada";
                            string detentor = feat.Attributes.Exists("detentor_nome") ? feat.Attributes["detentor_nome"]?.ToString() ?? "Não Declarado" : "Não Declarado";

                            conflitos.Add(new ConflitoSobreposicaoItem
                            {
                                CodigoImovelVizinho = codImovel,
                                NomeImovelVizinho = nomeImovel,
                                DetentorNome = detentor,
                                AreaSobreposicaoMetrosQuadrados = areaM2,
                                PoligonoConflito = intersecao
                            });
                        }
                    }
                }
            }

            progresso?.Report($"Auditoria concluída: {conflitos.Count} conflito(s) de sobreposição identificado(s).");
        }
        catch (Exception ex)
        {
            progresso?.Report($"Erro na conexão com acervo INCRA: {ex.Message}");
        }

        return conflitos;
    }
}
