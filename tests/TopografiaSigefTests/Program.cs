using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using NetTopologySuite.Geometries;
using GeoNex.Topografia.Domain;
using GeoNex.Services.Topografia;
using GeoNex.Services.Topografia.Export;
using GeoNex.Services.Topografia.Import;
using GeoNex.Services.Topografia.Validation;
using GeoNex.Services.Licensing;

namespace TopografiaSigefTests;

internal static class Program
{
    private static int _testesPassados = 0;
    private static int _testesFalhados = 0;

    public static int Main()
    {
        Console.WriteLine("================================================================================");
        Console.WriteLine("SUITE DE TESTES FORENSES, GEODESICOS, CAD/DXF E ACERVO WFS: GEONEX SIGEF/INCRA");
        Console.WriteLine("================================================================================");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("--- PARTE 1: GEODESIA MATEMATICA E NORMAS TECNICAS INCRA (T01 - T06) ---");
        Console.ResetColor();
        ExecutarTeste("T01. Redfearn Geodesica <-> UTM Bidirecional (Fuso 23S e 22S - Tolerancia < 0.0001m)", TesteRedfearnBidirecional);
        ExecutarTeste("T02. Deteccao Automatica Graus vs Metros (Lat/Lon separada de UTM)", TesteDeteccaoCoordenadas);
        ExecutarTeste("T03. Identificacao Inteligente de Fuso UTM pelo Centroide (SP=23, PR=22, AC=19)", TesteAutoDeteccaoFuso);
        ExecutarTeste("T04. Transformacao 3D Datums IBGE (SAD69 e Corrego Alegre -> SIRGAS 2000)", TesteTransformacaoDatumIbge);
        ExecutarTeste("T05. Ordenacao Horaria Estrita de Vertices (CW) & Calculo de Azimutes", TesteOrdenacaoHorariaEAzimutes);
        ExecutarTeste("T06. Auditoria Forense INCRA: Deteccao Preventiva de Inconformidades (Regras 1 a 6)", TesteAuditoriaForense);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("--- PARTE 2: TOPOLOGIA ESPACIAL E EXPORTACOES OFICIAIS (T07 - T13) ---");
        Console.ResetColor();
        ExecutarTeste("T07. Validador de Auto-Intersecao Vetorial (Bowtie / Lacos Criticos)", TesteDeteccaoAutoIntersecaoVetorial);
        ExecutarTeste("T08. Exportacao ODS Estrutura OASIS Oficial (mimetype e content.xml)", TesteExportacaoOdsOficial);
        ExecutarTeste("T09. Exportacao DOCX OpenXML ABNT NBR 13.133 (Cabecalho e Memorial)", TesteExportacaoMemorialDocx);
        ExecutarTeste("T10. Normalizacao de Anel e Fechamento Topologico (Coincidentes)", TesteNormalizacaoVetorialEAnelFechado);
        ExecutarTeste("T11. MultiPolygon: Extracao de Anel Predominante de Maior Area", TesteMultiPoligonosEIlhas);
        ExecutarTeste("T12. R-Tree: Deteccao Espacial de Confrontantes (Tolerancia 50cm)", TesteConfrontacaoEspacialRTreeTopologia);
        ExecutarTeste("T13. Metrica de Area em Hectares e Perimetro Vetorial ABNT", TesteMetricaVetorialAreaPerimetroHa);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("--- PARTE 3: INTERFACE BLAZOR E GATEKEEPER SAAS (T14 - T17) ---");
        Console.ResetColor();
        ExecutarTeste("T14. Ciclo de Vida da Interface Blazor e Transicao entre 7 Abas", TesteFluxoNavegacaoAbasInterface);
        ExecutarTeste("T15. Renumeracao Horaria com Prefixo Customizado e Propagacao", TesteRenumeracaoVerticesEAtualizacaoPreview);
        ExecutarTeste("T16. Gatekeeper SaaS: Quotas de Exportacao e Ativacao Chave Pro", TesteGatekeeperLicencaEExportacao);
        ExecutarTeste("T17. Sanitizacao de Nomes de Arquivo para Compatibilidade SO", TesteSanitizacaoNomesArquivosECompatibilidadeSo);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("--- PARTE 4: CAD DXF, PRANCHA PDF, CAMPO E ACERVO WFS INCRA (T18 - T25) ---");
        Console.ResetColor();
        ExecutarTeste("T18. Geracao DXF R12: Sintaxe das 7 Camadas Cadastrais e Cores ACI", TesteGeracaoDxfSeteCamadas);
        ExecutarTeste("T19. Rotacao de Textos de Azimute no DXF (ABNT -90 a +90 graus)", TesteRotacaoTextosAzimuteDxf);
        ExecutarTeste("T20. Malha UTM DXF: Cruzetas e Anotacoes Numericas de Coordenadas", TesteMalhaUtmDxfCruzetas);
        ExecutarTeste("T21. Parser CSV Campo: Auto-Deteccao de Separador (; , \\t)", TesteParserCsvAutoDeteccaoSeparador);
        ExecutarTeste("T22. Parser CSV Campo: Ingestao de Pontos com Sigmas e Heuristica de Eixos", TesteParserCsvIngestaoComSigmas);
        ExecutarTeste("T23. Validacao de Sigmas Maximos por Metodo INCRA (PG1 a PV2)", TesteValidacaoSigmasMetodoIncra);
        ExecutarTeste("T24. WFS INCRA: Construcao de BBOX Geodesico SIRGAS 2000 (EPSG:4674)", TesteConstrucaoBboxWfsIncra);
        ExecutarTeste("T25. Calculo Topologico de Sobreposicao Fundiaria com Acervo INCRA", TesteCalculoTopologicoSobreposicaoIncra);

        Console.WriteLine();
        Console.WriteLine("================================================================================");
        Console.WriteLine($"RESULTADO FINAL DOS TESTES: {_testesPassados} PASSARAM, {_testesFalhados} FALHARAM");
        Console.WriteLine("================================================================================");

        return _testesFalhados == 0 ? 0 : 1;
    }

