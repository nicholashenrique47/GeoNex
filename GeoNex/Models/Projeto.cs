namespace GeoNex.Models;

public class Projeto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Nome { get; set; } = string.Empty;
    public string CaminhoArquivo { get; set; } = string.Empty;

    // O padrão para Guaratuba: SIRGAS 2000 / UTM zone 22S
    public string CRS { get; set; } = "EPSG:31982";
    public DateTime CriadoEm { get; set; } = DateTime.Now;
    public string? CamadaBase { get; set; }
    public double OffsetMundoX { get; set; }
    public double OffsetMundoY { get; set; }
    public bool OffsetMundoDefinido { get; set; }
    public double CameraPanX { get; set; }
    public double CameraPanY { get; set; }
    public double CameraZoom { get; set; } = 1.0;
    public string? LayoutJson { get; set; }

    // Relacionamento do Banco: 1 Projeto tem N Camadas
    public List<Camada> Camadas { get; set; } = new();
}
