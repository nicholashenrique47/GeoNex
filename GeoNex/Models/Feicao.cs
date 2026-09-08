namespace GeoNex.Models;

public class Feicao
{
    public int Id { get; set; }
    public string Identificador { get; set; } = string.Empty; // Ex: "LOTE-045"

    // GeometriaWKT guarda o formato matemático do polígono. Ex: POLYGON((-48.5 -25.8, ...))
    public string GeometriaWKT { get; set; } = string.Empty;

    // AtributosJson guarda o formulário dinâmico (Proprietário, Inscrição, etc)
    public string AtributosJson { get; set; } = "{}";
    public double Area { get; set; }
    public double Perimetro { get; set; }

    // Chave Estrangeira: A qual camada este lote/ponto pertence?
    public int CamadaId { get; set; }
    public Camada? Camada { get; set; }
}