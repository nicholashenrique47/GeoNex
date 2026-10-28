namespace GeoNex.Services.Topografia.Export;

using System;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;
using GeoNex.Topografia.Domain;

public sealed class MemorialDescritivoExportService
{
    /// <summary>
    /// Gera o texto corrido tradicional do Memorial Descritivo para cartório de registro de imóveis.
    /// </summary>
    public string GerarTextoMemorialCorrido(ImovelCertificacaoSigef imovel)
    {
        if (imovel is null) throw new ArgumentNullException(nameof(imovel));

        var sb = new StringBuilder();
        sb.AppendLine("MEMORIAL DESCRITIVO GEORREFERENCIADO");
        sb.AppendLine("================================================================================");
        sb.AppendLine($"IMÓVEL: {imovel.Denominacao}");
        sb.AppendLine($"PROPRIETÁRIO: {imovel.ProprietarioNome} - CPF/CNPJ: {imovel.ProprietarioCpfCnpj}");
        sb.AppendLine($"MUNICÍPIO: {imovel.Municipio} - {imovel.Uf} | COMARCA: {imovel.Comarca}");
        sb.AppendLine($"ÁREA: {imovel.AreaHectares:N4} ha ({imovel.AreaMetrosQuadrados:N2} m²) | PERÍMETRO: {imovel.PerimetroMetros:N2} m");
        sb.AppendLine($"SISTEMA GEODÉSICO DE REFERÊNCIA: SIRGAS 2000 | FUSO: {imovel.FusoUtm}S (Meridiano Central: {imovel.FusoUtm * 6 - 183}° WGr)");
        sb.AppendLine("================================================================================");
        sb.AppendLine();
        sb.AppendLine("DESCRIÇÃO DO PERÍMETRO:");
        sb.AppendLine();

        if (imovel.Segmentos.Count > 0)
        {
            var seg0 = imovel.Segmentos[0];
            sb.Append($"Inicia-se a descrição deste perímetro no vértice {seg0.De.Codigo}, de coordenadas Planas no Sistema UTM (SIRGAS 2000) Este (E) = {seg0.De.EsteUtm:N2}m e Norte (N) = {seg0.De.NorteUtm:N2}m, cravado junto à divisa com {seg0.NomeConfrontante}; ");

            string confrontanteAtual = seg0.NomeConfrontante;

            for (int i = 0; i < imovel.Segmentos.Count; i++)
            {
                var s = imovel.Segmentos[i];
                if (s.NomeConfrontante != confrontanteAtual)
                {
                    sb.AppendLine();
                    sb.Append($"Deste ponto, passa a confrontar com {s.NomeConfrontante}{(string.IsNullOrWhiteSpace(s.MatriculaConfrontante) ? "" : " (Matrícula " + s.MatriculaConfrontante + ")")}, com os seguintes azimutes e distâncias: ");
                    confrontanteAtual = s.NomeConfrontante;
                }

                sb.Append($"até o vértice {s.Para.Codigo}, com azimute plano de {s.AzimuteFormatadoGms} e distância de {s.DistanciaPlanaMetros:N2}m; ");
            }

            sb.AppendLine();
            sb.AppendLine($"até o vértice inicial {seg0.De.Codigo}, ponto de partida desta descrição, encerrando uma área de {imovel.AreaHectares:N4} hectares ({imovel.AreaMetrosQuadrados:N2} m²) e um perímetro de {imovel.PerimetroMetros:N2} metros.");
        }

        sb.AppendLine();
        sb.AppendLine("QUADRO DE COORDENADAS DOS VÉRTICES:");
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine("Vértice        Este (m)         Norte (m)      Altitude(m)   Latitude           Longitude");
        sb.AppendLine("--------------------------------------------------------------------------------");
        foreach (var v in imovel.Vertices)
        {
            sb.AppendLine($"{v.Codigo,-12}  {v.EsteUtm,14:N2}  {v.NorteUtm,14:N2}  {v.AltitudeOrtometrica,10:N2}   {v.LatitudeGms,-17}  {v.LongitudeGms,-17}");
        }
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine();
        sb.AppendLine($"Local e Data: {imovel.Municipio} - {imovel.Uf}, {DateTime.Now:dd 'de' MMMM 'de' yyyy}.");
        sb.AppendLine();
        sb.AppendLine("____________________________________________________");
        sb.AppendLine($"{imovel.CredenciadoNome}");
        sb.AppendLine($"Responsável Técnico - {imovel.CredenciadoCreaCau}");
        sb.AppendLine($"Credenciamento INCRA: {imovel.CredenciadoCodigoIncra} | ART/TRT: {imovel.NumeroArtTrt}");

        return sb.ToString();
    }

