using System.Globalization;
using SkiaSharp;

namespace GeoNex.Services;

/// <summary>
/// Uma classe de valor para simbologia graduada ou categorizada.
/// </summary>
/// <summary>
/// Uma classe de valor para simbologia categorizada (QGIS / ArcGIS Pro).
/// </summary>
public sealed class ItemCategoriaUI
{
    public string Valor { get; set; } = "";
    public string CorHex { get; set; } = "#38bdf8";
    public string RotuloLegenda { get; set; } = "";
    public int ContagemFeicoes { get; set; } = 0;

    public ItemCategoriaUI() { }

    public ItemCategoriaUI(string valor, string corHex, string rotuloLegenda, int contagem)
    {
        Valor = valor;
        CorHex = corHex;
        RotuloLegenda = rotuloLegenda;
        ContagemFeicoes = contagem;
    }
}

public sealed class FaixaGraduada
{
    public double Minimo { get; set; }
    public double Maximo { get; set; }
    public string CorHex { get; set; } = "#38bdf8";
    public string RotuloLegenda { get; set; } = "";

    public FaixaGraduada() { }

    public FaixaGraduada(double min, double max, string corHex, string rotuloLegenda)
    {
        Minimo = min;
        Maximo = max;
        CorHex = corHex;
        RotuloLegenda = rotuloLegenda;
    }
}

/// <summary>
/// Métodos de classificação estatística profissional (padrão QGIS / ArcGIS Pro).
/// </summary>
public enum MetodoClassificacao
{
    QuebrasNaturaisJenks,
    IntervaloIgual,
    Quantil
}

/// <summary>
/// Motor matemático e cartográfico para geração de intervalos e rampas de cor.
/// </summary>
public static class ClassificationEngine
{
    public static readonly string[] RampasDisponiveis =
    [
        "Viridis",
        "Turbo",
        "Plasma",
        "Magma",
        "Spectral",
        "Azuis",
        "Vermelhos",
        "Verdes",
        "Laranjas"
    ];

    /// <summary>
    /// Gera 'classesCount' cores interpoladas ao longo de uma rampa pré-definida.
    /// </summary>
    public static string[] GerarCoresRampa(string nomeRampa, int classesCount, bool inverter = false)
    {
        if (classesCount <= 0) return [];
        if (classesCount == 1) return [ObterCorAmostra(nomeRampa, 0.5f)];

        var cores = new string[classesCount];
        for (int i = 0; i < classesCount; i++)
        {
            float t = (float)i / (classesCount - 1);
            if (inverter) t = 1.0f - t;
            cores[i] = ObterCorAmostra(nomeRampa, t);
        }
        return cores;
    }

    /// <summary>
    /// Amostra uma cor normalizada (0.0 a 1.0) para a rampa informada.
    /// </summary>
    public static string ObterCorAmostra(string nomeRampa, float t)
    {
        t = Math.Clamp(t, 0.0f, 1.0f);
        SKColor color = nomeRampa.ToLowerInvariant() switch
        {
            "viridis" => InterpolarCores(ViridisStops, t),
            "turbo" => InterpolarCores(TurboStops, t),
            "plasma" => InterpolarCores(PlasmaStops, t),
            "magma" => InterpolarCores(MagmaStops, t),
            "spectral" => InterpolarCores(SpectralStops, t),
            "azuis" => InterpolarCores(BluesStops, t),
            "vermelhos" => InterpolarCores(RedsStops, t),
            "verdes" => InterpolarCores(GreensStops, t),
            "laranjas" => InterpolarCores(OrangesStops, t),
            _ => InterpolarCores(ViridisStops, t)
        };

        return $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
    }

    private static SKColor InterpolarCores((float Pos, SKColor Color)[] stops, float t)
    {
        if (t <= stops[0].Pos) return stops[0].Color;
        if (t >= stops[^1].Pos) return stops[^1].Color;

        for (int i = 0; i < stops.Length - 1; i++)
        {
            if (t >= stops[i].Pos && t <= stops[i + 1].Pos)
            {
                float localT = (t - stops[i].Pos) / (stops[i + 1].Pos - stops[i].Pos);
                SKColor c1 = stops[i].Color;
                SKColor c2 = stops[i + 1].Color;

                byte r = (byte)(c1.Red + (c2.Red - c1.Red) * localT);
                byte g = (byte)(c1.Green + (c2.Green - c1.Green) * localT);
                byte b = (byte)(c1.Blue + (c2.Blue - c1.Blue) * localT);
                return new SKColor(r, g, b);
            }
        }

        return stops[^1].Color;
    }

