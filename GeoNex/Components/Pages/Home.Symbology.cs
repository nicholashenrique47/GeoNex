using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GeoNex.Models;
using GeoNex.Services;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace GeoNex.Components.Pages;

public partial class Home
{
    // === CATÁLOGOS CARTOGRÁFICOS DISPONÍVEIS ===
    public static readonly string[] FormasMarcadorDisponiveis =
    [
        "Circulo", "Quadrado", "Losango", "Triangulo", "TrianguloInvertido",
        "Cruz", "X", "Estrela", "Pentagono", "Hexagono",
        "MarcoINCRA", "MarcoGeodesicoQGIS", "EstacaoTopograficaQGIS",
        "VerticeTriangulacaoQGIS", "AlvoTarget", "PontoCotado", "PinoLocalizacao",
        "TorreEnergiaQGIS", "PosteEnergiaQGIS", "PocoAguaQGIS", "ArvoreFlorestalQGIS", "EdificacaoSedeQGIS"
    ];

    private string categoriaMarcadorAtiva = "Todos";

    public static readonly string[] CategoriasMarcadorDisponiveis =
    [
        "Todos", "Topografia", "Infraestrutura", "Meio Ambiente", "Geometria"
    ];

    public static string ObterCategoriaForma(string forma)
    {
        return forma switch
        {
            "MarcoINCRA" or "MarcoGeodesicoQGIS" or "EstacaoTopograficaQGIS" or "VerticeTriangulacaoQGIS" or "AlvoTarget" or "PontoCotado" or "TrianguloInvertido" => "Topografia",
            "TorreEnergiaQGIS" or "PosteEnergiaQGIS" or "EdificacaoSedeQGIS" => "Infraestrutura",
            "ArvoreFlorestalQGIS" or "PocoAguaQGIS" or "PinoLocalizacao" => "Meio Ambiente",
            _ => "Geometria"
        };
    }

    public static string ObterNomeLegivelForma(string forma)
    {
        return forma switch
        {
            "MarcoINCRA" => "Marco INCRA",
            "MarcoGeodesicoQGIS" => "Marco Geodésico",
            "EstacaoTopograficaQGIS" => "Estação Total",
            "VerticeTriangulacaoQGIS" => "Vértice Triang.",
            "AlvoTarget" => "Alvo GNSS",
            "PontoCotado" => "Ponto Cotado",
            "PinoLocalizacao" => "Pino / Sede",
            "TorreEnergiaQGIS" => "Torre Energia",
            "PosteEnergiaQGIS" => "Poste Rede",
            "PocoAguaQGIS" => "Poço / Água",
            "ArvoreFlorestalQGIS" => "Árvore / APP",
            "EdificacaoSedeQGIS" => "Edificação",
            "TrianguloInvertido" => "Piquete",
            _ => forma
        };
    }

    public record PresetEstiloPonto(string Nome, string Forma, string CorFill, string CorBorda, float Tamanho, float EspessuraBorda);

    public static readonly PresetEstiloPonto[] PresetsTopografia =
    [
        new("Marco INCRA (M)", "MarcoINCRA", "#0ea5e9", "#0284c7", 9f, 1.8f),
        new("Marco Geodésico", "MarcoGeodesicoQGIS", "#0f172a", "#38bdf8", 10f, 2.0f),
        new("Estação Total", "EstacaoTopograficaQGIS", "#f59e0b", "#d97706", 11f, 1.6f),
        new("Vértice Triang.", "VerticeTriangulacaoQGIS", "#dc2626", "#ffffff", 9f, 1.8f),
        new("Base GNSS / RTK", "AlvoTarget", "#10b981", "#047857", 9f, 2.0f),
        new("Ponto Cotado", "PontoCotado", "#38bdf8", "#0369a1", 8f, 1.5f),
        new("Piquete de Campo", "TrianguloInvertido", "#fbbf24", "#78350f", 8f, 1.5f),
        new("Torre / Linha", "TorreEnergiaQGIS", "#64748b", "#334155", 11f, 1.6f),
        new("Árvore / Florestal", "ArvoreFlorestalQGIS", "#15803d", "#166534", 11f, 1.5f),
        new("Poço / Nascente", "PocoAguaQGIS", "#0284c7", "#0369a1", 10f, 1.8f),
        new("Sede / Construção", "EdificacaoSedeQGIS", "#b45309", "#78350f", 10f, 1.5f),
        new("Pino de Interesse", "PinoLocalizacao", "#ec4899", "#be185d", 11f, 1.5f)
    ];

    public static readonly string[] PadroesHachuraDisponiveis =
    [
        "Solido", "DiagonalDireita", "DiagonalEsquerda", "Cruzada", "LinhasVerticais", "LinhasHorizontais", "Pontilhado"
    ];

    public static readonly string[] PaletasQualitativasDisponiveis =
    [
        "ArcMap", "Pastel", "Vivid", "ColorBrewer1", "Natureza"
    ];

    public static readonly string[] ModosMesclagemDisponiveis =
    [
        "SrcOver", "Multiply", "Screen", "Overlay", "Darken"
    ];

    public static readonly string[] MetodosEscalaProporcionalDisponiveis =
    [
        "Flannery", "Linear", "RaizQuadrada", "Logaritmica"
    ];

