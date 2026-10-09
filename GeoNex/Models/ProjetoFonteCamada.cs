namespace GeoNex.Models;

/// <summary>
/// Describes how a project layer is reopened. Secrets are intentionally absent;
/// connection passwords live in the OS secure-storage provider.
/// </summary>
public sealed class ProjetoFonteCamada
{
    public string Tipo { get; set; } = "Arquivo";
    public string? Identificador { get; set; }

    public string? Servidor { get; set; }
    public int? Porta { get; set; }
    public string? BancoDados { get; set; }
    public string? Usuario { get; set; }
    public string? ModoSsl { get; set; }
    public string? Esquema { get; set; }
    public string? Tabela { get; set; }
    public string? ColunaGeometria { get; set; }
    public string? TipoGeometria { get; set; }
    public int? Srid { get; set; }
    public int? DimensaoCoordenada { get; set; }
}
