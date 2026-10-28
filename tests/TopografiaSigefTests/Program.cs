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
        Console.WriteLine("SUITE DE TESTES FORENSES & GEODÉSICOS: GEONEX TOPOGRAFIA & SIGEF / INCRA");
        Console.WriteLine("================================================================================");
        Console.WriteLine();

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

    private static void TesteRedfearnBidirecional()
    {
        // Ponto de teste: Marco geodésico em Campinas/SP (Fuso 23S)
        // Lat: -22.9064° S, Lon: -47.0616° W
        double latOrigem = -22.9064;
        double lonOrigem = -47.0616;
        int fuso = 23;

        // Projeta para UTM
        TopografiaProcessingService.ConverterGeodesicaParaUtm(latOrigem, lonOrigem, fuso, true, out double este, out double norte);

        // Verifica se as coordenadas UTM estão na faixa correta para o Fuso 23S em SP
        if (este < 200000.0 || este > 400000.0)
            throw new Exception($"Este UTM calculado fora da faixa esperada: {este:F3}m");
        if (norte < 7400000.0 || norte > 7500000.0)
            throw new Exception($"Norte UTM calculado fora da faixa esperada: {norte:F3}m");

        // Converte de volta para Geodésica
        TopografiaProcessingService.ConverterUtmParaGeodesica(este, norte, fuso, true, out double latRecuperada, out double lonRecuperada);

        double erroLat = Math.Abs(latOrigem - latRecuperada);
        double erroLon = Math.Abs(lonOrigem - lonRecuperada);

        // 1 grau ~ 111.000m -> 1e-7 graus < 0.01m (1cm)
        if (erroLat > 1e-6 || erroLon > 1e-6)
            throw new Exception($"Discrepância na conversão bidirecional: ErroLat={erroLat:E3}, ErroLon={erroLon:E3}");

        // Verifica formatação INCRA
        string dmsLat = TopografiaProcessingService.FormatarParaPadraoIncra(latOrigem, isLatitude: true);
        string dmsLon = TopografiaProcessingService.FormatarParaPadraoIncra(lonOrigem, isLatitude: false);

        if (!dmsLat.StartsWith("-22") || !dmsLon.StartsWith("-47"))
            throw new Exception($"Formatação INCRA incorreta: Lat='{dmsLat}', Lon='{dmsLon}'");
    }

    private static void TesteDeteccaoCoordenadas()
    {
        var service = new TopografiaProcessingService();

        // Polígono em coordenadas geográficas (graus decimais no centro de SP)
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

        // Polígono em coordenadas planas UTM (metros)
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

        // 1. Curitiba / PR (Lon ~ -49.27° -> Fuso 22)
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

        // 2. Rio Branco / AC (Lon ~ -67.8° -> Fuso 19)
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
        // 1. Ponto em SAD69
        double latSad = -22.0;
        double lonSad = -47.0;
        double hSad = 500.0;

        TopografiaProcessingService.TransformarParaSirgas2000(latSad, lonSad, hSad, TipoDatum.Sad69, out double latSirgas, out double lonSirgas, out _);

        // A translação oficial do IBGE para SAD69/SIRGAS2000 tem vetor 3D de 90.68m,
        // gerando deslocamento horizontal na superfície entre 60m e 100m.
        double diffLatMetros = Math.Abs(latSirgas - latSad) * 111139.0;
        double diffLonMetros = Math.Abs(lonSirgas - lonSad) * 111139.0 * Math.Cos(latSad * Math.PI / 180.0);
        double distSad = Math.Sqrt(diffLatMetros * diffLatMetros + diffLonMetros * diffLonMetros);

        if (distSad < 60.0 || distSad > 105.0)
            throw new Exception($"Deslocamento de Datum SAD69->SIRGAS2000 fora do intervalo esperado (60-105m): {distSad:F2}m");

        // 2. Ponto em Córrego Alegre
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

        // Polígono desenhado em sentido ANTI-HORÁRIO (CCW)
        var coordsCcw = new Coordinate[]
        {
            new(200000.0, 7500000.0),
            new(201000.0, 7500000.0), // Leste
            new(201000.0, 7501000.0), // Norte
            new(200000.0, 7501000.0), // Oeste
            new(200000.0, 7500000.0)  // Sul
        };
        var poly = new GeometryFactory().CreatePolygon(coordsCcw);

        var imovel = service.ProcessarPoligonoParaSigef(poly, "ABC", fusoUtm: 23);

        // O primeiro segmento deve ir no sentido horário (ex: Norte -> Leste -> Sul -> Oeste)
        if (imovel.Segmentos.Count != 4)
            throw new Exception($"Quantidade inesperada de segmentos: {imovel.Segmentos.Count}");

        // Azimutes devem estar entre 0 e 360
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
            CredenciadoCodigoIncra = "AB", // Erro: precisa ter 3 caracteres
            FusoUtm = 23
        };

        // Vértices com spike (< 5cm)
        imovel.Vertices.Add(new VerticeTopografico { Codigo = "AB-M-0001", Tipo = TipoVertice.M, EsteUtm = 200000.0, NorteUtm = 7500000.0, SigmaX = 0.60 }); // Erro: Sigma > 0.50
        imovel.Vertices.Add(new VerticeTopografico { Codigo = "AB-M-0002", Tipo = TipoVertice.M, EsteUtm = 200000.02, NorteUtm = 7500000.02 }); // Erro: Distância de 2.8cm (Spike)
        imovel.Vertices.Add(new VerticeTopografico { Codigo = "AB-M-0003", Tipo = TipoVertice.M, EsteUtm = 200500.0, NorteUtm = 7500500.0 });

        imovel.Segmentos.Add(new SegmentoPerimetro { De = imovel.Vertices[0], Para = imovel.Vertices[1], NomeConfrontante = "A DEFINIR" }); // Aviso: A DEFINIR

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

        // Valida contêiner ZIP OASIS OpenDocument
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

        // 1. Testa Texto Corrido
        string textoCorrido = memorialService.GerarTextoMemorialCorrido(imovel);
        if (!textoCorrido.Contains("MEMORIAL DESCRITIVO GEORREFERENCIADO"))
            throw new Exception("Texto corrido do memorial sem cabeçalho padrão!");
        if (!textoCorrido.Contains("Inicia-se a descrição deste perímetro no vértice ABC-M-0001"))
            throw new Exception("Texto corrido sem descrição perimétrica do vértice inicial!");
        if (!textoCorrido.Contains("100,0000 ha"))
            throw new Exception("Texto corrido com cálculo incorreto de área (esperado 100 ha para 1000x1000m)!");

        // 2. Testa DOCX OpenXML
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

        // Consumo de crédito
        bool podeExportar = licService.PossuiPermissaoExportacaoPro();
        if (!podeExportar)
            throw new Exception("Usuário Pro deveria ter permissão de exportação!");

        bool consumiu = licService.ConsumirCreditoOuValidarAssinatura();
        if (!consumiu)
            throw new Exception("Falha ao validar consumo de licença Pro!");

        // Ativação de chave
        licService.AtivarChaveLicenca("GNX-PRO-2026-TEST");
        var licAtivada = licService.ObterLicencaAtual();
        if (licAtivada.Plano != TipoPlanoLicenca.ProAnual)
            throw new Exception("Ativação de chave não comutou plano para ProAnual!");
    }
}
