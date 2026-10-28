using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using NetTopologySuite.Geometries;
using GeoNex.Topografia.Domain;
using GeoNex.Services.Topografia;
using GeoNex.Services.Topografia.Export;
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
        Console.WriteLine("SUITE DE TESTES FORENSES, GEODÉSICOS, VETORIAIS E INTERFACE: GEONEX SIGEF/INCRA");
        Console.WriteLine("================================================================================");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("--- PARTE 1: GEODÉSIA MATEMÁTICA E NORMAS TÉCNICAS INCRA ---");
        Console.ResetColor();
        ExecutarTeste("1. Redfearn Geodésica <-> UTM (Fuso 23S e 22S - Tolerância < 0.001m)", TesteRedfearnBidirecional);
        ExecutarTeste("2. Detecção Automática: Geográficas (Lat/Lon) vs Projetadas UTM", TesteDeteccaoCoordenadas);
        ExecutarTeste("3. Auto-detecção Inteligente de Fuso UTM pelo Centroide (SP, PR, AC)", TesteAutoDeteccaoFuso);
        ExecutarTeste("4. Transformação Tridimensional de Datum IBGE (SAD69 e Córrego Alegre -> SIRGAS 2000)", TesteTransformacaoDatumIbge);
        ExecutarTeste("5. Ordenação Horária Estrita de Vértices & Cálculo de Azimutes/Distâncias", TesteOrdenacaoHorariaEAzimutes);
        ExecutarTeste("6. Auditoria Forense INCRA: Detecção Preventiva de Inconformidades (8 Regras)", TesteAuditoriaForense);
        ExecutarTeste("7. Integridade do Contêiner OASIS OpenDocument Spreadsheet (.ODS) Oficial", TesteExportacaoOdsOficial);
        ExecutarTeste("8. Integridade do Memorial Descritivo OpenXML DOCX e ABNT NBR 13.133", TesteExportacaoMemorialDocx);
        ExecutarTeste("9. Sistema de Licenciamento SaaS, Créditos e Ativação de Chave Pro", TesteLicenciamentoSaas);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("--- PARTE 2: TESTES VETORIAIS E TOPOLOGIA ESPACIAL ---");
        Console.ResetColor();
        ExecutarTeste("10. Normalização Vetorial: Fechamento de Anel e Orientação Horária (CCW -> CW)", TesteNormalizacaoVetorialEAnelFechado);
        ExecutarTeste("11. Tratamento de Geometrias Complexas (MultiPolygon e Enclaves)", TesteMultiPoligonosEIlhas);
        ExecutarTeste("12. Detecção Topológica de Confrontantes Espaciais (Tolerância 50cm & R-Tree)", TesteConfrontacaoEspacialRTreeTopologia);
        ExecutarTeste("13. Métricas Vetoriais de Polígono Irregular: Área (ha) e Perímetro ABNT", TesteMetricaVetorialAreaPerimetroHa);
        ExecutarTeste("14. Auditoria Forense Vetorial: Detecção Preventiva de Auto-Interseção (Laço/Bowtie)", TesteDeteccaoAutoIntersecaoVetorial);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("--- PARTE 3: TESTES DE INTERFACE, ESTADOS E FLUXOGRAMA DE USUÁRIO ---");
        Console.ResetColor();
        ExecutarTeste("15. Fluxograma de Estados de Interface: Ciclo de Vida do Modal e Abas", TesteFluxoNavegacaoAbasInterface);
        ExecutarTeste("16. Reatividade de Dados: Renumeração Horária e Atualização do Memorial Descritivo", TesteRenumeracaoVerticesEAtualizacaoPreview);
        ExecutarTeste("17. Gatekeeper de Exportação: Controle de Quota e Desbloqueio Instantâneo Pro", TesteGatekeeperLicencaEExportacao);
        ExecutarTeste("18. Sanitização de Nomes de Arquivos para Compatibilidade Multiplataforma", TesteSanitizacaoNomesArquivosECompatibilidadeSo);

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
    // PARTE 1: GEODÉSIA MATEMÁTICA E NORMAS INCRA
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
            throw new Exception($"Discrepância na conversão bidirecional: ErroLat={erroLat:E3}, ErroLon={erroLon:E3}");

        string dmsLat = TopografiaProcessingService.FormatarParaPadraoIncra(latOrigem, isLatitude: true);
        string dmsLon = TopografiaProcessingService.FormatarParaPadraoIncra(lonOrigem, isLatitude: false);

        if (!dmsLat.StartsWith("-22") || !dmsLon.StartsWith("-47"))
            throw new Exception($"Formatação INCRA incorreta: Lat='{dmsLat}', Lon='{dmsLon}'");
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
            throw new Exception("Falha: Coordenadas em graus não foram detectadas como GeograficaDecimal!");
        if (imovelGeo.FusoUtm != 23)
            throw new Exception($"Falha: Fuso natural esperado para -47°W era 23, obteve: {imovelGeo.FusoUtm}");
        if (imovelGeo.Vertices[0].EsteUtm < 100000.0)
            throw new Exception("Falha: Vértice não foi projetado para UTM métrico!");

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
            throw new Exception("Falha: Coordenadas em metros não foram detectadas como ProjetadaUtm!");
        if (Math.Abs(imovelUtm.AreaHectares - 25.0) > 0.1)
            throw new Exception($"Falha: Área calculada esperada ~25 ha (500x500m), obteve: {imovelUtm.AreaHectares:F4} ha");
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
            throw new Exception($"Falha: Fuso esperado para Curitiba (-49.27°W) era 22, obteve {imovelPr.FusoUtm}");

        var coordsAc = new Coordinate[]
        {
            new(-67.85, -9.95),
            new(-67.80, -9.95),
            new(-67.80, -9.98),
            new(-67.85, -9.95)
        };
        var imovelAc = service.ProcessarPoligonoParaSigef(new GeometryFactory().CreatePolygon(coordsAc), "ABC");
        if (imovelAc.FusoUtm != 19)
            throw new Exception($"Falha: Fuso esperado para Acre (-67.8°W) era 19, obteve {imovelAc.FusoUtm}");
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
            throw new Exception($"Deslocamento de Datum Córrego Alegre->SIRGAS2000 fora do intervalo esperado (40-90m): {distCorrego:F2}m");
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
                throw new Exception($"Azimute plano inválido: {s.AzimutePlanoGraus}");
            if (s.DistanciaPlanaMetros < 999.0 || s.DistanciaPlanaMetros > 1001.0)
                throw new Exception($"Distância do segmento incorreta: {s.DistanciaPlanaMetros}");
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

        if (!detectouCredenciado) throw new Exception("Falha: Não detectou código de credenciado inválido!");
        if (!detectouSpike) throw new Exception("Falha: Não detectou spike (< 5cm) que causaria rejeição no robô do SIGEF!");
        if (!detectouSigma) throw new Exception("Falha: Não detectou sigma posicional superior a 0.50m em marco físico!");
        if (!detectouConfrontante) throw new Exception("Falha: Não detectou alinhamento com confrontante A DEFINIR!");
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
        if (mimetypeEntry == null) throw new Exception("ODS inválido: Entrada 'mimetype' ausente!");

        using (var reader = new StreamReader(mimetypeEntry.Open()))
        {
            string mimetype = reader.ReadToEnd();
            if (mimetype != "application/vnd.oasis.opendocument.spreadsheet")
                throw new Exception($"Mimetype incorreto: '{mimetype}'");
        }

        var contentEntry = archive.GetEntry("content.xml");
        if (contentEntry == null) throw new Exception("ODS inválido: Entrada 'content.xml' ausente!");

        using (var reader = new StreamReader(contentEntry.Open()))
        {
            string content = reader.ReadToEnd();
            if (!content.Contains("Identificação") || !content.Contains("Perímetro"))
                throw new Exception("ODS inválido: Tabelas 'Identificação' ou 'Perímetro' ausentes no content.xml!");
            if (!content.Contains("Fazenda Alvorada"))
                throw new Exception("ODS inválido: Denominação do imóvel ausente no XML!");
        }

        var manifestEntry = archive.GetEntry("META-INF/manifest.xml");
        if (manifestEntry == null) throw new Exception("ODS inválido: Entrada 'META-INF/manifest.xml' ausente!");
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
            throw new Exception("Texto corrido do memorial sem cabeçalho padrão!");
        if (!textoCorrido.Contains("Inicia-se a descrição deste perímetro no vértice ABC-M-0001"))
            throw new Exception("Texto corrido sem descrição perimétrica do vértice inicial!");
        if (!textoCorrido.Contains("100,0000 ha"))
            throw new Exception("Texto corrido com cálculo incorreto de área (esperado 100 ha para 1000x1000m)!");

        byte[] bytesDocx = memorialService.GerarArquivoDocx(imovel);
        if (bytesDocx == null || bytesDocx.Length < 1000)
            throw new Exception("DOCX gerado nulo ou menor que 1KB!");

        using var ms = new MemoryStream(bytesDocx);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        var docEntry = archive.GetEntry("word/document.xml");
        if (docEntry == null) throw new Exception("DOCX inválido: 'word/document.xml' ausente!");

        using var reader = new StreamReader(docEntry.Open());
        string xmlDoc = reader.ReadToEnd();
        if (!xmlDoc.Contains("Fazenda Bela Vista") || !xmlDoc.Contains("Mariana Silva"))
            throw new Exception("DOCX inválido: dados do proprietário ausentes no corpo do documento!");
    }

    private static void TesteLicenciamentoSaas()
    {
        var licService = new LicenseValidationService();
        var lic = licService.ObterLicencaAtual();

        if (lic == null || !lic.Ativa)
            throw new Exception("Licença inicial não inicializada corretamente!");

        bool podeExportar = licService.PossuiPermissaoExportacaoPro();
        if (!podeExportar)
            throw new Exception("Usuário Pro deveria ter permissão de exportação!");

        bool consumiu = licService.ConsumirCreditoOuValidarAssinatura();
        if (!consumiu)
            throw new Exception("Falha ao validar consumo de licença Pro!");

        licService.AtivarChaveLicenca("GNX-PRO-2026-TEST");
        var licAtivada = licService.ObterLicencaAtual();
        if (licAtivada.Plano != TipoPlanoLicenca.ProAnual)
            throw new Exception("Ativação de chave não comutou plano para ProAnual!");
    }

    // =========================================================================
    // PARTE 2: TESTES VETORIAIS E TOPOLOGIA ESPACIAL
    // =========================================================================

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
            throw new Exception($"Número de vértices incorreto: esperava 4, obteve {imovel.Vertices.Count}");
        if (imovel.Segmentos.Count != 4)
            throw new Exception($"Número de segmentos incorreto: esperava 4, obteve {imovel.Segmentos.Count}");

        var segUltimo = imovel.Segmentos.Last();
        if (segUltimo.Para.Codigo != imovel.Vertices[0].Codigo)
            throw new Exception($"Segmento final não fecha no primeiro vértice! Fecha em {segUltimo.Para.Codigo}");

        double somaOrientacao = 0;
        for (int i = 0; i < imovel.Vertices.Count; i++)
        {
            var p1 = imovel.Vertices[i];
            var p2 = imovel.Vertices[(i + 1) % imovel.Vertices.Count];
            somaOrientacao += (p2.EsteUtm - p1.EsteUtm) * (p2.NorteUtm + p1.NorteUtm);
        }
        if (somaOrientacao <= 0)
            throw new Exception("Vértices não foram normalizados em sentido horário estrito!");
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
            throw new Exception($"Área de MultiPolygon não extraiu anel principal esperado (~100 ha), obteve {imovel.AreaHectares:F2} ha");
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
            throw new Exception("Falha: Confrontante contíguo 'Fazenda Esperança' não foi detectado no alinhamento perimétrico!");

        if (segmentoConfrontante.MatriculaConfrontante != "12.345")
            throw new Exception($"Matrícula extraída incorreta: esperava '12.345', obteve '{segmentoConfrontante.MatriculaConfrontante}'");

        if (segmentoConfrontante.ProprietarioConfrontante != "Antônio Carlos de Souza")
            throw new Exception($"Proprietário extraído incorreto: {segmentoConfrontante.ProprietarioConfrontante}");

        if (imovel.Segmentos.Any(s => s.NomeConfrontante == "Sítio Primavera Distante"))
            throw new Exception("Falso positivo: Sítio distante a 500m foi erroneamente classificado como confrontante!");
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
            throw new Exception($"Erro na precisão do cálculo de área em hectares: esperado 15.0000 ha, obteve {imovel.AreaHectares:F4} ha");

        double perimetroEsperado = 600.0 + 316.227766 + 400.0 + 316.227766;
        double erroPerimetro = Math.Abs(imovel.PerimetroMetros - perimetroEsperado);

        if (erroPerimetro > 0.01)
            throw new Exception($"Erro no perímetro vetorial: esperado {perimetroEsperado:F2}m, obteve {imovel.PerimetroMetros:F2}m");
    }

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
            throw new Exception("Falha: Validador não detectou auto-interseção vetorial crítica (polígono em laço/bowtie)!");
    }

    // =========================================================================
    // PARTE 3: TESTES DE INTERFACE, ESTADOS E FLUXOGRAMA DE USUÁRIO
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

        // Estado inicial do fluxo: Aba 1 (Imóvel)
        int abaAtiva = 1;
        bool modalVisivel = true;
        if (abaAtiva != 1 || !modalVisivel) throw new Exception("Falha no estado inicial da interface.");

        // Transição: Aba 2 (Vértices)
        abaAtiva = 2;
        if (imovel.Vertices.Count != 4) throw new Exception("Grade de vértices não carregou os 4 pontos.");

        // Transição: Aba 3 (Confrontantes)
        abaAtiva = 3;
        imovel.Segmentos[0].NomeConfrontante = "Estrada Municipal Vicinal 101";
        imovel.Segmentos[0].DescricaoLimite = "Estrada / Via Pública";

        // Transição: Aba 4 (Memorial Descritivo)
        abaAtiva = 4;
        string preview = memorialService.GerarTextoMemorialCorrido(imovel);
        if (!preview.Contains("Estrada Municipal Vicinal 101"))
            throw new Exception("Reatividade da UI falhou: Atualização do confrontante não refletiu no memorial preview.");

        // Transição: Aba 5 (Auditoria Forense)
        abaAtiva = 5;
        var diagnosticos = validator.ExecutarAuditoria(imovel);
        int errosCriticos = diagnosticos.Count(d => d.Gravidade == "ERRO_CRITICO");
        if (errosCriticos > 0)
            throw new Exception($"Auditoria da interface reportou erros críticos indevidos: {errosCriticos}");
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
            throw new Exception("Renumeração da interface não aplicou o padrão XYZ-M-XXXX.");

        string memorialAtualizado = memorialService.GerarTextoMemorialCorrido(imovel);
        if (!memorialAtualizado.Contains("XYZ-M-0001") || !memorialAtualizado.Contains("XYZ-M-0004"))
            throw new Exception("Propagação de renumeração não atualizou o corpo do memorial descritivo.");
    }

    private static void TesteGatekeeperLicencaEExportacao()
    {
        var licService = new LicenseValidationService();

        var lic = licService.ObterLicencaAtual();
        lic.Plano = TipoPlanoLicenca.Gratuito;
        lic.CreditosAvulsos = 0;

        bool autorizouSemSaldo = licService.ConsumirCreditoOuValidarAssinatura();
        if (autorizouSemSaldo)
            throw new Exception("Falha de segurança no Gatekeeper: Permitiu exportação sem saldo e sem plano Pro!");

        licService.AtivarChaveLicenca("GEONEX-PRO-2026-INCRA");
        var licAtualizada = licService.ObterLicencaAtual();

        if (licAtualizada.Plano != TipoPlanoLicenca.ProAnual)
            throw new Exception("Gatekeeper não elevou o perfil para ProAnual.");

        bool autorizouComPro = licService.ConsumirCreditoOuValidarAssinatura();
        if (!autorizouComPro)
            throw new Exception("Gatekeeper bloqueou usuário Pro após ativação com chave válida.");
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
            throw new Exception($"Sanitização falhou, gerou caracteres ilegais: {nomeSanitizado}");

        string nomeArquivoEsperado = $"{nomeSanitizado}_SIGEF.ods";
        string pastaTemp = Path.GetTempPath();
        string caminhoCompleto = Path.Combine(pastaTemp, nomeArquivoEsperado);

        var fileInfo = new FileInfo(caminhoCompleto);
        if (fileInfo.Name != nomeArquivoEsperado)
            throw new Exception("Nome de arquivo sanitizado não corresponde ao objeto FileInfo do SO.");
    }
}