    /// <summary>
    /// Classifica um conjunto de valores numéricos contínuos nas classes solicitadas.
    /// </summary>
    public static List<FaixaGraduada> Classificar(
        ReadOnlySpan<double> valores,
        int numClasses,
        MetodoClassificacao metodo,
        string nomeRampa,
        bool inverterRampa = false)
    {
        var faixas = new List<FaixaGraduada>();
        if (valores.Length == 0 || numClasses <= 0) return faixas;

        // Filtra valores finitos e ordena
        var lista = new List<double>(valores.Length);
        for (int i = 0; i < valores.Length; i++)
        {
            if (double.IsFinite(valores[i])) lista.Add(valores[i]);
        }

        if (lista.Count == 0) return faixas;
        lista.Sort();

        double min = lista[0];
        double max = lista[^1];

        // Se todos os valores forem iguais, cria classe única
        if (Math.Abs(max - min) < 1e-9 || numClasses == 1 || lista.Count < numClasses)
        {
            string cor = ObterCorAmostra(nomeRampa, 0.5f);
            faixas.Add(new FaixaGraduada(min, max, cor, $"{min:F2} - {max:F2}"));
            return faixas;
        }

        double[] quebras;
        switch (metodo)
        {
            case MetodoClassificacao.IntervaloIgual:
                quebras = CalcularIntervalosIguais(min, max, numClasses);
                break;
            case MetodoClassificacao.Quantil:
                quebras = CalcularQuantis(lista, numClasses);
                break;
            case MetodoClassificacao.QuebrasNaturaisJenks:
            default:
                quebras = CalcularFisherJenks(lista, numClasses);
                break;
        }

        string[] cores = GerarCoresRampa(nomeRampa, numClasses, inverterRampa);

        for (int i = 0; i < numClasses; i++)
        {
            double faixaMin = quebras[i];
            double faixaMax = quebras[i + 1];
            string cor = cores[i];
            string label = $"{faixaMin.ToString("0.##", CultureInfo.InvariantCulture)} - {faixaMax.ToString("0.##", CultureInfo.InvariantCulture)}";
            faixas.Add(new FaixaGraduada(faixaMin, faixaMax, cor, label));
        }

        return faixas;
    }

    private static double[] CalcularIntervalosIguais(double min, double max, int numClasses)
    {
        var quebras = new double[numClasses + 1];
        double step = (max - min) / numClasses;
        for (int i = 0; i <= numClasses; i++)
        {
            quebras[i] = (i == numClasses) ? max : min + i * step;
        }
        return quebras;
    }

    private static double[] CalcularQuantis(List<double> ordenados, int numClasses)
    {
        var quebras = new double[numClasses + 1];
        quebras[0] = ordenados[0];
        quebras[numClasses] = ordenados[^1];

        int n = ordenados.Count;
        for (int i = 1; i < numClasses; i++)
        {
            double idx = (double)i * n / numClasses;
            int intIdx = (int)Math.Floor(idx);
            double frac = idx - intIdx;
            if (intIdx + 1 < n)
                quebras[i] = ordenados[intIdx] * (1.0 - frac) + ordenados[intIdx + 1] * frac;
            else
                quebras[i] = ordenados[intIdx];
        }

        // Garante monotonicidade
        for (int i = 1; i <= numClasses; i++)
        {
            if (quebras[i] < quebras[i - 1]) quebras[i] = quebras[i - 1];
        }

        return quebras;
    }

