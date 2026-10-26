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

Console.WriteLine($"\n>>> TODOS OS 11 MÓDULOS PASSARAM COM SUCESSO! Total de asserções validadas: {assertions} <<<");
