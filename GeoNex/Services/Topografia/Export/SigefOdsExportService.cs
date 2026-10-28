namespace GeoNex.Services.Topografia.Export;

using System;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;
using GeoNex.Topografia.Domain;

public sealed class SigefOdsExportService
{
    public byte[] ExportarPlanilhaOds(ImovelCertificacaoSigef imovel)
    {
        if (imovel is null)
            throw new ArgumentNullException(nameof(imovel));

        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            // 1. mimetype (OBRIGATÓRIO: primeira entrada do ZIP, não compactada)
            var mimeEntry = archive.CreateEntry("mimetype", CompressionLevel.NoCompression);
            using (var writer = new StreamWriter(mimeEntry.Open(), Encoding.ASCII))
            {
                writer.Write("application/vnd.oasis.opendocument.spreadsheet");
            }

            // 2. META-INF/manifest.xml
            var manifestEntry = archive.CreateEntry("META-INF/manifest.xml", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(manifestEntry.Open(), Encoding.UTF8))
            {
                writer.Write(GerarManifestXml());
            }

            // 3. content.xml (Tabelas Identificação e Perímetro no formato INCRA 3ª Edição)
            var contentEntry = archive.CreateEntry("content.xml", CompressionLevel.Optimal);
            using (var writer = new StreamWriter(contentEntry.Open(), Encoding.UTF8))
            {
                writer.Write(GerarContentXml(imovel));
            }
        }

        return memoryStream.ToArray();
    }

    private static string GerarManifestXml()
    {
        return """
<?xml version="1.0" encoding="UTF-8"?>
<manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" manifest:version="1.2">
  <manifest:file-entry manifest:full-path="/" manifest:version="1.2" manifest:media-type="application/vnd.oasis.opendocument.spreadsheet"/>
  <manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/>
</manifest:manifest>
""";
    }

    private static string GerarContentXml(ImovelCertificacaoSigef imovel)
    {
        var sb = new StringBuilder();
        sb.Append("""
<?xml version="1.0" encoding="UTF-8"?>
<office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
                         xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0"
                         xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"
                         office:version="1.2">
  <office:body>
    <office:spreadsheet>
""");

        // ABA 1: Identificação
        sb.Append("""
      <table:table table:name="Identificação">
        <table:table-row>
          <table:table-cell office:value-type="string"><text:p>PARÂMETRO</text:p></table:table-cell>
          <table:table-cell office:value-type="string"><text:p>VALOR</text:p></table:table-cell>
        </table:table-row>
""");
        AdicionarLinhaIdentificacao(sb, "Código do Credenciado", imovel.CredenciadoCodigoIncra);
        AdicionarLinhaIdentificacao(sb, "Nome do Credenciado", imovel.CredenciadoNome);
        AdicionarLinhaIdentificacao(sb, "Número ART / TRT", imovel.NumeroArtTrt);
        AdicionarLinhaIdentificacao(sb, "Denominação do Imóvel", imovel.Denominacao);
        AdicionarLinhaIdentificacao(sb, "Município / UF", $"{imovel.Municipio} - {imovel.Uf}");
        AdicionarLinhaIdentificacao(sb, "Sistema de Referência", "SIRGAS 2000");
        AdicionarLinhaIdentificacao(sb, "Fuso UTM", imovel.FusoUtm.ToString());
        AdicionarLinhaIdentificacao(sb, "Área (ha)", imovel.AreaHectares.ToString("F4"));
        AdicionarLinhaIdentificacao(sb, "Perímetro (m)", imovel.PerimetroMetros.ToString("F2"));
        sb.Append("      </table:table>\n");

        // ABA 2: Perímetro (Estrutura rígida de submissão do robô SIGEF)
        sb.Append("""
      <table:table table:name="Perímetro">
        <table:table-row>
          <table:table-cell office:value-type="string"><text:p>Vértice</text:p></table:table-cell>
          <table:table-cell office:value-type="string"><text:p>Longitude</text:p></table:table-cell>
          <table:table-cell office:value-type="string"><text:p>Latitude</text:p></table:table-cell>
          <table:table-cell office:value-type="string"><text:p>Altitude (m)</text:p></table:table-cell>
          <table:table-cell office:value-type="string"><text:p>Sigma Longitude (m)</text:p></table:table-cell>
          <table:table-cell office:value-type="string"><text:p>Sigma Latitude (m)</text:p></table:table-cell>
          <table:table-cell office:value-type="string"><text:p>Sigma Altitude (m)</text:p></table:table-cell>
          <table:table-cell office:value-type="string"><text:p>Método</text:p></table:table-cell>
          <table:table-cell office:value-type="string"><text:p>Tipo Limite</text:p></table:table-cell>
          <table:table-cell office:value-type="string"><text:p>Descrição Limite</text:p></table:table-cell>
          <table:table-cell office:value-type="string"><text:p>Vértice Destino</text:p></table:table-cell>
          <table:table-cell office:value-type="string"><text:p>Confrontante</text:p></table:table-cell>
          <table:table-cell office:value-type="string"><text:p>Matrícula / CNS</text:p></table:table-cell>
        </table:table-row>
""");

        foreach (var seg in imovel.Segmentos)
        {
            sb.Append("        <table:table-row>\n");
            AdicionarCelula(sb, seg.De.Codigo);
            AdicionarCelula(sb, seg.De.LongitudeGms);
            AdicionarCelula(sb, seg.De.LatitudeGms);
            AdicionarCelulaNumero(sb, seg.De.AltitudeOrtometrica);
            AdicionarCelulaNumero(sb, seg.De.SigmaX);
            AdicionarCelulaNumero(sb, seg.De.SigmaY);
            AdicionarCelulaNumero(sb, seg.De.SigmaZ);
            AdicionarCelula(sb, seg.De.MetodoPosicionamento);
            AdicionarCelula(sb, seg.TipoLimite.ToString());
            AdicionarCelula(sb, seg.DescricaoLimite);
            AdicionarCelula(sb, seg.Para.Codigo);
            AdicionarCelula(sb, seg.NomeConfrontante);
            AdicionarCelula(sb, seg.MatriculaConfrontante);
            sb.Append("        </table:table-row>\n");
        }

        sb.Append("""
      </table:table>
    </office:spreadsheet>
  </office:body>
</office:document-content>
""");

        return sb.ToString();
    }

    private static void AdicionarLinhaIdentificacao(StringBuilder sb, string parametro, string valor)
    {
        sb.Append("        <table:table-row>\n");
        AdicionarCelula(sb, parametro);
        AdicionarCelula(sb, valor);
        sb.Append("        </table:table-row>\n");
    }

    private static void AdicionarCelula(StringBuilder sb, string texto)
    {
        string sanitizado = SecurityElement.Escape(texto ?? string.Empty);
        sb.Append($"          <table:table-cell office:value-type=\"string\"><text:p>{sanitizado}</text:p></table:table-cell>\n");
    }

    private static void AdicionarCelulaNumero(StringBuilder sb, double valor)
    {
        string valorFormatado = valor.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        string textoFormatado = valor.ToString("F2", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));
        sb.Append($"          <table:table-cell office:value-type=\"float\" office:value=\"{valorFormatado}\"><text:p>{textoFormatado}</text:p></table:table-cell>\n");
    }
}