    public static readonly string[] FamiliasFonteDisponiveis =
    [
        "Segoe UI", "Arial", "Roboto", "Times New Roman", "Consolas", "Trebuchet MS", "Georgia"
    ];

    public static readonly string[] TransformacoesTextoDisponiveis =
    [
        "Normal", "Maiusculas", "Minusculas", "Titulo"
    ];

    // === ESTADOS DE SIMBOLOGIA AVANÇADA ===
    private string formaMarcador = "Circulo";
    private float rotacaoMarcador = 0f;
    private string padraoPreenchimento = "Solido";
    private string corHachura = "#0ea5e9";
    private int espacamentoHachura = 12;
    private string modoMesclagem = "SrcOver";
    private bool efeitoSombra = false;
    private float raioSombra = 4.0f;
    private string corSombra = "#000000";

    // Proporcional
    private bool simbologiaProporcional = false;
    private string colunaTamanhoProporcional = "";
    private float tamanhoMinimoProporcional = 3.0f;
    private float tamanhoMaximoProporcional = 28.0f;
    private string metodoEscalaProporcional = "Flannery";

    // Categorizada
    private string paletaCategorizada = "ArcMap";
    private List<ItemCategoriaUI> listaCategoriasUI = new();

    // Graduada
    private MetodoClassificacao metodoClassificacao = MetodoClassificacao.QuebrasNaturaisJenks;
    private string colunaSimbologiaGraduada = "";
    private string rampaCores = "Viridis";
    private bool inverterRampa = false;
    private int numeroClasses = 5;
    private List<FaixaGraduada> faixasGraduadasUI = new();

    // Rótulos Avançados
    private string familiaFonteRotulo = "Segoe UI";
    private bool rotuloItalico = false;
    private string transformacaoTextoRotulo = "Normal";
    private int opacidadeRotulo = 100;
    private int opacidadeHaloRotulo = 85;
    private bool desenharLinhaChamada = false;
    private string corLinhaChamada = "#94a3b8";
    private float espessuraLinhaChamada = 1.0f;
    private bool usarPoloInacessibilidade = true;
    private float tamanhoMinimoFeicaoPixels = 32f;

    // Feedback UI e Modos de Visualização
    private string statusFeedbackEstilo = "";
    private bool statusEstiloSucesso = true;
    private bool _carregandoClassificacao = false;
    private bool previewFundoClaro = false;

    // === MÉTODOS DE SINCRONIZAÇÃO E CONTROLE ===
    private void SincronizarCamposEstiloParaUIAvancado(EstiloCamada estilo)
    {
        formaMarcador = string.IsNullOrEmpty(estilo.FormaMarcador) ? "Circulo" : estilo.FormaMarcador;
        rotacaoMarcador = estilo.RotacaoMarcador;
        padraoPreenchimento = string.IsNullOrEmpty(estilo.PadraoPreenchimento) ? "Solido" : estilo.PadraoPreenchimento;
        corHachura = string.IsNullOrEmpty(estilo.CorHachura) ? "#0ea5e9" : estilo.CorHachura;
        espacamentoHachura = estilo.EspacamentoHachura > 0 ? estilo.EspacamentoHachura : 12;
        modoMesclagem = string.IsNullOrEmpty(estilo.ModoMesclagem) ? "SrcOver" : estilo.ModoMesclagem;
        efeitoSombra = estilo.EfeitoSombra;
        raioSombra = estilo.RaioSombra > 0 ? estilo.RaioSombra : 4.0f;
        corSombra = string.IsNullOrEmpty(estilo.CorSombra) ? "#000000" : estilo.CorSombra;

        simbologiaProporcional = estilo.SimbologiaProporcional;
        colunaTamanhoProporcional = estilo.ColunaTamanhoProporcional ?? "";
        tamanhoMinimoProporcional = estilo.TamanhoMinimoProporcional > 0 ? estilo.TamanhoMinimoProporcional : 3.0f;
        tamanhoMaximoProporcional = estilo.TamanhoMaximoProporcional > 0 ? estilo.TamanhoMaximoProporcional : 28.0f;
        metodoEscalaProporcional = string.IsNullOrEmpty(estilo.MetodoEscalaProporcional) ? "Flannery" : estilo.MetodoEscalaProporcional;

        paletaCategorizada = string.IsNullOrEmpty(estilo.PaletaCategorizada) ? "ArcMap" : estilo.PaletaCategorizada;
        listaCategoriasUI.Clear();
        if (estilo.CoresCategorizadas.Count > 0)
        {
            foreach (var kvp in estilo.CoresCategorizadas)
            {
                string label = estilo.RotulosCategorizados.TryGetValue(kvp.Key, out var rot) ? rot : kvp.Key;
                listaCategoriasUI.Add(new ItemCategoriaUI(kvp.Key, kvp.Value, label, 0));
            }
        }

        colunaSimbologiaGraduada = (estilo.TipoSimbologia == "GRADUADA" && !string.IsNullOrEmpty(estilo.ColunaSimbologia))
            ? estilo.ColunaSimbologia
            : (ColunasDaCamadaAtual.FirstOrDefault() ?? "");
        metodoClassificacao = estilo.MetodoClassificacao;
        rampaCores = string.IsNullOrEmpty(estilo.RampaCores) ? "Viridis" : estilo.RampaCores;
        inverterRampa = estilo.InverterRampa;
        numeroClasses = estilo.NumeroClasses > 0 ? estilo.NumeroClasses : 5;

        faixasGraduadasUI.Clear();
        if (estilo.FaixasGraduadas.Count > 0)
        {
            foreach (var fx in estilo.FaixasGraduadas)
            {
                faixasGraduadasUI.Add(new FaixaGraduada(fx.Minimo, fx.Maximo, fx.CorHex, fx.RotuloLegenda));
            }
        }

        familiaFonteRotulo = string.IsNullOrEmpty(estilo.FamiliaFonteRotulo) ? "Segoe UI" : estilo.FamiliaFonteRotulo;
        rotuloItalico = estilo.RotuloItalico;
        transformacaoTextoRotulo = string.IsNullOrEmpty(estilo.TransformacaoTextoRotulo) ? "Normal" : estilo.TransformacaoTextoRotulo;
        opacidadeRotulo = (int)(Math.Clamp(estilo.OpacidadeRotulo, 0f, 1f) * 100f);
        opacidadeHaloRotulo = (int)(Math.Clamp(estilo.OpacidadeHaloRotulo, 0f, 1f) * 100f);
        desenharLinhaChamada = estilo.DesenharLinhaChamada;
        corLinhaChamada = string.IsNullOrEmpty(estilo.CorLinhaChamada) ? "#94a3b8" : estilo.CorLinhaChamada;
        espessuraLinhaChamada = estilo.EspessuraLinhaChamada > 0 ? estilo.EspessuraLinhaChamada : 1.0f;
        usarPoloInacessibilidade = estilo.UsarPoloInacessibilidade;
        tamanhoMinimoFeicaoPixels = estilo.TamanhoMinimoFeicaoPixels > 0 ? estilo.TamanhoMinimoFeicaoPixels : 32f;
        statusFeedbackEstilo = "";
    }

