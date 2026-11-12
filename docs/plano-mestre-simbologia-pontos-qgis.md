# PLANO MESTRE DE ENGENHARIA DE SOFTWARE: SISTEMA AVANÇADO DE SIMBOLOGIA CARTOGRÁFICA DE PONTOS E ÍCONES SVG ESTILO QGIS NO GEONEX

**Documento:** GEONEX-ENG-SYM-001  
**Versão:** 3.0.0-PRO  
**Status:** Aprovado para Implementação Técnica  
**Data:** 12 de Novembro de 2026  
**Autores:** Equipe de Arquitetura de Software e Engenharia Cartográfica GeoNex  
**Classificação:** Especificação de Engenharia e Design de Software  

---

## ÍNDICE GERAL

1. **Sumário Executivo e Objetivos Estratégicos**
   - 1.1 Contexto e Justificativa
   - 1.2 Visão Geral da Transformação do Subsistema de Pontos
   - 1.3 Metas de Desempenho e Requisitos de Interface
   - 1.4 Princípios de Design e Compatibilidade OGC / QGIS

2. **Diagnóstico da Implementação Atual do GeoNex vs. Padrão QGIS / ArcGIS Pro**
   - 2.1 Análise do Subsistema Atual (`EstiloCamada`, `CartographicPatternFactory`, `LocalMapServer`)
   - 2.2 Limitações Identificadas no Renderizador e no Modal de Propriedades
   - 2.3 Comparativo de Capacidades: GeoNex Atual vs. QGIS 3.34+ vs. ArcGIS Pro 3.2+
   - 2.4 Matriz de Lacunas Técnicas (Gap Analysis)

3. **Arquitetura do Modelo de Domínio e Estruturas de Dados**
   - 3.1 Hierarquia de Símbolos Vetoriais: O Padrão Composite Marker (Símbolo Composto Multi-camadas)
   - 3.2 Modelagem de Classes C# (`ISymbolLayer`, `MarkerSymbolLayer`, `SimpleMarker`, `SvgMarker`, `FontMarker`)
   - 3.3 Sistema de Sobrescrita Orientada a Dados (*Data-Defined Attribute Overrides*)
   - 3.4 Especificação do Esquema de Tipos e Propriedades
   - 3.5 Contratos de Imutabilidade e Thread-Safety

4. **Biblioteca Canônica de Ícones Cartográficos e SVGs Parametrizados**
   - 4.1 Padrão de SVG Parametrizado (Compatibilidade com Variáveis QGIS: `param(fill)`, `param(stroke)`)
   - 4.2 Catálogo de Topografia, Geodésia e Agrimensura (Padrão INCRA / SIGEF / IBGE)
   - 4.3 Catálogo de Infraestrutura Urbana, Energia, Saneamento e Telecomunicações
   - 4.4 Catálogo de Transporte, Trânsito, Ferrovias, Portos e Aeroportos
   - 4.5 Catálogo de Meio Ambiente, Geologia, Recursos Hídricos e Uso do Solo
   - 4.6 Catálogo de Equipamentos Comunitários, Saúde, Educação e Serviços (POI)
   - 4.7 Catálogo de Defesa Civil, Risco Geológico e Emergências

5. **Motor de Renderização Gráfica SkiaSharp de Alta Performance (`SkiaMarkerEngine`)**
   - 5.1 Pipeline de Renderização Vetorial em 60 FPS
   - 5.2 Parser e Compilador SVG Otimizado para Objetos SkiaSharp (`SKPath`, `SKPicture`)
   - 5.3 Sistema de Tintura Dinâmica e Modulação de Cores em Tempo Real
   - 5.4 Arquitetura do Atlas de Sprites em VRAM / Cache de Texturas GPU
   - 5.5 Algoritmo de Batching e Desenho com Zero Alocação (`DrawAtlas` / `DrawPoints`)
   - 5.6 Transformações Geométricas: Rotação Dinâmica, Escala e Âncoras Cartográficas

6. **Simbologia Orientada a Dados e Regras Avançadas (*Data-Driven Styling*)**
   - 6.1 Vínculo Direto de Atributos com Propriedades do Marcador (Tamanho, Rotação, Cor, Forma)
   - 6.2 Motor de Avaliação de Expressões Condicionais em Memória
   - 6.3 Simbologia Baseada em Regras (*Rule-Based Point Renderer*)
   - 6.4 Agrupamento e Renderização de Densidade (Clusterização Cartográfica / *Point Clusters*)

7. **Interface do Usuário (UI / UX) no Blazor WebAssembly / WebView2**
   - 7.1 Reestruturação do Modal de Simbologia de Camada (`LayerPropertiesModal.razor`)
   - 7.2 Galeria Visual Interativa de Ícones SVG com Paginação Virtualizada e Filtros
   - 7.3 Painel de Gerenciamento de Camadas de Marcador (Empilhamento Multi-nível)
   - 7.4 Editor de Parâmetros Avançados (Sliders de Precisão, Seletores Hex/Alpha, Ajuste de Âncora)
   - 7.5 Pré-visualização Dinâmica com Grade, Zoom e Modo Claro/Escuro
   - 7.6 Interface de Importação e Gerenciamento de SVGs Customizados pelo Usuário

8. **Interoperabilidade, Serialização e Padrões Abertos**
   - 8.1 Extensão do Formato de Projeto Nativo GeoNex (`.gnx` / SQLite / JSON)
   - 8.2 Parser e Serializador de Estilos QGIS (`.qml` XML)
   - 8.3 Compatibilidade com Padrão OGC Styled Layer Descriptor (SLD 1.1.0 / Symbology Encoding)
   - 8.4 Exportação de Pacotes de Estilos Compartilháveis (`.gnxstyle`)

9. **Desempenho, Gerenciamento de Memória e Concorrência**
   - 9.1 Estratégia de Caching Hierárquico L1 (CPU), L2 (GPU) e L3 (Disco)
   - 9.2 Integração com R-Tree Spatial Index e Spatial Culling de Viewport
   - 9.3 Perfilamento de Memória, Redução de GC Pressures e Alocações `Span<T>` / `Memory<T>`
   - 9.4 Benchmarks Alvo: 50.000 a 500.000 Feições Pontuais com Renderização Instantânea

10. **Plano de Execução Técnica Passo a Passo, Roteiro de Testes e Riscos**
    - 10.1 Fases e Sprints de Implementação
    - 10.2 Matriz de Rastreabilidade e Tarefas Detalhadas
    - 10.3 Bateria de Testes Unitários, de Integração e de Regressão Visual
    - 10.4 Análise de Riscos Técnicos e Estratégias de Mitigação
    - 10.5 Critérios Rigorosos de Aceite Final

---

# SEÇÃO 1: SUMÁRIO EXECUTIVO E OBJETIVOS ESTRATÉGICOS

### 1.1 Contexto e Justificativa

Em aplicações de Sistemas de Informação Geográfica (SIG / GIS) profissionais, a representação de dados pontuais vai muito além de círculos e quadrados geométricos coloridos. Profissionais de topografia, agrimensura, planejamento urbano, engenharia de redes, meio ambiente e mineração dependem de convenções cartográficas estritas e ricas para a tomada de decisões imediatas.

O **QGIS** e o **ArcGIS Pro** estabeleceram o padrão de ouro da indústria ao fornecerem:
1. Uma vasta biblioteca de marcadores baseados em vetores escaláveis (**SVG** - *Scalable Vector Graphics*).
2. A capacidade de customizar cor de preenchimento (*fill*), cor de borda (*stroke*) e espessura do traço diretamente nos arquivos SVG através de parâmetros dinâmicos.
3. Composição em múltiplas camadas de símbolos (*symbol layers*), permitindo que um único ponto geográfico seja desenhado combinando uma base geométrica (ex: escudo circular branco), uma moldura de destaque e um glifo central representativo (ex: cruz médica, torre de transmissão ou marco geodésico).
4. Controle orientado a atributos (*Data-Defined Overrides*), onde atributos de banco de dados (ex: azimute da antena, vazão do poço, tipo de poste) modulam dinamicamente a rotação, escala, cor ou o próprio ícone da feição.

Atualmente, o GeoNex possui uma base funcional de marcadores geométricos rígidos implementada em `CartographicPatternFactory.cs` (com cerca de 22 formas pré-compiladas) e alguns presets estáticos em `Home.Symbology.cs`. Embora esses marcadores ofereçam renderização rápida via SkiaSharp, eles são conceitualmente limitados: não permitem a importação livre de SVGs, não suportam a sobreposição de múltiplas camadas de símbolos por ponto, não possuem catalogação temática intuitiva e carecem da flexibilidade exigida por projetos de engenharia complexos e mapeamentos temáticos de alta fidelidade visual.

Este Plano Mestre define a arquitetura, a modelagem técnica de domínio, o motor gráfico acelerado por GPU e a experiência de usuário necessária para implantar no GeoNex um subsistema de simbologia de pontos totalmente compatível com a riqueza visual e operacional do QGIS.

### 1.2 Visão Geral da Transformação do Subsistema de Pontos

A modernização do sistema de marcadores pontuais do GeoNex será guiada por cinco pilares arquiteturais fundamentais:

```
+--------------------------------------------------------------------------------------------------+
|                                ARQUITETURA DO SISTEMA DE SIMBOLOGIA                              |
+--------------------------------------------------------------------------------------------------+
                                                 |
         +---------------------------------------+---------------------------------------+
         |                                       |                                       |
+------------------+                   +--------------------+                  +--------------------+
|  MODELO DOMÍNIO  |                   |    MOTOR GRÁFICO   |                  |    EXPERIÊNCIA     |
|   (ESTILO QGIS)  |                   |     (SKIASHARP)    |                  |    DO USUÁRIO      |
+------------------+                   +--------------------+                  +--------------------+
| * CompositeLayer |                   | * SVGLoader/Parser |                  | * Catálogo Visual  |
| * SimpleMarker   |                   | * Dynamic Tinting  |                  | * Busca em Tempo   |
| * SvgMarker      |                   | * SKPicture Cache  |                  |   Real             |
| * FontMarker     |                   | * GPU Sprite Atlas |                  | * Multi-Camadas    |
| * DataOverrides  |                   | * Batch DrawAtlas  |                  | * Live Preview     |
+------------------+                   +--------------------+                  +--------------------+
         |                                       |                                       |
         +---------------------------------------+---------------------------------------+
                                                 |
                               +-----------------------------------+
                               | INTEROPERABILIDADE E PERSISTÊNCIA |
                               +-----------------------------------+
                               | * Exportação/Importação QML (QGIS)|
                               | * Suporte OGC SLD 1.1.0 / SE      |
                               | * Persistência Nativa .GNX        |
                               | * Pacotes de Estilos Portáveis    |
                               +-----------------------------------+
```