    /// <summary>
    /// Algoritmo Jenks Natural Breaks com programação dinâmica em matriz contígua 1D.
    /// </summary>
    private static double[] CalcularFisherJenks(List<double> lista, int numClasses)
    {
        int n = lista.Count;
        // Para bases de dados gigantes, amostra até 3000 itens uniformemente para manter o cálculo em < 5ms
        List<double> amostra;
        if (n > 3000)
        {
            amostra = new List<double>(3000);
            double step = (double)n / 3000;
            for (int i = 0; i < 3000; i++)
                amostra.Add(lista[(int)(i * step)]);
            n = 3000;
        }
        else
        {
            amostra = lista;
        }

        int k = numClasses;
        var mat1 = new int[n + 1, k + 1];
        var mat2 = new double[n + 1, k + 1];

        for (int i = 1; i <= k; i++)
        {
            mat1[1, i] = 1;
            mat2[1, i] = 0.0;
            for (int j = 2; j <= n; j++)
                mat2[j, i] = double.MaxValue;
        }

        double v = 0.0;
        for (int l = 2; l <= n; l++)
        {
            double s1 = 0.0;
            double s2 = 0.0;
            double w = 0.0;

            for (int m = 1; m <= l; m++)
            {
                int i4 = l - m + 1;
                double val = amostra[i4 - 1];
                s2 += val * val;
                s1 += val;
                w++;
                v = s2 - (s1 * s1) / w;
                int i4_1 = i4 - 1;
                if (i4_1 != 0)
                {
                    for (int j = 2; j <= k; j++)
                    {
                        if (mat2[l, j] >= (v + mat2[i4_1, j - 1]))
                        {
                            mat1[l, j] = i4;
                            mat2[l, j] = v + mat2[i4_1, j - 1];
                        }
                    }
                }
            }
            mat1[l, 1] = 1;
            mat2[l, 1] = v;
        }

        int kclass = k;
        var kclassIndices = new int[k + 1];
        kclassIndices[k] = n;
        int countList = n;

        while (kclass >= 2)
        {
            int id = mat1[countList, kclass] - 2;
            kclassIndices[kclass - 1] = id + 1;
            countList = mat1[countList, kclass] - 1;
            kclass--;
        }

        var quebras = new double[k + 1];
        quebras[0] = lista[0];
        quebras[k] = lista[^1];

        for (int i = 1; i < k; i++)
        {
            int idx = kclassIndices[i];
            if (idx >= 0 && idx < amostra.Count)
                quebras[i] = amostra[idx];
            else
                quebras[i] = quebras[i - 1];
        }

        // Garante monotonicidade estrita
        for (int i = 1; i <= k; i++)
        {
            if (quebras[i] < quebras[i - 1]) quebras[i] = quebras[i - 1];
        }

        return quebras;
    }

    #region Color Stops Definições
    private static readonly (float Pos, SKColor Color)[] ViridisStops =
    [
        (0.00f, new SKColor(68, 1, 84)),
        (0.25f, new SKColor(59, 82, 139)),
        (0.50f, new SKColor(33, 145, 140)),
        (0.75f, new SKColor(94, 201, 98)),
        (1.00f, new SKColor(253, 231, 37))
    ];

    private static readonly (float Pos, SKColor Color)[] TurboStops =
    [
        (0.00f, new SKColor(48, 18, 59)),
        (0.20f, new SKColor(70, 134, 251)),
        (0.40f, new SKColor(27, 229, 181)),
        (0.60f, new SKColor(164, 252, 60)),
        (0.80f, new SKColor(251, 155, 38)),
        (1.00f, new SKColor(122, 4, 3))
    ];

    private static readonly (float Pos, SKColor Color)[] PlasmaStops =
    [
        (0.00f, new SKColor(13, 8, 135)),
        (0.25f, new SKColor(126, 3, 168)),
        (0.50f, new SKColor(204, 71, 120)),
        (0.75f, new SKColor(248, 149, 64)),
        (1.00f, new SKColor(240, 249, 33))
    ];

    private static readonly (float Pos, SKColor Color)[] MagmaStops =
    [
        (0.00f, new SKColor(0, 0, 4)),
        (0.25f, new SKColor(81, 18, 124)),
        (0.50f, new SKColor(182, 54, 121)),
        (0.75f, new SKColor(251, 136, 97)),
        (1.00f, new SKColor(252, 253, 191))
    ];

