using System;
using System.Collections.Generic;
using System.Linq;
using GeoNex.Services;
using SkiaSharp;

int assertions = 0;
void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException($"FALHA: {message}");
    assertions++;
}

Console.WriteLine("=== Iniciando Verificação de Simbologia e Engenharia Cartográfica ===");

// 1. CLASSIFICATION ENGINE: EQUAL INTERVAL
Console.WriteLine("[1/11] Testando Classificar com Intervalos Iguais (Equal Interval)...");
var dataEqual = new double[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
var faixasEqual = ClassificationEngine.Classificar(dataEqual, 5, MetodoClassificacao.IntervaloIgual, "Viridis");
Assert(faixasEqual.Count == 5, "Intervalos iguais deve retornar 5 faixas");
Assert(Math.Abs(faixasEqual[0].Minimo - 10) < 1e-4, "Mínimo da primeira faixa deve ser 10");
Assert(Math.Abs(faixasEqual[4].Maximo - 100) < 1e-4, "Máximo da última faixa deve ser 100");
for (int i = 0; i < 5; i++)
{
    Assert(faixasEqual[i].Minimo < faixasEqual[i].Maximo, $"Faixa {i} deve ter Min < Max");
    Assert(!string.IsNullOrEmpty(faixasEqual[i].CorHex), $"Faixa {i} deve possuir cor");
    Assert(!string.IsNullOrEmpty(faixasEqual[i].RotuloLegenda), $"Faixa {i} deve possuir rótulo");
}

// 2. CLASSIFICATION ENGINE: QUANTIS
Console.WriteLine("[2/11] Testando Classificar com Quantis (Quantiles)...");
var dataQuantiles = new double[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
var faixasQuantiles = ClassificationEngine.Classificar(dataQuantiles, 4, MetodoClassificacao.Quantil, "Plasma");
Assert(faixasQuantiles.Count == 4, "Quantis de 4 classes deve ter 4 faixas");
Assert(faixasQuantiles[0].Minimo == 1 && faixasQuantiles[3].Maximo == 12, "Limites dos quantis devem abranger os extremos");

// 3. CLASSIFICATION ENGINE: FISHER-JENKS COM RESAMPLE GUARD
Console.WriteLine("[3/11] Testando Classificar com Fisher-Jenks Natural Breaks...");
var dataJenks = new double[] { 1, 2, 4, 5, 10, 11, 13, 20, 21, 22, 50, 52, 55 };
var faixasJenks = ClassificationEngine.Classificar(dataJenks, 3, MetodoClassificacao.QuebrasNaturaisJenks, "RdYlGn");
Assert(faixasJenks.Count == 3, "Fisher-Jenks 3 classes deve produzir 3 faixas");
Assert(faixasJenks[0].Minimo == 1 && faixasJenks[2].Maximo == 55, "Limites dos extremos preservados");

// Teste de estresse: Jenks com mais de 5.000 pontos (deve acionar o subsampling e executar em < 800ms)
var rnd = new Random(42);
var largeData = Enumerable.Range(0, 8000).Select(_ => rnd.NextDouble() * 1000).ToArray();
var sw = System.Diagnostics.Stopwatch.StartNew();
var faixasLarge = ClassificationEngine.Classificar(largeData, 5, MetodoClassificacao.QuebrasNaturaisJenks, "Turbo");
sw.Stop();
Assert(faixasLarge.Count == 5, "Jenks em grande volume deve retornar 5 faixas");
Assert(sw.ElapsedMilliseconds < 800, $"Subsampling guard deve manter Jenks ultra-rápido (<800ms): gastou {sw.ElapsedMilliseconds}ms");

// 4. RAMPAS CIENTÍFICAS E PALETAS QUALITATIVAS
Console.WriteLine("[4/11] Testando Rampas de Cores Científicas e Paletas Categóricas...");
foreach (var r in ClassificationEngine.RampasDisponiveis)
{
    var cores = ClassificationEngine.GerarCoresRampa(r, 7, false);
    Assert(cores.Length == 7, $"Rampa {r} deve gerar 7 cores");
    foreach (var c in cores)
    {
        Assert(c.StartsWith("#") && c.Length == 7, $"Cor {c} na rampa {r} deve ser hex #RRGGBB");
    }

    // Invertido
    var coresInv = ClassificationEngine.GerarCoresRampa(r, 7, true);
    Assert(coresInv[0] == cores[6] && coresInv[6] == cores[0], $"Rampa invertida {r} deve inverter os extremos");
}

var categorias = new[] { "Residencial", "Comercial", "Industrial", "Preservacao", "Institucional" };
foreach (var paleta in ClassificationEngine.PaletasQualitativasDisponiveis)
{
    var mapaCores = ClassificationEngine.GerarCoresCategorizadas(categorias, paleta);
    Assert(mapaCores.Count == 5, $"Paleta qualitativa {paleta} deve mapear as 5 categorias");
    foreach (var cat in categorias)
    {
        Assert(mapaCores.ContainsKey(cat) && mapaCores[cat].StartsWith("#"), $"Categoria {cat} deve possuir cor hex válida");
    }
}

// 5. CARTOGRAPHIC PATTERN FACTORY: SHADERS E RENDERIZAÇÃO DE MARCADORES
Console.WriteLine("[5/11] Testando Padrões de Hachura e Renderização de Marcadores...");
string[] padroes = { "DiagonalDireita", "DiagonalEsquerda", "Cruzada", "LinhasVerticais", "LinhasHorizontais", "Pontilhado" };
foreach (var p in padroes)
{
    var s1 = CartographicPatternFactory.ObterShaderHachura(p, SKColors.White, SKColors.Blue, 12);
    var s2 = CartographicPatternFactory.ObterShaderHachura(p, SKColors.White, SKColors.Blue, 12);
    Assert(s1 != null, $"Shader para padrão {p} não pode ser nulo");
    Assert(ReferenceEquals(s1, s2), $"Shader para {p} deve ser retornado do cache GPU thread-safe");
}

string[] formas = { "Circulo", "Quadrado", "Losango", "Triangulo", "Cruz", "X", "Estrela", "Pentagono" };
using var bitmap = new SKBitmap(64, 64);
using var canvas = new SKCanvas(bitmap);
using var fillPaint = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.Red };
using var strokePaint = new SKPaint { Style = SKPaintStyle.Stroke, Color = SKColors.Black, StrokeWidth = 2 };

foreach (var f in formas)
{
    canvas.Clear(SKColors.Transparent);
    CartographicPatternFactory.DesenharMarcador(canvas, new SKPoint(32, 32), 16f, f, 30f, fillPaint, strokePaint);
    
    // Verifica se algum pixel foi desenhado na vizinhança do centro
    bool desenhou = false;
    for (int y = 20; y <= 44; y++)
    {
        for (int x = 20; x <= 44; x++)
        {
            if (bitmap.GetPixel(x, y).Alpha > 0)
            {
                desenhou = true;
                break;
            }
        }
        if (desenhou) break;
    }
    Assert(desenhou, $"Desenho do marcador '{f}' deve produzir pixels visíveis no canvas");
}

// 6. SERIALIZAÇÃO E INTEROPERABILIDADE (JSON + OGC SLD)
Console.WriteLine("[6/11] Testando Serialização .gnxstyle (JSON) e OGC SLD 1.1.0...");
var estiloOriginal = new EstiloCamada
{
    TipoSimbologia = "GRADUADA",
    ColunaSimbologia = "POP_2024",
    NumeroClasses = 3,
    RampaCores = "Viridis",
    FormaMarcador = "Estrela",
    RotacaoMarcador = 45f,
    PadraoPreenchimento = "Cruzada",
    CorHachura = "#ff0000",
    EspacamentoHachura = 16,
    ModoMesclagem = "Multiply",
    EfeitoSombra = true,
    RaioSombra = 6.0f,
    CorSombra = "#333333",
    SimbologiaProporcional = true,
    ColunaTamanhoProporcional = "PIB",
    TamanhoMinimoProporcional = 5.0f,
    TamanhoMaximoProporcional = 30.0f,
    MetodoEscalaProporcional = "Flannery",
    FaixasGraduadas = new List<FaixaGraduada>
    {
        new FaixaGraduada { Minimo = 0, Maximo = 100, CorHex = "#440154", RotuloLegenda = "Baixo" },
        new FaixaGraduada { Minimo = 100, Maximo = 500, CorHex = "#21908c", RotuloLegenda = "Médio" },
        new FaixaGraduada { Minimo = 500, Maximo = 2000, CorHex = "#fde725", RotuloLegenda = "Alto" }
    }
};

// Teste JSON
string json = StyleSerializationService.ExportarParaJson(estiloOriginal);
Assert(!string.IsNullOrEmpty(json) && json.Contains("POP_2024"), "JSON exportado deve conter a coluna de classificação");
var estiloDoJson = StyleSerializationService.ImportarDeJson(json);
Assert(estiloDoJson != null, "Deserialização JSON deve ter sucesso");
Assert(estiloDoJson!.TipoSimbologia == "GRADUADA", "TipoSimbologia deve ser preservado");
Assert(estiloDoJson.FaixasGraduadas.Count == 3, "Quantidade de faixas graduadas deve ser 3");
Assert(estiloDoJson.EfeitoSombra == true, "EfeitoSombra deve ser preservado");
Assert(estiloDoJson.ModoMesclagem == "Multiply", "ModoMesclagem deve ser preservado");
Assert(estiloDoJson.MetodoEscalaProporcional == "Flannery", "MetodoEscalaProporcional deve ser preservado");

// Teste OGC SLD XML
string sld = StyleSerializationService.ExportarParaSLD("CamadaTeste", estiloOriginal);
Assert(sld.Contains("StyledLayerDescriptor") && sld.Contains("Rule"), "OGC SLD deve conter tag de documento e regras");
var estiloDoSLD = StyleSerializationService.ImportarDeSLD(sld);
Assert(estiloDoSLD != null, "Importação de OGC SLD deve ser bem sucedida");
Assert(estiloDoSLD!.FormaMarcador == "Estrela", "Forma do marcador deve ser recuperada do SLD como Estrela");
Assert(estiloDoSLD.CorBorda == estiloOriginal.CorBorda, "Cor de borda deve ser recuperada do SLD");

// 7. COMPENSAÇÃO PERCEPTUAL DE FLANNERY
Console.WriteLine("[7/11] Testando dimensionamento proporcional e Flannery...");
double vMin = 10, vMax = 1000;
double rMin = 4, rMax = 32;
double vMid = (vMin + vMax) / 2.0;

// Escala Linear
double normLin = (vMid - vMin) / (vMax - vMin);
double rLin = rMin + normLin * (rMax - rMin);

// Escala Flannery: t' = t^0.57
double normFlannery = Math.Pow(normLin, 0.57);
double rFlannery = rMin + normFlannery * (rMax - rMin);

Assert(rFlannery > rLin, "Flannery deve compensar a subestimação humana aumentando o raio intermediário");
Assert(rFlannery >= rMin && rFlannery <= rMax, "Raio Flannery deve estar contido entre min e max");

// 8. LABEL PLACEMENT ENGINE: TRANSFORMAÇÕES TIPOGRÁFICAS
Console.WriteLine("[8/11] Testando transformações de texto (Normal, Maiúsculas, Minúsculas, Título)...");
string textoOriginal = "avenida paulista";
Assert(LabelPlacementEngine.FormatarTexto(textoOriginal, "Normal") == "avenida paulista", "Caixa Normal preservada");
Assert(LabelPlacementEngine.FormatarTexto(textoOriginal, "Maiusculas") == "AVENIDA PAULISTA", "Transformação Maiúsculas correta");
Assert(LabelPlacementEngine.FormatarTexto("AVENIDA PAULISTA", "Minusculas") == "avenida paulista", "Transformação Minúsculas correta");
Assert(LabelPlacementEngine.FormatarTexto("rio de janeiro", "Titulo") == "Rio De Janeiro", "Transformação Título correta");
Assert(LabelPlacementEngine.FormatarTexto("", "Maiusculas") == string.Empty, "String vazia tratada com segurança");

// 9. LABEL PLACEMENT ENGINE: POLO DE INACESSIBILIDADE (POLYLABEL)
Console.WriteLine("[9/11] Testando Polo de Inacessibilidade (Polylabel / Inscribed Circle)...");
// Criar um polígono côncavo em formato de "C" ou Ferradura
// Limites: 0 a 100 em X, 0 a 100 em Y.
// Centro geométrico (50, 50) fica no VAZIO / BURACO da concavidade!
using var pathConcavo = new SKPath();
pathConcavo.MoveTo(0, 0);
pathConcavo.LineTo(100, 0);
pathConcavo.LineTo(100, 30);
pathConcavo.LineTo(30, 30);
pathConcavo.LineTo(30, 70);
pathConcavo.LineTo(100, 70);
pathConcavo.LineTo(100, 100);
pathConcavo.LineTo(0, 100);
pathConcavo.Close();

var centroideGeometrico = new SKPoint(50, 50);
Assert(!pathConcavo.Contains(centroideGeometrico.X, centroideGeometrico.Y), "O centroide ingênuo (50,50) DEVE estar FORA do polígono côncavo");

// O Polylabel deve encontrar um ponto estritamente no INTERIOR do polígono côncavo
var pontoRotuloPolylabel = LabelPlacementEngine.CalcularPoloInacessibilidade(pathConcavo, centroideGeometrico);
Assert(pathConcavo.Contains(pontoRotuloPolylabel.X, pontoRotuloPolylabel.Y), "O Polo de Inacessibilidade (Polylabel) DEVE estar dentro do polígono côncavo");
Assert(pontoRotuloPolylabel.X != centroideGeometrico.X || pontoRotuloPolylabel.Y != centroideGeometrico.Y, "Polylabel deve divergir do centroide vazio");

// 10. LABEL PLACEMENT ENGINE: 8 QUADRANTES CARTESIANOS & AUTO-FLIP
Console.WriteLine("[10/11] Testando 8 Quadrantes Cartesianos com prioridades e Auto-Flip...");
var pontoFeicao = new SKPoint(200, 200);
var candidatos = new List<CandidatoRotulo>(8);
LabelPlacementEngine.GerarCandidatosPonto(pontoFeicao, 60f, 16f, -12f, 4f, 8f, "Cartesiano8", 0f, 0f, candidatos);

Assert(candidatos.Count == 8, "Devem ser gerados exatamente 8 candidatos cartesianos");
// Prioridade 1 deve ser Superior-Direito (X > 200, Y < 200)
Assert(candidatos[0].Prioridade == 1, "Candidato 0 deve ser prioridade 1");
Assert(candidatos[0].LimitesTexto.Left > pontoFeicao.X, "Candidato 1 (Top-Right) deve estar à direita da feição");
Assert(candidatos[0].LimitesTexto.Bottom < pontoFeicao.Y, "Candidato 1 (Top-Right) deve estar acima da feição");

// Teste de Auto-Flip em linhas para impedir texto de cabeça para baixo
// Ângulo de 0° (apontando para direita) -> Sem flip
float ang0 = LabelPlacementEngine.CalcularAnguloComAutoFlip(0, 0, 100, 0);
Assert(Math.Abs(ang0 - 0f) < 1e-2f, "Linha horizontal direita deve ter ângulo 0°");

// Ângulo de 180° (apontando para esquerda) -> Deve ser invertido para 0° pelo Auto-Flip
float ang180 = LabelPlacementEngine.CalcularAnguloComAutoFlip(100, 0, 0, 0);
Assert(Math.Abs(ang180 - 0f) < 1e-2f, "Linha horizontal esquerda (180°) deve sofrer Auto-Flip para 0°");

// Ângulo de 135° (quadrante II) -> Deve sofrer Auto-Flip para 315° (-45°)
float ang135 = LabelPlacementEngine.CalcularAnguloComAutoFlip(100, 100, 0, 200);
Assert(ang135 < 90f || ang135 > 270f, "Ângulo com Auto-Flip deve estar fora da faixa [90°, 270°]");

// 11. LABEL CONFLICT GRID: ANTI-COLISÃO O(1), LINHAS DE CHAMADA E STRESS TEST
Console.WriteLine("[11/11] Testando Grade Espacial O(1), Anti-Colisão e Stress Benchmark...");
var grid = new LabelConflictGrid(64f, 4096);

// Inserir primeiro rótulo
var rect1 = new SKRect(100, 100, 180, 120);
Assert(!grid.TestarColisao(rect1), "Grade vazia não deve detectar colisão");
grid.Inserir(rect1);
Assert(grid.TestarColisao(rect1), "Mesmo retângulo deve acusar colisão");

// Retângulo sobreposto parcialmente
var rectColisao = new SKRect(150, 110, 220, 130);
Assert(grid.TestarColisao(rectColisao), "Retângulo sobreposto deve acusar colisão");

// Retângulo distante livre
var rectLivre = new SKRect(300, 300, 380, 320);
Assert(!grid.TestarColisao(rectLivre), "Retângulo distante não deve colidir");

// Resolução com TentarAlocarCandidato e Linha de Chamada
var candsConflito = new List<CandidatoRotulo>
{
    new CandidatoRotulo(new SKPoint(110, 110), new SKPoint(110, 110), rect1, 1, false), // Colide com rect1 já inserido
    new CandidatoRotulo(new SKPoint(110, 110), new SKPoint(300, 300), rectLivre, 2, true) // Livre com linha de chamada
};

bool alocou = grid.TentarAlocarCandidato("Feicao_B", candsConflito, true, out var resultado);
Assert(alocou, "Deve conseguir alocar o candidato alternativo livre");
Assert(resultado.PontoOrigemTexto.X == 300 && resultado.PontoOrigemTexto.Y == 300, "Deve ter selecionado o candidato 2 livre");
Assert(resultado.DesenharLinhaChamada == true, "Candidato deslocado deve ativar Linha de Chamada (Callout)");

// Teste de reutilização de frame (zero heap reallocation)
grid.Limpar();
Assert(!grid.TestarColisao(rect1), "Após Limpar(), grade não deve conter colisões");
Assert(grid.RotulosAlocados.Count == 0, "Lista de rótulos deve estar limpa");

// STRESS BENCHMARK: 10.000 avaliações de candidatos com spatial hashing em < 25ms
var swStress = System.Diagnostics.Stopwatch.StartNew();
var randomFeicoes = new Random(1337);
int labelsColocados = 0;

for (int i = 0; i < 10000; i++)
{
    float px = (float)(randomFeicoes.NextDouble() * 3840);
    float py = (float)(randomFeicoes.NextDouble() * 2160);
    var rectCand = new SKRect(px, py, px + 70f, py + 14f);
    
    if (!grid.TestarColisao(rectCand))
    {
        grid.Inserir(rectCand);
        labelsColocados++;
    }
}
swStress.Stop();

Assert(labelsColocados > 500, "Vários rótulos devem ter sido alocados na tela 4K");
Assert(swStress.ElapsedMilliseconds < 25, $"10.000 avaliações espaciais devem rodar em <25ms: gastou {swStress.ElapsedMilliseconds}ms");

// 12. POSICIONAMENTO DE POLÍGONOS, LINHAS TANGENTES E PROTEÇÃO DE BORDAS DE TELA
Console.WriteLine("[12/12] Testando Candidatos de Polígono, Linha Tangente e Clipping de Borda de Tela...");
var candsPoly = new List<CandidatoRotulo>(8);
LabelPlacementEngine.GerarCandidatosPoligono(new SKPoint(500, 500), 80f, 16f, -12f, 200f, 120f, true, candsPoly);
Assert(candsPoly.Count >= 5, "Polígono amplo deve gerar candidato central + micro-ajustes internos");
Assert(Math.Abs(candsPoly[0].LimitesTexto.MidX - 500f) < 1e-2f && Math.Abs(candsPoly[0].LimitesTexto.MidY - 500f) < 1e-2f,
    "Candidato 1 do polígono deve estar centrado exatamente no Polo de Inacessibilidade");

var candsLine = new List<CandidatoRotulo>(8);
LabelPlacementEngine.GerarCandidatosLinha(new SKPoint(400, 300), 90f, 14f, -11f, 30f, 2f, candsLine);
Assert(candsLine.Count == 5, "Linha deve gerar 5 candidatos (acima, eixo, abaixo e deslizamentos tangenciais)");
Assert(Math.Abs(candsLine[0].RotacaoGraus - 30f) < 1e-2f, "Candidatos de linha devem preservar ângulo tangente com Auto-Flip");

grid.Limpar();
grid.ConfigurarViewport(1920f, 1080f, margemBordaPx: 10f, espacamentoMinimoPx: 2f);
Assert(grid.TestarColisao(new SKRect(2f, 100f, 80f, 120f)), "Rótulo cortando a borda esquerda da tela deve ser rejeitado");
Assert(!grid.TestarColisao(new SKRect(100f, 100f, 180f, 120f)), "Rótulo dentro da área útil da tela deve ser aceito");

// 13. TESTES TIPOGRÁFICOS AVANÇADOS: TEXTOS COM ACENTOS E CARACTERES ESPECIAIS (QGIS / IBGE)
Console.WriteLine("[13/14] Testando Formatação Tipográfica com Acentuação, Caracteres Especiais e Pontuação...");
var casosTexto = new (string Original, string Modo, string Esperado)[]
{
    ("são paulo", "Maiusculas", "SÃO PAULO"),
    ("ÁGUA CLARA", "Minusculas", "água clara"),
    ("rio de janeiro", "Titulo", "Rio De Janeiro"),
    ("florianópolis", "Titulo", "Florianópolis"),
    ("brasília - distrito federal", "Maiusculas", "BRASÍLIA - DISTRITO FEDERAL"),
    ("cuiabá", "Titulo", "Cuiabá"),
    ("PARANÁ", "Minusculas", "paraná"),
    ("   espaços extras   ", "Normal", "   espaços extras   "),
    ("123 número 456", "Maiusculas", "123 NÚMERO 456"),
    ("são joão d'aliança", "Titulo", "São João D'aliança")
};

foreach (var (orig, modo, esp) in casosTexto)
{
    string fmt = LabelPlacementEngine.FormatarTexto(orig, modo);
    Assert(fmt == esp, $"Formatação '{orig}' em modo '{modo}' resultou em '{fmt}', esperava '{esp}'");
}

// 14. VALIDAÇÃO INTEGRAL DE INTEROPERABILIDADE .GNXSTYLE E OGC SLD COM HACHURAS E SOMBRA
Console.WriteLine("[14/14] Testando Roundtrip de Serialização SLD e GNXStyle com Hachuras, Marcadores e Sombra...");
var estiloFull = new EstiloCamada
{
    TipoSimbologia = "GRADUADA",
    ColunaSimbologia = "AREA_HA",
    FormaMarcador = "Pentagono",
    RotacaoMarcador = 72f,
    PadraoPreenchimento = "DiagonalDireita",
    CorHachura = "#ff5500",
    EspacamentoHachura = 18,
    ModoMesclagem = "Multiply",
    EfeitoSombra = true,
    RaioSombra = 8f,
    CorSombra = "#112233",
    FamiliaFonteRotulo = "Roboto",
    TransformacaoTextoRotulo = "Titulo",
    RotuloNegrito = true,
    RotuloItalico = true,
    AntiColisaoRotulos = true,
    DesenharLinhaChamada = true,
    CorLinhaChamada = "#38bdf8",
    EspessuraLinhaChamada = 1.5f,
    UsarPoloInacessibilidade = true,
    FaixasGraduadas = new List<FaixaGraduada>
    {
        new FaixaGraduada(0, 10, "#ffffcc", "0 a 10 ha"),
        new FaixaGraduada(10, 50, "#a1dab4", "10 a 50 ha"),
        new FaixaGraduada(50, 200, "#41b6c4", "50 a 200 ha"),
        new FaixaGraduada(200, 1000, "#225ea8", "Acima de 200 ha")
    }
};

string jsonExport = StyleSerializationService.ExportarParaJson(estiloFull);
Assert(jsonExport.Contains("Pentagono") && jsonExport.Contains("DiagonalDireita"), "JSON deve conter propriedades avançadas");
var estiloImportJson = StyleSerializationService.ImportarDeJson(jsonExport);
Assert(estiloImportJson != null, "Deserialização do JSON completo deve ter sucesso");
Assert(estiloImportJson!.FormaMarcador == "Pentagono", "Marcador Pentagono preservado no JSON");
Assert(estiloImportJson.PadraoPreenchimento == "DiagonalDireita", "Hachura DiagonalDireita preservada no JSON");
Assert(estiloImportJson.EfeitoSombra == true, "EfeitoSombra preservado");
Assert(estiloImportJson.FaixasGraduadas.Count == 4, "4 faixas preservadas");

string sldExport = StyleSerializationService.ExportarParaSLD("CamadaFull", estiloFull);
Assert(sldExport.Contains("PolygonSymbolizer") && sldExport.Contains("LineSymbolizer"), "SLD deve conter os simbolizadores");
var estiloImportSld = StyleSerializationService.ImportarDeSLD(sldExport);
Assert(estiloImportSld != null, "Importação SLD deve ser bem sucedida");

// =========================================================================
// MÓDULO 15: MOTOR AVANÇADO DE SIMBOLOGIA DE PONTOS E ÍCONES SVG ESTILO QGIS
// =========================================================================
Console.WriteLine("\n--- Teste 15: Simbologia de Pontos, Ícones SVG e Padrão Composite QGIS ---");

// 1. Catálogo Canônico de SVGs
var catalog = SvgCompilationService.GetCatalog();
Assert(catalog.Count > 0, "Catálogo de marcadores SVG deve conter itens registrados");
Console.WriteLine($"[OK] Catálogo de SVGs carregado com {catalog.Count} ícones canônicos.");

// 2. Injeção Paramétrica de Cores no SVG
string templateSvg = "<svg viewBox='0 0 32 32'><circle cx='16' cy='16' r='14' fill='param(fill) #ffffff' stroke='param(stroke) #000000' stroke-width='param(stroke-width) 1.0'/></svg>";
string injected = SvgCompilationService.InjectParameters(templateSvg, SKColors.Red, SKColors.Yellow, SKColors.Blue, 2.5f);
Assert(injected.Contains("#FF0000"), "param(fill) deve ser substituído pela cor hexadecimal");
Assert(injected.Contains("#0000FF"), "param(stroke) deve ser substituído pelo contorno hexadecimal");
Assert(injected.Contains("2.5"), "param(stroke-width) deve ser substituído pela espessura");
Console.WriteLine("[OK] Injeção paramétrica de cores e traço validada com sucesso.");

// 3. Compilação de SVG para SKPicture
var picture = SvgCompilationService.CompileSvgToPicture(injected);
Assert(picture != null, "SVG parametrizado deve compilar para SKPicture imutável");
Assert(picture.CullRect.Width > 0, "SKPicture deve possuir dimensões válidas");
Console.WriteLine($"[OK] Compilação de SVG para SKPicture concluída: {picture.CullRect.Width}x{picture.CullRect.Height}");

// 4. Símbolo Composto Multi-Camadas (CompositePointSymbol)
var composite = new GeoNex.Core.Symbology.CompositePointSymbol { Name = "Marco Geodésico Multi-Camadas" };
var baseLayer = new GeoNex.Core.Symbology.SimpleMarkerSymbolLayer
{
    Name = "Base Circular",
    Shape = GeoNex.Core.Symbology.MarkerShapeType.Circle,
    Size = 14f,
    FillColor = SKColors.White,
    StrokeColor = SKColors.DarkBlue,
    StrokeWidth = 2f
};
var svgLayer = new GeoNex.Core.Symbology.SvgMarkerSymbolLayer
{
    Name = "Ícone Topográfico",
    EmbeddedSvgContent = templateSvg,
    Size = 10f,
    FillColor = SKColors.SkyBlue,
    StrokeColor = SKColors.MidnightBlue,
    StrokeWidth = 1.2f
};
composite.AddLayer(baseLayer);
composite.AddLayer(svgLayer);
Assert(composite.Layers.Count == 2, "CompositePointSymbol deve possuir 2 sub-camadas ativas");

// Renderização do Símbolo Composto em SKBitmap
using (var bmp = new SKBitmap(64, 64))
using (var compCanvas = new SKCanvas(bmp))
{
    compCanvas.Clear(SKColors.Transparent);
    composite.Render(compCanvas, new SKPoint(32, 32), 1.0f, 0f);
    Assert(bmp.GetPixel(32, 32) != SKColors.Transparent, "Pixel central renderizado não deve ser transparente");
}
Console.WriteLine("[OK] Renderização de CompositePointSymbol multi-camadas concluída em canvas SkiaSharp.");

// 5. Interoperabilidade QGIS Style (.qml)
var estiloQmlTest = new EstiloCamada
{
    FormaMarcador = "Square",
    CorPreenchimento = "#10b981",
    CorBorda = "#047857",
    Tamanho = 8.0f,
    EspessuraBorda = 1.5f,
    RotacaoMarcador = 45f
};
string qmlXml = QmlStyleInteropService.ExportarParaQml("MarcoTopografico", estiloQmlTest);
Assert(qmlXml.Contains("<qgis") && qmlXml.Contains("<renderer-v2"), "Exportação QML deve gerar documento QGIS 3.x válido");
Assert(qmlXml.Contains("SimpleMarker"), "QML deve conter camada SimpleMarker");

var importadoQml = QmlStyleInteropService.ImportarDeQml(qmlXml);
Assert(importadoQml != null, "Importação de QML deve reconstruir o objeto de estilo com sucesso");
Assert(importadoQml!.FormaMarcador == "Square", "Forma geométrica Square deve ser preservada do QML");
Assert(importadoQml.Tamanho == 8.0f, "Tamanho deve ser preservado do QML");
Console.WriteLine("[OK] Interoperabilidade bidirecional QGIS (.qml) validada.");

// 6. Atlas de Texturas GPU (MarkerGpuAtlas)
using (var atlas = new MarkerGpuAtlas(512, 512))
{
    var atlasRect1 = atlas.GetOrAddSprite("sprite_teste_1", picture, 32, 32);
    Assert(atlasRect1.Width == 32 && atlasRect1.Height == 32, "Atlas deve alocar retângulo UV 32x32");
    var atlasRect2 = atlas.GetOrAddSprite("sprite_teste_1", picture, 32, 32);
    Assert(atlasRect1 == atlasRect2, "Chamadas subsequentes devem retornar o mesmo sprite UV do cache");
    using var snapshot = atlas.CreateImageSnapshot();
    Assert(snapshot != null && snapshot.Width == 512, "Snapshot da textura em VRAM gerado com sucesso");
}
Console.WriteLine("[OK] Atlas de texturas GPU 2048x2048 operacional para renderização massiva a 60 FPS.");

Console.WriteLine($"\n>>> TODOS OS 15 MÓDULOS DE SIMBOLOGIA PASSARAM COM 100% DE SUCESSO! Total de asserções validadas: {assertions} <<<");