### 1.3 Metas de Desempenho e Requisitos de Interface

1. **Taxa de Quadros (Frame Rate):** Manter renderização sustentada a **60 FPS** durante operações contínuas de Pan e Zoom dinâmico com até **100.000 pontos com marcadores SVG complexos** visíveis no viewport.
2. **Tempo de Rasterização Inicial:** Carga, decodificação e compilação de qualquer glifo SVG para representação em memória intermediária em tempo inferior a **1.2 milissegundo**.
3. **Consumo de Memória:** Ocupação máxima de VRAM/RAM do subsistema de cache de sprites de marcadores limitada a **128 MB**, utilizando algoritmos de descarte adaptativo LRU (*Least Recently Used*).
4. **Fidelidade Visual:** Renderização nítida sub-pixel com antialiasing analítico de alta qualidade em displays normais e telas HiDPI / 4K (DPI scaling de 100% a 300%).
5. **Ergonomia e Produtividade:** Seleção de ícones em modal responsivo com categorização instantânea (Topografia, Infraestrutura, Transporte, Meio Ambiente, Serviços, Geometria), pesquisa fonética/textual instantânea e pré-visualização ao vivo com reflexo imediato no mapa.

### 1.4 Princípios de Design e Compatibilidade OGC / QGIS

- **Padronização OGC:** Alinhamento com a especificação *OGC Symbology Encoding (SE)* e *Styled Layer Descriptor (SLD)* versão 1.1.0, garantindo que os símbolos possam ser exportados e lidos por servidores de mapas (GeoServer, MapServer) e clientes GIS desktop.
- **Isomorfismo QGIS:** Cada símbolo SVG canônico distribuído com o GeoNex utilizará a convenção de parâmetros estabelecida pela comunidade QGIS:
  ```xml
  <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">
    <path fill="param(fill) #38bdf8"
          stroke="param(stroke) #0284c7"
          stroke-width="param(stroke-width) 1.5"
          fill-opacity="param(fill-opacity) 1.0"
          stroke-opacity="param(stroke-opacity) 1.0"
          d="..." />
  </svg>
  ```
- **Separação de Preocupações:** O modelo de estilo é puramente declarativo e desacoplado do pipeline de execução gráfica, permitindo que a mesma descrição de estilo seja renderizada na tela via SkiaSharp, gravada em arquivos de projeto SQLite ou impressa em pranchas cartográficas PDF vetoriais em alta resolução.

---

# SEÇÃO 2: DIAGNÓSTICO DA IMPLEMENTAÇÃO ATUAL DO GEONEX VS. PADRÃO QGIS / ARCGIS PRO

### 2.1 Análise do Subsistema Atual

No código atual do GeoNex, a renderização de feições pontuais está concentrada nas seguintes estruturas:

1. **Classe `EstiloCamada.cs`:**
   Possui uma única propriedade string para formato:
   ```csharp
   public string FormaMarcador { get; set; } = "Circulo";
   public float RotacaoMarcador { get; set; } = 0f;
   public float Tamanho { get; set; } = 4.0f;
   public string CorPreenchimento { get; set; } = "#38bdf8";
   public string CorBorda { get; set; } = "#0ea5e9";
   public float EspessuraBorda { get; set; } = 1.0f;
   ```
   **Limitação Crítica:** Uma camada só pode ter uma única forma geométrica. Não há conceito de camadas de símbolos (*symbol layers*) aninhadas, impossibilitando a criação de símbolos compostos (como um círculo com borda dupla, ou uma forma geométrica externa com um ícone interno de outra cor).

2. **Classe `CartographicPatternFactory.cs`:**
   Mantém um conjunto estático de objetos `SKPath` codificados diretamente em C# (`_pathQuadrado`, `_pathLosango`, `_pathMarcoIncra`, etc.) e um dicionário estático de sprites rasterizados:
   ```csharp
   private static readonly ConcurrentDictionary<MarkerSpriteKey, MarkerSpriteEntry> _markerSpriteCache = new();
   ```
   **Limitação Crítica:** Para adicionar um novo ícone, o desenvolvedor é forçado a escrever comandos matemáticos `MoveTo`, `LineTo`, `CubicTo` em código C# compilado. Não há interpretador de arquivos `.svg` em tempo de execução nem pasta de recursos dinâmicos.

3. **Arquivo de Interface `Home.razor` e `Home.Symbology.cs`:**
   A seleção de símbolos pontuais exibe uma grade estática de botões com 22 formas pré-determinadas, sem mecanismo de busca por palavras-chave, sem suporte a importação de arquivos SVG pelo usuário e sem customização independente de camadas de desenho.

4. **Motor de Renderização `LocalMapServer.cs`:**
   O pipeline percorre as feições e chama `canvas.DrawImage(fixedSprite, screenPoint.X - fixedHalf, screenPoint.Y - fixedHalf)`. Quando o tamanho é proporcional (`usarTamanhoProp == true`), ele descarta o sprite e desenha por primitiva, gerando sobrecarga de cálculo trigonométrico por vértice em cada frame.

### 2.2 Limitações Identificadas no Renderizador e no Modal de Propriedades

| Componente | Comportamento Atual | Comportamento Requerido (Padrão QGIS) | Impacto no Usuário |
|---|---|---|---|
| **Formas Disponíveis** | 22 formas geométricas fixas em C#. | Mais de 150 ícones SVG categorizados + importação livre de `.svg` pelo usuário. | Severo: Impossibilita mapas cadastrais, ambientais ou urbanos padronizados. |
| **Composição** | Apenas 1 nível de desenho por ponto. | Múltiplas camadas de marcadores sobrepostos (ex: Moldura + Fundo + Ícone + Seta). | Severo: Símbolos profissionais exigem contraste de fundo e contornos múltiplos. |
| **Customização SVG** | Não suporta leitura de SVG externo. | Leitura de SVG padrão com injeção de parâmetros dinâmicos de cor e contorno. | Alto: Usuário não consegue utilizar as bibliotecas SVG padrão de órgãos como INCRA, DNIT ou Marinha. |
| **Data-Defined Overrides**| Apenas tamanho proporcional linear por campo numérico. | Modulação de rotação, cor, tamanho e ícone por qualquer campo da tabela ou expressão. | Alto: Impossibilita representar direção de vento, azimute de postes ou status de vistorias. |
| **UI de Seleção** | Grade simples de 22 botões estáticos. | Modal moderno com abas, busca textual em tempo real, visualização ampliada e favoritos. | Médio/Alto: Difícil navegação e falta de intuitividade para catálogos extensos. |

### 2.3 Comparativo de Capacidades: GeoNex vs. Concorrentes

```
Recurso / Capacidade                  GeoNex Atual      QGIS 3.34 LTR     ArcGIS Pro 3.2    GeoNex Pro (Alvo)
----------------------------------------------------------------------------------------------------------------
Marcadores Vetoriais Simples          Sim (22 formas)   Sim (34 formas)   Sim (40+ formas)  Sim (40+ formas)
Marcadores SVG Parametrizados         Não               Sim (Milhares)    Sim (CIM)         Sim (Completo)
Camadas de Marcadores Múltiplas       Não               Sim (Ilimitado)   Sim (Ilimitado)   Sim (Ilimitado)
Importação de SVG pelo Usuário        Não               Sim (.svg)        Sim (.svg, .emf)  Sim (.svg nativo)
Biblioteca Especializada Topografia   Parcial (Básica)  Via plugins/SVGs  Estilos Militares Nativa Integrada
Data-Defined Override por Atributo    Apenas Tamanho    Tamanho/Cor/Rot/Forma Todos atributos Todos atributos
Cache de Textura GPU (VRAM)           Sim (Básico)      Parcial (CPU Qt)  Sim (DirectX)     Sim (Skia Vulkan/GL)
Exportação/Importação QML             Não               Nativo (.qml)     Não               Suporte Completo
Suporte a Fontes Glifos (TrueType)    Não               Sim (FontMarker)  Sim (GlyphMarker) Sim (FontMarker)
```

### 2.4 Matriz de Lacunas Técnicas (Gap Analysis)

1. **Gap de Estrutura de Classes:** Ausência de uma interface unificada `IMarkerSymbol` capaz de abstrair se o marcador é um polígono simples, um arquivo SVG decodificado, um caractere de fonte TrueType ou um agrupamento de múltiplos sub-símbolos.
2. **Gap de Processamento SVG:** Ausência de um motor leve e de alto desempenho para transformar nós XML de SVG (`<path>`, `<circle>`, `<rect>`, `<polygon>`) em primitivas `SKPath` do SkiaSharp sem dependências pesadas de bibliotecas externas não gerenciadas.
3. **Gap de Parametrização:** Falta de um substituidor eficiente de tokens de cores nos arquivos SVG para habilitar a alteração em tempo de execução das cores de preenchimento e contorno sem recompilar os assets.
4. **Gap de Interface:** O componente Blazor de simbologia atual (`Home.razor`) mistura lógica de renderização com formulários HTML em mais de 1000 linhas monolíticas, necessitando de uma componentização modular e limpa para gerenciar símbolos complexos.

---

# SEÇÃO 3: ARQUITETURA DO MODELO DE DOMÍNIO E ESTRUTURAS DE DADOS

### 3.1 Hierarquia de Símbolos Vetoriais: O Padrão Composite Marker

Para suportar o mesmo nível de expressividade do QGIS, o GeoNex adotará o padrão de projeto *Composite Pattern*. Um símbolo pontual (`PointSymbol`) deixará de ser uma entidade monolítica e passará a ser composto por uma coleção ordenada de **Camadas de Marcador** (`MarkerSymbolLayer`).

```
                              +--------------------+
                              |    PointSymbol     |
                              +--------------------+
                              | - Opacidade: float |
                              | - Ativo: bool      |
                              +--------------------+
                                        | 1
                                        | possui
                                        | *
                           +--------------------------+
                           |    IMarkerSymbolLayer    | <<interface>>
                           +--------------------------+
                           | + Render(...)            |
                           | + Clone()                |
                           | + ObterEnvelope()        |
                           +--------------------------+
                                        ^
                                        | implementa
           +----------------------------+----------------------------+
           |                            |                            |
+---------------------+      +---------------------+      +---------------------+
|  SimpleMarkerLayer  |      |   SvgMarkerLayer    |      |   FontMarkerLayer   |
+---------------------+      +---------------------+      +---------------------+
| - Forma: MarkerShape|      | - SvgPath: string   |      | - FontFamily: string|
| - CorFill: SKColor  |      | - SvgSource: string |      | - Character: char   |
| - CorStroke: SKColor|      | - CorFill: SKColor  |      | - CorText: SKColor  |
| - StrokeWidth: float|      | - CorStroke: SKColor|      | - HaloWidth: float  |
| - Tamanho: float    |      | - Tamanho: float    |      | - Tamanho: float    |
| - Rotacao: float    |      | - Rotacao: float    |      | - Rotacao: float    |
| - Offset: SKPoint   |      | - Offset: SKPoint   |      | - Offset: SKPoint   |
+---------------------+      +---------------------+      +---------------------+
```