    private void SalvarCamposUIParaEstiloAvancado(EstiloCamada estilo)
    {
        estilo.FormaMarcador = formaMarcador;
        estilo.RotacaoMarcador = rotacaoMarcador;
        estilo.PadraoPreenchimento = padraoPreenchimento;
        estilo.CorHachura = corHachura;
        estilo.EspacamentoHachura = espacamentoHachura;
        estilo.ModoMesclagem = modoMesclagem;
        estilo.EfeitoSombra = efeitoSombra;
        estilo.RaioSombra = raioSombra;
        estilo.CorSombra = corSombra;

        estilo.SimbologiaProporcional = simbologiaProporcional;
        estilo.ColunaTamanhoProporcional = colunaTamanhoProporcional;
        estilo.TamanhoMinimoProporcional = tamanhoMinimoProporcional;
        estilo.TamanhoMaximoProporcional = tamanhoMaximoProporcional;
        estilo.MetodoEscalaProporcional = metodoEscalaProporcional;

        estilo.PaletaCategorizada = paletaCategorizada;
        estilo.MetodoClassificacao = metodoClassificacao;
        estilo.RampaCores = rampaCores;
        estilo.InverterRampa = inverterRampa;
        estilo.NumeroClasses = numeroClasses;

        if (tipoSimbologia == "GRADUADA")
        {
            estilo.ColunaSimbologia = colunaSimbologiaGraduada;
            estilo.FaixasGraduadas = new List<FaixaGraduada>(faixasGraduadasUI);
            estilo.CoresCategorizadas.Clear();
            foreach (var fx in faixasGraduadasUI)
            {
                estilo.CoresCategorizadas[fx.RotuloLegenda] = fx.CorHex;
            }
        }
        else if (tipoSimbologia == "CATEGORIZADA")
        {
            estilo.ColunaSimbologia = colunaSimbologiaCategorizada;
            estilo.CoresCategorizadas.Clear();
            estilo.RotulosCategorizados.Clear();
            foreach (var item in listaCategoriasUI)
            {
                estilo.CoresCategorizadas[item.Valor] = item.CorHex;
                estilo.RotulosCategorizados[item.Valor] = item.RotuloLegenda;
            }
        }

        estilo.FamiliaFonteRotulo = familiaFonteRotulo;
        estilo.RotuloItalico = rotuloItalico;
        estilo.TransformacaoTextoRotulo = transformacaoTextoRotulo;
        estilo.OpacidadeRotulo = opacidadeRotulo / 100f;
        estilo.OpacidadeHaloRotulo = opacidadeHaloRotulo / 100f;
        estilo.DesenharLinhaChamada = desenharLinhaChamada;
        estilo.CorLinhaChamada = corLinhaChamada;
        estilo.EspessuraLinhaChamada = espessuraLinhaChamada;
        estilo.UsarPoloInacessibilidade = usarPoloInacessibilidade;
        estilo.TamanhoMinimoFeicaoPixels = tamanhoMinimoFeicaoPixels;
    }

