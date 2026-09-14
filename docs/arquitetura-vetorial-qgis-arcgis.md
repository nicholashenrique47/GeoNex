# Arquitetura vetorial de alto desempenho para o GeoNex

## Recomendação

Evoluir para **fontes de dados independentes do formato + núcleo C++ orientado a blocos + índices por componente + níveis de detalhe persistentes + composição incremental**. Manter C#/Blazor para interface e aplicação. Não reescrever tudo em C++ nem criar novos parsers para cada extensão.

Não existe técnica única que garanta desempenho igual ao QGIS/ArcGIS para qualquer arquivo. A meta verificável é equivalência em uma matriz de dados, hardware e qualidade visual. Esta análise usa código/API QGIS 3.44 como referência estável, documentação pública ArcGIS Pro e documentação GDAL/GEOS/Skia consultadas em 12/09/2026. Referências `latest/stable` são móveis; não são versões fixadas para implementação. Não é possível inventariar todos os algoritmos internos proprietários do ArcGIS.

## O que os produtos efetivamente documentam

| Técnica | Evidência pública | Aplicação no GeoNex |
|---|---|---|
| Buscar apenas a área necessária | QGIS usa `setFilterRect` e seleção de atributos no renderer.[^1] | Consultar antes de decodificar; carregar atributos sob demanda. |
| Generalização para a tela | QGIS tem simplificação MapToPixel, inclusive substituição por envelope em casos apropriados.[^2] | Não desenhar detalhes menores que pixels; separar representação visual de geometria original. |
| Camadas paralelas e prévias | `QgsMapRendererParallelJob` oferece render paralelo, cancelamento não bloqueante e imagem intermediária.[^3] | Não deixar rede/raster serializar todo o vetor. |
| Cache com dependências | QGIS mantém imagens e invalida conforme as camadas dependentes.[^4] | Editar seleção/rótulo não deve reconstruir o fundo inteiro. |
| Índices, escala, consultas e reprojeção | Esri documenta índices espaciais/atributivos, custo da reprojeção, visibilidade por escala e controle de rótulos.[^5] | Reduzir dados solicitados e trabalho invisível; não apenas acelerar loops. |
| Cache de camadas e feições | Esri distingue cache de extensões visitadas e cache de feições de serviços.[^5] | Separar cache visual, geometria e dados remotos; invalidar por revisão. |
| Tiles vetoriais e densidade | ArcGIS documenta generalização por escala e esquema indexado para criação de tiles.[^6] | Representação de visualização por blocos/LOD; não substituir o armazenamento de edição por tiles. |
| Renderização acelerada | ArcGIS publica opções de backend gráfico e antialiasing.[^7] | Avaliar superfície GPU persistente. Isso não comprova que toda operação GIS rode na GPU. |

QGIS usa `QPainter` no caminho vetorial examinado; portanto “o segredo é colocar tudo na GPU” não é conclusão sustentada pelo código.[^1] Os detalhes internos de triangulação, cache e execução de cada caminho do ArcGIS não foram demonstrados pelas fontes públicas.

## Gargalos concretos do GeoNex

Inspeção do código local, distinta de evidência sobre os concorrentes:

- `ProjetoService.CompilarParaShapefileNativo` cria uma conversão temporária nova para formatos não SHP. Há custo de conversão e releitura; não há reutilização persistente dessa preparação.
- O enquadramento depende de metadados publicados após leitura/indexação. Uma requisição rápida de frame não torna essa preparação instantânea.
- `LocalMapServer` ainda tem uma fila principal serial para trabalho pesado e entrega imagem codificada ao WebView. A prévia por cache tem exceção própria, mas não elimina a dependência quando falta cobertura.
- `BuildBatchPathWithTransform` e o caminho SHP nativo têm otimizações diferentes. Ambos precisam convergir para um contrato comum de geometria, não necessariamente o mesmo leitor físico.
- O GeoJSON de inundação tem 8 feições, 27.494 anéis e 1.078.031 vértices. Um índice com apenas oito envelopes grandes não resolve a complexidade interna desses MultiPolygons.
- O teste isolado recente encontrou aproximadamente 17 ms para construir o path e 216 ms para desenho final. Mesmo eliminar completamente aqueles 17 ms daria apenas cerca de **1,08×** nessa soma. Isso não mede abertura, reprojeção, tiles online ou WebView. Ver [medições locais](performance-projected-polygons.md).