### 3.2 Modelagem de Classes C#

Abaixo está a especificação técnica detalhada das classes de domínio que serão implementadas no namespace `GeoNex.Core.Symbology`:

```csharp
namespace GeoNex.Core.Symbology
{
    using System;
    using System.Collections.Generic;
    using System.Text.Json.Serialization;
    using SkiaSharp;

    /// <summary>
    /// Tipo discriminador da camada de marcador para serialização polimórfica JSON/QML.
    /// </summary>
    public enum MarkerLayerType
    {
        Simple,
        Svg,
        Font
    }

    /// <summary>
    /// Formas canônicas para marcadores geométricos simples.
    /// </summary>
    public enum SimpleMarkerShape
    {
        Circle,
        Square,
        Diamond,
        Triangle,
        TriangleInverted,
        Cross,
        CrossRotatedX,
        Star,
        Pentagon,
        Hexagon,
        Octagon,
        SemiCircle,
        ThirdArc,
        QuarterArc,
        LineHorizontal,
        LineVertical
    }

    /// <summary>
    /// Ponto de ancoragem cartográfica do símbolo em relação à coordenada geográfica.
    /// </summary>
    public enum MarkerAnchorPoint
    {
        Center,
        Top,
        Bottom,
        Left,
        Right,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    /// <summary>
    /// Interface base para qualquer camada componente de um símbolo pontual.
    /// </summary>
    public interface IMarkerSymbolLayer : ICloneable
    {
        Guid Id { get; }
        string Name { get; set; }
        MarkerLayerType LayerType { get; }
        bool Enabled { get; set; }
        bool Locked { get; set; }
        float Size { get; set; }
        float RotationAngle { get; set; }
        SKPoint Offset { get; set; }
        MarkerAnchorPoint AnchorPoint { get; set; }
        float Opacity { get; set; }

        // Sobrescritas orientadas a dados
        DataDefinedPropertyCollection DataDefinedProperties { get; }

        /// <summary>
        /// Renderiza esta camada específica no canvas gráfico do SkiaSharp.
        /// </summary>
        void Render(SKCanvas canvas, SKPoint screenPos, float scaleFactor, float dynamicRotation = 0f);

        /// <summary>
        /// Retorna os limites retangulares da camada renderizada para culling espacial.
        /// </summary>
        SKRect CalculateBounds(SKPoint screenPos, float scaleFactor);
    }

    /// <summary>
    /// Implementação de Marcador Geométrico Simples de Alta Velocidade.
    /// </summary>
    public sealed class SimpleMarkerSymbolLayer : IMarkerSymbolLayer
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string Name { get; set; } = "Marcador Simples";
        public MarkerLayerType LayerType => MarkerLayerType.Simple;
        public bool Enabled { get; set; } = true;
        public bool Locked { get; set; } = false;

        public SimpleMarkerShape Shape { get; set; } = SimpleMarkerShape.Circle;
        public string FillColorHex { get; set; } = "#38bdf8";
        public string StrokeColorHex { get; set; } = "#0284c7";
        public float StrokeWidth { get; set; } = 1.2f;
        public float Size { get; set; } = 8.0f;
        public float RotationAngle { get; set; } = 0f;
        public SKPoint Offset { get; set; } = SKPoint.Empty;
        public MarkerAnchorPoint AnchorPoint { get; set; } = MarkerAnchorPoint.Center;
        public float Opacity { get; set; } = 1.0f;

        public DataDefinedPropertyCollection DataDefinedProperties { get; } = new();

        public void Render(SKCanvas canvas, SKPoint screenPos, float scaleFactor, float dynamicRotation = 0f)
        {
            if (!Enabled || Opacity <= 0f) return;
            // Executado diretamente pelo SkiaMarkerEngine com zero alocação de heap
            SkiaMarkerEngine.DrawSimpleMarker(canvas, this, screenPos, scaleFactor, dynamicRotation);
        }

        public SKRect CalculateBounds(SKPoint screenPos, float scaleFactor)
        {
            float halfSize = (Size * scaleFactor * 0.5f) + (StrokeWidth * scaleFactor);
            return new SKRect(
                screenPos.X - halfSize + Offset.X,
                screenPos.Y - halfSize + Offset.Y,
                screenPos.X + halfSize + Offset.X,
                screenPos.Y + halfSize + Offset.Y);
        }

        public object Clone() => this.MemberwiseClone();
    }

    /// <summary>
    /// Implementação de Marcador Vetorial baseado em SVG Parametrizado (Padrão QGIS).
    /// </summary>
    public sealed class SvgMarkerSymbolLayer : IMarkerSymbolLayer
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string Name { get; set; } = "Marcador SVG";
        public MarkerLayerType LayerType => MarkerLayerType.Svg;
        public bool Enabled { get; set; } = true;
        public bool Locked { get; set; } = false;

        /// <summary>
        /// Caminho relativo ou absoluto do arquivo .svg, ou identificador da biblioteca interna (ex: "geonex://topografia/marco_incra.svg")
        /// </summary>
        public string SvgSourcePath { get; set; } = "geonex://topografia/marco_incra.svg";

        /// <summary>
        /// Conteúdo XML do SVG embutido diretamente no projeto (quando o projeto for empacotado como portátil)
        /// </summary>
        public string? EmbeddedSvgContent { get; set; }

        public string FillColorHex { get; set; } = "#0ea5e9";
        public string StrokeColorHex { get; set; } = "#0284c7";
        public float StrokeWidth { get; set; } = 1.0f;
        public float Size { get; set; } = 12.0f;
        public float RotationAngle { get; set; } = 0f;
        public SKPoint Offset { get; set; } = SKPoint.Empty;
        public MarkerAnchorPoint AnchorPoint { get; set; } = MarkerAnchorPoint.Center;
        public float Opacity { get; set; } = 1.0f;

        public DataDefinedPropertyCollection DataDefinedProperties { get; } = new();

        public void Render(SKCanvas canvas, SKPoint screenPos, float scaleFactor, float dynamicRotation = 0f)
        {
            if (!Enabled || Opacity <= 0f) return;
            SkiaMarkerEngine.DrawSvgMarker(canvas, this, screenPos, scaleFactor, dynamicRotation);
        }

        public SKRect CalculateBounds(SKPoint screenPos, float scaleFactor)
        {
            float halfSize = (Size * scaleFactor * 0.5f) + (StrokeWidth * scaleFactor);
            return new SKRect(
                screenPos.X - halfSize + Offset.X,
                screenPos.Y - halfSize + Offset.Y,
                screenPos.X + halfSize + Offset.X,
                screenPos.Y + halfSize + Offset.Y);
        }

        public object Clone() => this.MemberwiseClone();
    }

    /// <summary>
    /// Implementação de Marcador Baseado em Caractere de Fonte (TrueType / OpenType).
    /// </summary>
    public sealed class FontMarkerSymbolLayer : IMarkerSymbolLayer
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string Name { get; set; } = "Marcador de Fonte";
        public MarkerLayerType LayerType => MarkerLayerType.Font;
        public bool Enabled { get; set; } = true;
        public bool Locked { get; set; } = false;

        public string FontFamily { get; set; } = "Segoe UI Symbol";
        public string CharacterCode { get; set; } = "\u2605"; // Estrela
        public string TextColorHex { get; set; } = "#f59e0b";
        public string StrokeColorHex { get; set; } = "#000000";
        public float StrokeWidth { get; set; } = 0.5f;
        public float Size { get; set; } = 12.0f;
        public float RotationAngle { get; set; } = 0f;
        public SKPoint Offset { get; set; } = SKPoint.Empty;
        public MarkerAnchorPoint AnchorPoint { get; set; } = MarkerAnchorPoint.Center;
        public float Opacity { get; set; } = 1.0f;

        public DataDefinedPropertyCollection DataDefinedProperties { get; } = new();

        public void Render(SKCanvas canvas, SKPoint screenPos, float scaleFactor, float dynamicRotation = 0f)
        {
            if (!Enabled || Opacity <= 0f) return;
            SkiaMarkerEngine.DrawFontMarker(canvas, this, screenPos, scaleFactor, dynamicRotation);
        }

        public SKRect CalculateBounds(SKPoint screenPos, float scaleFactor)
        {
            float halfSize = (Size * scaleFactor * 0.5f);
            return new SKRect(
                screenPos.X - halfSize + Offset.X,
                screenPos.Y - halfSize + Offset.Y,
                screenPos.X + halfSize + Offset.X,
                screenPos.Y + halfSize + Offset.Y);
        }

        public object Clone() => this.MemberwiseClone();
    }

    /// <summary>
    /// Símbolo Pontual Composto completo, contendo uma ou mais camadas empilhadas.
    /// </summary>
    public sealed class CompositePointSymbol : ICloneable
    {
        public Guid Id { get; init; } = Guid.NewGuid();
        public string Name { get; set; } = "Símbolo Composto";
        public float OverallOpacity { get; set; } = 1.0f;
        public List<IMarkerSymbolLayer> Layers { get; } = new();

        public void AddLayer(IMarkerSymbolLayer layer) => Layers.Add(layer);
        public void RemoveLayer(Guid id) => Layers.RemoveAll(l => l.Id == id);

        public void Render(SKCanvas canvas, SKPoint screenPos, float scaleFactor, float dynamicRotation = 0f)
        {
            if (OverallOpacity <= 0f || Layers.Count == 0) return;

            // Renderiza de baixo para cima (camada 0 é a base, camada N é o topo)
            for (int i = 0; i < Layers.Count; i++)
            {
                var layer = Layers[i];
                if (layer.Enabled)
                {
                    layer.Render(canvas, screenPos, scaleFactor, dynamicRotation);
                }
            }
        }

        public object Clone()
        {
            var clone = new CompositePointSymbol
            {
                Id = Guid.NewGuid(),
                Name = this.Name,
                OverallOpacity = this.OverallOpacity
            };
            foreach (var layer in Layers)
            {
                clone.AddLayer((IMarkerSymbolLayer)layer.Clone());
            }
            return clone;
        }
    }
}
```

### 3.3 Sistema de Sobrescrita Orientada a Dados (*Data-Defined Overrides*)

No QGIS, qualquer propriedade de um marcador (tamanho, ângulo, cor, opacidade) pode ser controlada por um campo da tabela ou por uma expressão matemática. 

A classe `DataDefinedPropertyCollection` encapsula esse comportamento com suporte à compilação em delegates C# de alta performance:

```csharp
namespace GeoNex.Core.Symbology
{
    public enum DataDefinedPropertyType
    {
        Size,
        Rotation,
        FillColor,
        StrokeColor,
        StrokeWidth,
        Opacity,
        SvgSource,
        Character
    }

    public sealed class DataDefinedProperty
    {
        public DataDefinedPropertyType PropertyType { get; set; }
        public bool IsActive { get; set; } = false;
        public string FieldName { get; set; } = string.Empty;
        public string Expression { get; set; } = string.Empty;
        
        // Mapeamento de valores / Escalas
        public bool UseTransformation { get; set; } = false;
        public double InputMin { get; set; } = 0.0;
        public double InputMax { get; set; } = 100.0;
        public double OutputMin { get; set; } = 4.0;
        public double OutputMax { get; set; } = 24.0;

        public float EvaluateFloat(object? attributeValue, float defaultValue)
        {
            if (!IsActive || attributeValue == null) return defaultValue;
            if (double.TryParse(attributeValue.ToString(), out double rawVal))
            {
                if (!UseTransformation) return (float)rawVal;
                
                // Mapeamento linear seguro
                if (Math.Abs(InputMax - InputMin) < 0.00001) return (float)OutputMin;
                double clamped = Math.Clamp(rawVal, InputMin, InputMax);
                double norm = (clamped - InputMin) / (InputMax - InputMin);
                return (float)(OutputMin + norm * (OutputMax - OutputMin));
            }
            return defaultValue;
        }

        public string EvaluateColorHex(object? attributeValue, string defaultHex)
        {
            if (!IsActive || attributeValue == null) return defaultHex;
            string strVal = attributeValue.ToString() ?? string.Empty;
            if (strVal.StartsWith("#") && (strVal.Length == 7 || strVal.Length == 9))
            {
                return strVal;
            }
            return defaultHex;
        }
    }

    public sealed class DataDefinedPropertyCollection
    {
        private readonly Dictionary<DataDefinedPropertyType, DataDefinedProperty> _properties = new();

        public DataDefinedProperty GetProperty(DataDefinedPropertyType type)
        {
            if (!_properties.TryGetValue(type, out var prop))
            {
                prop = new DataDefinedProperty { PropertyType = type };
                _properties[type] = prop;
            }
            return prop;
        }

        public void SetProperty(DataDefinedPropertyType type, DataDefinedProperty prop)
        {
            _properties[type] = prop;
        }

        public bool HasActiveProperty(DataDefinedPropertyType type)
        {
            return _properties.TryGetValue(type, out var prop) && prop.IsActive;
        }
    }
}
```

---

# SEÇÃO 4: BIBLIOTECA CANÔNICA DE ÍCONES CARTOGRÁFICOS E SVGS PARAMETRIZADOS

### 4.1 Padrão de SVG Parametrizado

Para que um ícone SVG possa ser colorido em tempo de execução sem perder gradientes, detalhes internos ou contornos, ele deve seguir a especificação de SVG parametrizado suportada pelo QGIS.

Um arquivo SVG padrão utiliza as palavras-chave `param(fill)`, `param(stroke)`, `param(stroke-width)` e `param(fill-opacity)` como fallbacks de CSS:

```xml
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32" width="32" height="32">
  <!-- Círculo externo de proteção -->
  <circle cx="16" cy="16" r="14" 
          fill="param(fill) #ffffff" 
          fill-opacity="param(fill-opacity) 1.0"
          stroke="param(stroke) #0284c7" 
          stroke-width="param(stroke-width) 2.0" />
  
  <!-- Símbolo interno (Marco Topográfico) -->
  <path d="M16 6 L26 24 L6 24 Z" 
        fill="param(fill-secondary) #0ea5e9" 
        stroke="param(stroke) #0284c7" 
        stroke-width="param(stroke-width) 1.2" />
        
  <!-- Ponto central de centragem física (Pino geodésico) -->
  <circle cx="16" cy="18" r="2.5" 
          fill="param(stroke) #0284c7" />
</svg>
```

O GeoNex incorporará um motor de injeção direta que substitui essas diretivas em memória antes da compilação para `SKPicture` do SkiaSharp, permitindo performance nativa sem re-ler o arquivo em disco.

---

### 4.2 Catálogo Detalhado de Ícones por Categoria

Abaixo é apresentado o catálogo estruturado com mais de **150 ícones cartográficos vetoriais nativos** distribuídos com o instalador do GeoNex:

#### Categoria A: Topografia, Agrimensura e Georreferenciamento (SIGEF / INCRA / IBGE)

| Identificador Canônico | Nome em Português | Descrição Cartográfica e Simbologia | SVG Base |
|---|---|---|---|
| `topografia/marco_incra_m` | Marco Tipo M (Concreto) | Vértice de limite de propriedade certificado pelo INCRA/SIGEF com pino metálico e identificador cravado. | Triângulo equilátero com ponto de cobre central e círculo concêntrico. |
| `topografia/marco_incra_p` | Ponto Tipo P (Piquete) | Vértice de limite cravado em piquete de madeira tratada ou barra de ferro. | Quadrado inscrito com triângulo invertido interno. |
| `topografia/marco_incra_v` | Vértice Tipo V (Virtual) | Ponto de amarração não materializado fisicamente (cálculo geodésico). | Cruz circular tracejada com circunferência externa. |
| `topografia/marco_geodesico_ibge` | Marco Geodésico IBGE | Vértice da Rede Geodésica Fundamental Brasileira (SGB). | Triângulo com cruz interna e círculo externo duplo. |
| `topografia/estacao_total` | Estação Total Topográfica | Ponto de estação instrumental ocupado com equipamento de medição ótica/eletrônica. | Silhueta estilizada de teodolito com tripé e prumo. |
| `topografia/base_gnss_rtk` | Base Receptora GNSS / RTK | Ponto base fixo de rastreio de constelação satelital (GPS/GLONASS/Galileo). | Antena geodésica em tripé com ondas de rádio concêntricas. |
| `topografia/rover_gnss` | Ponto Móvel RTK (Rover) | Vértice medido em campo via receptor móvel com bastão e antena. | Bastão vertical graduado com antena no topo e pino inferior. |
| `topografia/vertice_triangulacao` | Vértice de Triangulação | Ponto histórico de rede trigonométrica de alta precisão. | Triângulo com ponto central e linhas de visada nos vértices. |
| `topografia/nivelamento_rn` | Referência de Nível (RN) | Ponto altimétrico de precisão milimétrica cravado em rocha ou estrutura. | Barra horizontal com seta superior invertida e sigla RN. |
| `topografia/ponto_cotado` | Ponto Cotado de Altimetria | Ponto com elevação z conhecida para curvas de nível. | Ponto com mira cruciforme fina e espaço para etiqueta z. |
| `topografia/pino_estrada` | Pino de Aço em Asfalto | Ponto de poligonal cravado em leito de rodovia ou pavimento. | Círculo chanfrado com pino central sextavado. |
| `topografia/testemunha_concreto` | Testemunha de Concreto | Pilar auxiliar para localização de marco oculto ou submerso. | Retângulo vertical com chanfro superior e listras diagonais. |
| `topografia/mira_nivelamento` | Ponto de Mira | Ponto de leitura de mira graduada em nivelamento geométrico. | Barra quadriculada em xadrez preto e branco. |
| `topografia/prisma_refletor` | Prisma Refletor | Alvo retrorefletor de medição laser/EDM. | Hexágono com triângulo interno em perspectiva tridimensional. |
| `topografia/canto_muro` | Canto de Muro / Divisa | Vértice de confrontação física constituída por muro de alvenaria. | Ângulo reto com hachura externa de tijolo. |
| `topografia/cerca_mourão` | Mourão de Cerca de Arame | Vértice materializado em esteio ou mourão de cerca de divisa. | Círculo sólido com arames farpados convergentes. |
| `topografia/eixo_rio` | Vértice em Talvegue / Rio | Vértice definido pelo eixo médio ou margem de curso d'água natural. | Duas linhas onduladas paralelas com cruz de amarração. |
| `topografia/vala_divisoria` | Vértice em Vala | Marco situado em vala divisória de propriedades rurais. | Trapézio invertido hachurado. |
| `topografia/linha_cumeeira` | Marco em Linha de Espigão | Vértice situado no divisor de águas topográfico natural. | Triângulo pontiagudo com crista de relevo. |
| `topografia/arvore_marco` | Árvore Notável de Divisa | Árvore secular utilizada como marco descritivo em títulos antigos. | Silhueta de copa com tronco entalhado com cruz. |

#### Categoria B: Infraestrutura Urbana, Redes Elétricas, Hidráulicas e Telecom

| Identificador Canônico | Nome em Português | Descrição Cartográfica |
|---|---|---|
| `infra/poste_concreto_simples` | Poste de Concreto Simples | Suporte de rede aérea de baixa/média tensão urbana. |
| `infra/poste_transformador` | Poste com Transformador | Poste equipado com trafo aéreo de distribuição elétrica. |
| `infra/poste_iluminacao` | Poste de Iluminação Pública | Ponto de luminária viária municipal com pétalas LED/vapor. |
| `infra/torre_transmissao` | Torre de Alta Tensão | Estrutura metálica treliçada de linha de transmissão (LT). |
| `infra/torre_eolica` | Aerogerador Eólico | Ponto de turbina eólica de geração renovável com pás. |
| `infra/subestacao_energia` | Subestação Elétrica | Ponto de conexão de malha de distribuição com pátio. |
| `infra/antena_telecom` | Torre de Telecomunicação (ERB) | Estação rádio base celular e enlaces de micro-ondas. |
| `infra/caixa_inspecao_eletrica` | Caixa de Passagem Subterrânea | Poço de visita ou caixa subterrânea de cabos de força. |
| `infra/bueiro_boca_lobo` | Boca de Lobo / Bueiro | Ponto de captação de drenagem pluvial em meio-fio. |
| `infra/poco_visita_esgoto` | Poço de Visita de Esgoto (PV) | Acesso à rede coletora de esgotamento sanitário. |
| `infra/estacao_elevatoria_esgoto`| Estação Elevatória (EEE) | Conjunto motobomba para transposição de efluentes sanitários. |
| `infra/hidrante_urbano` | Hidrante de Coluna | Ponto de combate a incêndio em via pública. |
| `infra/registro_valvula_agua` | Válvula / Registro de Gaveta | Ponto de controle e manobra de adutora ou rede de água. |
| `infra/reservatorio_elevado` | Caixa d'Água Elevada | Castelo d'água para reservação e pressão de rede. |
| `infra/estacao_tratamento_agua` | Estação de Tratamento (ETA) | Complexo de decantação, floculação e cloração de água potável. |
| `infra/gasoduto_valvula` | Válvula de Gasoduto | Ponto de corte e inspeção de rede de gás natural canalizado. |
| `infra/ponto_fibra_otica` | Caixa de Emenda Óptica (CEO) | Ponto aéreo ou subterrâneo de terminação de telecomunicações. |
| `infra/painel_solar` | Usina Fotovoltaica Central | Ponto de conexão de usina solar com inversores centralizados. |
| `infra/lixeira_conteiner` | Contêiner de Resíduos Sólidos | Ponto de coleta seletiva ou conteinerizada urbana. |
| `infra/placa_solar_rural` | Ponto de Bombeamento Solar | Sistema autônomo fotovoltaico para dessedentação animal. |