    private void SincronizarCamposEstiloParaUI(EstiloCamada estilo)
    {
        tipoSimbologia = string.IsNullOrEmpty(estilo.TipoSimbologia) ? "UNICA" : estilo.TipoSimbologia;
        colunaSimbologiaCategorizada = estilo.ColunaSimbologia ?? "";
        opacidadePreenchimento = (int)(estilo.Opacidade * 100f);
        espessuraLinha = estilo.EspessuraLinha;
        espessuraBorda = estilo.EspessuraBorda;
        tipoLinha = estilo.TipoLinha ?? "Solid";
        tamanhoPonto = estilo.Tamanho;

        semCorPreenchimento = (estilo.CorPreenchimento == "transparent");
        corPreenchimento = string.IsNullOrEmpty(estilo.CorPreenchimento) ? "#38bdf8" : (semCorPreenchimento ? "#38bdf8" : estilo.CorPreenchimento);

        semCorBorda = (estilo.CorBorda == "transparent");
        corBorda = string.IsNullOrEmpty(estilo.CorBorda) ? "#0ea5e9" : (semCorBorda ? "#0ea5e9" : estilo.CorBorda);

        exibirRotulos = estilo.ExibirRotulos;
        colunaRotuloSelecionada = estilo.ColunaRotulo ?? "";
        tamanhoFonteRotulo = (int)(estilo.TamanhoTextoRotulo > 0 ? estilo.TamanhoTextoRotulo : 12f);
        tamanhoHaloRotulo = estilo.TamanhoHaloRotulo;
        corFonteRotulo = string.IsNullOrEmpty(estilo.CorTextoRotulo) ? "#FFFFFF" : estilo.CorTextoRotulo;
        corHaloRotulo = string.IsNullOrEmpty(estilo.CorHaloRotulo) ? "#000000" : estilo.CorHaloRotulo;
        rotuloNegrito = estilo.RotuloNegrito;
        antiColisaoRotulos = estilo.AntiColisaoRotulos;
        escalaMinimaRotulo = estilo.EscalaMinimaRotulo;

        SincronizarCamposEstiloParaUIAvancado(estilo);
    }

    private void SalvarCamposUIParaEstilo(EstiloCamada estilo)
    {
        estilo.TipoSimbologia = tipoSimbologia;
        estilo.Tamanho = Convert.ToSingle(tamanhoPonto);
        estilo.EspessuraLinha = Convert.ToSingle(espessuraLinha);
        estilo.EspessuraBorda = Convert.ToSingle(espessuraBorda);
        estilo.TipoLinha = tipoLinha;
        estilo.Opacidade = Convert.ToSingle(opacidadePreenchimento) / 100f;

        estilo.BordaTransparente = semCorBorda;
        estilo.CorBorda = semCorBorda ? "transparent" : corBorda;
        estilo.PreenchimentoTransparente = semCorPreenchimento;
        estilo.CorPreenchimento = semCorPreenchimento ? "transparent" : corPreenchimento;

        estilo.ExibirRotulos = exibirRotulos && !string.IsNullOrEmpty(colunaRotuloSelecionada);
        estilo.ColunaRotulo = colunaRotuloSelecionada;
        estilo.CorTextoRotulo = corFonteRotulo;
        estilo.CorHaloRotulo = corHaloRotulo;
        estilo.TamanhoTextoRotulo = (float)tamanhoFonteRotulo;
        estilo.TamanhoHaloRotulo = (float)tamanhoHaloRotulo;
        estilo.RotuloNegrito = rotuloNegrito;
        estilo.AntiColisaoRotulos = antiColisaoRotulos;
        estilo.EscalaMinimaRotulo = escalaMinimaRotulo;

        SalvarCamposUIParaEstiloAvancado(estilo);
    }

    public string ObterTipoGeometriaCamada(string camada)
    {
        if (string.IsNullOrEmpty(camada)) return "VETOR";
        if (MapService.PontosPorCamada.ContainsKey(camada)) return "PONTO";
        if (MapService.LinhasPorCamada.ContainsKey(camada)) return "LINHA";
        if (MapService.VetoresPorCamada.ContainsKey(camada)) return "POLÍGONO";
        return "VETOR";
    }

    public static string ObterCssGradienteRampa(string rampa, bool inverter)
    {
        try
        {
            string c0 = ClassificationEngine.ObterCorAmostra(rampa, inverter ? 1.0f : 0.0f);
            string c25 = ClassificationEngine.ObterCorAmostra(rampa, inverter ? 0.75f : 0.25f);
            string c50 = ClassificationEngine.ObterCorAmostra(rampa, 0.5f);
            string c75 = ClassificationEngine.ObterCorAmostra(rampa, inverter ? 0.25f : 0.75f);
            string c100 = ClassificationEngine.ObterCorAmostra(rampa, inverter ? 0.0f : 1.0f);
            return $"linear-gradient(to right, {c0}, {c25}, {c50}, {c75}, {c100})";
        }
        catch
        {
            return "linear-gradient(to right, #38bdf8, #6366f1)";
        }
    }

