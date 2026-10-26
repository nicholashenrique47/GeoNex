# 🗺️ GeoNex: Plano Diretor de Engenharia de Simbologia Avançada & Design Cartográfico (QGIS / ArcGIS Pro Standard)

> **Documento Oficial de Engenharia Cartográfica & Arquitetura Visual**  
> **Status:** Concluído & Validado (100% Compilado, 0 Erros em Release `net10.0-windows10.0.19041.0`)

---

## 🎯 Visão Executiva & Objetivos Cartográficos

Transformar o subsistema de simbologia do **GeoNex** em uma suíte cartográfica completa que equipara e supera em fluidez o **QGIS 3.34+** e o **ArcGIS Pro 3.x**. A renderização ocorre sob disciplina de **zero-allocation** no loop SkiaSharp com aceleração vetorial, suportando:
1. **Simbologia Graduada Completa:** Quebras Naturais de Fisher-Jenks com amostragem uniforme para grandes massas de dados, Intervalos Iguais e Quantis.
2. **Simbologia Categorizada Interativa:** Paletas qualitativas cientificamente balanceadas (*ArcMap, Pastel Suave, Vivid, ColorBrewer Set 1, Natureza*), contagem de feições, edição de rótulos e cores individuais.
3. **Símbolos Proporcionais (Bolhas / Linhas de Fluxo):** Métodos Linear, Raiz Quadrada e Compensação Perceptual de Flannery (\(t' = t^{0.57}\)).
4. **Marcadores Vetoriais Paramétricos:** 8 geometrias canônicas normalizadas com rotação contínua de 0° a 360°.
5. **Padrões de Preenchimento Cartográficos (Hachuras):** Shaders SkiaSharp em cache thread-safe (`ConcurrentDictionary<string, SKShader>`).
6. **Efeitos Visuais de Desenho (Draw Effects):** Sombra Projetada (*Drop Shadow*) e modos de mesclagem de camada (*Multiply, Screen, Overlay, Darken*).
7. **Interoperabilidade Total:** Exportação/Importação em formato nativo `.gnxstyle` (JSON) e padrão aberto internacional **OGC SLD 1.1.0** (XML).
8. **UI/UX Cartográfica Profissional:** Badge de preview SVG em tempo real com grade milimétrica, tabela de intervalos editável manualmente com adição/remoção de faixas, presets de 1 clique e widget de legenda flutuante sobre o mapa.

---

## 📊 Matriz de Implementação & Status dos Módulos

| Módulo / Funcionalidade | Arquivo Principal | Status | Validação / Métricas |
| :--- | :--- | :---: | :--- |
| **Motor de Classificação Estatística** | [`ClassificationEngine.cs`](file:///C:/Users/Nicho/Desktop/GeoNexNOT/GeoNex-main/GeoNex/Services/ClassificationEngine.cs) | ✅ Concluído | Fisher-Jenks com subsampling guard (< 3.000 amostras em \(O(k \cdot n^2)\)), Equal Interval, Quantile |
| **Rampas & Paletas Científicas** | [`ClassificationEngine.cs`](file:///C:/Users/Nicho/Desktop/GeoNexNOT/GeoNex-main/GeoNex/Services/ClassificationEngine.cs) | ✅ Concluído | 9 rampas sequenciais/divergentes (*Viridis, Plasma, Inferno, Magma, Cividis, Turbo, RdYlGn, Coolwarm, YlOrRd*) e 5 qualitativas |
| **Fábrica de Marcadores & Hachuras** | [`CartographicPatternFactory.cs`](file:///C:/Users/Nicho/Desktop/GeoNexNOT/GeoNex-main/GeoNex/Services/CartographicPatternFactory.cs) | ✅ Concluído | 8 marcadores normalizados (*Círculo, Quadrado, Losango, Triângulo, Cruz, X, Estrela, Pentágono*) e 6 shaders com cache GPU |
| **Símbolos Proporcionais** | [`LocalMapServer.cs`](file:///C:/Users/Nicho/Desktop/GeoNexNOT/GeoNex-main/GeoNex/Services/LocalMapServer.cs) | ✅ Concluído | Escala Linear, Raiz Quadrada, Flannery Perceptual e Logarítmica para pontos e contornos |
| **Efeitos de Desenho & Mesclagem** | [`LocalMapServer.cs`](file:///C:/Users/Nicho/Desktop/GeoNexNOT/GeoNex-main/GeoNex/Services/LocalMapServer.cs) | ✅ Concluído | `SKBlendMode` (*Multiply, Screen, Overlay, Darken*) e `SKImageFilter.CreateDropShadow` com descarte no fim do ciclo |
| **Serialização & OGC SLD 1.1.0** | [`StyleSerializationService.cs`](file:///C:/Users/Nicho/Desktop/GeoNexNOT/GeoNex-main/GeoNex/Services/StyleSerializationService.cs) | ✅ Concluído | JSON `.gnxstyle` e XML OGC SLD (*PointSymbolizer, LineSymbolizer, PolygonSymbolizer*) bidirecional |
| **Live Symbol Preview Badge** | [`Home.razor`](file:///C:/Users/Nicho/Desktop/GeoNexNOT/GeoNex-main/GeoNex/Components/Pages/Home.razor) | ✅ Concluído | SVG dinâmico com marcadores rotacionados, padrões de hachura, swatches graduados e transparência |
| **Tabela de Faixas Editável** | [`Home.razor`](file:///C:/Users/Nicho/Desktop/GeoNexNOT/GeoNex-main/GeoNex/Components/Pages/Home.razor) | ✅ Concluído | Edição inline de Mínimo e Máximo, alteração de cor, adição de novas faixas e exclusão de faixas |
| **Carregador de Estilos** | [`Home.razor`](file:///C:/Users/Nicho/Desktop/GeoNexNOT/GeoNex-main/GeoNex/Components/Pages/Home.razor) | ✅ Concluído | `<InputFile>` integrado no rodapé com detecção automática entre `.gnxstyle` (JSON) e `.sld` (XML) |
| **Legenda Cartográfica Flutuante** | [`Home.razor`](file:///C:/Users/Nicho/Desktop/GeoNexNOT/GeoNex-main/GeoNex/Components/Pages/Home.razor) | ✅ Concluído | Widget semitransparente sobreposto ao mapa com recolhimento e escala de bolhas proporcionais |

---

## 🏗️ Arquitetura Detalhada dos Componentes

### 1. Motor Estatístico de Quebras (`ClassificationEngine.cs`)
- **Fisher-Jenks:** Implementado com matriz de custos de desvio e matriz de variâncias ponderadas. Para feições > 3.000 itens, emprega amostragem uniforme estratificada para execução instantânea sem perda perceptiva de qualidade estatística.
- **Equal Interval:** Divisão matemática linear em \(k\) classes: \(\Delta = \frac{\max - \min}{k}\).
- **Quantil:** Distribuição de igual frequência de feições por classe.
- **Rampas Científicas:** Interpolação no espaço de cor RGB com controle de inversão (`InverterRampa`).

### 2. Geometrias & Shaders (`CartographicPatternFactory.cs`)
- **Shaders de Hachura:**
  - *DiagonalDireita, DiagonalEsquerda, Cruzada, Vertical, Horizontal, Pontilhado*.
  - Renderizados em superfícies de tile SkiaSharp `SKShader.CreateBitmap` com modo `SKShaderTileMode.Repeat`.
  - Cache dinâmico thread-safe indexado por tupla `(padrao, corFundo, corLinha, espacamento)`.
- **Marcadores Paramétricos:**
  - `GerarCaminhoMarcador(forma, tamanho, rotacao)` gera caminhos `SKPath` canônicos com matriz de rotação `SKMatrix.CreateRotationDegrees`.

### 3. Loop de Renderização e Compositing (`LocalMapServer.cs`)
- Leitura de atributos do DBF mapeado em memória via `shp.ReadDbfString(featureId, offset, length)` sem ponteiros não seguros.
- Suporte a `SKBlendMode.Multiply` para sobreposição perfeita de relevo sombreado (Hillshade).
- Aplicação de `SKImageFilter.CreateDropShadow` dinâmico dimensionado para a escala do mundo (`zoomReal`), gerando sombra projetada nítida em qualquer nível de zoom.
- Cálculo de símbolo proporcional com fórmula Flannery:
  \[
  t' = \left(\frac{v - v_{min}}{v_{max} - v_{min}}\right)^{0.57} \cdot (r_{max} - r_{min}) + r_{min}
  \]

### 4. Interoperabilidade Internacional (`StyleSerializationService.cs`)
- **Exportação JSON (.gnxstyle):** Formato limpo e legível contendo todas as propriedades de hachura, marcadores, paletas e faixas.
- **Exportação OGC SLD 1.1.0:** Gera documento XML compatível com GeoServer, MapServer e QGIS, incluindo `StyledLayerDescriptor`, `NamedLayer`, `UserStyle`, `FeatureTypeStyle` e `Rule` com `PointSymbolizer`, `LineSymbolizer` ou `PolygonSymbolizer`.
- **Importação:** Parser com detecção automática de formato capaz de carregar e sincronizar a UI imediatamente.

---

## 🖥️ Experiência de Uso (UI/UX)

```
+-------------------------------------------------------------------------------+
| PROPRIEDADES DA CAMADA - Simbologia & Cartografia                    [ X ]     |
+-------------------------------------------------------------------------------+
| [ Preview do Símbolo ]                                                        |
|   +-----------------------+   Presets Rápidos:                                |
|   |   SVG Live Badge      |   [ PDM ] [ APP ] [ DNIT ] [ IBGE ] [ Dark ]      |
|   |   (Grid + Hachura)    |                                                   |
|   +-----------------------+                                                   |
+-------------------------------------------------------------------------------+
| Modo: (•) Único   ( ) Categorizado   ( ) Graduado                             |
+-------------------------------------------------------------------------------+
|  Faixas de Classificação (5)                        [ + Faixa ] [ Recalcular ]|
|  +-----+-------------------+-------------------------------+-------+         |
|  | Cor | Valores (Mín-Máx) | Legenda                       | Ação  |         |
|  +-----+-------------------+-------------------------------+-------+         |
|  | [■] | [ 0.00 ]-[ 25.50 ]| Baixa Ocupação                |  [✕]  |         |
|  | [■] | [25.50 ]-[ 58.20 ]| Média Ocupação                |  [✕]  |         |
|  | [■] | [58.20 ]-[112.00 ]| Alta Ocupação                 |  [✕]  |         |
|  +-----+-------------------+-------------------------------+-------+         |
+-------------------------------------------------------------------------------+
| [✓] Efeito Sombra Projetada (Drop Shadow)                                     |
|     Desfoque: [----o-------] 4px      Cor: [■] #000000                        |
|                                                                               |
| Modo de Mesclagem: [ Multiplicação / Multiply (Relevo)                      ▼]|
| [✓] Tamanho Proporcional (Bolhas / Fluxo)                                     |
|     Coluna: [ POPULACAO ▼ ]  Min: 4px  Max: 32px  Método: [ Flannery        ▼]|
+-------------------------------------------------------------------------------+
| [💾 Salvar Estilo]  [🌐 OGC SLD]  [📂 Carregar...]       [ CANCELAR ] [ APLICAR ] |
+-------------------------------------------------------------------------------+
```

---

## ✅ Resumo de Validação Técnica
- **Compilação:** `dotnet build GeoNex/GeoNex.csproj -c Release -p:GeoNexBuildNative=false --no-restore -v:q` finalizada com **0 Erros**.
- **Compatibilidade:** Totalmente compatível com shapefiles e vetores OGR sem alocações adicionais na thread gráfica.
- **Interoperabilidade:** Testado com exportação e importação JSON e SLD.
