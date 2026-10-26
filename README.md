# 🗺️ GeoNex

**GeoNex** é um aplicativo desktop de geoprocessamento e Sistema de Informação Geográfica (SIG / GIS) de alta performance para Windows x64, desenvolvido em .NET 10, C# e SkiaSharp, com aceleração nativa C++/AVX2, GDAL/OGR e NetTopologySuite.

O GeoNex foi projetado com rigorosa disciplina de **zero-allocation** no ciclo de renderização gráfica, processamento assíncrono em segundo plano e motores estatísticos e vetoriais comparáveis e interoperáveis com o **QGIS 3.34+ (Processing Toolbox / PAL)** e **ArcGIS Pro 3.x (Spatial Analyst / Maplex)**.

---

## 🌟 Principais Recursos e Módulos

### 1. 🌐 Geoprocessamento Vetorial Avançado & Caixa de Ferramentas Espaciais (QGIS / ArcGIS Standard)

O GeoNex conta com um catálogo extensível de **30 algoritmos de geoprocessamento** distribuídos em 8 categorias temáticas formais, executados de forma totalmente desacoplada da interface via threads assíncronas dedicadas:

#### A. Superposição Topológica Robusta (Vector Overlay)
* **Recorte (*Clip*):** Preserva atributos da camada de entrada com recorte exato pelo polígono de máscara. Filtragem dimensional estrita (`Polygon`, `LineString`, `Point`) impedindo colapso topológico.
* **Intersecção (*Intersection*):** Geometrias comuns combinadas com fusão integral de tabelas de atributos e desambiguação de nomes de colunas no padrão DBF (limite de 10 caracteres com sufixos `_1`, `_2`).
* **Diferença (*Difference / Erase*):** Subtração topológica com suporte a anéis internos (furos) e geração limpa de `MultiPolygon`.
* **Diferença Simétrica (*Symmetrical Difference / XOR Espacial*):** Retém exclusivamente as porções espaciais presentes em $A$ ou em $B$, descartando trechos coincidentes com desambiguação tabular completa.
* **União Topológica Plena (*Full Topology Union*):** Particiona todas as geometrias em nós planares (`UnaryUnionOp`), gerando cobertura poligonal estrita contígua e sem sobreposições mútuas.
* **Atualização Espacial (*Spatial Update / Patch*):** Aplica uma camada prioritária de atualização substituindo trechos da camada base com recortes precisos de sobreposição.
* **Auto-Reparo de Topologia:** Tratamento transparente de auto-intersecções e geometrias corrompidas via `Buffer(0)` antes de cada operação booleana.

#### B. Análise de Proximidade e Tesselação Contínua
* **Polígonos de Voronoi (*Thiessen Polygons*):** Gera células poligonais de Dirichlet/Thiessen a partir de pontos com herança integral de atributos da feição geradora e envelope de expansão ajustável.
* **Triangulação de Delaunay (*TIN*):** Constrói malha triangular contígua não-degenerada garantindo o critério de círculo vazio (*empty circumcircle*) para modelagem de superfície e relevo.
* **Análise do Vizinho Mais Próximo (*Clark-Evans NNA*):** Calcula a matriz de proximidade $O(N \log N)$ via `STRtree`, gera vetores visuais de conexão ao vizinho imediato e calcula o índice $R$ de agregação espacial ($R < 1$ agrupado, $R = 1$ aleatório, $R > 1$ disperso) e escore $Z$.

#### C. Generalização Cartográfica e Suavização
* **Simplificação Douglas-Peucker com Preservação Topológica:** Reduz a densidade de vértices com base em tolerância métrica perpendicular garantindo anéis fechados e sem auto-intersecções (`TopologyPreservingSimplifier`).
* **Simplificação por Área Efetiva (*Visvalingam-Whyatt*):** Avalia a área do triângulo formado por três vértices consecutivos e elimina progressivamente nós com menor relevância geométrica.
* **Suavização Cartográfica de Chaikin (*Corner-Cutting*):** Gera curvas suaves contínuas (aproximação B-spline quadrática) substituindo cantos angulares por segmentos ponderados em 1/4 e 3/4 com preservação de extremidades.

#### D. Decomposição e Conversão Estrutural
* **Decompor Multipartes para Partes Simples (*Explode Multipart*):** Desmembra geometrias compostas (`MultiPolygon`, `MultiLineString`, `MultiPoint`) em feições individuais com preservação dos atributos originais e índices `PART_IDX` e `PART_TOTAL`.
* **Agrupar Partes Simples em Multipartes (*Combine Singlepart*):** Agrupa feições isoladas em geometrias multipartes com base em um campo categórico comum.
* **Polígonos para Linhas de Borda (*Polygon to Boundary Lines*):** Converte cascas externas e anéis internos em feições lineares mantendo identificadores `RING_TYPE` e `RING_IDX`.
* **Poligonização de Linhas (*Line Polygonizer*):** Monta rede planar topológica a partir de segmentos desconexos e extrai todas as faces poligonais fechadas e ciclos de borda.
* **Extração Analítica de Vértices:** Extrai nós de linhas e polígonos calculando azimute cartesiano ($0^\circ\text{--}360^\circ$), distância métrica acumulada do início do traçado (`DIST_M`) e flags de extremidade (`IS_ENDPT`).

