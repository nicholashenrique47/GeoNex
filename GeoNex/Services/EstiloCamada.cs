using System.Collections.Generic;

namespace GeoNex.Services
{
    public class EstiloCamada
    {
        public string TipoSimbologia { get; set; } = "UNICA"; // "UNICA", "CATEGORIZADA", "GRADUADA"
        public string CorPreenchimento { get; set; } = "#38bdf8";
        public string CorBorda { get; set; } = "#0ea5e9";
        public float EspessuraLinha { get; set; } = 1.0f;
        public float EspessuraBorda { get; set; } = 1.0f;
        public float Tamanho { get; set; } = 4.0f;
        public float Opacidade { get; set; } = 0.8f;
        public string TipoLinha { get; set; } = "Solid"; // "Solid", "Dash", "Dot", "DashDot"
        public bool PreenchimentoTransparente { get; set; } = false;
        public bool BordaTransparente { get; set; } = false;

        // === SIMBOLOGIA AVANÇADA: MARCADORES E HACHURAS (QGIS / ARCGIS PRO) ===
        public string FormaMarcador { get; set; } = "Circulo"; // Circulo, Quadrado, Losango, Triangulo, Cruz, X, Estrela, Pentagono
        public float RotacaoMarcador { get; set; } = 0f; // 0 a 360 graus
        public string PadraoPreenchimento { get; set; } = "Solido"; // Solido, DiagonalDireita, DiagonalEsquerda, Cruzada, LinhasVerticais, LinhasHorizontais, Pontilhado
        public string CorHachura { get; set; } = "#0ea5e9";
        public int EspacamentoHachura { get; set; } = 12; // 6 a 48 pixels

        // === SÍMBOLOS PROPORCIONAIS (BUBBLE MAPS & FLOW LINES) ===
        public bool SimbologiaProporcional { get; set; } = false;
        public string ColunaTamanhoProporcional { get; set; } = "";
        public float TamanhoMinimoProporcional { get; set; } = 3.0f;
        public float TamanhoMaximoProporcional { get; set; } = 28.0f;
        public string MetodoEscalaProporcional { get; set; } = "Linear"; // Linear, RaizQuadrada, Flannery, Logaritmica
        public double ValorMinimoAtributo { get; set; } = 0.0;
        public double ValorMaximoAtributo { get; set; } = 100.0;

        // === COMPOSITING & LAYER BLENDING ===
        public string ModoMesclagem { get; set; } = "SrcOver"; // SrcOver, Multiply, Screen, Overlay, Darken
        public bool EfeitoSombra { get; set; } = false;
        public float RaioSombra { get; set; } = 4.0f;
        public string CorSombra { get; set; } = "#000000";

        // === SIMBOLOGIA CATEGORIZADA ===
        public string ColunaSimbologia { get; set; } = "";
        public string PaletaCategorizada { get; set; } = "ArcMap"; // ArcMap, Pastel, Vivid, ColorBrewer1, Natureza
        public Dictionary<string, string> CoresCategorizadas { get; set; } = new();
        public Dictionary<string, string> RotulosCategorizados { get; set; } = new();

        // === SIMBOLOGIA GRADUADA (INTERVALOS QUANTITATIVOS) ===
        public MetodoClassificacao MetodoClassificacao { get; set; } = MetodoClassificacao.QuebrasNaturaisJenks;
        public string RampaCores { get; set; } = "Viridis"; // Viridis, Plasma, Inferno, Magma, Cividis, Turbo, RdYlGn, Coolwarm, YlOrRd
        public bool InverterRampa { get; set; } = false;
        public int NumeroClasses { get; set; } = 5;
        public List<FaixaGraduada> FaixasGraduadas { get; set; } = new();

        // === MOTOR DE RÓTULOS CARTOGRÁFICOS DINÂMICOS (QGIS PAL / MAPLEX STANDARD) ===
        public bool ExibirRotulos { get; set; } = false;
        public string ColunaRotulo { get; set; } = "";
        public string FamiliaFonteRotulo { get; set; } = "Segoe UI";
        public float TamanhoTextoRotulo { get; set; } = 12f;
        public bool RotuloNegrito { get; set; } = true;
        public bool RotuloItalico { get; set; } = false;
        public string TransformacaoTextoRotulo { get; set; } = "Normal"; // Normal, Maiusculas, Minusculas, Titulo
        public string CorTextoRotulo { get; set; } = "#FFFFFF";
        public float OpacidadeRotulo { get; set; } = 1.0f;
        public string CorHaloRotulo { get; set; } = "#000000";
        public float TamanhoHaloRotulo { get; set; } = 2.5f;
        public float OpacidadeHaloRotulo { get; set; } = 0.85f;

        // Posicionamento & Conflito
        public string ModoPosicionamentoRotulo { get; set; } = "Quadrantes"; // Quadrantes, TopRight, Fixo, SobrePonto
        public float DeslocamentoRotuloX { get; set; } = 4f;
        public float DeslocamentoRotuloY { get; set; } = -4f;
        public bool UsarPoloInacessibilidade { get; set; } = true;
        public bool AntiColisaoRotulos { get; set; } = true;
        public bool DesenharLinhaChamada { get; set; } = false; // Linha conector (Leader Line / Callout)
        public string CorLinhaChamada { get; set; } = "#94a3b8";
        public float EspessuraLinhaChamada { get; set; } = 1.0f;

        // Escala e Descarte
        public int EscalaMinimaRotulo { get; set; } = 0; // 0 = sem limite
        public int EscalaMaximaRotulo { get; set; } = 0; // 0 = sem limite
        public float TamanhoMinimoFeicaoPixels { get; set; } = 32f; // Não rotula feições menores que N px na tela
    }
}
