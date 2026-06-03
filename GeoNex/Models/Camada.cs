namespace GeoNex.Models;

public class Camada
{
    public int Id { get; set; }
    public string Nome { get; set; } = string.Empty;

    // Define se é "Raster" (Imagem) ou "Vetor" (Shapefile/Polígono)
    public string Tipo { get; set; } = "Vetor";
    public double Opacidade { get; set; } = 1.0;
    public bool Visivel { get; set; } = true;
    public int Ordem { get; set; } // Z-Index (quem fica por cima no mapa)
    public string? CaminhoFonteOriginal { get; set; }

    // Chave Estrangeira: A qual projeto esta camada pertence?
    public Guid ProjetoId { get; set; }
    public Projeto? Projeto { get; set; }

    // Relacionamento do Banco: 1 Camada tem N Feições
    public List<Feicao> Feicoes { get; set; } = new();
}