#### E. Segmentação Linear e Partição
* **Quebrar Linhas em Intersecções (*Split Lines at Intersections*):** Nodificação planar de redes viárias e hidrográficas dividindo linhas em todos os cruzamentos e confluências.
* **Estaqueamento Linear Métrico (*Line Chaining / Stationing*):** Subdivide eixos lineares em estacas regulares de comprimento métrico fixo com marcação precisa de quilometragem inicial e final (`STA_START`, `STA_END`).
* **Partição de Polígonos por Linhas (*Split Polygons with Lines*):** Utiliza feições lineares como lâminas cortantes para fatiar polígonos em fragmentos atômicos independentes.

#### F. Malhas Espaciais e Amostragem Cartográfica
* **Grade Regular Cartesiana:** Cria malha ortogonal uniforme de polígonos retangulares/quadrados a partir de extensões espaciais ou envelopes de camadas.
* **Grade Hexagonal Tesselada (*Hexbins*):** Gera mosaico contíguo de hexágonos regulares de raio $R$ com deslocamento alternado de linhas para agrupamento estatístico de densidade.
* **Amostragem Aleatória Poisson Disk:** Distribui pontos aleatórios garantindo distância euclidiana mínima configurável entre quaisquer pares de amostras.
* **Amostragem Regular de Pontos:** Cria matriz pontual reticulada cobrindo integralmente o interior de feições poligonais.

#### G. Estatísticas Zonais e Agregação Multidimensional
* **Contagem e Soma de Pontos em Polígonos:** Agrega métricas pontuais por zona poligonal (`PT_COUNT`, `PT_SUM`, `PT_MEAN`, `PT_MIN`, `PT_MAX`) acelerada por geometrias preparadas (`IPreparedGeometry`).
* **Comprimento e Densidade Linear em Polígonos:** Calcula a quilometragem linear total de vias ou redes e densidade espacial ($\text{km}/\text{km}^2$) por polígono zonal.
* **Junção Espacial Avançada (*Spatial Join*):** Funde tabelas de atributos tabulares com base em 6 predicados topológicos OGC estritos (*Intersects*, *Contains*, *Within*, *Touches*, *Crosses*, *WithinDistance*).

#### H. Agregação e Geometrias Construtivas Fundamentais
* **Dissolução Global e Categórica (*Dissolve*):** Agrupamento contíguo com `CascadedPolygonUnion` e funções de agregação numérica.
* **Buffer Paramétrico:** Buffers métricos com controle de pontas (*Round*, *Flat*, *Square*), junções (*Round*, *Miter*, *Bevel*) e dissolução de fronteiras internas.
* **Envoltória Convexa (*Convex Hull*):** Menor polígono convexo envolvente individual ou sobre nuvens globais.
* **Centroides e Polo de Inacessibilidade (*Polylabel*):** Garante matematicamente o ponto no interior de polígonos côncavos em ferradura ou formato de "C".

#### I. Arquitetura do Catálogo Extensível e Motor em Lote (*Batch Queue*)
* **Contrato `IGeoprocessingAlgorithm`:** Interface base desacoplada para extensibilidade de ferramentas.
* **`ProcessingAlgorithmRegistry`:** Catálogo central singleton com registro de 30 algoritmos, busca fuzzy por nome, descrição ou tags e agrupamento por categorias formais.
* **`BatchGeoprocessingEngine`:** Fila FIFO assíncrona com processamento transacional em lote de diretórios inteiros de arquivos Shapefile.

---

### 2. 🏷️ Motor de Rótulos Dinâmicos & Posicionamento Cartográfico (QGIS PAL / Maplex Standard)
* **Polo de Inacessibilidade (Polylabel / Maior Círculo Inscrito):** Posicionamento inteligente para polígonos côncavos em formato de "C", ferraduras, arquipélagos ou fiordes. Garante matematicamente que o rótulo nunca caia fora do corpo do polígono ou sobre vazios.
* **Resolução Espacial de Conflitos e Anti-Colisão $O(1)$:** Grade espacial de resolução de sobreposição em coordenadas de tela (*Screen-Space Bitwise Spatial Hash Grid* `((long)x << 32) | (uint)y`). Processa mais de 10.000 avaliações de candidatos a rótulos em menos de 15ms sem alocações de heap.
* **Geração de Candidatos em 8 Quadrantes Cartesianos:** Posicionamento preferencial Superior-Direito (*Top-Right*) com fallback ordenado por prioridade (*Top*, *Right*, *Top-Left*, *Bottom-Right*, *Left*, *Bottom*, *Bottom-Left*).
* **Proteção contra Inversão Angular (Auto-Flip):** Para feições lineares, detecta se o vetor de alinhamento está no intervalo $(90^\circ, 270^\circ)$ e aplica rotação automática de $180^\circ$, impedindo rótulos invertidos.
* **Linhas de Chamada Dinâmicas (*Callouts / Leader Lines*):** Quando um rótulo sofre deslocamento ou fallback para contornar densidades urbanas ou feições vizinhas, uma linha de chamada cartográfica nítida conecta a âncora da feição ao texto.
* **Tipografia Cartográfica Profissional:** Famílias tipográficas tipicamente cartográficas (*Segoe UI, Arial, Trebuchet MS, Consolas, Georgia, Times New Roman, Impact*), opacidade contínua e Halo Cartográfico configurável para contraste.
* **Filtros e Gatilhos de Escala:** Visibilidade por intervalo de escala cartográfica (Escala Mínima / Máxima $1:N$) e descarte por limiar de pixels em tela.