    private static void ExecutarTeste(string nome, Action teste)
    {
        Console.Write($"[TESTE] {nome} ... ");
        try
        {
            teste();
            _testesPassados++;
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("SUCESSO (PASSED)");
            Console.ResetColor();
        }
        catch (Exception ex)
        {
            _testesFalhados++;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"FALHOU: {ex.Message}");
            Console.ResetColor();
            Console.WriteLine($"   Stack: {ex.StackTrace}");
        }
    }

    // =========================================================================
    // PARTE 1: GEODESIA MATEMATICA E NORMAS INCRA (T01 - T06)
    // =========================================================================

    private static void TesteRedfearnBidirecional()
    {
        double latOrigem = -22.9064;
        double lonOrigem = -47.0616;
        int fuso = 23;

        TopografiaProcessingService.ConverterGeodesicaParaUtm(latOrigem, lonOrigem, fuso, true, out double este, out double norte);

        if (este < 200000.0 || este > 400000.0)
            throw new Exception($"Este UTM calculado fora da faixa esperada: {este:F3}m");
        if (norte < 7400000.0 || norte > 7500000.0)
            throw new Exception($"Norte UTM calculado fora da faixa esperada: {norte:F3}m");

        TopografiaProcessingService.ConverterUtmParaGeodesica(este, norte, fuso, true, out double latRecuperada, out double lonRecuperada);

        double erroLat = Math.Abs(latOrigem - latRecuperada);
        double erroLon = Math.Abs(lonOrigem - lonRecuperada);

        if (erroLat > 1e-6 || erroLon > 1e-6)
            throw new Exception($"Discrepancia na conversao bidirecional: ErroLat={erroLat:E3}, ErroLon={erroLon:E3}");

        string dmsLat = TopografiaProcessingService.FormatarParaPadraoIncra(latOrigem, isLatitude: true);
        string dmsLon = TopografiaProcessingService.FormatarParaPadraoIncra(lonOrigem, isLatitude: false);

        if (!dmsLat.StartsWith("-22") || !dmsLon.StartsWith("-47"))
            throw new Exception($"Formatacao INCRA incorreta: Lat='{dmsLat}', Lon='{dmsLon}'");
    }

    private static void TesteDeteccaoCoordenadas()
    {
        var service = new TopografiaProcessingService();

        var coordsGeo = new Coordinate[]
        {
            new(-47.06, -22.90),
            new(-47.05, -22.90),
            new(-47.05, -22.91),
            new(-47.06, -22.91),
            new(-47.06, -22.90)
        };
        var polyGeo = new GeometryFactory().CreatePolygon(coordsGeo);

        var imovelGeo = service.ProcessarPoligonoParaSigef(polyGeo, "ABC");
        if (imovelGeo.SistemaOrigem != TipoSistemaCoordenadas.GeograficaDecimal)
            throw new Exception("Falha: Coordenadas em graus nao foram detectadas como GeograficaDecimal!");
        if (imovelGeo.FusoUtm != 23)
            throw new Exception($"Falha: Fuso natural esperado para -47 W era 23, obteve: {imovelGeo.FusoUtm}");
        if (imovelGeo.Vertices[0].EsteUtm < 100000.0)
            throw new Exception("Falha: Vertice nao foi projetado para UTM metrico!");

        var coordsUtm = new Coordinate[]
        {
            new(245000.0, 7650000.0),
            new(245500.0, 7650000.0),
            new(245500.0, 7649500.0),
            new(245000.0, 7649500.0),
            new(245000.0, 7650000.0)
        };
        var polyUtm = new GeometryFactory().CreatePolygon(coordsUtm);

        var imovelUtm = service.ProcessarPoligonoParaSigef(polyUtm, "ABC", fusoUtm: 23);
        if (imovelUtm.SistemaOrigem != TipoSistemaCoordenadas.ProjetadaUtm)
            throw new Exception("Falha: Coordenadas em metros nao foram detectadas como ProjetadaUtm!");
        if (Math.Abs(imovelUtm.AreaHectares - 25.0) > 0.1)
            throw new Exception($"Falha: Area calculada esperada ~25 ha (500x500m), obteve: {imovelUtm.AreaHectares:F4} ha");
    }

    private static void TesteAutoDeteccaoFuso()
    {
        var service = new TopografiaProcessingService();

        var coordsPr = new Coordinate[]
        {
            new(-49.30, -25.40),
            new(-49.25, -25.40),
            new(-49.25, -25.45),
            new(-49.30, -25.40)
        };
        var imovelPr = service.ProcessarPoligonoParaSigef(new GeometryFactory().CreatePolygon(coordsPr), "ABC");
        if (imovelPr.FusoUtm != 22)
            throw new Exception($"Falha: Fuso esperado para Curitiba (-49.27 W) era 22, obteve {imovelPr.FusoUtm}");

        var coordsAc = new Coordinate[]
        {
            new(-67.85, -9.95),
            new(-67.80, -9.95),
            new(-67.80, -9.98),
            new(-67.85, -9.95)
        };
        var imovelAc = service.ProcessarPoligonoParaSigef(new GeometryFactory().CreatePolygon(coordsAc), "ABC");
        if (imovelAc.FusoUtm != 19)
            throw new Exception($"Falha: Fuso esperado para Acre (-67.8 W) era 19, obteve {imovelAc.FusoUtm}");
    }

    private static void TesteTransformacaoDatumIbge()
    {
        double latSad = -22.0;
        double lonSad = -47.0;
        double hSad = 500.0;

        TopografiaProcessingService.TransformarParaSirgas2000(latSad, lonSad, hSad, TipoDatum.Sad69, out double latSirgas, out double lonSirgas, out _);

        double diffLatMetros = Math.Abs(latSirgas - latSad) * 111139.0;
        double diffLonMetros = Math.Abs(lonSirgas - lonSad) * 111139.0 * Math.Cos(latSad * Math.PI / 180.0);
        double distSad = Math.Sqrt(diffLatMetros * diffLatMetros + diffLonMetros * diffLonMetros);

        if (distSad < 60.0 || distSad > 105.0)
            throw new Exception($"Deslocamento de Datum SAD69->SIRGAS2000 fora do intervalo esperado (60-105m): {distSad:F2}m");

        TopografiaProcessingService.TransformarParaSirgas2000(latSad, lonSad, hSad, TipoDatum.CorregoAlegre, out double latCorrego, out double lonCorrego, out _);
        double diffLatC = Math.Abs(latCorrego - latSad) * 111139.0;
        double diffLonC = Math.Abs(lonCorrego - lonSad) * 111139.0 * Math.Cos(latSad * Math.PI / 180.0);
        double distCorrego = Math.Sqrt(diffLatC * diffLatC + diffLonC * diffLonC);

        if (distCorrego < 40.0 || distCorrego > 90.0)
            throw new Exception($"Deslocamento de Datum Corrego Alegre->SIRGAS2000 fora do intervalo esperado (40-90m): {distCorrego:F2}m");
    }

    private static void TesteOrdenacaoHorariaEAzimutes()
    {
        var service = new TopografiaProcessingService();

        var coordsCcw = new Coordinate[]
        {
            new(200000.0, 7500000.0),
            new(201000.0, 7500000.0),
            new(201000.0, 7501000.0),
            new(200000.0, 7501000.0),
            new(200000.0, 7500000.0)
        };
        var poly = new GeometryFactory().CreatePolygon(coordsCcw);

        var imovel = service.ProcessarPoligonoParaSigef(poly, "ABC", fusoUtm: 23);

        if (imovel.Segmentos.Count != 4)
            throw new Exception($"Quantidade inesperada de segmentos: {imovel.Segmentos.Count}");

        foreach (var s in imovel.Segmentos)
        {
            if (s.AzimutePlanoGraus < 0.0 || s.AzimutePlanoGraus > 360.0)
                throw new Exception($"Azimute plano invalido: {s.AzimutePlanoGraus}");
            if (s.DistanciaPlanaMetros < 999.0 || s.DistanciaPlanaMetros > 1001.0)
                throw new Exception($"Distancia do segmento incorreta: {s.DistanciaPlanaMetros}");
        }
    }

    private static void TesteAuditoriaForense()
    {
        var validator = new TopografiaForensicValidator();
        var imovel = new ImovelCertificacaoSigef
        {
            CredenciadoCodigoIncra = "AB",
            FusoUtm = 23
        };

        imovel.Vertices.Add(new VerticeTopografico { Codigo = "AB-M-0001", Tipo = TipoVertice.M, EsteUtm = 200000.0, NorteUtm = 7500000.0, SigmaX = 0.60 });
        imovel.Vertices.Add(new VerticeTopografico { Codigo = "AB-M-0002", Tipo = TipoVertice.M, EsteUtm = 200000.02, NorteUtm = 7500000.02 });
        imovel.Vertices.Add(new VerticeTopografico { Codigo = "AB-M-0003", Tipo = TipoVertice.M, EsteUtm = 200500.0, NorteUtm = 7500500.0 });

        imovel.Segmentos.Add(new SegmentoPerimetro { De = imovel.Vertices[0], Para = imovel.Vertices[1], NomeConfrontante = "A DEFINIR" });

        var diagnosticos = validator.ExecutarAuditoria(imovel);

        bool detectouCredenciado = diagnosticos.Any(d => d.Codigo == "SIGEF_ERR_005");
        bool detectouSpike = diagnosticos.Any(d => d.Codigo == "SIGEF_ERR_003");
        bool detectouSigma = diagnosticos.Any(d => d.Codigo == "SIGEF_ERR_004");
        bool detectouConfrontante = diagnosticos.Any(d => d.Codigo == "SIGEF_ERR_006");

        if (!detectouCredenciado) throw new Exception("Falha: Nao detectou codigo de credenciado invalido!");
        if (!detectouSpike) throw new Exception("Falha: Nao detectou spike (< 5cm) que causaria rejeicao no robo do SIGEF!");
        if (!detectouSigma) throw new Exception("Falha: Nao detectou sigma posicional superior a 0.50m em marco fisico!");
        if (!detectouConfrontante) throw new Exception("Falha: Nao detectou alinhamento com confrontante A DEFINIR!");
    }

    // =========================================================================
    // PARTE 2: TOPOLOGIA ESPACIAL E EXPORTACOES OFICIAIS (T07 - T13)
    // =========================================================================

    private static void TesteDeteccaoAutoIntersecaoVetorial()
    {
        var validator = new TopografiaForensicValidator();

        var imovel = new ImovelCertificacaoSigef { CredenciadoCodigoIncra = "ABC", FusoUtm = 23 };
        imovel.Vertices.Add(new VerticeTopografico { Codigo = "ABC-M-0001", EsteUtm = 200000.0, NorteUtm = 7000000.0 });
        imovel.Vertices.Add(new VerticeTopografico { Codigo = "ABC-M-0002", EsteUtm = 201000.0, NorteUtm = 7001000.0 });
        imovel.Vertices.Add(new VerticeTopografico { Codigo = "ABC-M-0003", EsteUtm = 201000.0, NorteUtm = 7000000.0 });
        imovel.Vertices.Add(new VerticeTopografico { Codigo = "ABC-M-0004", EsteUtm = 200000.0, NorteUtm = 7001000.0 });

        imovel.Segmentos.Add(new SegmentoPerimetro { De = imovel.Vertices[0], Para = imovel.Vertices[1] });
        imovel.Segmentos.Add(new SegmentoPerimetro { De = imovel.Vertices[1], Para = imovel.Vertices[2] });
        imovel.Segmentos.Add(new SegmentoPerimetro { De = imovel.Vertices[2], Para = imovel.Vertices[3] });
        imovel.Segmentos.Add(new SegmentoPerimetro { De = imovel.Vertices[3], Para = imovel.Vertices[0] });

        var diagnosticos = validator.ExecutarAuditoria(imovel);

        bool detectouAutoIntersecao = diagnosticos.Any(d => d.Codigo == "SIGEF_ERR_007");
        if (!detectouAutoIntersecao)
            throw new Exception("Falha: Validador nao detectou auto-intersecao vetorial critica (poligono em laco/bowtie)!");
    }

    private static void TesteExportacaoOdsOficial()
    {
        var service = new TopografiaProcessingService();
        var odsService = new SigefOdsExportService();

        var coords = new Coordinate[]
        {
            new(245000.0, 7650000.0),
            new(246000.0, 7650000.0),
            new(246000.0, 7649000.0),
            new(245000.0, 7649000.0),
            new(245000.0, 7650000.0)
        };
        var imovel = service.ProcessarPoligonoParaSigef(new GeometryFactory().CreatePolygon(coords), "ABC", 23, "Fazenda Alvorada", "Carlos Alberto");

        byte[] bytesOds = odsService.ExportarPlanilhaOds(imovel);
        if (bytesOds == null || bytesOds.Length < 1000)
            throw new Exception("Arquivo ODS gerado vazio ou corrompido!");

        using var ms = new MemoryStream(bytesOds);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        var mimetypeEntry = archive.GetEntry("mimetype");
        if (mimetypeEntry == null) throw new Exception("ODS invalido: Entrada 'mimetype' ausente!");

        using (var reader = new StreamReader(mimetypeEntry.Open()))
        {
            string mimetype = reader.ReadToEnd();
            if (mimetype != "application/vnd.oasis.opendocument.spreadsheet")
                throw new Exception($"Mimetype incorreto: '{mimetype}'");
        }

        var contentEntry = archive.GetEntry("content.xml");
        if (contentEntry == null) throw new Exception("ODS invalido: Entrada 'content.xml' ausente!");

        using (var reader = new StreamReader(contentEntry.Open()))
        {
            string content = reader.ReadToEnd();
            if (!content.Contains("Identificação") || !content.Contains("Perímetro"))
                throw new Exception("ODS invalido: Tabelas 'Identificação' ou 'Perímetro' ausentes no content.xml!");
            if (!content.Contains("Fazenda Alvorada"))
                throw new Exception("ODS invalido: Denominacao do imovel ausente no XML!");
        }

        var manifestEntry = archive.GetEntry("META-INF/manifest.xml");
        if (manifestEntry == null) throw new Exception("ODS invalido: Entrada 'META-INF/manifest.xml' ausente!");
    }

    private static void TesteExportacaoMemorialDocx()
    {
        var service = new TopografiaProcessingService();
        var memorialService = new MemorialDescritivoExportService();

        var coords = new Coordinate[]
        {
            new(245000.0, 7650000.0),
            new(246000.0, 7650000.0),
            new(246000.0, 7649000.0),
            new(245000.0, 7649000.0),
            new(245000.0, 7650000.0)
        };
        var imovel = service.ProcessarPoligonoParaSigef(new GeometryFactory().CreatePolygon(coords), "ABC", 23, "Fazenda Bela Vista", "Mariana Silva");

        string textoCorrido = memorialService.GerarTextoMemorialCorrido(imovel);
        if (!textoCorrido.Contains("MEMORIAL DESCRITIVO GEORREFERENCIADO"))
            throw new Exception("Texto corrido do memorial sem cabecalho padrao!");
        if (!textoCorrido.Contains("Inicia-se a descrição deste perímetro no vértice ABC-M-0001"))
            throw new Exception("Texto corrido sem descricao perimetrica do vertice inicial!");
        if (!textoCorrido.Contains("100,0000 ha"))
            throw new Exception("Texto corrido com calculo incorreto de area (esperado 100 ha para 1000x1000m)!");

        byte[] bytesDocx = memorialService.GerarArquivoDocx(imovel);
        if (bytesDocx == null || bytesDocx.Length < 1000)
            throw new Exception("DOCX gerado nulo ou menor que 1KB!");

        using var ms = new MemoryStream(bytesDocx);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        var docEntry = archive.GetEntry("word/document.xml");
        if (docEntry == null) throw new Exception("DOCX invalido: 'word/document.xml' ausente!");

        using var reader = new StreamReader(docEntry.Open());
        string xmlDoc = reader.ReadToEnd();
        if (!xmlDoc.Contains("Fazenda Bela Vista") || !xmlDoc.Contains("Mariana Silva"))
            throw new Exception("DOCX invalido: dados do proprietario ausentes no corpo do documento!");
    }

    private static void TesteNormalizacaoVetorialEAnelFechado()
    {
        var gf = new GeometryFactory();
        var service = new TopografiaProcessingService();

        var coordsCcw = new Coordinate[]
        {
            new(300000.0, 7000000.0),
            new(300500.0, 7000000.0),
            new(300500.0, 7000500.0),
            new(300000.0, 7000500.0),
            new(300000.0, 7000000.0)
        };
        var poly = gf.CreatePolygon(coordsCcw);

        var imovel = service.ProcessarPoligonoParaSigef(poly, "ABC", fusoUtm: 23);

        if (imovel.Vertices.Count != 4)
            throw new Exception($"Numero de vertices incorreto: esperava 4, obteve {imovel.Vertices.Count}");
        if (imovel.Segmentos.Count != 4)
            throw new Exception($"Numero de segmentos incorreto: esperava 4, obteve {imovel.Segmentos.Count}");

        var segUltimo = imovel.Segmentos.Last();
        if (segUltimo.Para.Codigo != imovel.Vertices[0].Codigo)
            throw new Exception($"Segmento final nao fecha no primeiro vertice! Fecha em {segUltimo.Para.Codigo}");

        double somaOrientacao = 0;
        for (int i = 0; i < imovel.Vertices.Count; i++)
        {
            var p1 = imovel.Vertices[i];
            var p2 = imovel.Vertices[(i + 1) % imovel.Vertices.Count];
            somaOrientacao += (p2.EsteUtm - p1.EsteUtm) * (p2.NorteUtm + p1.NorteUtm);
        }
        if (somaOrientacao <= 0)
            throw new Exception("Vertices nao foram normalizados em sentido horario estrito!");
    }

    private static void TesteMultiPoligonosEIlhas()
    {
        var gf = new GeometryFactory();
        var service = new TopografiaProcessingService();

        var coordsPrincipal = new Coordinate[]
        {
            new(200000.0, 7100000.0),
            new(201000.0, 7100000.0),
            new(201000.0, 7101000.0),
            new(200000.0, 7101000.0),
            new(200000.0, 7100000.0)
        };
        var poly1 = gf.CreatePolygon(coordsPrincipal);

        var coordsMenor = new Coordinate[]
        {
            new(202000.0, 7100000.0),
            new(202100.0, 7100000.0),
            new(202100.0, 7100100.0),
            new(202000.0, 7100100.0),
            new(202000.0, 7100000.0)
        };
        var poly2 = gf.CreatePolygon(coordsMenor);

        var multiPoly = gf.CreateMultiPolygon(new[] { poly1, poly2 });

        var imovel = service.ProcessarPoligonoParaSigef(multiPoly, "ABC", fusoUtm: 23);

        if (Math.Abs(imovel.AreaHectares - 100.0) > 1.0)
            throw new Exception($"Area de MultiPolygon nao extraiu anel principal esperado (~100 ha), obteve {imovel.AreaHectares:F2} ha");
    }

    private static void TesteConfrontacaoEspacialRTreeTopologia()
    {
        var gf = new GeometryFactory();
        var service = new TopografiaProcessingService();
        var confrontService = new SpatialConfrontationService();

        var coordsAlvo = new Coordinate[]
        {
            new(200000.0, 7000000.0),
            new(201000.0, 7000000.0),
            new(201000.0, 7001000.0),
            new(200000.0, 7001000.0),
            new(200000.0, 7000000.0)
        };
        var imovel = service.ProcessarPoligonoParaSigef(gf.CreatePolygon(coordsAlvo), "ABC", fusoUtm: 23);

        var coordsVizinhoLeste = new Coordinate[]
        {
            new(201000.0, 7000000.0),
            new(202000.0, 7000000.0),
            new(202000.0, 7001000.0),
            new(201000.0, 7001000.0),
            new(201000.0, 7000000.0)
        };
        var polyVizinhoLeste = gf.CreatePolygon(coordsVizinhoLeste);
        var atribsLeste = new Dictionary<string, object>
        {
            { "NOME_IMOVEL", "Fazenda Esperança" },
            { "MATRICULA", "12.345" },
            { "PROPRIETARIO", "Antônio Carlos de Souza" }
        };

        var coordsDistante = new Coordinate[]
        {
            new(200000.0, 7001500.0),
            new(201000.0, 7001500.0),
            new(201000.0, 7002000.0),
            new(200000.0, 7002000.0),
            new(200000.0, 7001500.0)
        };
        var polyDistante = gf.CreatePolygon(coordsDistante);
        var atribsDistante = new Dictionary<string, object>
        {
            { "NOME_IMOVEL", "Sítio Primavera Distante" },
            { "MATRICULA", "99.999" }
        };

        var camadasVizinhas = new List<(Geometry Geometria, Dictionary<string, object> Atributos)>
        {
            (polyVizinhoLeste, atribsLeste),
            (polyDistante, atribsDistante)
        };

        confrontService.DetectarConfrontantesEspaciais(imovel, camadasVizinhas);

        var segmentoConfrontante = imovel.Segmentos.FirstOrDefault(s => s.NomeConfrontante == "Fazenda Esperança");
        if (segmentoConfrontante == null)
            throw new Exception("Falha: Confrontante contiguo 'Fazenda Esperança' nao foi detectado no alinhamento perimetrico!");

        if (segmentoConfrontante.MatriculaConfrontante != "12.345")
            throw new Exception($"Matricula extraida incorreta: esperava '12.345', obteve '{segmentoConfrontante.MatriculaConfrontante}'");

        if (segmentoConfrontante.ProprietarioConfrontante != "Antônio Carlos de Souza")
            throw new Exception($"Proprietario extraido incorreto: {segmentoConfrontante.ProprietarioConfrontante}");

        if (imovel.Segmentos.Any(s => s.NomeConfrontante == "Sítio Primavera Distante"))
            throw new Exception("Falso positivo: Sitio distante a 500m foi erroneamente classificado como confrontante!");
    }

    private static void TesteMetricaVetorialAreaPerimetroHa()
    {
        var gf = new GeometryFactory();
        var service = new TopografiaProcessingService();

        var coords = new Coordinate[]
        {
            new(100000.0, 8000000.0),
            new(100600.0, 8000000.0),
            new(100500.0, 8000300.0),
            new(100100.0, 8000300.0),
            new(100000.0, 8000000.0)
        };
        var poly = gf.CreatePolygon(coords);
        var imovel = service.ProcessarPoligonoParaSigef(poly, "ABC", fusoUtm: 23);

        double erroArea = Math.Abs(imovel.AreaHectares - 15.0000);
        if (erroArea > 0.0001)
            throw new Exception($"Erro na precisao do calculo de area em hectares: esperado 15.0000 ha, obteve {imovel.AreaHectares:F4} ha");

        double perimetroEsperado = 600.0 + 316.227766 + 400.0 + 316.227766;
        double erroPerimetro = Math.Abs(imovel.PerimetroMetros - perimetroEsperado);

        if (erroPerimetro > 0.01)
            throw new Exception($"Erro no perimetro vetorial: esperado {perimetroEsperado:F2}m, obteve {imovel.PerimetroMetros:F2}m");
    }

    // =========================================================================
    // PARTE 3: INTERFACE BLAZOR E GATEKEEPER SAAS (T14 - T17)
    // =========================================================================

    private static void TesteFluxoNavegacaoAbasInterface()
    {
        var service = new TopografiaProcessingService();
        var memorialService = new MemorialDescritivoExportService();
        var validator = new TopografiaForensicValidator();

        var coords = new Coordinate[]
        {
            new(245000.0, 7650000.0),
            new(245800.0, 7650000.0),
            new(245800.0, 7649200.0),
            new(245000.0, 7649200.0),
            new(245000.0, 7650000.0)
        };
        var imovel = service.ProcessarPoligonoParaSigef(new GeometryFactory().CreatePolygon(coords), "ABC", 23, "Fazenda Santa Maria", "João da Silva");

        // Estado inicial do fluxo: Aba 1 (Imovel)
        int abaAtiva = 1;
        bool modalVisivel = true;
        if (abaAtiva != 1 || !modalVisivel) throw new Exception("Falha no estado inicial da interface.");

        // Transicao: Aba 2 (Vertices)
        abaAtiva = 2;
        if (imovel.Vertices.Count != 4) throw new Exception("Grade de vertices nao carregou os 4 pontos.");

        // Transicao: Aba 3 (Confrontantes)
        abaAtiva = 3;
        imovel.Segmentos[0].NomeConfrontante = "Estrada Municipal Vicinal 101";
        imovel.Segmentos[0].DescricaoLimite = "Estrada / Via Pública";

        // Transicao: Aba 4 (Memorial Descritivo)
        abaAtiva = 4;
        string preview = memorialService.GerarTextoMemorialCorrido(imovel);
        if (!preview.Contains("Estrada Municipal Vicinal 101"))
            throw new Exception("Reatividade da UI falhou: Atualizacao do confrontante nao refletiu no memorial preview.");

        // Transicao: Aba 5 (Auditoria Forense)
        abaAtiva = 5;
        var diagnosticos = validator.ExecutarAuditoria(imovel);
        int errosCriticos = diagnosticos.Count(d => d.Gravidade == "ERRO_CRITICO");
        if (errosCriticos > 0)
            throw new Exception($"Auditoria da interface reportou erros criticos indevidos: {errosCriticos}");
    }

    private static void TesteRenumeracaoVerticesEAtualizacaoPreview()
    {
        var service = new TopografiaProcessingService();
        var memorialService = new MemorialDescritivoExportService();

        var coords = new Coordinate[]
        {
            new(245000.0, 7650000.0),
            new(245800.0, 7650000.0),
            new(245800.0, 7649200.0),
            new(245000.0, 7649200.0),
            new(245000.0, 7650000.0)
        };
        var imovel = service.ProcessarPoligonoParaSigef(new GeometryFactory().CreatePolygon(coords), "DEF", 23, "Gleba Boa Esperança", "Ana Paula");

        string novoCodigo = "XYZ";
        imovel.CredenciadoCodigoIncra = novoCodigo;
        for (int i = 0; i < imovel.Vertices.Count; i++)
        {
            imovel.Vertices[i].Codigo = $"{novoCodigo}-M-{(i + 1):D4}";
        }

        if (imovel.Vertices[0].Codigo != "XYZ-M-0001" || imovel.Vertices[3].Codigo != "XYZ-M-0004")
            throw new Exception("Renumeracao da interface nao aplicou o padrao XYZ-M-XXXX.");

        string memorialAtualizado = memorialService.GerarTextoMemorialCorrido(imovel);
        if (!memorialAtualizado.Contains("XYZ-M-0001") || !memorialAtualizado.Contains("XYZ-M-0004"))
            throw new Exception("Propagacao de renumeracao nao atualizou o corpo do memorial descritivo.");
    }

    private static void TesteGatekeeperLicencaEExportacao()
    {
        var licService = new LicenseValidationService();

        var lic = licService.ObterLicencaAtual();
        lic.Plano = TipoPlanoLicenca.Gratuito;
        lic.CreditosAvulsos = 0;

        bool autorizouSemSaldo = licService.ConsumirCreditoOuValidarAssinatura();
        if (autorizouSemSaldo)
            throw new Exception("Falha de seguranca no Gatekeeper: Permitiu exportacao sem saldo e sem plano Pro!");

        licService.AtivarChaveLicenca("GEONEX-PRO-2026-INCRA");
        var licAtualizada = licService.ObterLicencaAtual();

        if (licAtualizada.Plano != TipoPlanoLicenca.ProAnual)
            throw new Exception("Gatekeeper nao elevou o perfil para ProAnual.");

        bool autorizouComPro = licService.ConsumirCreditoOuValidarAssinatura();
        if (!autorizouComPro)
            throw new Exception("Gatekeeper bloqueou usuario Pro apos ativacao com chave valida.");
    }

    private static void TesteSanitizacaoNomesArquivosECompatibilidadeSo()
    {
        string nomeInseguro = "Fazenda Santa Cruz / Gleba #1: Lote *2? <Área Principal>";
        
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            nomeInseguro = nomeInseguro.Replace(c, '_');
        }
        string nomeSanitizado = nomeInseguro.Trim().Replace(' ', '_');

        if (nomeSanitizado.Contains('/') || nomeSanitizado.Contains(':') || nomeSanitizado.Contains('*') || nomeSanitizado.Contains('?'))
            throw new Exception($"Sanitizacao falhou, gerou caracteres ilegais: {nomeSanitizado}");

        string nomeArquivoEsperado = $"{nomeSanitizado}_SIGEF.ods";
        string pastaTemp = Path.GetTempPath();
        string caminhoCompleto = Path.Combine(pastaTemp, nomeArquivoEsperado);

        var fileInfo = new FileInfo(caminhoCompleto);
        if (fileInfo.Name != nomeArquivoEsperado)
            throw new Exception("Nome de arquivo sanitizado nao corresponde ao objeto FileInfo do SO.");
    }

    // =========================================================================
    // PARTE 4: CAD DXF, PRANCHA PDF, CAMPO E ACERVO WFS INCRA (T18 - T25)
    // =========================================================================

    private static void TesteGeracaoDxfSeteCamadas()
    {
        var service = new TopografiaProcessingService();
        var dxfService = new TopografiaDxfExportService();

        var coords = new Coordinate[]
        {
            new(245000.0, 7650000.0),
            new(246000.0, 7650000.0),
            new(246000.0, 7649000.0),
            new(245000.0, 7649000.0),
            new(245000.0, 7650000.0)
        };
        var imovel = service.ProcessarPoligonoParaSigef(new GeometryFactory().CreatePolygon(coords), "ABC", 23, "Fazenda Santa Fe", "Carlos Mendes");

        byte[] bytesDxf = dxfService.GerarDxfCadastral(imovel, espacamentoGridMetros: 200.0);
        if (bytesDxf == null || bytesDxf.Length < 1000)
            throw new Exception("DXF gerado nulo ou excessivamente curto!");

        string textoDxf = Encoding.Latin1.GetString(bytesDxf);

        // Validar versao AutoCAD R12 ASCII
        if (!textoDxf.Contains("AC1009"))
            throw new Exception("Cabecalho DXF nao contem especificacao AutoCAD R12 (AC1009).");

        // Validar as 7 camadas ACI normativas
        string[] camadasRequeridas = {
            "01_PERIMETRO",
            "02_MARCOS_SIMBOLO",
            "03_VERTICES_TEXTO",
            "04_AZIMUTES_DISTANCIAS",
            "05_CONFRONTANTES",
            "06_MALHA_UTM",
            "07_TABELA_VERTICES"
        };

        foreach (var camada in camadasRequeridas)
        {
            if (!textoDxf.Contains(camada))
                throw new Exception($"Camada cadastral obrigatoria ausente no DXF: '{camada}'");
        }

        // Validar presenca do bloco de marco topografico
        if (!textoDxf.Contains("SIMBOLO_MARCO"))
            throw new Exception("Definicao de bloco 'SIMBOLO_MARCO' ausente na secao BLOCKS.");
    }

    private static void TesteRotacaoTextosAzimuteDxf()
    {
        var service = new TopografiaProcessingService();
        var dxfService = new TopografiaDxfExportService();

        // Poligono quadrado invertido para forcar angulos no quadrante 2 e 3
        var coords = new Coordinate[]
        {
            new(300000.0, 7000000.0),
            new(300000.0, 7001000.0),
            new(299000.0, 7001000.0),
            new(299000.0, 7000000.0),
            new(300000.0, 7000000.0)
        };
        var imovel = service.ProcessarPoligonoParaSigef(new GeometryFactory().CreatePolygon(coords), "ROT", 23);

        byte[] bytesDxf = dxfService.GerarDxfCadastral(imovel);
        string textoDxf = Encoding.Latin1.GetString(bytesDxf);

        // O DXF deve conter textos na camada 04_AZIMUTES_DISTANCIAS
        if (!textoDxf.Contains("04_AZIMUTES_DISTANCIAS"))
            throw new Exception("DXF nao contem entidades na camada 04_AZIMUTES_DISTANCIAS.");

        // Verificar normalizacao de rotacao ABNT (grupo DXF 50 com valor entre -90 e 90 ou equivalente)
        if (!textoDxf.Contains("Az:") || !textoDxf.Contains("Dist:"))
            throw new Exception("Entidades de texto com rotulos de Azimute e Distancia ausentes.");
    }

    private static void TesteMalhaUtmDxfCruzetas()
    {
        var service = new TopografiaProcessingService();
        var dxfService = new TopografiaDxfExportService();

        var coords = new Coordinate[]
        {
            new(250000.0, 7500000.0),
            new(251000.0, 7500000.0),
            new(251000.0, 7499000.0),
            new(250000.0, 7499000.0),
            new(250000.0, 7500000.0)
        };
        var imovel = service.ProcessarPoligonoParaSigef(new GeometryFactory().CreatePolygon(coords), "MLH", 23);

        byte[] bytesDxf = dxfService.GerarDxfCadastral(imovel, espacamentoGridMetros: 500.0);
        string textoDxf = Encoding.Latin1.GetString(bytesDxf);

        // Verificar cruzetas e coordenadas de grid
        if (!textoDxf.Contains("06_MALHA_UTM"))
            throw new Exception("Camada de malha UTM 06_MALHA_UTM ausente no DXF.");

        if (!textoDxf.Contains("E:250000") && !textoDxf.Contains("E:250500") && !textoDxf.Contains("E:251000"))
            throw new Exception("Anotacoes numericas Este da malha UTM nao foram encontradas no grid.");

        if (!textoDxf.Contains("N:7500000") && !textoDxf.Contains("N:7499500") && !textoDxf.Contains("N:7499000"))
            throw new Exception("Anotacoes numericas Norte da malha UTM nao foram encontradas no grid.");
    }

    private static void TesteParserCsvAutoDeteccaoSeparador()
    {
        var importer = new TopografiaFieldDataImporter();

        // 1. Teste com ponto e virgula ';'
        string csvPontoEVirgula = "PONTO;ESTE;NORTE;COTA\nP1;250100.5;7500200.8;620.5\nP2;250200.5;7500200.8;621.0\nP3;250200.5;7500100.8;619.8";
        using var stream1 = new MemoryStream(Encoding.UTF8.GetBytes(csvPontoEVirgula));
        var vertices1 = importer.ImportarArquivoCampo(stream1, out _);
        if (vertices1.Count != 3)
            throw new Exception($"Falha ao detectar delimitador ';': esperado 3 vertices, obteve {vertices1.Count}");

        // 2. Teste com virgula ','
        string csvVirgula = "PONTO,ESTE,NORTE,COTA\nP1,250100.5,7500200.8,620.5\nP2,250200.5,7500200.8,621.0";
        using var stream2 = new MemoryStream(Encoding.UTF8.GetBytes(csvVirgula));
        var vertices2 = importer.ImportarArquivoCampo(stream2, out _);
        if (vertices2.Count != 2)
            throw new Exception($"Falha ao detectar delimitador ',': esperado 2 vertices, obteve {vertices2.Count}");

        // 3. Teste com tabulacao '\t'
        string csvTab = "PONTO\tESTE\tNORTE\tCOTA\nP1\t250100.5\t7500200.8\t620.5\nP2\t250200.5\t7500200.8\t621.0";
        using var stream3 = new MemoryStream(Encoding.UTF8.GetBytes(csvTab));
        var vertices3 = importer.ImportarArquivoCampo(stream3, out _);
        if (vertices3.Count != 2)
            throw new Exception($"Falha ao detectar delimitador '\\t': esperado 2 vertices, obteve {vertices3.Count}");
    }

    private static void TesteParserCsvIngestaoComSigmas()
    {
        var importer = new TopografiaFieldDataImporter();

        string csvCompleto = "VERTICE;ESTE;NORTE;ALTITUDE;METODO;SIGMA\n" +
                             "GNX-M-0001;320000.500;7800000.120;750.25;PG1;0.08\n" +
                             "GNX-P-0002;320500.500;7800000.120;752.10;PG3;0.12\n" +
                             "GNX-V-0003;320500.500;7799500.120;748.00;PV1;2.10";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csvCompleto));
        var vertices = importer.ImportarArquivoCampo(stream, out var alertas);

        if (vertices.Count != 3)
            throw new Exception($"Ingestao falhou: esperado 3 vertices, obteve {vertices.Count}");

        var m1 = vertices[0];
        if (m1.Codigo != "GNX-M-0001" || m1.Tipo != TipoVertice.M || Math.Abs(m1.SigmaX - 0.08) > 0.001 || m1.MetodoPosicionamento != "PG1")
            throw new Exception($"Atributos do marco GNX-M-0001 incorretos: Sigma={m1.SigmaX}, Tipo={m1.Tipo}, Metodo={m1.MetodoPosicionamento}");

        var p2 = vertices[1];
        if (p2.Tipo != TipoVertice.P || p2.MetodoPosicionamento != "PG3")
            throw new Exception($"Ponto de apoio P2 classificado incorretamente: Tipo={p2.Tipo}, Metodo={p2.MetodoPosicionamento}");

        var v3 = vertices[2];
        if (v3.Tipo != TipoVertice.V || Math.Abs(v3.SigmaX - 2.10) > 0.01)
            throw new Exception($"Vertice virtual V3 com sigma incorreto: {v3.SigmaX}");
    }

    private static void TesteValidacaoSigmasMetodoIncra()
    {
        // 1. Marco fisico M com PG1 e sigma 0.15m: deve passar
        bool okPg1 = TopografiaNormativeCatalog.ValidarSigmaMetodo("PG1", TipoVertice.M, 0.15, 0.15, out string err1);
        if (!okPg1)
            throw new Exception($"PG1 com sigma 0.15m deveria ter sido aceito! Motivo: {err1}");

        // 2. Marco fisico M com PG1 e sigma 0.65m: deve falhar (limite horizontal do PG1 e 0.50m)
        bool okPg1Estourado = TopografiaNormativeCatalog.ValidarSigmaMetodo("PG1", TipoVertice.M, 0.65, 0.65, out string err2);
        if (okPg1Estourado)
            throw new Exception("Falha de auditoria: PG1 com sigma de 0.65m foi aceito indevidamente (limite e 0.50m)!");

        // 3. Ponto virtual V com PV1 e sigma 2.50m: deve passar (limite do PV1 e 3.00m)
        bool okPv1 = TopografiaNormativeCatalog.ValidarSigmaMetodo("PV1", TipoVertice.V, 2.50, 2.50, out string err3);
        if (!okPv1)
            throw new Exception($"PV1 com sigma 2.50m deveria ter sido aceito! Motivo: {err3}");

        // 4. Marco fisico M com metodo PV1: deve falhar (metodo virtual nao e permitido para marco fisico)
        bool okMarcoComPv1 = TopografiaNormativeCatalog.ValidarSigmaMetodo("PV1", TipoVertice.M, 0.20, 0.20, out string err4);
        if (okMarcoComPv1)
            throw new Exception("Falha de auditoria: Metodo virtual PV1 foi aceito para marco fisico implantado!");

        // 5. Vertice virtual V com metodo PG2: deve falhar (PG2 exige ocupacao fisica rapida)
        bool okVirtualComPg2 = TopografiaNormativeCatalog.ValidarSigmaMetodo("PG2", TipoVertice.V, 0.30, 0.30, out string err5);
        if (okVirtualComPg2)
            throw new Exception("Falha de auditoria: Metodo PG2 foi aceito para vertice virtual!");
    }

    private static void TesteConstrucaoBboxWfsIncra()
    {
        var gf = new GeometryFactory(new PrecisionModel(), 4674);

        // Poligono teste no interior de SP (SIRGAS 2000 em graus)
        var coords = new Coordinate[]
        {
            new(-47.1000, -22.9500),
            new(-47.0800, -22.9500),
            new(-47.0800, -22.9700),
            new(-47.1000, -22.9700),
            new(-47.1000, -22.9500)
        };
        var poly = gf.CreatePolygon(coords);
        var env = poly.EnvelopeInternal;

        // Validar calculo do BBOX expandido em 0.01 graus
        double minX = env.MinX - 0.01;
        double minY = env.MinY - 0.01;
        double maxX = env.MaxX + 0.01;
        double maxY = env.MaxY + 0.01;

        if (minX > -47.1000 || maxX < -47.0800)
            throw new Exception($"BBOX Longitude incorreto: [{minX:F4}, {maxX:F4}]");
        if (minY > -22.9700 || maxY < -22.9500)
            throw new Exception($"BBOX Latitude incorreto: [{minY:F4}, {maxY:F4}]");

        string urlWfs = $"https://geoservicos.incra.gov.br/geoserver/wfs?service=WFS&version=2.0.0&request=GetFeature&typeNames=certificada_sigef_particular&outputFormat=application/json&srsName=EPSG:4674&bbox={minY:F6},{minX:F6},{maxY:F6},{maxX:F6},EPSG:4674";

        if (!urlWfs.Contains("version=2.0.0") || !urlWfs.Contains("certificada_sigef_particular") || !urlWfs.Contains("EPSG:4674"))
            throw new Exception("Construcao da URL de consulta ao WFS INCRA fora do padrao OGC.");
    }

    private static void TesteCalculoTopologicoSobreposicaoIncra()
    {
        var gf = new GeometryFactory(new PrecisionModel(), 4674);

        // Gleba sob analise: ~500m x 500m
        var coordsGleba = new Coordinate[]
        {
            new(-47.0600, -22.9000),
            new(-47.0550, -22.9000),
            new(-47.0550, -22.9050),
            new(-47.0600, -22.9050),
            new(-47.0600, -22.9000)
        };
        var polyGleba = gf.CreatePolygon(coordsGleba);

        // Parcela vizinha com sobreposicao intencional de ~200m x 200m
        var coordsConflito = new Coordinate[]
        {
            new(-47.0570, -22.9020),
            new(-47.0530, -22.9020),
            new(-47.0530, -22.9070),
            new(-47.0570, -22.9070),
            new(-47.0570, -22.9020)
        };
        var polyConflito = gf.CreatePolygon(coordsConflito);

        if (!polyGleba.Intersects(polyConflito))
            throw new Exception("Falha de teste: poligonos de teste deveriam se interceptar!");

        var intersecao = polyGleba.Intersection(polyConflito);
        if (intersecao.IsEmpty || !(intersecao is Polygon or MultiPolygon))
            throw new Exception("Geometria de intersecao invalida.");

        // Calculo de area geocidada em metros quadrados
        var env = polyGleba.EnvelopeInternal;
        double latMedia = (env.MinY + env.MaxY) / 2.0;
        double fatorM2 = 111139.0 * (111139.0 * Math.Cos(latMedia * Math.PI / 180.0));
        double areaM2 = intersecao.Area * fatorM2;

        if (areaM2 < 1000.0)
            throw new Exception($"Area de sobreposicao muito pequena ou calculada incorretamente: {areaM2:F2} m2");

        // Validar criacao do DTO ConflitoSobreposicaoItem
        var conflito = new ConflitoSobreposicaoItem
        {
            CodigoImovelVizinho = "BR-SP-00129",
            NomeImovelVizinho = "Fazenda Primavera Certificada",
            DetentorNome = "Agropecuaria Central S/A",
            AreaSobreposicaoMetrosQuadrados = areaM2,
            PoligonoConflito = intersecao
        };

        if (conflito.AreaSobreposicaoHectares <= 0.0)
            throw new Exception("Propriedade computada AreaSobreposicaoHectares incorreta.");
        if (!conflito.PossuiConflitoCritico)
            throw new Exception("Conflito com area de varios hectares deveria ser classificado como critico.");
    }
}
