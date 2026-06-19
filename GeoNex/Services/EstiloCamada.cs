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
        // ... (as suas variáveis de categorizado estão aqui em cima) ...

        // === MOTOR DE RÓTULOS ===
        // === MOTOR DE RÓTULOS ===
        public bool ExibirRotulos { get; set; } = false;
        public string ColunaRotulo { get; set; } = "";
        public string CorTextoRotulo { get; set; } = "#FFFFFF";
        public string CorHaloRotulo { get; set; } = "#000000";
        public float TamanhoTextoRotulo { get; set; } = 12f;
        public float TamanhoHaloRotulo { get; set; } = 3f; // Novo: Controlo do Halo
        public bool RotuloNegrito { get; set; } = true;    // Novo: Controlo de Fonte
        public bool AntiColisaoRotulos { get; set; } = true; // Novo: Motor Anti-Colisão
        public int EscalaMinimaRotulo { get; set; } = 0; // 0 = Exibe Sempre, 100 = Exibe só de perto
    }
}