    public static string[] ObterAmostrasPaletaCategorizada(string nomePaleta)
    {
        return nomePaleta switch
        {
            "Pastel" => ["#fdba74", "#f472b6", "#c084fc", "#818cf8", "#38bdf8", "#4ade80"],
            "Vivid" => ["#e11d48", "#ea580c", "#ca8a04", "#16a34a", "#0284c7", "#9333ea"],
            "ColorBrewer1" => ["#e41a1c", "#377eb8", "#4daf4a", "#984ea3", "#ff7f00", "#ffff33"],
            "Natureza" => ["#2d6a4f", "#52b788", "#74c69d", "#b7e4c7", "#d8f3dc", "#95d5b2"],
            _ => ["#38bdf8", "#0ea5e9", "#0284c7", "#0369a1", "#075985", "#0c4a6e"] // ArcMap
        };
    }

    private void SelecionarFormaMarcador(string forma)
    {
        formaMarcador = forma;
        AtualizarSimbologiaAoVivo();
    }

    private void AplicarPresetPonto(PresetEstiloPonto preset)
    {
        formaMarcador = preset.Forma;
        corPreenchimento = preset.CorFill;
        corBorda = preset.CorBorda;
        semCorPreenchimento = false;
        semCorBorda = false;
        tamanhoPonto = preset.Tamanho;
        espessuraLinha = preset.EspessuraBorda;
        espessuraBorda = preset.EspessuraBorda;
        AtualizarSimbologiaAoVivo();
    }

    private void SelecionarPadraoPreenchimento(string padrao)
    {
        padraoPreenchimento = padrao;
        AtualizarSimbologiaAoVivo();
    }

    private void SelecionarTipoLinha(string tipo)
    {
        tipoLinha = tipo;
        AtualizarSimbologiaAoVivo();
    }

    private void AlternarInversaoRampa()
    {
        inverterRampa = !inverterRampa;
        if (tipoSimbologia == "GRADUADA" && faixasGraduadasUI.Count > 0)
        {
            ExecutarClassificacaoGraduada();
        }
        else
        {
            AtualizarSimbologiaAoVivo();
        }
    }

    private void ExecutarClassificacaoGraduada()
    {
        if (string.IsNullOrEmpty(camadaEmEdicao) || string.IsNullOrEmpty(colunaSimbologiaGraduada))
        {
            statusFeedbackEstilo = "Selecione uma coluna numérica para classificar.";
            statusEstiloSucesso = false;
            return;
        }

        try
        {
            _carregandoClassificacao = true;
            var valores = MapService.ObterValoresNumericosColuna(camadaEmEdicao, colunaSimbologiaGraduada);
            if (valores.Count == 0)
            {
                statusFeedbackEstilo = $"Nenhum valor numérico válido encontrado na coluna '{colunaSimbologiaGraduada}'.";
                statusEstiloSucesso = false;
                return;
            }

            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(valores);
            faixasGraduadasUI = ClassificationEngine.Classificar(span, numeroClasses, metodoClassificacao, rampaCores, inverterRampa);

            var estilo = MapService.EstilosPorCamada.GetOrAdd(camadaEmEdicao, _ => new EstiloCamada());
            estilo.FaixasGraduadas = new List<FaixaGraduada>(faixasGraduadasUI);

            statusFeedbackEstilo = $"Classificação gerou {faixasGraduadasUI.Count} faixas ({valores.Count} feições analisadas).";
            statusEstiloSucesso = true;
        }
        catch (Exception ex)
        {
            statusFeedbackEstilo = "Erro ao classificar: " + ex.Message;
            statusEstiloSucesso = false;
        }
        finally
        {
            _carregandoClassificacao = false;
        }
    }

    private void ExecutarClassificacaoCategorizada()
    {
        if (string.IsNullOrEmpty(camadaEmEdicao) || string.IsNullOrEmpty(colunaSimbologiaCategorizada))
        {
            statusFeedbackEstilo = "Selecione uma coluna de atributos para categorizar.";
            statusEstiloSucesso = false;
            return;
        }

        try
        {
            _carregandoClassificacao = true;
            var distintos = MapService.ObterValoresDistintosColuna(camadaEmEdicao, colunaSimbologiaCategorizada);
            if (distintos.Count == 0)
            {
                statusFeedbackEstilo = $"Nenhum valor encontrado na coluna '{colunaSimbologiaCategorizada}'.";
                statusEstiloSucesso = false;
                return;
            }

            var chaves = distintos.Keys.ToArray();
            var mapaCores = ClassificationEngine.GerarCoresCategorizadas(chaves, paletaCategorizada);

            listaCategoriasUI.Clear();
            foreach (var k in chaves)
            {
                string cor = mapaCores.TryGetValue(k, out var c) ? c : "#38bdf8";
                listaCategoriasUI.Add(new ItemCategoriaUI(k, cor, k, distintos[k]));
            }

            statusFeedbackEstilo = $"{listaCategoriasUI.Count} categorias identificadas com sucesso.";
            statusEstiloSucesso = true;
        }
        catch (Exception ex)
        {
            statusFeedbackEstilo = "Erro ao categorizar: " + ex.Message;
            statusEstiloSucesso = false;
        }
        finally
        {
            _carregandoClassificacao = false;
        }
    }

