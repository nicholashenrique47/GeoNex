namespace GeoNex.Models;

public class Projeto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Nome { get; set; } = string.Empty;
    public string CaminhoArquivo { get; set; } = string.Empty;

    // O padrão para Guaratuba: SIRGAS 2000 / UTM zone 22S
    public string CRS { get; set; } = "EPSG:31982";
    public DateTime CriadoEm { get; set; } = DateTime.Now;

    // Relacionamento do Banco: 1 Projeto tem N Camadas
    public List<Camada> Camadas { get; set; } = new();
}