    /// <summary>
    /// Gera o arquivo .docx (OpenXML nativo) pronto para impressão e assinatura cartorial.
    /// </summary>
    public byte[] GerarArquivoDocx(ImovelCertificacaoSigef imovel)
    {
        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // 1. [Content_Types].xml
            var contentTypes = archive.CreateEntry("[Content_Types].xml", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(contentTypes.Open(), Encoding.UTF8))
            {
                writer.Write("""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
</Types>
""");
            }

            // 2. _rels/.rels
            var pkgRels = archive.CreateEntry("_rels/.rels", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(pkgRels.Open(), Encoding.UTF8))
            {
                writer.Write("""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
</Relationships>
""");
            }

            // 3. word/document.xml
            var docEntry = archive.CreateEntry("word/document.xml", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(docEntry.Open(), Encoding.UTF8))
            {
                writer.Write(GerarWordDocumentXml(imovel));
            }
        }

        return memoryStream.ToArray();
    }

    private string GerarWordDocumentXml(ImovelCertificacaoSigef imovel)
    {
        var sb = new StringBuilder();
        sb.Append("""
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:body>
""");

        AdicionarParagrafoWord(sb, "MEMORIAL DESCRITIVO GEORREFERENCIADO", true, 28);
        AdicionarParagrafoWord(sb, $"IMÓVEL: {imovel.Denominacao}", true, 22);
        AdicionarParagrafoWord(sb, $"PROPRIETÁRIO: {imovel.ProprietarioNome} | CPF/CNPJ: {imovel.ProprietarioCpfCnpj}", false, 20);
        AdicionarParagrafoWord(sb, $"MUNICÍPIO: {imovel.Municipio}-{imovel.Uf} | COMARCA: {imovel.Comarca}", false, 20);
        AdicionarParagrafoWord(sb, $"ÁREA: {imovel.AreaHectares:N4} ha ({imovel.AreaMetrosQuadrados:N2} m²) | PERÍMETRO: {imovel.PerimetroMetros:N2} m", true, 20);
        AdicionarParagrafoWord(sb, $"SISTEMA DE REFERÊNCIA: SIRGAS 2000 | FUSO UTM: {imovel.FusoUtm}S", false, 20);
        AdicionarParagrafoWord(sb, "", false, 20);

        AdicionarParagrafoWord(sb, "DESCRIÇÃO DO PERÍMETRO:", true, 22);
        string textoCorrido = GerarTextoMemorialCorrido(imovel);
        foreach (var linha in textoCorrido.Split('\n'))
        {
            string trim = linha.Trim();
            if (!string.IsNullOrWhiteSpace(trim))
            {
                AdicionarParagrafoWord(sb, trim, false, 20);
            }
        }

        sb.Append("""
  </w:body>
</w:document>
""");
        return sb.ToString();
    }

    private static void AdicionarParagrafoWord(StringBuilder sb, string texto, bool negrito, int tamanhoMeioPonto)
    {
        string sanitizado = SecurityElement.Escape(texto ?? string.Empty);
        sb.Append("<w:p><w:r>");
        if (negrito) sb.Append("<w:rPr><w:b/></w:rPr>");
        sb.Append($"<w:t>{sanitizado}</w:t></w:r></w:p>\n");
    }
}
