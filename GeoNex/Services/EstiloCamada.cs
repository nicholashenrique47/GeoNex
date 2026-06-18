namespace GeoNex.Services
{
    public class EstiloCamada
    {
        public string TipoSimbologia { get; set; } = "UNICA";
        public string CorPreenchimento { get; set; } = "#38bdf8";
        public string CorBorda { get; set; } = "#0ea5e9";
        public float EspessuraBorda { get; set; } = 1.0f;
        public float Opacidade { get; set; } = 0.35f;

        // Disjuntores de Transparência Absoluta
        public bool PreenchimentoTransparente { get; set; } = false;
        public bool BordaTransparente { get; set; } = false;

        // Memória do Modo Categorizado
        public string ColunaSimbologia { get; set; } = "";
        public Dictionary<string, string> CoresCategorizadas { get; set; } = new();
    }
}