#### Categoria C: Transporte, Logística e Trânsito

| Identificador Canônico | Nome em Português | Descrição Cartográfica |
|---|---|---|
| `transporte/aeroporto_internacional`| Aeroporto Comercial | Pista pavimentada com terminal de passageiros e torre. |
| `transporte/heliponto` | Heliponto Homologado | Plataforma de pouso e decolagem de helicópteros com letra H. |
| `transporte/posto_combustivel` | Posto de Combustíveis | Ponto de abastecimento veicular com bombas de gasolina/diesel. |
| `transporte/estacao_ferroviaria` | Estação de Trem / Metrô | Terminal de embarque e desembarque ferroviário de passageiros. |
| `transporte/cruzamento_ferrovia` | Passagem de Nível (PN) | Cruzamento em nível entre ferrovia e rodovia com cancela. |
| `transporte/porto_maritimo` | Porto / Terminal Portuário | Ponto de atracação de navios cargueiros com guindastes e cais. |
| `transporte/rampa_embarque_barco` | Rampa Náutica / Trapiche | Acesso a rio ou lago para pequenas embarcações e balsas. |
| `transporte/ponto_onibus` | Parada de Ônibus | Ponto de parada do transporte público coletivo com abrigo. |
| `transporte/estacionamento` | Estacionamento Público/Privado | Área delimitada para guarda de veículos com símbolo P. |
| `transporte/semaforo` | Semáforo de Trânsito | Dispositivo eletrônico de controle de tráfego de 3 tempos. |
| `transporte/pedagio_praca` | Praça de Pedágio | Estrutura de cobrança tarifária em rodovia concessionada. |
| `transporte/pesagem_balanca` | Posto de Pesagem de Caminhões | Balança rodoviária fiscalizatória de carga e eixos. |
| `transporte/radar_velocidade` | Radar / Fiscalização Eletrônica | Ponto de medição de velocidade por laço indutivo ou doppler. |
| `transporte/ponte_viaduto` | Apoio de Ponte / Pilar | Vértice de infraestrutura de obra de arte especial (OAE). |
| `transporte/ciclovia_parada` | Paraciclo / Bicicletário | Ponto de estacionamento e apoio a ciclistas urbanos. |
| `transporte/terminal_cargas` | Centro de Distribuição / Carga | Plataforma de transbordo multimodal de mercadorias. |
| `transporte/posto_fiscal` | Posto Fiscal de Fronteira | Ponto de controle tributário e sanitário estadual/federal. |
| `transporte/balsa_travessia` | Terminal de Ferry-Boat / Balsa | Ponto de transbordo aquaviário de veículos e pedestres. |
| `transporte/passarela_pedestres` | Passarela Aérea | Obra de arte para travessia segura de pedestres em rodovias. |
| `transporte/acesso_rodoviario` | Trevo / Entroncamento | Ponto de confluência ou bifurcação de malha viária. |

#### Categoria D: Meio Ambiente, Florestal, Recursos Hídricos e Uso do Solo

| Identificador Canônico | Nome em Português | Descrição Cartográfica |
|---|---|---|
| `ambiental/nascente_agua` | Nascente / Olho d'Água | Ponto de surgência natural de água subterrânea em APP. |
| `ambiental/poco_artesiano` | Poço Tubular Profundo | Captação subterrânea outorgada de água potável. |
| `ambiental/estacao_fluviometrica` | Estação Fluviométrica | Ponto de monitoramento de nível e vazão de rio (ANA/CPRM). |
| `ambiental/estacao_pluviometrica` | Estação Pluviométrica | Ponto de medição diária de precipitação pluviométrica. |
| `ambiental/estacao_meteorologica` | Estação Meteorológica Automática | Torre com sensores de vento, temperatura, umidade e radiação. |
| `ambiental/arvore_nativa` | Espécime Arbóreo Notável | Árvore imune ao corte ou objeto de compensação florestal. |
| `ambiental/arvore_exotica` | Reflorestamento Comercial | Ponto de plantio de espécies exóticas (Pinus, Eucalipto). |
| `ambiental/ponto_amostragem_solo` | Amostra de Solo para Laudo | Coleta georreferenciada de terra para análise agronômica. |
| `ambiental/foco_calor_incendio` | Foco de Queimada / Calor | Registro de anomalia térmica detectada por satélite ambiental. |
| `ambiental/area_degradada` | Ponto de Erosão / Voçoroca | Ponto de monitoramento de processo erosivo severo de encosta. |
| `ambiental/deposito_residuos` | Aterro Sanitário / Lixão | Ponto central de disposição final de resíduos sólidos. |
| `ambiental/mina_extracao` | Ponto de Mineração / Lavra | Frente de lavra ativa a céu aberto ou subterrânea. |
| `ambiental/caverna_espeleologia` | Entrada de Gruta / Caverna | Cavidade natural subterrânea cadastrada no CECAV. |
| `ambiental/queda_dagua` | Cachoeira / Salto | Desnível abrupto em leito rochoso de curso d'água. |
| `ambiental/pesquisa_fauna` | Avistamento de Fauna Silvestre | Ponto de registro de espécie ameaçada ou censo ecológico. |
| `ambiental/qualidade_agua` | Ponto de Coleta IQA | Monitoramento de parâmetros físico-químicos de manancial. |
| `ambiental/barragem_rejeito` | Piezômetro / Barramento | Ponto de instrumentação geotécnica de barragem de rejeitos. |
| `ambiental/lagoa_represa` | Ponto Médio de Açude / Represa | Superfície de espelho d'água represado em propriedade rural. |
| `ambiental/corredor_ecologico` | Passagem de Fauna | Ponto de travessia subterrânea ou aérea para animais em pista. |
| `ambiental/unidade_conservacao` | Sede de Parque / UC | Ponto de fiscalização e guarita de Unidade de Conservação. |

#### Categoria E: Pontos de Interesse (POI), Equipamentos Públicos e Serviços

| Identificador Canônico | Nome em Português | Descrição Cartográfica |
|---|---|---|
| `servicos/hospital_pronto_socorro`| Hospital Geral | Unidade de atendimento médico de alta complexidade com cruz. |
| `servicos/posto_saude_ubs` | Unidade Básica de Saúde (UBS) | Centro de saúde primário municipal de atendimento local. |
| `servicos/farmacia` | Farmácia / Drogaria | Estabelecimento de dispensação farmacêutica com caduceu. |
| `servicos/escola_colegio` | Escola / Colégio | Unidade de ensino infantil e fundamental com capelo de formatura.|
| `servicos/universidade_campus` | Campus Universitário | Complexo educacional superior com livro aberto. |
| `servicos/policia_batalhao` | Delegacia / Batalhão PM | Ponto de segurança pública civil ou militar com brasão policial.|
| `servicos/corpo_bombeiros` | Quartel de Bombeiros | Unidade de salvamento e resgate com machados cruzados. |
| `servicos/prefeitura_sede` | Prefeitura Municipal | Edifício sede do poder executivo municipal com bandeira. |
| `servicos/camara_vereadores` | Câmara de Vereadores | Edifício sede do poder legislativo local com plenário. |
| `servicos/forum_judiciario` | Fórum da Justiça | Edifício do poder judiciário com balança da justiça. |
| `servicos/igreja_templo` | Igreja / Templo Religioso | Edifício de culto e práticas religiosas com crucifixo. |
| `servicos/cemiterio` | Cemitério Municipal | Campo santo de sepultamentos com cruz tumular. |
| `servicos/agencia_bancaria` | Banco / Instituição Financeira | Agência de atendimento com símbolo de moeda/cifrão. |
| `servicos/correios_agencia` | Agência de Correios | Ponto de serviços postais e encomendas com corneta/envelope. |
| `servicos/mercado_supermercado` | Supermercado / Comércio | Ponto de comércio varejista de alimentos com carrinho de compras.|
| `servicos/hotel_pousada` | Hotel / Hospedagem | Ponto de hospedagem turística e comercial com cama estilizada. |
| `servicos/restaurante` | Restaurante / Alimentação | Ponto de gastronomia e alimentação com garfo e faca. |
| `servicos/quadra_esportes` | Ginásio / Quadra Poliesportiva | Equipamento esportivo e de lazer municipal com bola. |
| `servicos/estadio_futebol` | Estádio de Futebol | Praça esportiva de grande capacidade com arquibancadas. |
| `servicos/teatro_cultura` | Teatro / Centro Cultural | Equipamento de artes cênicas com máscaras de tragédia e comédia.|

#### Categoria F: Defesa Civil, Riscos Geológicos e Emergências

| Identificador Canônico | Nome em Português | Descrição Cartográfica |
|---|---|---|
| `risco/ponto_encontro_evacuacao` | Ponto de Encontro Seguro | Local determinado para reunião de população durante catástrofes. |
| `risco/deslizamento_encosta` | Risco Alto de Deslizamento | Setor crítico de instabilidade de talude e movimento de massa. |
| `risco/inundacao_alagamento` | Cota de Alagamento / Cheia | Ponto histórico ou probabilístico de invasão por água pluvial. |
| `risco/abrigo_emergencia` | Abrigo Provisório | Instalação preparada para acolhimento de desabrigados. |
| `risco/sirene_alerta` | Torre de Sirene de Alarme | Dispositivo sonoro de alerta antecipado para evacuação. |
| `risco/bloqueio_viario_crise` | Bloqueio de Emergência | Ponto de interdição total de tráfego por colapso estrutural. |
| `risco/posto_triagem_avancado` | Posto Médico de Campanha | Triagem de vítimas em cenário de catástrofe natural. |
| `risco/vazamento_quimico` | Risco Tecnológico / Químico | Ponto de contenção de produtos perigosos ou inflamáveis. |
| `risco/queda_ponte` | Colapso de Obra Viária | Interrupção viária por queda de ponte ou cabeceira de bueiro. |
| `risco/heliponto_emergencia` | Pouso de Resgate Aéreo | Zona desimpedida para operação de aeronaves de salvamento. |

---

# SEÇÃO 5: MOTOR DE RENDERIZAÇÃO GRÁFICA SKIASHARP DE ALTA PERFORMANCE (`SkiaMarkerEngine`)

### 5.1 Pipeline de Renderização Vetorial em 60 FPS