## Arquitetura proposta

```text
SHP / GeoJSON / GPKG / demais drivers disponíveis
                  ↓
VectorSource: metadados, filtros, atributos, leitura em lotes
                  ↓
Blocos geométricos C++ + identidade original + índice hierárquico
                  ↓
Reprojeção e LOD sob demanda → cache RAM/disco versionado
                  ↓
Jobs visíveis prioritários → imagens/blocos ou buffers gráficos
                  ↓
Composição ordenada: mapa base | vetores | seleção | rótulos
                  ↓
Superfície de apresentação persistente; exportação independente
```

O desenho acima é uma recomendação para GeoNex, não uma descrição integral do QGIS ou ArcGIS.

### 1. Fontes de dados sem conversão obrigatória

Criar `VectorSource` com capacidades: extensão rápida, índice existente, filtros espaciais, projeção de colunas, identificador estável, geometrias suportadas e leitura por lotes. Manter o leitor SHP otimizado e usar GDAL/OGR para os demais drivers disponíveis. Não anunciar suporte a formatos/drivers ausentes.

GeoJSON deve abrir diretamente; uma preparação binária opcional pode ocorrer em segundo plano e ser reutilizada. A primeira leitura de JSON sem índice continua tendo custo de varredura, sobretudo com uma única feição gigante. Não bloquear a interface esperando contagem exata ou índice completo quando metadados/parciais já permitem progresso.

SHP não serve de modelo universal: há restrições documentadas de campos, tipos e largura, relevantes à versão GDAL 3.12.1 do projeto.[^8] Conservar fonte original, nomes, tipos, nulos, valores, Z/M e identidade; informar qualquer representação não suportada.

### 2. Núcleo C++ por lotes, não por objetos individuais

Usar buffers contíguos de coordenadas, offsets de anéis/componentes e IDs; fronteira C#/C++ por lote, com ABI versionada, ownership explícito e cancelamento. SIMD com detecção de CPU e fallback; evitar reconstrução de objetos por vértice. Skia/GDAL e parte do GeoNex já executam código nativo: portar chamadas equivalentes não elimina o trabalho.

Avaliar Arrow onde houver implementação eficiente do driver, sem presumir ganho universal: GDAL informa que o fallback de `GetArrowStream` usa `GetNextFeature` internamente e impõe regras de vida útil/estado do stream.[^9] Limitar lotes por **bytes e vértices**, não apenas número de feições.

### 3. Índice hierárquico e cache de preparação

Indexar camada → feição → componente poligonal; componentes muito grandes podem exigir subdivisão adicional apenas para desenho. Preservar associação dos buracos, anéis externos e `sourceFID`. Não converter divisões visuais em novas feições de análise.

Reutilizar índices de origem. Para cache derivado predominantemente de leitura, testar FlatGeobuf indexado; ele tem restrições, inclusive geometrias nulas com índice e memória de construção proporcional às feições.[^10] GeoPackage é uma alternativa para armazenamento editável/multicamada com índice espacial.[^11] Nenhum formato é vencedor universal.

Manifesto do cache: identidade/conteúdo da fonte e dependências, camada, esquema, versões do formato/GDAL/PROJ, SRC/operação e parâmetros de preparação. Publicação atômica; entradas incompletas nunca válidas. Evicção por bytes e revisão. Não usar somente nome de arquivo como chave, nem executar uma segunda leitura completa só para hash se ele puder ser calculado durante a primeira.

### 4. LOD consistente, incluindo mapa parado

Manter três contratos: prévia interativa, representação de tela estabilizada com erro validado e geometria original para edição/análise/exportação. A tela parada não precisa automaticamente reconstruir todos os vértices invisíveis; deve satisfazer o contrato cartográfico escolhido. Disponibilizar modo exato.

Preparar níveis geométricos reutilizáveis com histerese entre escalas; determinar tolerância em pixels físicos, considerando DPI e transformação. Reprojetar apenas candidatos visíveis e reaproveitar blocos reprojetados por operação/SRC. Não simplificar em graus usando uma tolerância tratada como metros.

Para coberturas válidas sem sobreposição, considerar GEOS CoverageSimplifier, que preserva a topologia da cobertura.[^12] Isso **não** se aplica automaticamente a manchas de inundação sobrepostas. Preservar winding não garante ausência de auto-interseção, buracos corretos ou fronteiras compartilhadas; os testes precisam verificar esses casos.

