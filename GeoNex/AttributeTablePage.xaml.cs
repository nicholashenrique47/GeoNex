namespace GeoNex;

public partial class AttributeTablePage : ContentPage
{
    public AttributeTablePage(string? nomeCamada = null)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(nomeCamada))
        {
            Title = $"Tabela de Atributos — {nomeCamada} - GeoNex";
        }
    }
}