    private void AdicionarFaixaGraduada()
    {
        double novoMin = faixasGraduadasUI.Count > 0 ? faixasGraduadasUI[^1].Maximo : 0.0;
        double novoMax = novoMin + 10.0;
        string cor = ClassificationEngine.ObterCorAmostra(rampaCores, 0.5f);
        faixasGraduadasUI.Add(new FaixaGraduada(novoMin, novoMax, cor, $"{novoMin.ToString("0.##", CultureInfo.InvariantCulture)} - {novoMax.ToString("0.##", CultureInfo.InvariantCulture)}"));
    }

    private void RemoverFaixaGraduada(int index)
    {
        if (index >= 0 && index < faixasGraduadasUI.Count)
        {
            faixasGraduadasUI.RemoveAt(index);
        }
    }

    private void AlterarCorFaixaGraduada(int index, string corHex)
    {
        if (index >= 0 && index < faixasGraduadasUI.Count && !string.IsNullOrEmpty(corHex))
        {
            faixasGraduadasUI[index].CorHex = corHex;
            AtualizarSimbologiaAoVivo();
        }
    }

    private void AlterarCorCategoria(int index, string corHex)
    {
        if (index >= 0 && index < listaCategoriasUI.Count && !string.IsNullOrEmpty(corHex))
        {
            listaCategoriasUI[index].CorHex = corHex;
            AtualizarSimbologiaAoVivo();
        }
    }

    private async Task ExportarEstiloJsonAsync()
    {
        if (string.IsNullOrEmpty(camadaEmEdicao)) return;
        var estilo = MapService.EstilosPorCamada.GetOrAdd(camadaEmEdicao, _ => new EstiloCamada());
        SalvarCamposUIParaEstilo(estilo);
        string json = StyleSerializationService.ExportarParaJson(estilo);
        string nomeArquivo = $"{camadaEmEdicao}_estilo.gnxstyle";
        await JSRuntime.InvokeVoidAsync("geonexDownloadText", json, nomeArquivo);
        statusFeedbackEstilo = $"Estilo exportado: {nomeArquivo}";
        statusEstiloSucesso = true;
    }

    private async Task ExportarEstiloSldAsync()
    {
        if (string.IsNullOrEmpty(camadaEmEdicao)) return;
        var estilo = MapService.EstilosPorCamada.GetOrAdd(camadaEmEdicao, _ => new EstiloCamada());
        SalvarCamposUIParaEstilo(estilo);
        string sld = StyleSerializationService.ExportarParaSLD(camadaEmEdicao, estilo);
        string nomeArquivo = $"{camadaEmEdicao}_estilo.sld";
        await JSRuntime.InvokeVoidAsync("geonexDownloadText", sld, nomeArquivo);
        statusFeedbackEstilo = $"OGC SLD exportado: {nomeArquivo}";
        statusEstiloSucesso = true;
    }

    private async Task CarregarEstiloArquivoAsync(InputFileChangeEventArgs e)
    {
        if (string.IsNullOrEmpty(camadaEmEdicao) || e.File == null) return;
        try
        {
            using var stream = e.File.OpenReadStream(maxAllowedSize: 5 * 1024 * 1024);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string conteudo = await reader.ReadToEndAsync();

            EstiloCamada? estiloImportado = null;
            if (conteudo.TrimStart().StartsWith("<"))
            {
                estiloImportado = StyleSerializationService.ImportarDeSLD(conteudo);
            }
            else
            {
                estiloImportado = StyleSerializationService.ImportarDeJson(conteudo);
            }

            if (estiloImportado != null)
            {
                MapService.EstilosPorCamada[camadaEmEdicao] = estiloImportado;
                SincronizarCamposEstiloParaUI(estiloImportado);
                AtualizarSimbologiaAoVivo();
                statusFeedbackEstilo = $"Estilo carregado com sucesso ({e.File.Name})!";
                statusEstiloSucesso = true;
            }
            else
            {
                statusFeedbackEstilo = "Formato de arquivo de estilo inválido ou não suportado.";
                statusEstiloSucesso = false;
            }
        }
        catch (Exception ex)
        {
            statusFeedbackEstilo = "Falha ao carregar estilo: " + ex.Message;
            statusEstiloSucesso = false;
        }
    }

    // === GERADORES DE PREVIEW SVG ===
    private string ObterSvgDashArray(string tipo)
    {
        return tipo switch
        {
            "Dash" => "6,4",
            "Dot" => "2,3",
            "DashDot" => "6,3,2,3",
            _ => "none"
        };
    }

    private string ObterSvgMarkerPath(string forma, float r)
    {
        return forma switch
        {
            "Quadrado" => $"M {-r * 0.85f} {-r * 0.85f} L {r * 0.85f} {-r * 0.85f} L {r * 0.85f} {r * 0.85f} L {-r * 0.85f} {r * 0.85f} Z",
            "Losango" or "Diamante" => $"M 0 {-r * 1.15f} L {r * 1.15f} 0 L 0 {r * 1.15f} L {-r * 1.15f} 0 Z",
            "Triangulo" => $"M 0 {-r * 1.15f} L {r * 1.05f} {r * 0.85f} L {-r * 1.05f} {r * 0.85f} Z",
            "TrianguloInvertido" => $"M 0 {r * 1.15f} L {r * 1.05f} {-r * 0.85f} L {-r * 1.05f} {-r * 0.85f} Z",
            "Cruz" => $"M {-r * 0.35f} {-r * 1.1f} L {r * 0.35f} {-r * 1.1f} L {r * 0.35f} {-r * 0.35f} L {r * 1.1f} {-r * 0.35f} L {r * 1.1f} {r * 0.35f} L {r * 0.35f} {r * 0.35f} L {r * 0.35f} {r * 1.1f} L {-r * 0.35f} {r * 1.1f} L {-r * 0.35f} {r * 0.35f} L {-r * 1.1f} {r * 0.35f} L {-r * 1.1f} {-r * 0.35f} L {-r * 0.35f} {-r * 0.35f} Z",
            "X" => $"M 0 {-r * 0.5f} L {r * 0.5f} {-r} L {r} {-r * 0.5f} L {r * 0.5f} 0 L {r} {r * 0.5f} L {r * 0.5f} {r} L 0 {r * 0.5f} L {-r * 0.5f} {r} L {-r} {r * 0.5f} L {-r * 0.5f} 0 L {-r} {-r * 0.5f} L {-r * 0.5f} {-r} Z",
            "Estrela" => GerarEstrelaSvgPath(r * 1.15f, r * 0.48f),
            "Pentagono" => GerarPentagonoSvgPath(r * 1.1f),
            "Hexagono" => GerarHexagonoSvgPath(r * 1.1f),
            "MarcoINCRA" => $"M 0 {-r} A {r} {r} 0 1 1 0 {r} A {r} {r} 0 1 1 0 {-r} M {-r * 1.25f} 0 L {r * 1.25f} 0 M 0 {-r * 1.25f} L 0 {r * 1.25f}",
            "MarcoGeodesicoQGIS" => $"M 0 {-r} A {r} {r} 0 1 1 0 {r} A {r} {r} 0 1 1 0 {-r} M 0 0 L {r} 0 A {r} {r} 0 0 1 0 {r} Z M 0 0 L {-r} 0 A {r} {r} 0 0 1 0 {-r} Z M {-r * 1.3f} 0 L {r * 1.3f} 0 M 0 {-r * 1.3f} L 0 {r * 1.3f}",
            "EstacaoTopograficaQGIS" => $"M 0 {-r * 0.2f} L {-r * 0.8f} {r * 1.15f} M 0 {-r * 0.2f} L 0 {r * 1.15f} M 0 {-r * 0.2f} L {r * 0.8f} {r * 1.15f} M {-r * 0.35f} {-r * 0.65f} h {r * 0.7f} v {r * 0.45f} h {-r * 0.7f} Z M {-r * 0.75f} {-r * 0.85f} h {r * 1.5f}",
            "VerticeTriangulacaoQGIS" => $"M 0 {-r * 1.15f} L {r * 1.05f} {r * 0.85f} L {-r * 1.05f} {r * 0.85f} Z M 0 {-r * 0.1f} m {-r * 0.3f} 0 a {r * 0.3f} {r * 0.3f} 0 1 0 {r * 0.6f} 0 a {r * 0.3f} {r * 0.3f} 0 1 0 {-r * 0.6f} 0",
            "AlvoTarget" => $"M 0 {-r * 1.1f} A {r * 1.1f} {r * 1.1f} 0 1 1 0 {r * 1.1f} A {r * 1.1f} {r * 1.1f} 0 1 1 0 {-r * 1.1f} M 0 {-r * 0.45f} A {r * 0.45f} {r * 0.45f} 0 1 0 0 {r * 0.45f} A {r * 0.45f} {r * 0.45f} 0 1 0 0 {-r * 0.45f}",
            "PontoCotado" => $"M 0 {-r * 0.35f} A {r * 0.35f} {r * 0.35f} 0 1 1 0 {r * 0.35f} A {r * 0.35f} {r * 0.35f} 0 1 1 0 {-r * 0.35f} M {-r * 1.25f} 0 L {r * 1.25f} 0 M 0 {-r * 1.25f} L 0 {r * 1.25f}",
            "PinoLocalizacao" => $"M 0 {r * 1.15f} C {r * 0.7f} {r * 0.35f} {r * 0.85f} {-r * 0.2f} {r * 0.85f} {-r * 0.5f} A {r * 0.85f} {r * 0.85f} 0 0 0 {-r * 0.85f} {-r * 0.5f} C {-r * 0.85f} {-r * 0.2f} {-r * 0.7f} {r * 0.35f} 0 {r * 1.15f} Z",
            "TorreEnergiaQGIS" => $"M {-r * 0.75f} {r * 1.15f} L {-r * 0.25f} {-r * 0.6f} L 0 {-r * 1.15f} L {r * 0.25f} {-r * 0.6f} L {r * 0.75f} {r * 1.15f} M {-r * 0.95f} {-r * 0.55f} h {r * 1.9f} M {-r * 0.75f} {-r * 0.15f} h {r * 1.5f} M {-r * 0.5f} {r * 0.5f} L {r * 0.5f} {r * 0.1f} M {r * 0.5f} {r * 0.5f} L {-r * 0.5f} {r * 0.1f}",
            "PosteEnergiaQGIS" => $"M 0 {r * 1.15f} L 0 {-r * 1.05f} M {-r * 0.85f} {-r * 0.7f} L {r * 0.85f} {-r * 0.7f} M {-r * 0.85f} {-r * 0.7f} m {-r * 0.12f} 0 a {r * 0.12f} {r * 0.12f} 0 1 0 {r * 0.24f} 0 a {r * 0.12f} {r * 0.12f} 0 1 0 {-r * 0.24f} 0 M {r * 0.85f} {-r * 0.7f} m {-r * 0.12f} 0 a {r * 0.12f} {r * 0.12f} 0 1 0 {r * 0.24f} 0 a {r * 0.12f} {r * 0.12f} 0 1 0 {-r * 0.24f} 0",
            "PocoAguaQGIS" => $"M 0 {-r * 1.1f} A {r * 1.1f} {r * 1.1f} 0 1 1 0 {r * 1.1f} A {r * 1.1f} {r * 1.1f} 0 1 1 0 {-r * 1.1f} M 0 {-r * 0.75f} A {r * 0.75f} {r * 0.75f} 0 1 1 0 {r * 0.75f} A {r * 0.75f} {r * 0.75f} 0 1 1 0 {-r * 0.75f} M 0 {-r * 0.55f} C {r * 0.35f} {-r * 0.15f} {r * 0.45f} {r * 0.2f} {r * 0.45f} {r * 0.35f} A {r * 0.45f} {r * 0.45f} 0 0 1 {-r * 0.45f} {r * 0.35f} C {-r * 0.45f} {r * 0.2f} {-r * 0.35f} {-r * 0.15f} 0 {-r * 0.55f} Z",
            "ArvoreFlorestalQGIS" => $"M {-r * 0.15f} {r * 0.4f} h {r * 0.3f} v {r * 0.75f} h {-r * 0.3f} Z M 0 {-r * 0.4f} m {-r * 0.55f} 0 a {r * 0.55f} {r * 0.55f} 0 1 0 {r * 1.1f} 0 a {r * 0.55f} {r * 0.55f} 0 1 0 {-r * 1.1f} 0 M {-r * 0.45f} {r * 0.05f} m {-r * 0.45f} 0 a {r * 0.45f} {r * 0.45f} 0 1 0 {r * 0.9f} 0 a {r * 0.45f} {r * 0.45f} 0 1 0 {-r * 0.9f} 0 M {r * 0.45f} {r * 0.05f} m {-r * 0.45f} 0 a {r * 0.45f} {r * 0.45f} 0 1 0 {r * 0.9f} 0 a {r * 0.45f} {r * 0.45f} 0 1 0 {-r * 0.9f} 0",
            "EdificacaoSedeQGIS" => $"M {-r * 0.85f} {r * 0.05f} L 0 {-r * 0.95f} L {r * 0.85f} {r * 0.05f} L {r * 0.7f} {r * 0.05f} L {r * 0.7f} {r * 1.1f} L {-r * 0.7f} {r * 1.1f} L {-r * 0.7f} {r * 0.05f} Z M {-r * 0.2f} {r * 0.55f} h {r * 0.4f} v {r * 0.55f} h {-r * 0.4f} Z",
            _ => $"M 0 {-r} A {r} {r} 0 1 1 0 {r} A {r} {r} 0 1 1 0 {-r}"
        };
    }

    private static string GerarEstrelaSvgPath(float rExt, float rInt)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 10; i++)
        {
            double angle = -Math.PI / 2.0 + (i * Math.PI / 5.0);
            float r = (i % 2 == 0) ? rExt : rInt;
            float x = (float)(r * Math.Cos(angle));
            float y = (float)(r * Math.Sin(angle));
            if (i == 0) sb.Append(CultureInfo.InvariantCulture, $"M {x:F1} {y:F1}");
            else sb.Append(CultureInfo.InvariantCulture, $" L {x:F1} {y:F1}");
        }
        sb.Append(" Z");
        return sb.ToString();
    }