A renderização rápida de centenas de milhares de pontos no SkiaSharp requer evitar chamadas individuais de alto custo (`SKCanvas.DrawPath`) repetidas dentro de loops de feições. A abordagem recomendada pelo time de engenharia da Google para a biblioteca Skia em gráficos 2D massivos consiste no uso de **Atlas de Texturas GPU** e o método unificado `SKCanvas.DrawAtlas`.

```
+---------------------------------------------------------------------------------------------------+
|                               PIPELINE DE DESENHO DE PONTOS GEONEX                                |
+---------------------------------------------------------------------------------------------------+
                                                  |
                               +----------------------------------+
                               | 1. Query no Spatial Index R-Tree |
                               |   (Filtra feições no Viewport)   |
                               +----------------------------------+
                                                  |
                               +----------------------------------+
                               | 2. Leitura de Coordenadas e CRS  |
                               |  (Reprojeção SIMD em Batches)    |
                               +----------------------------------+
                                                  |
                               +----------------------------------+
                               | 3. Avaliação de Data-Overrides   |
                               |    (Tamanho / Rotação / Cor)     |
                               +----------------------------------+
                                                  |
                    +-----------------------------+-----------------------------+
                    |                                                           |
     [Símbolo Estático / Cacheável]                             [Símbolo com Rotação Dinâmica]
                    |                                                           |
                    v                                                           v
     +------------------------------+                            +------------------------------+
     | 4a. Busca no Atlas de Sprites|                            | 4b. Compilação para SKPicture|
     |     (Texture ID na VRAM)     |                            |     (Gravação de Comandos)   |
     +------------------------------+                            +------------------------------+
                    |                                                           |
                    +-----------------------------+-----------------------------+
                                                  |
                               +----------------------------------+
                               | 5. Despacho em Lote para GPU     |
                               |    canvas.DrawAtlas(...)         |
                               |    ou Direct DrawBatch           |
                               +----------------------------------+
                                                  |
                               +----------------------------------+
                               | 6. Apresentação no Frame Buffer  |
                               |       (Hardware Display)         |
                               +----------------------------------+
```

### 5.2 Parser e Compilador SVG Otimizado para Objetos SkiaSharp

O GeoNex utilizará a biblioteca nativa `SkiaSharp.Extended.Svg` ou compilação direta para `SKPath`. Abaixo está a implementação da classe de infraestrutura `SvgCompilationService`:

```csharp
namespace GeoNex.Services
{
    using System;
    using System.Collections.Concurrent;
    using System.IO;
    using System.Text;
    using SkiaSharp;
    using SkiaSharp.Extended.Svg;

    public static class SvgCompilationService
    {
        private readonly record struct SvgVariantKey(
            string SourcePath,
            uint FillColor,
            uint StrokeColor,
            int StrokeWidthTenths,
            int SizeSubPx);

        private static readonly ConcurrentDictionary<SvgVariantKey, SKPicture> _pictureCache = new();
        private static readonly ConcurrentDictionary<string, string> _rawXmlCache = new();

        /// <summary>
        /// Carrega o XML bruto de um arquivo SVG ou recurso embutido.
        /// </summary>
        public static string ObterXmlBruto(string svgSourcePath)
        {
            return _rawXmlCache.GetOrAdd(svgSourcePath, path =>
            {
                if (path.StartsWith("geonex://", StringComparison.OrdinalIgnoreCase))
                {
                    // Lê do diretório de assets embutidos do GeoNex
                    string relativePath = path.Substring("geonex://".Length);
                    string localFullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "Markers", relativePath);
                    if (File.Exists(localFullPath))
                    {
                        return File.ReadAllText(localFullPath, Encoding.UTF8);
                    }
                }
                else if (File.Exists(path))
                {
                    return File.ReadAllText(path, Encoding.UTF8);
                }

                // Fallback para marcador padrão caso o arquivo não seja localizado
                return ObterSvgFallbackCirculo();
            });
        }

        /// <summary>
        /// Aplica os parâmetros de cor, borda e espessura no template SVG no padrão QGIS.
        /// </summary>
        public static string InjetarParametrosSvg(string xmlTemplate, SKColor fill, SKColor stroke, float strokeWidth)
        {
            string fillHex = $"#{fill.Red:X2}{fill.Green:X2}{fill.Blue:X2}";
            string strokeHex = $"#{stroke.Red:X2}{stroke.Green:X2}{stroke.Blue:X2}";
            float fillOpacity = fill.Alpha / 255.0f;
            float strokeOpacity = stroke.Alpha / 255.0f;

            var sb = new StringBuilder(xmlTemplate);
            
            // Substituições padrão QGIS
            sb.Replace("param(fill)", fillHex);
            sb.Replace("param(stroke)", strokeHex);
            sb.Replace("param(stroke-width)", strokeWidth.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            sb.Replace("param(fill-opacity)", fillOpacity.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            sb.Replace("param(stroke-opacity)", strokeOpacity.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));

            return sb.ToString();
        }

        /// <summary>
        /// Compila o SVG para um SKPicture imutável acelerado por hardware.
        /// </summary>
        public static SKPicture CompilarParaPicture(string svgSourcePath, SKColor fill, SKColor stroke, float strokeWidth, float targetSize)
        {
            var key = new SvgVariantKey(
                svgSourcePath,
                (uint)fill,
                (uint)stroke,
                (int)(strokeWidth * 10),
                (int)(targetSize * 10));

            return _pictureCache.GetOrAdd(key, k =>
            {
                string rawXml = ObterXmlBruto(k.SourcePath);
                string parameterizedXml = InjetarParametrosSvg(rawXml, fill, stroke, strokeWidth);

                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(parameterizedXml));
                var svg = new SKSvg();
                svg.Load(stream);

                if (svg.Picture == null || svg.Model?.RootElement == null)
                {
                    return CompilarPictureFallback(targetSize, fill, stroke);
                }

                // Normaliza para o tamanho desejado
                var bounds = svg.Picture.CullRect;
                float maxDim = Math.Max(bounds.Width, bounds.Height);
                float scale = maxDim > 0 ? targetSize / maxDim : 1f;

                var recorder = new SKPictureRecorder();
                var canvas = recorder.BeginRecording(new SKRect(-targetSize * 0.5f, -targetSize * 0.5f, targetSize * 0.5f, targetSize * 0.5f));

                canvas.Translate(-bounds.MidX * scale, -bounds.MidY * scale);
                canvas.Scale(scale);
                canvas.DrawPicture(svg.Picture);

                return recorder.EndRecording();
            });
        }

        private static SKPicture CompilarPictureFallback(float size, SKColor fill, SKColor stroke)
        {
            var recorder = new SKPictureRecorder();
            float half = size * 0.5f;
            var canvas = recorder.BeginRecording(new SKRect(-half, -half, half, half));
            using var paintFill = new SKPaint { Color = fill, IsAntialias = true, Style = SKPaintStyle.Fill };
            using var paintStroke = new SKPaint { Color = stroke, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
            canvas.DrawCircle(0, 0, half - 1f, paintFill);
            canvas.DrawCircle(0, 0, half - 1f, paintStroke);
            return recorder.EndRecording();
        }

        private static string ObterSvgFallbackCirculo()
        {
            return @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 24 24"">
                        <circle cx=""12"" cy=""12"" r=""10"" 
                                fill=""param(fill) #38bdf8"" 
                                stroke=""param(stroke) #0284c7"" 
                                stroke-width=""param(stroke-width) 1.5"" />
                     </svg>";
        }
    }
}
```

### 5.3 Arquitetura do Atlas de Sprites em VRAM / Cache de Texturas GPU

Quando milhares de feições pontuais compartilham o mesmo símbolo e tamanho, converter cada vértice individualmente em operações de canvas gera overhead de comandos no driver gráfico.

A classe `MarkerGpuAtlas` gerencia uma textura única de 2048x2048 pixels alocada diretamente na memória de vídeo, agrupando centenas de ícones rasterizados em sub-regiões retangulares:

```csharp
namespace GeoNex.Services
{
    using System;
    using System.Collections.Concurrent;
    using SkiaSharp;

    public sealed class MarkerGpuAtlas : IDisposable
    {
        private const int AtlasDimension = 2048;
        private readonly SKBitmap _atlasBitmap;
        private readonly SKCanvas _atlasCanvas;
        private SKImage? _cachedImage;
        private bool _isDirty = true;

        private int _currentX = 2;
        private int _currentY = 2;
        private int _rowMaxHeight = 0;

        private readonly ConcurrentDictionary<string, SKRect> _glyphRegions = new(StringComparer.Ordinal);
        private readonly object _allocationLock = new();

        public MarkerGpuAtlas()
        {
            _atlasBitmap = new SKBitmap(AtlasDimension, AtlasDimension, SKColorType.Rgba8888, SKAlphaType.Premul);
            _atlasCanvas = new SKCanvas(_atlasBitmap);
            _atlasCanvas.Clear(SKColors.Transparent);
        }

        public bool TentarObterRegiaoSprite(string chaveSprite, out SKRect regiao)
        {
            return _glyphRegions.TryGetValue(chaveSprite, out regiao);
        }

        public SKRect RegistrarSprite(string chaveSprite, SKPicture picture, int width, int height)
        {
            lock (_allocationLock)
            {
                if (_glyphRegions.TryGetValue(chaveSprite, out var existente))
                {
                    return existente;
                }

                // Verifica se cabe na linha atual
                if (_currentX + width + 2 >= AtlasDimension)
                {
                    // Pula para a próxima linha
                    _currentX = 2;
                    _currentY += _rowMaxHeight + 2;
                    _rowMaxHeight = 0;
                }

                // Verifica se estourou o atlas verticalmente
                if (_currentY + height + 2 >= AtlasDimension)
                {
                    // Limpa o atlas e reinicia (Estratégia de descarte de cache total)
                    _atlasCanvas.Clear(SKColors.Transparent);
                    _glyphRegions.Clear();
                    _currentX = 2;
                    _currentY = 2;
                    _rowMaxHeight = 0;
                }

                var rectDest = new SKRect(_currentX, _currentY, _currentX + width, _currentY + height);
                
                // Desenha o picture na área do atlas
                _atlasCanvas.Save();
                _atlasCanvas.Translate(rectDest.MidX, rectDest.MidY);
                _atlasCanvas.DrawPicture(picture);
                _atlasCanvas.Restore();

                _currentX += width + 2;
                if (height > _rowMaxHeight) _rowMaxHeight = height;

                _glyphRegions[chaveSprite] = rectDest;
                _isDirty = true;

                return rectDest;
            }
        }

        public SKImage ObterImagemAtlas()
        {
            if (_isDirty || _cachedImage == null)
            {
                _cachedImage?.Dispose();
                _cachedImage = SKImage.FromBitmap(_atlasBitmap);
                _isDirty = false;
            }
            return _cachedImage;
        }

        public void Dispose()
        {
            _cachedImage?.Dispose();
            _atlasCanvas.Dispose();
            _atlasBitmap.Dispose();
        }
    }
}
```