---

### 3. 🎨 Simbologia Avançada & Design Cartográfico Profissional
* **Simbologia Graduada:**
  * **Quebras Naturais de Fisher-Jenks:** Otimização matemática com proteção de amostragem uniforme para execução instantânea em feições volumosas (> 3.000 pontos) em menos de 50ms.
  * **Intervalos Iguais (*Equal Interval*) & Quantis (*Quantiles*):** Divisão linear e fatiamento equilibrado.
  * **Tabela de Faixas Interativa:** Ajuste manual de valores inline, adição/remoção de faixas e cores customizadas.
* **Simbologia Categorizada:** Mapeamento automático de valores distintos a partir do DBF com 5 paletas cartográficas qualitativas.
* **Símbolos Proporcionais (Bolhas e Linhas de Fluxo):** Dimensionamento dinâmico baseado em atributos numéricos com métodos **Linear**, **Raiz Quadrada** e **Compensação Perceptual de Flannery** ($t' = t^{0.57}$).
* **Marcadores Vetoriais Paramétricos:** 8 Formas canônicas (*Círculo, Quadrado, Losango, Triângulo, Cruz, X, Estrela, Pentágono*) com rotação de 0° a 360°.
* **Padrões de Preenchimento Cartográficos (Hachuras):** 6 padrões em shaders SkiaSharp em cache GPU thread-safe com ladrilhamento contínuo.
* **Efeitos de Desenho (*Draw Effects*) e Modos de Mesclagem:** Sombra projetada (*Drop Shadow*) e modos de mesclagem (*Multiply*, *Screen*, *Overlay*, *Darken*, *Normal*).
* **Interoperabilidade Padrão Aberto:** Exportação e Importação nativa em **JSON (`.gnxstyle`)** e **OGC SLD 1.1.0 (XML)** compatível com QGIS, GeoServer e MapServer.

---

### 4. ⚡ Motor de Renderização de Alta Performance
* **Zero-Allocation no Loop Gráfico:** Reutilização estrita de pincéis, matrizes matemáticas e buffers nativos.
* **Aceleração AVX2 / SIMD:** Parsers e transformações de coordenadas em C++ nativo (`GeoNex.Core`).
* **Índice Espacial Particionado:** Indexação hierárquica por chunks com culling agressivo de viewport.
* **LOD (Level of Detail) Adaptativo:** Simplificação dinâmica de geometrias com base na densidade de pixels da tela.

---

### 5. 📐 Edição e Digitalização Vetorial
* **Snapping Magnético:** Aderência a vértices e arestas de feições existentes com tolerância configurável.
* **Histórico de Vértices:** Sistema completo de desfazer/refazer (*Undo/Redo*) durante a vetorização.
* **Construção Geométrica Assistida:** Travamento ortogonal, restrição de distância fixa e ângulo.
* **Cálculo Geodésico:** Medição precisa de distâncias no elipsoide e áreas planimétricas.

---

### 6. 💾 Persistência de Projetos (`.gnx`)
* Armazenamento relacional SQLite com validação de fontes e links relativos portáveis.
* Migração automática de schemas e recuperação tolerante a falhas.

---

## 🧪 Suíte de Testes Automatizados

O GeoNex possui suítes de testes automatizados que validam rigorosamente contratos matemáticos, topológicos, espaciais e cartográficos:

```powershell
# 1. Testes do Motor de Geoprocessamento Avançado (Fases 1 e 2 - 17 módulos, 360 asserções)
dotnet run --project tests/GeoprocessingCore/GeoprocessingCore.csproj

# 2. Testes de Simbologia Cartográfica e Rótulos Dinâmicos (11 módulos, 194 asserções)
dotnet run --project tests/SymbologyCore/SymbologyCore.csproj

# 3. Testes de Digitalização e Edição Vetorial (179 asserções)
dotnet run --project tests/DigitizingCore/DigitizingCore.csproj

# 4. Testes de Escala Cartográfica de Impressão e DPI (60 asserções)
dotnet run --project tests/PrintScale/PrintScale.csproj
```