### 5. Trabalho incremental e composição correta

Separar filas de I/O remoto, preparação geométrica e renderização. Priorizar viewport atual, centro e camada recém-adicionada; cancelar jobs obsoletos, limitar tarefas em voo e só depois fazer prefetch. Não remover o lock atual sem substituir suas garantias: OGRLayer/GDALDataset compartilhados não são genericamente thread-safe.[^13]

Cachear imagens por camada/bloco e recompor somente o que mudou. Chave visual inclui revisão, estilo, SRC, resolução/DPI, rotação e qualidade. Rótulos e seleção ficam separados; contornos, transparência e ordem continuam determinísticos.

Não dividir um path em draws independentes indiscriminadamente: transparências podem acumular, buracos mudar e recortes ganhar bordas artificiais. Tiles precisam de margem, tratamento de junções e composição com semântica por feição. As tentativas locais anteriores com diferenças de pixels não devem ser retomadas sem esses contratos.

### 6. Apresentação GPU como etapa própria

Prototipar superfície nativa persistente para o mapa, mantendo a interface Blazor. No Windows, SwapChainPanel oferece integração DirectX/XAML; encaixar isso no layout MAUI/WebView exige validar z-order, entrada, DPI e acessibilidade.[^14] Não é substituição de uma linha de código.

Escolher backend realmente disponível no build. Skia exige contexto/dispositivo gráfico e vida útil corretos; não o cria automaticamente.[^15] Evitar GPU → CPU → PNG → decode → GPU em cada frame. Reutilizar buffers/tesselação por bloco/LOD se o perfil justificar, com recuperação de perda de dispositivo e fallback CPU.

## Ordem de engenharia e critérios de conclusão

| Prioridade | Entrega | Como aceitar |
|---|---|---|
| P0 | Benchmark completo e reprodução do autoenquadramento | Fonte, conversão, índice, reprojeção, fill, stroke, encode, apresentação e espera de rede separados. |
| P1 | `VectorSource` sem SHP intermediário obrigatório | SHP e GeoJSON corretos; campos/FIDs/SRC preservados; medir abertura fria e reutilização. |
| P2 | Índice por componentes + cache de reprojeção/LOD | Zoom local não processa todos os componentes distantes; memória limitada; invalidação testada. |
| P3 | Render por camada/bloco e filas independentes | Tile online lento não retém jobs vetoriais prontos; seleção não redesenha a base. |
| P4 | Superfície gráfica persistente | Comparação antes/depois incluindo apresentação; nenhuma regressão no notebook integrado. |
| P5 | Ajustes SIMD, alocação, paralelismo e rótulos | Ganho no perfil completo, não apenas em microbenchmark. |

Sugestões de metas, **não resultados nem promessas**: resposta visual à entrada p95 ≤100 ms; navegação com cache buscando 30 FPS em notebook e 60 FPS em GPU dedicada. Medir separadamente tempo de primeiro conteúdo e de qualidade estabilizada. Definir memória máxima por perfil de máquina e testar pressão de RAM; não fixar “abre qualquer arquivo em 1 segundo”.

Comparar versões instaladas e configurações registradas de GeoNex/QGIS/ArcGIS no mesmo hardware, formato, SRC, extensão, símbolos, antialiasing, rótulos e política de LOD. Fazer testes frios/quentes, pelo menos 30 interações por cenário, reportar p50/p95, RAM e CPU; não calcular p99 confiável com amostra pequena. Incluir os três GeoJSONs reais, SHPs com milhões de feições, poucas feições gigantes, buracos/sobreposições, pontos, linhas, Z/M, atributos complexos e mapa base com rede controlada.

## Escolha de implementação

**Recomendação para a base atual: evolução incremental do núcleo C++**, começando por P0/P1/P2, com fallback para o renderer existente. Reutilizar GDAL/PROJ/GEOS/Skia; não reconstruir um GIS completo a partir de algoritmos próprios.

Se a prioridade empresarial for herdar rapidamente funcionalidades maduras, avaliar um protótipo incorporando bibliotecas QGIS antes de investir numa reconstrução extensa. Há suporte documentado a aplicações independentes, mas integração Qt, distribuição e licença GPL exigem avaliação própria.[^16] Isso não garante automaticamente desempenho idêntico numa interface diferente. Não copiar código QGIS ignorando sua licença.