---

# SEÇÃO 6: SIMBOLOGIA ORIENTADA A DADOS E REGRAS AVANÇADAS (*DATA-DRIVEN STYLING*)

### 6.1 Vínculo de Atributos com Propriedades do Marcador

A simbologia orientada a atributos permite automatizar a representação visual em conformidade direta com a tabela do banco de dados geográfico ou Shapefile:

1. **Rotação Dinâmica (Azimute de Ponto / Direção de Voo / Rumo de Divisa):**
   - Vínculo direto de um campo como `AZIMUTE`, `RUMO`, `DIRECAO` ou `ROTACAO`.
   - Se o campo contém `45.5`, o ícone (por exemplo, uma seta ou antena transmissora) é rotacionado exatamente em 45.5° no sentido horário em relação ao norte cartográfico.
2. **Tamanho Dinâmico Proporcional (Magnitude / População / Tensão Elétrica):**
   - Mapeamento matemático por escala Linear, Raiz Quadrada (Percepção Visual de Flannery) ou Logarítmica.
   - Um valor de 13.8 kV a 230 kV mapeado continuamente entre 8 px e 32 px.
3. **Cores Dinâmicas Condicionais (Status / Situação Cadastral):**
   - Atributos textuais como `SITUACAO = 'CERTIFICADO'` recebem verde (`#10b981`), enquanto `SITUACAO = 'PENDENTE'` recebem laranja (`#f59e0b`) e `SITUACAO = 'SOBREPOSICAO'` recebem vermelho (`#ef4444`).
4. **Forma Dinâmica por Categoria de Feição:**
   - Campo `TIPO_MARCO` definindo dinamicamente se o símbolo desenhado será `topografia/marco_incra_m`, `topografia/marco_incra_p` ou `topografia/marco_incra_v`.

### 6.2 Avaliador de Regras Cartográficas em Tempo Real

```csharp
namespace GeoNex.Core.Symbology
{
    using System;
    using System.Collections.Generic;

    public sealed class RuleBasedPointRenderer
    {
        public sealed class PointStyleRule
        {
            public string Label { get; set; } = "Nova Regra";
            public string AttributeName { get; set; } = string.Empty;
            public string ComparisonOperator { get; set; } = "=="; // ==, !=, >, >=, <, <=, Contem, ComecaCom
            public string ComparisonValue { get; set; } = string.Empty;
            public CompositePointSymbol Symbol { get; set; } = new();
            public bool IsActive { get; set; } = true;

            public bool Match(IReadOnlyDictionary<string, object?> attributes)
            {
                if (!IsActive || string.IsNullOrWhiteSpace(AttributeName)) return true;
                if (!attributes.TryGetValue(AttributeName, out var val) || val == null) return false;

                string valStr = val.ToString() ?? string.Empty;

                return ComparisonOperator switch
                {
                    "==" => string.Equals(valStr, ComparisonValue, StringComparison.OrdinalIgnoreCase),
                    "!=" => !string.Equals(valStr, ComparisonValue, StringComparison.OrdinalIgnoreCase),
                    "Contem" => valStr.Contains(ComparisonValue, StringComparison.OrdinalIgnoreCase),
                    ">" => double.TryParse(valStr, out double d1) && double.TryParse(ComparisonValue, out double d2) && d1 > d2,
                    ">=" => double.TryParse(valStr, out double d1) && double.TryParse(ComparisonValue, out double d2) && d1 >= d2,
                    "<" => double.TryParse(valStr, out double d1) && double.TryParse(ComparisonValue, out double d2) && d1 < d2,
                    "<=" => double.TryParse(valStr, out double d1) && double.TryParse(ComparisonValue, out double d2) && d1 <= d2,
                    _ => false
                };
            }
        }

        public List<PointStyleRule> Rules { get; } = new();
        public CompositePointSymbol DefaultSymbol { get; set; } = new();

        public CompositePointSymbol ResolveSymbol(IReadOnlyDictionary<string, object?> attributes)
        {
            for (int i = 0; i < Rules.Count; i++)
            {
                var rule = Rules[i];
                if (rule.Match(attributes))
                {
                    return rule.Symbol;
                }
            }
            return DefaultSymbol;
        }
    }
}
```

---

# SEÇÃO 7: INTERFACE DO USUÁRIO (UI / UX) NO BLAZOR WEBASSEMBLY / WEBVIEW2

### 7.1 Reestruturação do Modal de Simbologia

O modal de simbologia de camada (`LayerPropertiesModal.razor`) será expandido com uma aba dedicada de **"Simbologia de Marcadores"**, projetada para operadores SIG exigentes.

A interface é dividida em quatro áreas funcionais:
1. **Árvore de Camadas de Símbolo (À Esquerda):** Permite empilhar marcadores, adicionar novas camadas de desenho (`[+] Marcador Simples`, `[+] Marcador SVG`, `[+] Marcador de Fonte`), reordenar com arrastar ou setas, e ocultar/bloquear sub-camadas.
2. **Galeria de Ícones e SVGs (Ao Centro):** Exibição de miniaturas vetoriais com busca instantânea em tempo real, barra de filtros por categoria temática e botão de upload para novos SVGs.
3. **Painel de Propriedades da Camada Ativa (À Direita):** Sliders de precisão para tamanho (3px a 128px), ângulo de rotação (0° a 360° com bússola interativa), seletores de cor com paletas pré-configuradas e ajuste fino de deslocamento X/Y e ancoragem.
4. **Painel Inferior de Pré-visualização Dinâmica:** Um canvas interativo com zoom, mostrando exatamente como o símbolo final ficará sobre fundos claros, escuros e sobre ortofoto.

```
+--------------------------------------------------------------------------------------------------------------------+
|                                      PROPRIEDADES DA CAMADA: PONTOS DE CONTROLE                                   [X]
+--------------------------------------------------------------------------------------------------------------------+
|  [ Informações ]  [ Simbologia de Marcadores ]*  [ Rótulos Cartográficos ]  [ Tabela de Campos ]  [ Metadados ]   |
+--------------------------------------------------------------------------------------------------------------------+
|  ESTRUTURA DE CAMADAS     |  CATÁLOGO DE ÍCONES SVG                           |  PROPRIEDADES DO MARCADOR          |
|  +---------------------+  |  [ Buscar ícone (ex: marco, poço, antena)... ]    |  Tipo: Marcador SVG Parametrizado  |
|  | [x] 2. Ícone Central|  |  Categorias: (Todos) [Topografia]* [Redes] [Amb]  |  --------------------------------  |
|  | [x] 1. Fundo Círculo|  |  +----------------------------------------------+ |  Tamanho: [-------O------] 16.0 px |
|  +---------------------+  |  |  [+]     [+]     [+]     [+]     [+]     [+] | |  Rotação: [---O----------]  45.0 ° |
|  [+] Adicionar Camada     |  |  Marco M Marco P Marco V BaseRTK Estacao Nivel | |  Âncora:   [Centro v]            |
|  [-] Excluir Camada       |  |                                              | |  Deslocamento X/Y: [0.0] [0.0] px  |
|  [^] Subir  [v] Descer    |  |  [+]     [+]     [+]     [+]     [+]     [+] | |  --------------------------------  |
|                           |  |  Poço   Nascente Arvore  Torre   Poste   Pino | |  Cor Preenchimento:  [ #0ea5e9 ]  |
|  [ Importar SVG Próprio ] |  |                                              | |  Cor do Contorno:    [ #0284c7 ]  |
|                           |  +----------------------------------------------+ |  Espessura da Borda: [ 1.5 px   ]  |
|                           |  Mostrando 64 ícones da categoria Topografia     |  Opacidade:          [ 100%     ]  |
+--------------------------------------------------------------------------------------------------------------------+
|  PRÉ-VISUALIZAÇÃO EM TEMPO REAL:                                                                                   |
|  +--------------------------------------------------------------------+    [ Restaurar Padrão ]   [ Cancelar ]     |
|  |                               ( + )                                |                                            |
|  |                        [Escala: 400% | Fundo: Escuro v]            |                 [ APLICAR ] [ SALVAR E OK ]|
+--------------------------------------------------------------------------------------------------------------------+
```

### 7.2 Implementação do Componente Blazor (`SvgMarkerSelector.razor`)

```razor
@using GeoNex.Core.Symbology
@using GeoNex.Services
@inject IJSRuntime JSRuntime

<div class="svg-marker-library-container">
    <div class="library-header-bar">
        <div class="search-box">
            <svg viewBox="0 0 24 24" width="16" height="16" stroke="currentColor" fill="none"><circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/></svg>
            <input type="search" placeholder="Pesquisar ícones cartográficos (ex: marco, torre, poste)..." 
                   @bind="TermoBusca" @bind:event="oninput" />
        </div>
        
        <div class="category-tabs">
            @foreach (var cat in CategoriasDisponiveis)
            {
                <button type="button" class="category-pill @(CategoriaAtiva == cat ? "selected" : "")"
                        @onclick="() => SelecionarCategoria(cat)">
                    @cat
                </button>
            }
        </div>
    </div>

    <div class="marker-grid-scroll">
        @if (IconesFiltrados.Count == 0)
        {
            <div class="empty-results">
                <span>Nenhum ícone encontrado para "@TermoBusca".</span>
            </div>
        }
        else
        {
            <div class="marker-tiles-grid">
                @foreach (var icone in IconesFiltrados)
                {
                    <div class="marker-tile @(IconeSelecionadoPath == icone.Path ? "active" : "")"
                         title="@icone.NomeLegivel - @icone.Descricao"
                         @onclick="() => EscolherIcone(icone.Path)">
                        <div class="tile-preview">
                            @((MarkupString)icone.SvgRenderizadoHtml)
                        </div>
                        <span class="tile-label">@icone.NomeLegivel</span>
                    </div>
                }
            </div>
        }
    </div>

    <div class="library-footer-bar">
        <label class="btn-import-custom-svg">
            <svg viewBox="0 0 24 24" width="14" height="14" stroke="currentColor" fill="none"><path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"/><polyline points="17 8 12 3 7 8"/><line x1="12" y1="3" x2="12" y2="15"/></svg>
            <span>Carregar SVG Customizado...</span>
            <InputFile OnChange="OnArquivoSvgCarregado" accept=".svg" style="display:none;" />
        </label>
        <span class="item-count">Total de ícones disponíveis: @IconesFiltrados.Count</span>
    </div>
</div>

@code {
    [Parameter] public string IconeSelecionadoPath { get; set; } = "geonex://topografia/marco_incra_m.svg";
    [Parameter] public EventCallback<string> OnIconeSelecionado { get; set; }

    private string TermoBusca = string.Empty;
    private string CategoriaAtiva = "Todos";

    public record IconeItemView(string Path, string NomeLegivel, string Categoria, string Descricao, string SvgRenderizadoHtml);

    private readonly List<string> CategoriasDisponiveis = new()
    {
        "Todos", "Topografia & SIGEF", "Infraestrutura & Redes", "Transporte", "Meio Ambiente", "Serviços & POI", "Defesa Civil"
    };

    private List<IconeItemView> TodosIcones = new();

    protected override void OnInitialized()
    {
        CarregarCatalogo();
    }

    private void CarregarCatalogo()
    {
        TodosIcones = SvgMarkerCatalogService.ObterTodosIconesFormatados();
    }

    private IEnumerable<IconeItemView> IconesFiltrados => TodosIcones.Where(i =>
        (CategoriaAtiva == "Todos" || i.Categoria == CategoriaAtiva) &&
        (string.IsNullOrWhiteSpace(TermoBusca) || 
         i.NomeLegivel.Contains(TermoBusca, StringComparison.OrdinalIgnoreCase) ||
         i.Descricao.Contains(TermoBusca, StringComparison.OrdinalIgnoreCase)));

    private void SelecionarCategoria(string cat) => CategoriaAtiva = cat;

    private async Task EscolherIcone(string path)
    {
        IconeSelecionadoPath = path;
        await OnIconeSelecionado.InvokeAsync(path);
    }

    private async Task OnArquivoSvgCarregado(InputFileChangeEventArgs e)
    {
        var file = e.File;
        if (file == null || !file.Name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return;

        using var stream = file.OpenReadStream(maxAllowedSize: 512 * 1024); // Limite de 512 KB para SVG
        using var reader = new StreamReader(stream);
        string svgXml = await reader.ReadToEndAsync();

        string userFileName = Path.GetFileNameWithoutExtension(file.Name);
        string customPath = SvgMarkerCatalogService.SalvarSvgCustomizado(userFileName, svgXml);

        CarregarCatalogo();
        await EscolherIcone(customPath);
    }
}
```

