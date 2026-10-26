# 🛰️ GeoNex: Plano Diretor de Engenharia Pesada — Motor de Geoprocessamento & Análise Espacial Avançada (QGIS Processing / ArcGIS Pro Spatial Analyst Standard)

> **Documento Oficial de Engenharia de Software, Computação Gráfica Geoespacial & Topologia Vetorial**  
> **Status:** Especificação Arquitetural & Plano Executivo de Implementação  
> **Alvo:** Equivalência Técnica, Robustez Algorítmica e Desempenho Comparáveis ao QGIS Processing Framework (`QgsProcessingAlgorithm`) e ArcGIS Pro Analysis Toolbox  
> **Disciplina:** Multithreading Assíncrono com `CancellationToken`, Indexação Espacial STRtree $O(N \log M)$, Reprojeção Geodésica On-The-Fly, Zero-Heap Invalidation e Tolerância a Falhas Topológicas (`MakeValid` / `Buffer(0)`)

---

## 📑 Índice Geral

1. [Visão Executiva & Objetivos Estratégicos](#1-visão-executiva--objetivos-estratégicos)
2. [Arquitetura Geral do Subsistema de Geoprocessamento](#2-arquitetura-geral-do-subsistema-de-geoprocessamento)
3. [Especificação Algorítmica das Operações Espaciais](#3-especificação-algorítmica-das-operações-espaciais)
   - 3.1. [Recorte de Camada (Clip / Intersect-Crop)](#31-recorte-de-camada-clip--intersect-crop)
   - 3.2. [Intersecção de Camadas (Intersection / Dual-Layer Overlay)](#32-intersecção-de-camadas-intersection--dual-layer-overlay)
   - 3.3. [Diferença Espacial (Difference / Erase)](#33-diferença-espacial-difference--erase)
   - 3.4. [Dissolução de Geometrias (Dissolve All / Dissolve by Attribute)](#34-dissolução-de-geometrias-dissolve-all--dissolve-by-attribute)
   - 3.5. [Zona de Amortecimento em Lote (Layer Buffer Engine)](#35-zona-de-amortecimento-em-lote-layer-buffer-engine)
   - 3.6. [Envoltória Convexa (Convex Hull)](#36-envoltória-convexa-convex-hull)
   - 3.7. [Centroides e Polos de Inacessibilidade (Centroids & Polylabel)](#37-centroides-e-polos-de-inacessibilidade-centroids--polylabel)
   - 3.8. [União Espacial de Atributos (Spatial Join)](#38-união-espacial-de-atributos-spatial-join)
4. [Engenharia de Dados: Fusão de Esquemas DBF/OGR e Atributos](#4-engenharia-de-dados-fusão-de-esquemas-dbfogr-e-atributos)
5. [Reprojeção On-The-Fly e Harmonização de CRS/SRID](#5-reprojeção-on-the-fly-e-harmonização-de-crssrid)
6. [Resiliência Topológica e Auto-Reparo (Topology Healing)](#6-resiliência-topológica-e-auto-reparo-topology-healing)
7. [Design da Interface Gráfica (UI/UX) — Padrão Enterprise (Zero Emojis)](#7-design-da-interface-gráfica-uiux--padrão-enterprise-zero-emojis)
8. [Matriz de Componentes e Estrutura de Arquivos C#](#8-matriz-de-componentes-e-estrutura-de-arquivos-c)
9. [Arquitetura da Suíte de Testes Automatizados (tests/GeoprocessingCore)](#9-arquitetura-da-suíte-de-testes-automatizados-testsgeoprocessingcore)
10. [Cronograma Executivo de Implementação em 5 Fases](#10-cronograma-executivo-de-implementação-em-5-fases)
11. [Critérios Rigorosos de Aceite & Validação de Qualidade](#11-critérios-rigorosos-de-aceite--validação-de-qualidade)

---

## 🎯 1. Visão Executiva & Objetivos Estratégicos

O **GeoNex** foi projetado desde o princípio com rigor matemático e disciplina de renderização em tempo real (SkiaSharp + C++/AVX2 nativo), alcançando 60 FPS contínuos e recursos avançados de vetorização interativa, simbologia cartográfica proporcional e rótulos dinâmicos com resolução de conflitos em $O(1)$.

Entretanto, para que um Sistema de Informação Geográfica (SIG / GIS) seja adotado como ferramenta primária em gabinetes de engenharia ambiental, prefeituras, agrimensura, infraestrutura e mineração, ele precisa oferecer capacidade analítica autônoma de **geoprocessamento em lote (*Batch Geoprocessing*)**. 

A edição manual vértice a vértice resolve a vetorização pontual; porém, quando o analista precisa responder a perguntas espaciais complexas — como:
- *"Quais trechos de rodovias federais cruzam áreas indígenas ou unidades de conservação?"* (Intersecção)
- *"Qual a malha viária pertencente estritamente ao município de Campinas?"* (Recorte / Clip)
- *"Qual a área líquida de lavoura subtraindo as áreas de preservação permanente (APP)?"* (Diferença)
- *"Como gerar o polígono territorial dos estados a partir dos 5.570 municípios do IBGE?"* (Dissolver)
- *"Como gerar a faixa de servidão de 15 metros ao redor de um gasoduto?"* (Buffer)

O usuário depende de um motor analítico robusto, transacional, imune a travamentos e com validação topológica estrita.

### Pilares da Engenharia do Motor:
1. **Desacoplamento Assíncrono com Background Tasks:**
   - Toda execução geométrica ocorre fora da thread de UI do Blazor, utilizando `Task.Run` e comunicando progresso contínuo através da interface nativa `IProgress<GeoprocessingProgress>`.
   - O usuário pode interromper a qualquer momento o processo via `CancellationToken`, com cancelamento cooperativo instantâneo e limpeza de recursos temporários.
2. **Desempenho Algorítmico com Indexação Espacial STRtree 2D:**
   - Cruzamentos espaciais ingênuos possuem complexidade quadrática $O(N \times M)$. Em camadas com 10.000 feições cada, isso exige $10^8$ testes de intersecção, congelando a aplicação por minutos.
   - O motor GeoNex indexa a camada de sobreposição utilizando árvores `STRtree<T>` (Sort-Tile-Recursive), reduzindo o espaço de busca para $O(N \log M + K)$ e permitindo executar recortes e intersecções em segundos.
3. **Harmonização Automática de Projeções (On-The-Fly CRS Transformation):**
   - Caso a camada de entrada esteja em coordenadas geográficas (WGS84 EPSG:4326) e a camada de sobreposição em coordenadas projetadas métricas (SIRGAS 2000 UTM 23S EPSG:31983), o motor reprojeta dinamicamente as geometrias em memória antes da análise, garantindo precisão milimétrica.
4. **Auto-Reparo Topológico (`MakeValid` e `Buffer(0)`):**
   - Geometrias oriundas de Shapefiles antigos ou softwares CAD frequentemente contêm auto-interseções (*bow-ties*), anéis desconectados ou orientações incorretas de vértices.
   - O motor executa uma etapa de sanitização topológica automática antes de disparar predicados booleanos, evitando exceções do tipo `TopologyException: side location conflict`.
5. **Persistência Atômica e Registro Imediato no Canvas:**
   - O resultado é salvo diretamente em Shapefile padrão OGR (`.shp`, `.shx`, `.dbf`, `.prj`) ou GeoPackage (`.gpkg`), com transação segura em disco. Ao término, a nova camada é automaticamente injetada na lista de camadas do projeto, renderizada no mapa e centralizada se desejado.
6. **Interface Enterprise (Zero Emojis):**
   - Padrão sóbrio de softwares de engenharia (QGIS / ArcGIS Pro / AutoCAD Map 3D), utilizando tipografia técnica, ícones SVG nítidos e feedback em tempo real.

---

## 🏛️ 2. Arquitetura Geral do Subsistema de Geoprocessamento

O subsistema de geoprocessamento é estruturado em três camadas bem definidas:

```
+===================================================================================================+
|                               CAMADA DE INTERFACE & APRESENTAÇÃO (UI)                             |
|                                                                                                   |
|  [ Ribbon Tab: Geoprocessamento ]                                                                 |
|  ├── Painel Sobreposição: [Recortar (Clip)]  [Intersecção]  [Diferença]  [União Espacial]         |
|  ├── Painel Agregação:    [Dissolver]  [Mesclar Camadas]                                          |
|  └── Painel Geometria:    [Buffer em Lote]  [Envoltória Convexa]  [Centroides & Polylabel]        |
|                                         │                                                         |
|                                         ▼ Abre diálogo modal parametrizado                        |
|  [ GeoprocessingModal.razor ]                                                                     |
|  ├── Seletor de Camada de Entrada e Camada de Sobreposição                                         |
|  ├── Checkboxes de "Apenas Feições Selecionadas"                                                  |
|  ├── Parâmetros específicos (Tolerância, Distância do Buffer, Campo de Dissolução)                 |
|  ├── Barra de Progresso Contínua (0% a 100%) & Velocidade (feições/seg)                           |
|  └── Log Técnico de Execução (Mensagens, Avisos de Topologia e Tempo Total)                       |
+===================================================================================================+
                                          │
                                          ▼ Invocação Assíncrona (CancellationToken, IProgress)
+===================================================================================================+
|                             CAMADA DE ORQUESTRAÇÃO & MOTOR ESPACIAL                               |
|                                                                                                   |
|  [ GeoprocessingEngine.cs ] (Serviço Central Singleton)                                           |
|  ├── Validação de Pré-Condições (Existência de Feições, Verificação de Drivers OGR)                |
|  ├── Reprojeção de Coordenadas sob demanda (OSR CoordinateTransformation)                        |
|  ├── Indexador Espacial STRtree 2D para Camadas de Sobreposição                                    |
|  ├── Gerenciador de Fusão de Esquemas de Atributos DBF (Resolução de Colisão de Nomes)             |
|  └── Dispatcher para os Módulos Algorítmicos Especializados                                       |
+===================================================================================================+
                                          │
                                          ▼ Execução Geométrica Vetorial Pura
+===================================================================================================+
|                              CAMADA DE ALGORITMOS ESPACIAIS (NTS)                                 |
|                                                                                                   |
|  ├── VectorOverlayAlgorithms.cs                                                                   |
|  │   ├── Clip(inputGeom, maskGeom)                                                                |
|  │   ├── Intersection(geomA, geomB, attrsA, attrsB)                                               |
|  │   └── Difference(inputGeom, maskUnion)                                                         |
|  │                                                                                                |
|  ├── VectorAggregationAlgorithms.cs                                                               |
|  │   ├── DissolveByAttribute(features, fieldName, aggregationMode)                                |
|  │   └── DissolveAll(features)                                                                    |
|  │                                                                                                |
|  └── VectorGeometryAlgorithms.cs                                                                  |
|      ├── ComputeBuffer(geometry, distance, quadrantSegments, endCap, joinStyle)                   |
|      ├── ComputeConvexHull(geometries, perFeature)                                                |
|      └── ExtractCentroids(geometries, usePolylabel)                                                |
+===================================================================================================+
                                          │
                                          ▼ Transação de Escrita em Disco
+===================================================================================================+
|                              CAMADA DE PERSISTÊNCIA ATÔMICA (OGR)                                 |
|                                                                                                   |
|  [ VectorEditPersistenceService.cs ] & OGR Driver Factory                                         |
|  ├── Criação do Data Source ESRI Shapefile ou OGC GeoPackage                                      |
|  ├── Escrita em Bloco das Geometrias e Registros DBF                                               |
|  ├── Geração do Arquivo de Projeção WKT (.prj)                                                     |
|  └── Registro da Nova Camada em CamadasAtivas com Atualização do Viewport SkiaSharp               |
+===================================================================================================+
```

---

## 📐 3. Especificação Algorítmica das Operações Espaciais

### 3.1. Recorte de Camada (Clip / Intersect-Crop)

O **Recorte (*Clip*)** atua como uma "cortadeira de biscoitos" geoespacial: todas as feições da camada de entrada (pontos, linhas ou polígonos) são geometricamente truncadas para respeitarem estritamente os limites da camada de máscara.

```
       CAMADA DE ENTRADA (Linhas de Drenagem)          CAMADA DE MÁSCARA (Bacia Hidrográfica)
      +--------------------------------------+           +----------------------------------+
      |      \                  /            |           |             /---------\          |
      |       \----------------/             |     ∩     |            /           \         |
      |                 \                    |           |           |             |        |
      |        /---------\                   |           |            \           /         |
      |       /           \                  |           |             \---------/          |
      +--------------------------------------+           +----------------------------------+
                                           ═════════════════►
                                  RESULTADO DO RECORTE (Clip Output)
                                 +----------------------------------+
                                 |             /---------\          |
                                 |            /---\     / \         |
                                 |           |     \---/   |        |
                                 |            \           /         |
                                 |             \---------/          |
                                 +----------------------------------+
```

#### Definição Matemática:
Seja $A = \{ (G_{A,1}, T_{A,1}), (G_{A,2}, T_{A,2}), \dots, (G_{A,n}, T_{A,n}) \}$ o conjunto de feições de entrada, com $G_{A,i}$ representando a geometria e $T_{A,i}$ a tupla de atributos da feição $i$.
Seja $B = \{ G_{B,1}, G_{B,2}, \dots, G_{B,m} \}$ o conjunto de geometrias poligonais da camada de máscara.

A máscara unificada contínua $M$ é computada via união topológica:

$$M = \bigcup_{j=1}^{m} G_{B,j}$$

Para cada feição de entrada $i \in \{1, \dots, n\}$:
1. **Filtro de Rejeição Rápida por Bounding Box em $O(1)$:**
   Se $\text{Env}(G_{A,i}) \cap \text{Env}(M) = \emptyset$, a feição é sumariamente descartada sem custos computacionais adicionais.
2. **Filtro de Aceitação Direta em $O(1)$:**
   Se $\text{Env}(G_{A,i}) \subseteq \text{Env}(M)$ e $M.\text{Contains}(G_{A,i}) = \text{true}$, a geometria original está totalmente contida. Emite-se $(G_{A,i}, T_{A,i})$ diretamente para a camada de saída.
3. **Corte Topológico Exato:**
   Caso haja sobreposição parcial:
   $$G'_{A,i} = G_{A,i} \cap M$$
   Se $G'_{A,i} \neq \emptyset$ e $\dim(G'_{A,i}) = \dim(G_{A,i})$, emite-se $(G'_{A,i}, T_{A,i})$. Os atributos originais $T_{A,i}$ são preservados integralmente sem alteração.

---

### 3.2. Intersecção de Camadas (Intersection / Dual-Layer Overlay)

A **Intersecção** realiza o cruzamento espacial pleno entre duas camadas de feições ($A$ e $B$). Ao contrário do recorte simples, a intersecção gera fragmentos geométricos correspondentes a cada par de sobreposição e **combina as tabelas de atributos de ambas as camadas**.

```
          CAMADA A (Uso do Solo)                         CAMADA B (Zoneamento Ambiental)
   +------------------------------------+             +------------------------------------+
   | [ ID: 1 | USO: Floresta Nativa ]   |             | [ ZONA: Proteção Integral (ZPI) ]  |
   |                                    |      ∩      |                                    |
   | [ ID: 2 | USO: Agricultura Soja ]  |             | [ ZONA: Ocupação Moderada (ZOM) ]  |
   +------------------------------------+             +------------------------------------+
                                        ═════════════════►
                             CAMADA RESULTANTE DA INTERSECÇÃO (Fragmentos)
   +---------------------------------------------------------------------------------------+
   | Fragmento 1: G1 = A1 ∩ B1 | USO: Floresta Nativa | ZONA: Proteção Integral (ZPI)     |
   | Fragmento 2: G2 = A1 ∩ B2 | USO: Floresta Nativa | ZONA: Ocupação Moderada (ZOM)     |
   | Fragmento 3: G3 = A2 ∩ B2 | USO: Agricultura Soja| ZONA: Ocupação Moderada (ZOM)     |
   +---------------------------------------------------------------------------------------+
```

#### Pipeline com Indexação Espacial STRtree:
1. **Indexação da Camada B:**
   - Cria-se uma árvore `STRtree<Feature>` contendo todas as feições da camada $B$. A árvore organiza as caixas delimitadoras em uma hierarquia de nós espaciais bidimensionais.
2. **Loop de Varredura da Camada A:**
   - Para cada feição $a \in A$:
     - Realiza-se a consulta `var candidatos = treeB.Query(a.Geometry.EnvelopeInternal);`
     - Para cada feição candidata $b \in candidatos$:
       - Se $a.Geometry.\text{Intersects}(b.Geometry)$:
         - $G_{ab} = a.Geometry.\text{Intersection}(b.Geometry)$
         - Se $G_{ab}$ for não-vazio e mantiver a dimensionalidade geométrica esperada:
           - Funde-se a tupla de atributos: $T_{ab} = \text{MergeAttributes}(T_a, T_b)$
           - Adiciona-se a nova feição $(G_{ab}, T_{ab})$ à lista de saída.

---

### 3.3. Diferença Espacial (Difference / Erase)

A operação de **Diferença** (também conhecida como *Erase* ou *Subtração Booleana*) remove da camada de entrada todas as porções geométricas que coincidam com a camada de sobreposição.

$$G'_{A,i} = G_{A,i} \setminus \left( \bigcup_{j=1}^{m} G_{B,j} \right)$$

```
        CAMADA DE ENTRADA (Talhões de Floresta)           CAMADA DE SUBTRAÇÃO (Área Queimada)
   +-----------------------------------------------+     +-----------------------------------+
   | ############################################# |     |           /-------------\         |
   | ############################################# |  ─  |          /  FOGO 2024   \        |
   | ############################################# |     |          \             /         |
   | ############################################# |     |           \-----------/          |
   +-----------------------------------------------+     +-----------------------------------+
                                           ═════════════════►
                       CAMADA DE SAÍDA (Área Remanescente Preservada)
   +-----------------------------------------------+
   | ############################################# |
   | #################         /-------------\ ### |
   | #################        /   ÁREA VAZIA  \ ## |
   | #################        \  (SUBTRAÍDA) / ### |
   | #################         \-----------/ #### |
   +-----------------------------------------------+
```

#### Regras Topológicas Especiais para Diferença:
- **Fragmentação em MultiPolígonos:** Quando a área subtraída corta um polígono ao meio, a geometria resultante é automaticamente encapsulada como um `MultiPolygon` válido, mantendo a integridade cadastral da feição de origem.
- **Limpeza de Linhas e Pontos Espúrios:** Quando as bordas de $A$ e $B$ se tangenciam, o operador de diferença da NTS pode gerar linhas ou pontos residuais de dimensão inferior na fronteira. O motor GeoNex aplica a rotina `GeometryExtracter.Extract<Polygon>(result)` para garantir que a geometria de saída contenha estritamente polígonos.

---

### 3.4. Dissolução de Geometrias (Dissolve All / Dissolve by Attribute)

A **Dissolução** agrupa feições vetoriais eliminando fronteiras internas comuns, unificando polígonos adjacentes em grandes massas contínuas ou agregando linhas conectadas.

```
       CAMADA ORIGINAL (Bairros Municipais com Região)      CAMADA DISSOLVIDA POR 'REGIAO'
   +--------------------+--------------------+            +---------------------------------+
   | Bairro 1           | Bairro 2           |            |                                 |
   | [Regiao: NORTE]    | [Regiao: NORTE]    |  Dissolve  |         ZONA NORTE              |
   +--------------------+--------------------+ ═════════► |                                 |
   | Bairro 3           | Bairro 4           | (REGIAO)   +---------------------------------+
   | [Regiao: SUL]      | [Regiao: SUL]      |            |         ZONA SUL                |
   +--------------------+--------------------+            +---------------------------------+
```

#### Modos de Operação:
1. **Dissolver Tudo (*Dissolve All*):**
   - Todas as feições da camada são submetidas a uma união topológica em cascata (`CascadedPolygonUnion` ou `UnaryUnionOp`), resultando em uma única feição geométrica (`MultiPolygon` ou `MultiLineString`).
2. **Dissolver por Campo Categórico (*Dissolve by Attribute*):**
   - As feições são particionadas em dicionários indexados pelo valor do campo selecionado (ex: `Dictionary<object, List<Geometry>>`).
   - Para cada chave, executa-se a união topológica de suas geometrias.
   - Os atributos numéricos podem ser agregados conforme parâmetros:
     - `Primeiro`: Mantém o primeiro valor encontrado no grupo.
     - `Soma`: Soma os valores numéricos das feições agregadas (ex: somar população total).
     - `Média`: Calcula a média aritmética dos valores do grupo.
     - `Contagem`: Adiciona uma nova coluna com a contagem de feições primitivas que formaram aquele grupo.

---

### 3.5. Zona de Amortecimento em Lote (Layer Buffer Engine)

O motor de **Buffer em Lote** calcula zonas de afastamento equidistante com suporte a parâmetros cartográficos de alta precisão baseados na especificação OGC e NTS `BufferParameters`.

```
                  PONTO                          LINHA                       POLÍGONO
                    ●                          ────────                     ┌───────┐
                                                                            └───────┘
                                             ═══════════►
                 /─────\                      /────────\                    /─────────\
                |   ●   |                    (  ────────  )                | ┌───────┐ |
                 \─────/                      \────────/                    \ └───────┘ /
            Buffer Circular                 Buffer Capsulado              Buffer Perimetral
```

#### Parâmetros Matemáticos Cartográficos:
1. **Distância Métricas vs. Geodésicas:**
   - Para camadas em Projeção UTM (ex: SIRGAS 2000 UTM EPSG:31983), a distância em metros é aplicada diretamente no plano euclidiano.
   - Para camadas em coordenadas geográficas angulares (EPSG:4326), o motor converte automaticamente a distância métrica em graus aproximados com base na latitude média:
     $$\Delta \phi = \frac{d}{111.320}, \quad \Delta \lambda = \frac{d}{111.320 \cdot \cos(\phi_{\text{médio}})}$$
2. **Segmentos de Quadrante (`QuadrantSegments`):**
   - Controla a suavidade de curvatura nos cantos e círculos. Padrão: 16 segmentos por quadrante de 90° (resultando em 64 vértices para um círculo completo de 360°, sem artefatos poligonais visíveis).
3. **Estilo de Terminação de Ponta (`EndCapStyle`):**
   - `Round` (Arredondado): Clássico semicírculo na extremidade.
   - `Flat` (Plano / Reto): Linha de corte estritamente perpendicular ao último vértice.
   - `Square` (Quadrado): Extensão retangular com comprimento igual ao raio do buffer.
4. **Estilo de Junção de Vértices (`JoinStyle`):**
   - `Round` (Arredondado): Suaviza todas as esquinas convexas.
   - `Mitre` (Bisel / Canto Vivo): Estende as bordas até convergirem em ponta fina (com limite de bisel para evitar projeções infinitas).
   - `Bevel` (Chanfrado): Corta as pontas vivas com uma faceta reta.
5. **Opção de Dissolver Buffers (*Dissolve Buffers*):**
   - Quando ativada, todas as áreas de buffer sobrepostas são fundidas em um único polígono/multipolígono contínuo, eliminando sobreposições internas.

---

### 3.6. Envoltória Convexa (Convex Hull)

A **Envoltória Convexa (*Convex Hull*)** computa o menor polígono convexo que engloba integralmente todos os vértices de uma geometria ou de um conjunto de geometrias.

$$\text{ConvexHull}(S) = \left\{ \sum_{i=1}^k \alpha_i p_i \;\middle|\; p_i \in S, \alpha_i \ge 0, \sum_{i=1}^k \alpha_i = 1 \right\}$$

#### Modos Suportados no GeoNex:
1. **Global da Camada:** Coleta todos os vértices de todas as feições da camada e computa 1 único polígono convexo perimetral. Ideal para determinar a área de abrangência de levantamentos de campo ou nuvens de pontos GPS.
2. **Individual por Feição:** Converte cada linha sinuosa ou multipolígono complexo em sua respectiva envoltória convexa simplificada, mantendo os atributos originais da feição.
3. **Agrupado por Atributo:** Gera uma envoltória convexa para cada categoria única da tabela de atributos (ex: envoltória de pontos de ocorrência por espécie botânica).

---

### 3.7. Centroides e Polos de Inacessibilidade (Centroids & Polylabel)

Esta ferramenta converte geometrias complexas (linhas e polígonos) em uma nova camada vetorial de pontos de referência.

```
       CENTROIDE DE MASSA GEOMÉTRICO (Físico)         POLO DE INACESSIBILIDADE (Polylabel)
       (Pode cair fora em formas côncavas)            (Garante que fica no INTERIOR da feição)
               +-------------+                                +-------------+
               |             |                                |             |
               |     (●)     |                                |             |
               |   CENTROIDE |                                |   ● POLO    |
               |    NO VAZIO |                                |  DE INACES. |
               +-------+     |                                +-------+     |
                       |     |                                        |     |
                       +-----+                                        +-----+
```

1. **Centroide de Massa Físico (*Centroid*):**
   - Computado através dos momentos de inércia e centro de gravidade da superfície poligonal. É ideal para estudos de dispersão e física espacial, mas possui a limitação de poder cair fora do polígono em geometrias côncavas (formato em "C", ferraduras, baías ou ilhas).
2. **Polo de Inacessibilidade (*Polylabel / Maximum Inscribed Circle*):**
   - Utiliza o motor que já desenvolvemos e validamos em [LabelPlacementEngine.cs](file:///C:/Users/Nicho/Desktop/GeoNexNOT/GeoNex-main/GeoNex/Services/LabelPlacementEngine.cs). Executa a amostragem QuadTree iterativa para encontrar a célula interna com maior raio livre em relação às bordas, garantindo que o ponto gerado fique rigorosamente dentro do corpo da feição.
3. **Preservação Integral de Atributos:**
   - Todos os dados da tabela DBF (área original, código de matrícula, perímetro, proprietário) são fielmente replicados na nova feição pontual.

---

### 3.8. União Espacial de Atributos (Spatial Join)

A **União Espacial** transfere os dados da tabela de atributos da camada de junção $B$ para a camada alvo $A$ com base na relação posicional das geometrias no espaço geográfico.

- **Predicados Topológicos Suportados:**
  - `Intersects`: Feição $A$ toca ou sobrepõe feição $B$.
  - `Contains`: Feição $A$ contém totalmente a feição $B$.
  - `Within`: Feição $A$ está totalmente no interior da feição $B$.
  - `Touches`: Feição $A$ e $B$ compartilham pelo menos um ponto de fronteira sem sobreposição de interiores.
- **Estratégias de Cardinalidade:**
  - **1 para 1 com Resumo (*One-to-One Summary*):** Se múltiplos pontos de $B$ caírem dentro de um polígono de $A$, o motor calcula automaticamente a contagem de pontos (`COUNT_B`) e estatísticas de campos numéricos selecionados (`SUM_VAL`, `AVG_VAL`).
  - **1 para Muitos (*One-to-Many*):** Duplica a feição $A$ para cada feição correspondente de $B$.

---

## 💾 4. Engenharia de Dados: Fusão de Esquemas DBF/OGR e Atributos

Ao gerar novas camadas vetoriais derivadas — especialmente em **Intersecção** e **União Espacial** —, o motor de geoprocessamento deve fundir esquemas de dados heterogêneos respeitando as limitações físicas do formato **ESRI Shapefile / dBASE III+**:

1. **Regra de Ouro do dBASE (Limite de 10 Caracteres):**
   - Os nomes de campos no arquivo `.dbf` têm comprimento máximo de 10 bytes ASCII e não diferenciam maiúsculas de minúsculas.
2. **Resolução Determinística de Conflitos de Nomes:**
   - Se a Camada $A$ tiver um campo chamado `NOME` e a Camada $B$ também tiver `NOME`:
     - O campo da Camada $A$ é mantido como `NOME`.
     - O campo da Camada $B$ é automaticamente renomeado para `NOME_1`.
     - Caso o nome original já tenha 10 caracteres (ex: `POPULACAO`), o motor trunca a raiz para criar o sufixo: `POPULAC_1`.
3. **Mapeamento e Conversão de Tipos OGR:**
   - Tipos numéricos inteiros (`OFTInteger`, `OFTInteger64`) são preservados.
   - Números de ponto flutuante de precisão dupla (`OFTReal`) mantêm sua escala.
   - Strings (`OFTString`) são sanitizadas para evitar corrupção de caracteres acentuados utilizando codificação UTF-8 ou Latin-1 (`Windows-1252`) compatível com o arquivo `.cpg` do Shapefile.

---

## 🌐 5. Reprojeção On-The-Fly e Harmonização de CRS/SRID

Uma das falhas mais frequentes em softwares GIS amadores é a execução de operações booleanas entre camadas que estão em sistemas de coordenadas diferentes, resultando em saídas vazias ou distorções catastróficas.

No **GeoNex**, o motor de geoprocessamento implementa a harmonização prévia de CRS:
1. **Detecção e Inspeção de Sistemas de Coordenadas:**
   - O motor inspeciona o CRS da Camada de Entrada ($CRS_A$) e da Camada de Sobreposição ($CRS_B$) utilizando os objetos `OSGeo.OSR.SpatialReference`.
2. **Verificação de Equivalência:**
   - Se `srsA.IsSame(srsB) == 1`, as geometrias já estão no mesmo referencial métrico ou geográfico, procedendo sem conversão.
3. **Reprojeção em Memória com GDAL/OSR:**
   - Se os CRS forem distintos, o motor instancia uma `CoordinateTransformation(srsB, srsA)`.
   - Todas as geometrias da Camada $B$ são reprojetadas dinamicamente para o espaço de coordenadas da Camada $A$ antes de entrarem na árvore `STRtree` e nos algoritmos booleanos.
   - A camada de saída é gravada no mesmo CRS da Camada de Entrada, acompanhada de seu respectivo arquivo `.prj` padronizado em WKT (Well-Known Text).

---

## 🛡️ 6. Resiliência Topológica e Auto-Reparo (Topology Healing)

Operações booleanas em geometrias reais (especialmente polígonos complexos importados de fontes externas) estão sujeitas a erros de precisão numérica de ponto flutuante (`Double-Precision Arithmetic Drift`) e defeitos de desenho (auto-interseções, vértices duplicados consecutivos, laços em nó de gravata / *bow-tie*).

Para garantir que o motor nunca lance exceções não tratadas e execute o processamento até o fim, é implementado o pipeline de **Sanitização Topológica de 3 Níveis**:

```
                                  PIPELINE DE VALIDAÇÃO E REPARO
                                  
     [ Geometria Candidata ]
                │
                ▼
     ┌──────────────────────┐
     │ IsValidOp.IsValid()? │───── SIM ─────► [ Geometria Pronta para Operação ]
     └──────────────────────┘
                │ NÃO
                ▼
     ┌────────────────────────────────────────────────────────────────────────┐
     │ NÍVEL 1: Buffer(0) Repair                                              │
     │ Aplica buffer de raio zero via NTS para reconstruir a topologia plana. │
     └────────────────────────────────────────────────────────────────────────┘
                │
                ▼
     ┌──────────────────────┐
     │ IsValidOp.IsValid()? │───── SIM ─────► [ Geometria Reparada ]
     └──────────────────────┘
                │ NÃO
                ▼
     ┌────────────────────────────────────────────────────────────────────────┐
     │ NÍVEL 2: Enhanced Precision Operation (Snapping Snap-Rounding)         │
     │ Arredonda nós espúrios em micro-grade de precisão.                     │
     └────────────────────────────────────────────────────────────────────────┘
                │
                ▼
     ┌──────────────────────┐
     │ IsValidOp.IsValid()? │───── SIM ─────► [ Geometria Reparada ]
     └──────────────────────┘
                │ NÃO
                ▼
     ┌────────────────────────────────────────────────────────────────────────┐
     │ NÍVEL 3: Polygonizer Rebuilding                                        │
     │ Decompõe a geometria em arestas nodadas e remonta faces poligonais.    │
     └────────────────────────────────────────────────────────────────────────┘
```

---

## 🎨 7. Design da Interface Gráfica (UI/UX) — Padrão Enterprise (Zero Emojis)

A interface gráfica de geoprocessamento segue com rigor os padrões de ergonomia de estações de trabalho de engenharia, **sem emojis**, com ícones SVG vetoriais nítidos e alta densidade de informação útil.

### 7.1. Adição da Aba `Geoprocessamento` no Ribbon

Em [Home.razor](file:///C:/Users/Nicho/Desktop/GeoNexNOT/GeoNex-main/GeoNex/Components/Pages/Home.razor), o vetor de abas do Ribbon será estendido:
```csharp
private static readonly string[] RibbonTabs = ["Mapa", "Camadas", "Vetorização", "Geoprocessamento"];
```

#### Estrutura Visual da Aba no Ribbon:
```
+--------------------------------------------------------------------------------------------------------------------+
| ARQUIVO | PROJETO | MAPA | CAMADAS | VETORIZAÇÃO | GEOPROCESSAMENTO (Ativo) | AJUDA                             [_][X]|
+--------------------------------------------------------------------------------------------------------------------+
|  [ SOBREPOSIÇÃO ]                  [ AGREGAÇÃO ]             [ GEOMETRIA ]                 [ EXECUÇÃO ]            |
|  ┌────────┐ ┌────────┐ ┌────────┐  ┌────────┐ ┌────────┐    ┌────────┐ ┌────────┐ ┌────────┐ ┌──────────────────┐  |
|  │  [◩]   │ │  [∩]   │ │  [⊟]   │  │  [⊞]   │ │  [⊶]   │    │  [◎]   │ │  [⬡]   │ │  [·]   │ │ Histórico / Logs │  |
|  │Recortar│ │Intersec│ │Diferen.│  │Dissolv.│ │Mesclar │    │ Buffer │ │ConvexH.│ │Centroid│ │ Cancelar Ação   │  |
|  └────────┘ └────────┘ └────────┘  └────────┘ └────────┘    └────────┘ └────────┘ └────────┘ └──────────────────┘  |
+--------------------------------------------------------------------------------------------------------------------+
```

---

### 7.2. Modal Universal de Geoprocessamento (`GeoprocessingModal.razor`)

Um diálogo técnico modal unificado com seletor de ferramentas à esquerda e parâmetros contextuais à direita:

```
+---------------------------------------------------------------------------------------------------+
| CAIXA DE FERRAMENTAS DE GEOPROCESSAMENTO — [ Recortar Camada (Clip) ]                      [ X ]  |
+---------------------------------------------------------------------------------------------------+
|  PAINEL DE FERRAMENTAS     | CONFIGURAÇÃO DOS PARÂMETROS TÉCNICOS                                 |
| ────────────────────────── | ───────────────────────────────────────────────────────────────────  |
| Sobreposição:              | Camada Vetorial de Entrada:                                          |
|  ▶ [✓] Recortar (Clip)     | [ Malha_Viaria_Estadual_2024 (Linha, 14.820 feições)             ▼]  |
|    [ ] Intersecção         | [✓] Processar apenas feições selecionadas (0 selecionadas)           |
|    [ ] Diferença           |                                                                      |
|    [ ] União Espacial      | Camada de Máscara de Recorte:                                        |
|                            | [ Limite_Municipal_Campinas (Polígono, 1 feição)                  ▼]  |
| Agregação & Limpeza:       | [✓] Processar apenas feições selecionadas                            |
|    [ ] Dissolver           |                                                                      |
|    [ ] Mesclar Camadas     | Tolerância Geométrica / Snap (metros):                               |
|                            | [ 0.001                                                            ] |
| Geometria:                 |                                                                      |
|    [ ] Buffer Vetorial     | Arquivo de Destino (Shapefile):                                      |
|    [ ] Envoltória Convexa  | [ C:\Projetos\SIG\Malha_Viaria_Campinas_Clip.shp               ] [...] |
|    [ ] Centroides & Polos  | [✓] Adicionar camada resultante ao mapa automaticamente ao concluir  |
|                            |                                                                      |
|                            | STATUS DE EXECUÇÃO:                                                  |
|                            | [████████████████████████████████████████▒▒▒▒▒▒▒] 74% (10.966/14.820) |
|                            | Processando: 1.250 feições/segundo | Tempo Restante: 00:03s           |
|                            |                                                                      |
|                            | LOG TÉCNICO DE PROCESSAMENTO:                                        |
|                            | ┌──────────────────────────────────────────────────────────────────┐ |
|                            | │ [INFO] 20:15:02 - Indexando camada de máscara via STRtree 2D...  │ |
|                            | │ [INFO] 20:15:02 - CRS Verificado: EPSG:31983 (SIRGAS 2000 UTM)   │ |
|                            | │ [INFO] 20:15:03 - 3.854 feições fora do envelope descartadas.    │ |
|                            | │ [INFO] 20:15:04 - 10.966 feições recortadas com integridade.     │ |
|                            | └──────────────────────────────────────────────────────────────────┘ |
+---------------------------------------------------------------------------------------------------+
| [ AJUDA TÉCNICA ]                                              [ FECHAR ]   [ EXECUTAR PROCESSO ] |
+---------------------------------------------------------------------------------------------------+
```

---

## 🏗️ 8. Matriz de Componentes e Estrutura de Arquivos C#

| Componente | Arquivo de Destino | Responsabilidade Técnica |
| :--- | :--- | :--- |
| **`GeoprocessingEngine.cs`** | `GeoNex/Services/GeoprocessingEngine.cs` | Orquestrador central assíncrono de análise espacial, multithreading, cancelamento e transações de shapefiles. |
| **`VectorOverlayAlgorithms.cs`** | `GeoNex/Services/VectorOverlayAlgorithms.cs` | Implementação matemática dos algoritmos de Recorte (*Clip*), Intersecção (*Intersection*) e Diferença (*Difference*). |
| **`VectorAggregationAlgorithms.cs`** | `GeoNex/Services/VectorAggregationAlgorithms.cs` | Implementação de Dissolução (*Dissolve*), Agregação estatística de atributos e Mesclagem de camadas. |
| **`VectorGeometryAlgorithms.cs`** | `GeoNex/Services/VectorGeometryAlgorithms.cs` | Algoritmos de Buffer multissegmentar, Envoltória Convexa (*Convex Hull*) e Centroides/Polylabel. |
| **`GeoprocessingTypes.cs`** | `GeoNex/Services/GeoprocessingTypes.cs` | Modelos de dados: `GeoprocessingParameters`, `GeoprocessingProgress`, `GeoprocessingResult`, enums de operações e opções de buffer. |
| **`Home.razor`** & Ribbon | `GeoNex/Components/Pages/Home.razor` | Adição da aba de ribbon `Geoprocessamento`, atalhos de ferramentas, integração com o modal técnico e registro de camadas. |
| **`GeoprocessingCore`** *(Testes)* | `tests/GeoprocessingCore/Program.cs` | Nova suíte de testes automatizados com centenas de asserções cobrindo geometrias sintéticas e validações topológicas. |

---

## 🧪 9. Arquitetura da Suíte de Testes Automatizados (`tests/GeoprocessingCore`)

O novo projeto de teste automatizado `tests/GeoprocessingCore/GeoprocessingCore.csproj` validará rigorosamente todos os contratos matemáticos e espaciais:

```
tests/GeoprocessingCore/
├── GeoprocessingCore.csproj
└── Program.cs
    ├── [1/8] Teste de Recorte (Clip): Polígonos, Linhas e Pontos contra Máscara Côncava
    ├── [2/8] Teste de Intersecção (Intersection): Cruzamento com Fusão de Esquema DBF
    ├── [3/8] Teste de Diferença (Difference): Subtração com Furos e MultiPolígonos
    ├── [4/8] Teste de Dissolver (Dissolve): Fusão por Campo Categórico e Dissolve All
    ├── [5/8] Teste de Buffer Vetorial: Distância Fixa, Arcos Suaves e Dissolve de Buffers
    ├── [6/8] Teste de Envoltória Convexa (Convex Hull): Polígonos Irregulares e Nuvens de Pontos
    ├── [7/8] Teste de Centroides e Polylabel: Centros de Massa e Ponto em Polígono Côncavo
    └── [8/8] Teste de Robustez e Estresse: 20.000 feições com STRtree em menos de 800ms
```

---

## 📅 10. Cronograma Executivo de Implementação em 5 Fases

```
FASE 1: Modelos de Tipos & Algoritmos de Sobreposição (Overlay)
├── Criar GeoprocessingTypes.cs com registros e enums
├── Implementar VectorOverlayAlgorithms.cs (Clip, Intersection, Difference)
└── Validar cálculos topológicos NTS iniciais em testes unitários

FASE 2: Algoritmos de Agregação, Geometria e Buffer
├── Implementar VectorAggregationAlgorithms.cs (Dissolve com agregação de atributos)
├── Implementar VectorGeometryAlgorithms.cs (Buffer multissegmentar, Convex Hull, Centroides)
└── Criar projeto de teste automatizado tests/GeoprocessingCore

FASE 3: Motor Assíncrono & Persistência em Shapefile
├── Desenvolver GeoprocessingEngine.cs com suporte a CancellationToken e IProgress
├── Implementar gerador de Shapefiles atômicos compatíveis com GDAL/OGR
└── Garantir reprojeção on-the-fly para camadas em CRS distintos

FASE 4: Interface Gráfica Profissional (Ribbon & Modal Universal)
├── Adicionar aba 'Geoprocessamento' no Ribbon com ícones SVG profissionais (zero emojis)
├── Construir o modal de diálogo de geoprocessamento em Home.razor
└── Integrar atualização automática do mapa com adição imediata da camada gerada

FASE 5: Bateria Completa de Testes, Validação e Release
├── Executar suíte completa tests/GeoprocessingCore (100% de sucesso)
├── Executar suítes de regressão (SymbologyCore, DigitizingCore, PrintScale, Persistence)
└── Compilação do aplicativo principal GeoNex.csproj com 0 Erros em modo Release
```

---

## 🛡️ 11. Critérios Rigorosos de Aceite & Validação de Qualidade

1. **Integridade de Topologia:** Nenhuma geometria inválida ou auto-intersectante emitida nas camadas resultantes (`geometry.IsValid == true`).
2. **Preservação de Dados:** Nenhum atributo original perdido ou truncado indevidamente durante operações de recorte ou intersecção.
3. **Desempenho O(N log M):** Processamento de recorte de 10.000 feições contra máscara em menos de 1 segundo utilizando STRtree.
4. **Ausência de Travamento:** Durante o processamento em lote, a interface Blazor permanece totalmente responsiva a 60 FPS, permitindo cancelar a qualquer momento.
5. **Estética Profissional:** Zero emojis em toda a interface do módulo de geoprocessamento.
6. **Compilação e Testes:** 100% dos testes aprovados e 0 erros de build em modo Release (`net10.0-windows10.0.19041.0`).
---

## 🔬 12. Caderno Técnico: Formulação Matemática Detalhada dos Operadores

### 12.1. Topologia de Ponto-Conjunto (Point-Set Topology) e Matriz DE-9IM
O GeoNex adota o modelo dimensional estendido de 9 intersecções (**DE-9IM - Dimensionally Extended 9-Intersection Model**) padronizado pela OGC e ISO/TC 211 para formalizar as relações topológicas entre quaisquer duas geometrias $A$ e $B$:

$$\text{DE-9IM}(A, B) = \begin{bmatrix}
\dim(I(A) \cap I(B)) & \dim(I(A) \cap B(B)) & \dim(I(A) \cap E(B)) \\
\dim(B(A) \cap I(B)) & \dim(B(A) \cap B(B)) & \dim(B(A) \cap E(B)) \\
\dim(E(A) \cap I(B)) & \dim(E(A) \cap B(B)) & \dim(E(A) \cap E(B))
\end{bmatrix}$$

Onde:
- $I(A)$ representa o Interior da geometria $A$ (conjunto de pontos estritamente internos sem incluir a borda).
- $B(A)$ representa a Borda (*Boundary*) da geometria $A$ (linhas periféricas para polígonos, vértices extremos para linhas).
- $E(A)$ representa o Exterior da geometria $A$ (todo o plano restante fora de $A$).
- $\dim(S) \in \{-1, 0, 1, 2\}$ indica a dimensão do conjunto resultante:
  - $-1$: Conjunto vazio ($\emptyset$);
  - $0$: Ponto isolado ou conjunto de pontos;
  - $1$: Linha ou curva;
  - $2$: Área ou superfície poligonal.

#### Mapeamento dos Predicados Utilizados no Geoprocessamento do GeoNex:
- **Intersects:** $\neg \text{Disjoint}(A, B) \iff (I(A) \cap I(B) \neq \emptyset) \lor (I(A) \cap B(B) \neq \emptyset) \lor (B(A) \cap I(B) \neq \emptyset) \lor (B(A) \cap B(B) \neq \emptyset)$.
- **Contains:** $I(A) \cap I(B) \neq \emptyset \land I(B) \cap E(A) = \emptyset \land B(B) \cap E(A) = \emptyset$.
- **Within:** $B \text{ Contains } A$.
- **Touches:** $I(A) \cap I(B) = \emptyset \land ( (B(A) \cap B(B) \neq \emptyset) \lor (B(A) \cap I(B) \neq \emptyset) \lor (I(A) \cap B(B) \neq \emptyset) )$.

---

### 12.2. Algoritmo de União Poligonal em Cascata (Cascaded Polygon Union)
A união direta iterativa de $N$ polígonos:
$$U_k = U_{k-1} \cup P_k$$
apresenta complexidade assintótica de pior caso de $O(N^2)$, pois a geometria acumulada $U_{k-1}$ cresce em número de vértices a cada iteração, tornando as uniões subsequentes progressivamente mais lentas.

O **GeoNex** implementa o algoritmo de **União em Cascata (*Cascaded Polygon Union*)**, que organiza os polígonos em uma árvore hierárquica baseada em sua proximidade espacial:

```
NÍVEL 0:  [P1]  [P2]    [P3]  [P4]    [P5]  [P6]    [P7]  [P8]
             \   /        \   /        \   /        \   /
NÍVEL 1:    [P1 ∪ P2]    [P3 ∪ P4]    [P5 ∪ P6]    [P7 ∪ P8]
                \            /            \            /
NÍVEL 2:       [(P1..P4)]                 [(P5..P8)]
                     \                         /
NÍVEL 3:               [ UNISSO POLIGONAL FINAL ]
```

A união ocorre sempre entre polígonos de complexidade e tamanho equivalentes, reduzindo o tempo de processamento de $O(N^2)$ para $O(N \log N)$, garantindo ganhos de até 40x na operação de **Dissolver** e na unificação de máscaras de **Recorte**.

---

### 12.3. Matemática da Curvatura e Segmentação do Buffer
Para cada segmento de reta $\vec{S} = P_1 P_2$, o buffer constrói dois vetores normais perpendiculares unitários:

$$\vec{n} = \left( -\frac{y_2 - y_1}{\|\vec{S}\|}, \frac{x_2 - x_1}{\|\vec{S}\|} \right)$$

As linhas de offset distanciadas por $d$ são dadas por:
$$L_1(t) = P_1 + t\vec{S} + d\vec{n}, \quad L_2(t) = P_1 + t\vec{S} - d\vec{n}$$

Nas curvas convexas com ângulo $\alpha$, o arco circular é subdividido em $k = \lceil \frac{\alpha}{\pi / 2} \cdot \text{quadrantSegments} \rceil$ vértices:

$$V_j = P + d \begin{pmatrix} \cos(\theta_0 + j \cdot \Delta \theta) \\ \sin(\theta_0 + j \cdot \Delta \theta) \end{pmatrix}, \quad j = 0, 1, \dots, k$$

Esse modelo matemático garante que as curvas geradas pelo GeoNex possuam continuidade tangencial $C^1$ sem descontinuidades abruptas.

---

### 12.4. Resolução de Bounding Boxes com Índices STRtree (Sort-Tile-Recursive)
O empacotamento da árvore STRtree funciona em duas etapas:
1. **Ordenação Dimensional X:** As $N$ feições são ordenadas pela coordenada X do centro de suas caixas delimitadoras. Elas são particionadas em $S = \lceil \sqrt{N / C} \rceil$ faixas verticais, onde $C$ é a capacidade do nó folha (padrão: 10 feições por nó).
2. **Ordenação Dimensional Y:** Dentro de cada faixa vertical, as feições são ordenadas pela coordenada Y de seus centros e agrupadas em nós folhas de tamanho $C$.

Essa organização balanceia perfeitamente a árvore com altura $h = \lceil \log_C N \rceil$, eliminando a necessidade de rotações dinâmicas e garantindo tempos de consulta constantes e previsíveis.

---

## 💻 13. Especificações Técnicas de Implementação em C#

### 13.1. Estrutura de Tipos (`GeoprocessingTypes.cs`)
```csharp
namespace GeoNex.Services
{
    public enum TipoOperacaoGeoprocessamento
    {
        Recortar,
        Interseccao,
        Diferenca,
        Dissolver,
        Buffer,
        EnvoltoriaConvexa,
        Centroides,
        UniaoEspacial
    }

    public enum BufferEndCapStyle
    {
        Round = 1,
        Flat = 2,
        Square = 3
    }

    public enum BufferJoinStyle
    {
        Round = 1,
        Mitre = 2,
        Bevel = 3
    }

    public enum ModoAgregacaoDissolve
    {
        PrimeiroValor,
        Soma,
        Media,
        Contagem
    }

    public sealed record GeoprocessingParameters(
        TipoOperacaoGeoprocessamento Operacao,
        string NomeCamadaEntrada,
        string? NomeCamadaSobreposicao = null,
        bool ApenasSelecionadasEntrada = false,
        bool ApenasSelecionadasSobreposicao = false,
        double DistanciaBufferMetros = 0.0,
        string? CampoDistanciaBuffer = null,
        int SegmentosQuadranteBuffer = 16,
        BufferEndCapStyle EndCapBuffer = BufferEndCapStyle.Round,
        BufferJoinStyle JoinBuffer = BufferJoinStyle.Round,
        bool DissolverBuffers = false,
        string? CampoDissolucao = null,
        ModoAgregacaoDissolve AgregacaoDissolucao = ModoAgregacaoDissolve.PrimeiroValor,
        bool UsarPolylabelEmCentroides = true,
        double ToleranciaSnapMetros = 0.001,
        string? CaminhoArquivoSaida = null,
        bool AdicionarAoMapaAoConcluir = true
    );

    public readonly record struct GeoprocessingProgress(
        double Percentual,
        string Mensagem,
        int FeicoesProcessadas,
        int TotalFeicoes
    );

    public sealed record GeoprocessingResult(
        bool Sucesso,
        int TotalFeicoesCriadas,
        string? CaminhoCamadaGerada,
        string? MensagemErro = null,
        TimeSpan TempoDecorrido = default
    );
}
```

---

### 13.2. Estrutura do Algoritmo de Buffer (`VectorGeometryAlgorithms.cs`)
```csharp
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Buffer;

namespace GeoNex.Services
{
    public static class VectorGeometryAlgorithms
    {
        public static Geometry ExecutarBuffer(
            Geometry geometria,
            double distancia,
            int segmentosQuadrante = 16,
            BufferEndCapStyle endCap = BufferEndCapStyle.Round,
            BufferJoinStyle joinStyle = BufferJoinStyle.Round)
        {
            if (geometria == null || geometria.IsEmpty)
                return GeometryFactory.Default.CreatePolygon();

            var endCapNts = endCap switch
            {
                BufferEndCapStyle.Flat => EndCapStyle.Flat,
                BufferEndCapStyle.Square => EndCapStyle.Square,
                _ => EndCapStyle.Round
            };

            var joinNts = joinStyle switch
            {
                BufferJoinStyle.Mitre => JoinStyle.Mitre,
                BufferJoinStyle.Bevel => JoinStyle.Bevel,
                _ => JoinStyle.Round
            };

            var bufferParams = new BufferParameters(segmentosQuadrante)
            {
                EndCapStyle = endCapNts,
                JoinStyle = joinNts,
                QuadrantSegments = Math.Clamp(segmentosQuadrante, 4, 64)
            };

            return BufferOp.Buffer(geometria, distancia, bufferParams);
        }

        public static Geometry ExecutarEnvoltoriaConvexa(IEnumerable<Geometry> geometrias)
        {
            var colecao = new GeometryCollection(geometrias.ToArray());
            return colecao.ConvexHull();
        }

        public static Point ExecutarCentroide(Geometry geometria, bool usarPolylabel = false)
        {
            if (geometria == null || geometria.IsEmpty)
                return GeometryFactory.Default.CreatePoint(new Coordinate(0, 0));

            if (!usarPolylabel || geometria is not Polygon and not MultiPolygon)
            {
                return geometria.Centroid;
            }

            // Polo de Inacessibilidade para polígonos côncavos
            var centroid = geometria.Centroid;
            if (geometria.Contains(centroid))
            {
                return centroid;
            }

            // Amostragem interior refinada
            var interiorPoint = geometria.InteriorPoint;
            return interiorPoint is Point p ? p : centroid;
        }
    }
}
```

---

### 13.3. Estrutura do Algoritmo de Dissolver (`VectorAggregationAlgorithms.cs`)
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.Operation.Union;

namespace GeoNex.Services
{
    public static class VectorAggregationAlgorithms
    {
        public static List<Feature> ExecutarDissolucao(
            IReadOnlyList<Feature> feicoes,
            string? campoAgrupamento,
            ModoAgregacaoDissolve modoAgregacao)
        {
            var resultado = new List<Feature>();

            // Modo 1: Dissolver Tudo em uma única feição
            if (string.IsNullOrWhiteSpace(campoAgrupamento))
            {
                var geometrias = feicoes.Select(f => f.Geometry).Where(g => g != null && !g.IsEmpty).ToList();
                var uniao = CascadedPolygonUnion.Union(geometrias);
                if (uniao != null && !uniao.IsEmpty)
                {
                    var attrs = new AttributesTable();
                    attrs.Add("TOTAL_AGR", feicoes.Count);
                    resultado.Add(new Feature(uniao, attrs));
                }
                return resultado;
            }

            // Modo 2: Dissolver Agrupado por Atributo
            var grupos = feicoes
                .Where(f => f.Geometry != null && !f.Geometry.IsEmpty)
                .GroupBy(f => f.Attributes.Exists(campoAgrupamento) ? f.Attributes[campoAgrupamento]?.ToString() ?? "" : "");

            foreach (var grupo in grupos)
            {
                var chave = grupo.Key;
                var geomsGrupo = grupo.Select(f => f.Geometry).ToList();
                var uniaoGrupo = CascadedPolygonUnion.Union(geomsGrupo);

                if (uniaoGrupo != null && !uniaoGrupo.IsEmpty)
                {
                    var attrs = new AttributesTable();
                    attrs.Add(campoAgrupamento, chave);
                    attrs.Add("CONTAGEM", grupo.Count());

                    resultado.Add(new Feature(uniaoGrupo, attrs));
                }
            }

            return resultado;
        }
    }
}
```

---

## 🏁 14. Considerações Finais & Conclusão Técnica

A execução deste Plano Diretor equipará o **GeoNex** com a infraestrutura analítica completa necessária para atender projetos de engenharia civil, agrimensura cadastral, regularização fundiária (INCRA/CAR) e estudos de impacto ambiental (EIA/RIMA).

Com arquitetura limpa, computação matemática exata (NTS/GDAL), multithreading cooperativo seguro e interface sóbria (zero emojis), o módulo de **Geoprocessamento** solidificará o GeoNex como uma solução de engenharia cartográfica nacional de alto nível técnico e performance incomparável.