    private static readonly (float Pos, SKColor Color)[] SpectralStops =
    [
        (0.00f, new SKColor(158, 1, 66)),
        (0.20f, new SKColor(213, 62, 79)),
        (0.40f, new SKColor(253, 174, 97)),
        (0.60f, new SKColor(230, 245, 152)),
        (0.80f, new SKColor(102, 194, 165)),
        (1.00f, new SKColor(94, 79, 162))
    ];

    private static readonly (float Pos, SKColor Color)[] BluesStops =
    [
        (0.00f, new SKColor(239, 243, 255)),
        (0.33f, new SKColor(189, 215, 231)),
        (0.66f, new SKColor(107, 174, 214)),
        (1.00f, new SKColor(8, 81, 156))
    ];

    private static readonly (float Pos, SKColor Color)[] RedsStops =
    [
        (0.00f, new SKColor(254, 224, 210)),
        (0.33f, new SKColor(252, 146, 114)),
        (0.66f, new SKColor(222, 45, 38)),
        (1.00f, new SKColor(165, 15, 21))
    ];

    private static readonly (float Pos, SKColor Color)[] GreensStops =
    [
        (0.00f, new SKColor(237, 248, 233)),
        (0.33f, new SKColor(186, 228, 179)),
        (0.66f, new SKColor(116, 196, 118)),
        (1.00f, new SKColor(0, 109, 44))
    ];

    private static readonly (float Pos, SKColor Color)[] OrangesStops =
    [
        (0.00f, new SKColor(254, 237, 222)),
        (0.33f, new SKColor(253, 190, 133)),
        (0.66f, new SKColor(253, 141, 60)),
        (1.00f, new SKColor(217, 71, 1))
    ];
    #endregion

    public static readonly string[] PaletasQualitativasDisponiveis =
    [
        "ArcMap (Harmonia SIG)",
        "Pastel Suave",
        "Vivid / Contraste",
        "Set 1 (ColorBrewer)",
        "Tons de Natureza"
    ];

    private static readonly string[] Set1Colors =
    [
        "#E41A1C", "#377EB8", "#4DAF4A", "#984EA3", "#FF7F00",
        "#FFFF33", "#A65628", "#F781BF", "#999999", "#66C2A5",
        "#FC8D62", "#8DA0CB", "#E78AC3", "#A6D854", "#FFD92F"
    ];

    private static readonly string[] NatureColors =
    [
        "#2E7D32", "#8BC34A", "#0288D1", "#4FC3F7", "#6D4C41",
        "#A1887F", "#F57C00", "#FFB74D", "#00897B", "#80CBC4",
        "#5D4037", "#C0CA33", "#E64A19", "#0097A7", "#78909C"
    ];

    public static Dictionary<string, string> GerarCoresCategorizadas(IList<string> categorias, string paleta)
    {
        var dic = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < categorias.Count; i++)
        {
            string cat = categorias[i];
            string hex;

            switch (paleta)
            {
                case "Set 1 (ColorBrewer)":
                    hex = Set1Colors[i % Set1Colors.Length];
                    break;

                case "Tons de Natureza":
                    hex = NatureColors[i % NatureColors.Length];
                    break;

                case "Pastel Suave":
                {
                    float hue = (i * 137.508f) % 360f;
                    var col = SKColor.FromHsv(hue, 42f, 95f);
                    hex = $"#{col.Red:X2}{col.Green:X2}{col.Blue:X2}";
                    break;
                }

                case "Vivid / Contraste":
                {
                    float hue = (i * 137.508f) % 360f;
                    var col = SKColor.FromHsv(hue, 85f, 95f);
                    hex = $"#{col.Red:X2}{col.Green:X2}{col.Blue:X2}";
                    break;
                }

                default: // ArcMap (Harmonia SIG)
                {
                    float hue = (i * 137.508f) % 360f;
                    int perfil = i % 4;
                    float sat = perfil switch { 0 => 75f, 1 => 45f, 2 => 65f, _ => 55f };
                    float val = perfil switch { 0 => 95f, 1 => 100f, 2 => 85f, _ => 90f };
                    var col = SKColor.FromHsv(hue, sat, val);
                    hex = $"#{col.Red:X2}{col.Green:X2}{col.Blue:X2}";
                    break;
                }
            }

            dic[cat] = hex;
        }

        return dic;
    }
}