O ganho estrutural vem de **ler menos, preparar uma vez, desenhar somente o necessário e apresentar sem cópias desnecessárias**. C++ é a ferramenta para executar essa arquitetura, não a arquitetura em si.

## Fontes

Execução incremental posterior à pesquisa: [recursos adaptativos e reuso vetorial entre escalas](performance-vectors-adaptive.md), em 12/09/2026. P0/P2 parcialmente implementados e testados; P1 (provider direto), blocos persistentes e apresentação GPU permanecem pendentes.

Continuação: [reprojeção adaptativa de metadados](performance-vector-projection-batches.md), com workers isolados, buffers proporcionais e testes de identidade numérica. Experimentos de desenho que alteraram pixels foram rejeitados.

Navegação: [agendamento de prévias e refinamento final](fix-navigation-final-scheduling.md), evitando pedidos finais prematuros durante gestos e cancelamento de prévias da mesma câmera. I/O online e apresentação ainda compartilham o quadro.

Fontes primárias consultadas em 12/09/2026; sem data editorial quando não indicada. Evidências locais: `ProjetoService.cs`, `LocalMapServer.cs`, `WkbSkiaParser.cs`, `GeoNexNative.cpp` e o relatório de medições ligado acima. Nenhuma alteração de código foi feita nesta pesquisa.

[^1]: QGIS 3.44, [QgsVectorLayerRenderer — código-fonte](https://api.qgis.org/api/3.44/qgsvectorlayerrenderer_8cpp_source.html), especialmente request/filter, atributos, simplificação e painter.
[^2]: QGIS 3.44, [QgsMapToPixelSimplifier](https://api.qgis.org/api/3.44/classQgsMapToPixelSimplifier.html).
[^3]: QGIS 3.44, [QgsMapRendererParallelJob](https://api.qgis.org/api/3.44/classQgsMapRendererParallelJob.html).
[^4]: QGIS 3.44, [QgsMapRendererCache](https://api.qgis.org/api/3.44/classQgsMapRendererCache.html).
[^5]: Esri, [Settings that affect data access performance](https://doc.esri.com/en/arcgis-pro/latest/get-started/layer-map-project-perf-optimization.html).
[^6]: Esri, ArcGIS Pro 3.5, [Author a map for vector tile creation](https://pro.arcgis.com/en/pro-app/3.5/help/mapping/map-authoring/author-a-map-for-vector-tile-creation.htm). Refere-se à criação de tiles, não a todos os caminhos de desenho desktop.
[^7]: Esri, [Display settings](https://doc.esri.com/en/arcgis-pro/latest/get-started/display-settings.html).
[^8]: GDAL, [ESRI Shapefile / DBF](https://gdal.org/en/stable/drivers/vector/shapefile.html), seção Creation Issues; a documentação também registra mudanças posteriores ao GDAL instalado.
[^9]: GDAL, [Vector API tutorial — Arrow C Stream](https://gdal.org/en/stable/tutorials/vector_api_tut.html#reading-from-ogr-using-the-arrow-c-stream-data-interface).
[^10]: GDAL, [FlatGeobuf](https://gdal.org/en/stable/drivers/vector/flatgeobuf.html).
[^11]: GDAL, [GeoPackage vector](https://gdal.org/en/stable/drivers/vector/gpkg.html).
[^12]: GEOS, [CoverageSimplifier](https://libgeos.org/doxygen/classgeos_1_1coverage_1_1CoverageSimplifier.html), garantias condicionadas à validade da cobertura de entrada.
[^13]: GDAL, [Multi-threading](https://gdal.org/en/stable/user/multithreading.html); o modo thread-safe raster não estende automaticamente essa garantia a vetores.
[^14]: Microsoft, [SwapChainPanel — Windows App SDK](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.swapchainpanel).
[^15]: Skia, [SkCanvas Creation](https://skia.org/docs/user/api/skcanvas_creation/).
[^16]: QGIS, [Standalone applications](https://docs.qgis.org/3.44/en/docs/pyqgis_developer_cookbook/intro.html) e [License](https://qgis.org/license/). A decisão de incorporação depende de revisão das obrigações aplicáveis ao produto.