    private static string GerarPentagonoSvgPath(float r)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 5; i++)
        {
            double angle = -Math.PI / 2.0 + (i * 2.0 * Math.PI / 5.0);
            float x = (float)(r * Math.Cos(angle));
            float y = (float)(r * Math.Sin(angle));
            if (i == 0) sb.Append(CultureInfo.InvariantCulture, $"M {x:F1} {y:F1}");
            else sb.Append(CultureInfo.InvariantCulture, $" L {x:F1} {y:F1}");
        }
        sb.Append(" Z");
        return sb.ToString();
    }

    private static string GerarHexagonoSvgPath(float r)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 6; i++)
        {
            double angle = -Math.PI / 2.0 + (i * 2.0 * Math.PI / 6.0);
            float x = (float)(r * Math.Cos(angle));
            float y = (float)(r * Math.Sin(angle));
            if (i == 0) sb.Append(CultureInfo.InvariantCulture, $"M {x:F1} {y:F1}");
            else sb.Append(CultureInfo.InvariantCulture, $" L {x:F1} {y:F1}");
        }
        sb.Append(" Z");
        return sb.ToString();
    }

    private string ObterTextoFormatadoPreview()
    {
        string textoBase = string.IsNullOrEmpty(colunaRotuloSelecionada) ? "GeoNex Cartografia" : colunaRotuloSelecionada;
        return LabelPlacementEngine.FormatarTexto(textoBase, transformacaoTextoRotulo);
    }
}