---

# SEÇÃO 8: INTEROPERABILIDADE, SERIALIZAÇÃO E PADRÕES ABERTOS

### 8.1 Extensão do Formato de Projeto Nativo GeoNex (`.gnx`)

O projeto `.gnx` armazena os estilos na tabela interna do SQLite ou em JSON estruturado com esquema versionado:

```json
{
  "versaoEsquema": "3.0",
  "camada": "Marcos_Geodesicos_2026",
  "tipoGeometria": "Point",
  "simbologia": {
    "tipo": "Composta",
    "opacidadeGeral": 1.0,
    "camadas": [
      {
        "tipo": "Simple",
        "nome": "Halo Externo Branco",
        "forma": "Circle",
        "tamanho": 16.0,
        "corFill": "#ffffff",
        "corStroke": "#000000",
        "strokeWidth": 1.0,
        "offset": [0.0, 0.0]
      },
      {
        "tipo": "Svg",
        "nome": "Marco INCRA Central",
        "svgPath": "geonex://topografia/marco_incra_m.svg",
        "tamanho": 12.0,
        "corFill": "#0ea5e9",
        "corStroke": "#0284c7",
        "strokeWidth": 1.2,
        "rotacao": 0.0,
        "dataDefined": {
          "rotation": {
            "isActive": true,
            "fieldName": "AZIMUTE"
          }
        }
      }
    ]
  }
}
```

### 8.2 Parser e Serializador de Estilos QGIS (`.qml`)

O QGIS salva seus estilos de camada em arquivos XML com extensão `.qml`. O GeoNex implementará o mapeamento bidirecional da tag `<renderer-v2>` e `<symbols>`:

```xml
<!DOCTYPE qgis PUBLIC 'http://mrcc.com/qgis.dtd' 'SYSTEM'>
<qgis styleCategories="Symbology" version="3.34.0-Prizren">
  <renderer-v2 type="singleSymbol" forceraster="0" enableorderby="0">
    <symbols>
      <symbol type="marker" name="0" alpha="1" clip_to_extent="1">
        <data_defined_properties>
          <Option type="Map">
            <Option type="QString" value="" name="name"/>
            <Option name="properties"/>
            <Option type="QString" value="collection" name="type"/>
          </Option>
        </data_defined_properties>
        <!-- Camada 1: Círculo de base -->
        <layer pass="0" class="SimpleMarker" locked="0" enabled="1">
          <Option type="Map">
            <Option type="QString" value="0" name="angle"/>
            <Option type="QString" value="square" name="cap_style"/>
            <Option type="QString" value="14,165,233,255" name="color"/>
            <Option type="QString" value="circle" name="name"/>
            <Option type="QString" value="0,0" name="offset"/>
            <Option type="QString" value="10" name="size"/>
            <Option type="QString" value="MM" name="size_unit"/>
          </Option>
        </layer>
        <!-- Camada 2: SVG Marker -->
        <layer pass="1" class="SvgMarker" locked="0" enabled="1">
          <Option type="Map">
            <Option type="QString" value="0" name="angle"/>
            <Option type="QString" value="255,255,255,255" name="color"/>
            <Option type="QString" value="survey/landmark.svg" name="name"/>
            <Option type="QString" value="0,0" name="offset"/>
            <Option type="QString" value="8" name="size"/>
          </Option>
        </layer>
      </symbol>
    </symbols>
  </renderer-v2>
</qgis>
```

---

# SEÇÃO 9: DESEMPENHO, GERENCIAMENTO DE MEMÓRIA E CONCORRÊNCIA

### 9.1 Estratégia de Caching Hierárquico L1 (CPU), L2 (GPU) e L3 (Disco)

1. **L1 Cache (Heap Gerenciada .NET):** Objetos imutáveis `SKPicture` compilados na escala e cores exatas em um `ConcurrentDictionary` com limite de 1.000 variantes ativas.
2. **L2 Cache (Atlas de Textura GPU Skia):** Bitmap de textura 2048x2048 montado na VRAM, permitindo que o loop de renderização faça o desenho de dezenas de milhares de pontos via índices diretos sem chamar o rasterizador de curvas repetidamente.
3. **L3 Cache (Armazenamento Local Persistente):** Pasta de cache local `%APPDATA%/GeoNex/Cache/SvgCompiled/` com hashes MD5 do conteúdo SVG e atributos para inicialização instantânea a quente do software.

### 9.2 Integração com R-Tree Spatial Index e Culling

Antes de despachar pontos para o motor de desenho, o GeoNex aplica um filtro retangular no índice espacial `RTree<Feature>`:
- Feições fora da caixa de corte do viewport mais uma margem de segurança de 32 pixels são descartadas em tempo de sub-microssegundo.
- Apenas pontos visíveis entram no loop de desenho, garantindo que mesmo um Shapefile de 2 milhões de pontos de postes elétricos do Brasil inteiro mantenha 60 FPS quando o operador estiver dando zoom em um bairro ou município específico.

---

# SEÇÃO 10: PLANO DE EXECUÇÃO TÉCNICA PASSO A PASSO, ROTEIRO DE TESTES E RISCOS

### 10.1 Cronograma de Sprints e Fases

```
+----------------------------------------------------------------------------------------------------+
| SPRINT 1: MODELO DE DOMÍNIO E COMPILADOR SVG                                                       |
| - Implementação das classes IMarkerSymbolLayer, SimpleMarker, SvgMarker, FontMarker                |
| - Desenvolvimento do SvgCompilationService com injeção de parâmetros dinâmicos                      |
| - Criação dos testes unitários de parsing e serialização JSON                                      |
+----------------------------------------------------------------------------------------------------+
                                                  |
                                                  v
+----------------------------------------------------------------------------------------------------+
| SPRINT 2: MOTOR GRÁFICO SKIA E ATLAS GPU                                                           |
| - Implementação de MarkerGpuAtlas e SkiaMarkerEngine                                               |
| - Integração do novo renderizador em LocalMapServer.cs                                             |
| - Benchmarks de stress com 50.000 pontos em movimento contínuo                                      |
+----------------------------------------------------------------------------------------------------+
                                                  |
                                                  v
+----------------------------------------------------------------------------------------------------+
| SPRINT 3: CATÁLOGO DE ÍCONES CARTOGRÁFICOS NATIVOS                                                 |
| - Mapeamento e vetorização dos 150+ SVGs canônicos (Topografia, Redes, Transporte, etc.)           |
| - Criação de SvgMarkerCatalogService com categorias temáticas                                      |
| - Validação dos parâmetros SVG em alta resolução                                                   |
+----------------------------------------------------------------------------------------------------+
                                                  |
                                                  v
+----------------------------------------------------------------------------------------------------+
| SPRINT 4: INTERFACE BLAZOR E MODAL DE PROPRIEDADES                                                 |
| - Construção de SvgMarkerSelector.razor com pesquisa fonética e categorização                     |
| - Construção do editor de camadas de símbolos (Composite Marker Manager)                           |
| - Canvas de pré-visualização ao vivo com reflexo instantâneo no mapa                               |
+----------------------------------------------------------------------------------------------------+
                                                  |
                                                  v
+----------------------------------------------------------------------------------------------------+
| SPRINT 5: INTEROPERABILIDADE QML / SLD E HOMOLOGAÇÃO FINAL                                         |
| - Implementação de importação e exportação de estilos QGIS (.qml)                                  |
| - Bateria de testes de regressão com projetos reais de topografia e cadastro                      |
| - Geração da Release oficial do executável e documentação do usuário                               |
+----------------------------------------------------------------------------------------------------+
```

### 10.2 Bateria de Testes Automatizados

1. **Testes de Contrato de Renderização:** Validação de que 100% dos 150 SVGs compilam sem exceções de parsing e geram bitmaps não nulos.
2. **Testes de Sobrescrita de Dados:** Testes com tabelas contendo valores nulos, strings vazias, valores numéricos extremos (-9999, +999999) e caracteres especiais.
3. **Testes de Carga (Benchmark de Stress):**
   - Dataset de 100.000 pontos aleatórios: Tempo total de quadro inferior a 16.6 ms (garantindo 60 FPS).
   - Teste de consumo de memória contínuo durante 30 minutos de navegação ininterrupta para garantir ausência de memory leaks de texturas SkiaSharp.

### 10.3 Critérios de Aceite Final

- O operador consegue abrir as propriedades de qualquer camada vetorial de pontos e escolher entre mais de 150 ícones profissionais estilo QGIS.
- É possível trocar a cor de preenchimento e borda de qualquer ícone SVG e ver o resultado mudar na hora no mapa.
- É possível empilhar 2 ou mais ícones no mesmo ponto (ex: círculo de fundo + pino central).
- O mapa mantém navegação fluida e rápida mesmo com milhares de ícones na tela.
- O projeto salva e reabre preservando exatamente os ícones e cores configurados.

---
**Fim do Documento Técnico GEONEX-ENG-SYM-001**
