# P1-C — cache de imagem de polígonos

Data: 13/09/2026. Primeiro incremento da fase P1-C do [plano mestre](plano-mestre-motor-gis-qgis.md), não conclusão integral da fase nem declaração de paridade com QGIS/ArcGIS.

## Problema e referência

O cache geométrico já evitava reconstruir o SKPath, mas cada refinamento do basemap ainda executava DrawPath em todos os polígonos. Em LOTES, essa pintura podia consumir mais de 100 ms mesmo com `geometry_ms=0`.

O [QgsMapRendererCache, tag QGIS 4.2.0](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/maprenderer/qgsmaprenderercache.cpp) associa imagens a extensão/transformação e dependências de camadas, invalidando-as quando suas fontes pedem redesenho. A implementação local aplica esse princípio em um subconjunto conservador, sem copiar o código QGIS e sem introduzir dependências.

## Implementação

- `PolygonImageCache`: imagem RGBA premultiplicada, imutável, por camada. Preenchimento e borda são pintados juntos, na ordem original, no mesmo bitmap transparente.
- Reuso somente com mesma fonte, revisão vetorial, viewport, origem, CRS/offsets, matriz física, dimensões e parâmetros dos pincéis. Não estica imagem antiga para atender zoom novo.
- Integração nos dois caminhos de polígonos uniformes: geometria recém-construída e geometria em cache. Não altera arquivos, vértices, IDs, consultas espaciais nem simplificação.
- Elegibilidade inicial: quadro final de tela, sem rotação, sem rótulos, linha sólida e path com pelo menos 2.048 pontos. Paths menores, impressão, interação, categorias e efeitos seguem o desenho anterior.
- `SceneRevision` continua protegendo o quadro global contra publicação antiga. `VectorPresentationRevision` não muda quando **apenas pixels de um raster online** são publicados. `RequestRedraw` e a invalidação normal continuam conservadores: invalidam imagens vetoriais também.
- Imagem composta no Z original da camada; ferramentas de edição/seleção e demais overlays permanecem fora do cache. Não separa a cena em elementos DOM nem modifica o transporte PNG.
- Contadores no servidor: `PolygonImageCacheHits`, `PolygonImageCacheBuilds`, `PolygonImageCacheBytes`. O tempo de composição continua dentro de `draw`.

## RAM e concorrência

Teto: menor entre 64 MiB e metade do orçamento vetorial adaptativo existente. Este último considera RAM total/disponível e fica zerado sob pressão severa. Não há piso de memória imposto pelo novo cache.

Máximo de 64 entradas, uma câmera por camada, descarte LRU antes de alocar a substituição. Contabilidade dos pixels inclui o bitmap em construção. No ensaio 2050×1350, uma imagem reteve **11.070.000 bytes (~10,6 MiB)**. Scratch interno do Skia, metadados e outras caches não estão incluídos; o governador global P5 continua pendente.

A manutenção de cada render completo reduz o orçamento e descarta revisões antigas, mesmo quando os polígonos seguintes são pequenos. Descarte/uso têm exclusão mútua; não há publicação em background. Cancelamento entre preenchimento/borda e antes da publicação; bitmap parcial nunca entra no cache. Falha de alocação antes de compor mantém o fallback direto. Após iniciar a composição, não se repete a pintura em caso de erro.

## Qualidade: resultado e limitação explícitos

Uma tentativa de armazenar preenchimento e borda separadamente foi rejeitada: LOTES mostrou diferença de até 10 níveis de canal. A versão entregue preserva a pintura completa da camada em um único bitmap.

- LOTES sobre fundo transparente: **pixels idênticos** ao renderer direto nos zooms relativos 1, 4, 16 e 64, inclusive após PNG/decode.
- Fixture sintética: diagonais subpixel, furos, contornos sobrepostos, fundos transparentes/semitransparentes/opacos e DPI 1/1,5/2. Máximo observado de 1/255 por canal premultiplicado; imagem transparente idêntica.
- LOTES sobre tiles locais texturizados opacos: diferença RGB máxima **5/255**, RMS **0,2843/255**, alpha idêntico. Compor uma camada RGBA já rasterizada não é numericamente igual a executar cada operação diretamente sobre o fundo: `SrcOver` inteiro de 8 bits não é associativo.
- Contrato desse ensaio sobre tiles: máximo RGB ≤8/255, RMS ≤0,5/255, alpha idêntico, e igualdade exata entre construção e reutilização. O limite RGB não autoriza perda de feições/furos ou deslocamento geométrico. Não é garantia universal para qualquer combinação de símbolos/transparências; ampliar a matriz antes de expandir a elegibilidade.
- Impressão, rotação e preview: bypass confirmado pela DLL real, sem criação de imagens e pixels idênticos com o recurso ligado/desligado.

Não prometer igualdade byte a byte com a composição antiga sobre todo fundo. A exportação continua no caminho original. A navegação manual no WebView e comparação visual interativa com QGIS não foram realizadas neste incremento.

## Medições locais

Windows, .NET 10 Release, SkiaSharp 3.119.2, GDAL 3.12.1; C++ ABI 4 existente, sem alterações nativas. LOTES.geojson: 57.454 feições, convertido pelo importador existente para SHP de cache; original não editado. Viewport CSS 1600×900, DPI 1, quadro físico 2050×1350 devido ao overscan existente.

| Cenário na DLL real | Pintura direta | Construção do cache | Reutilização |
|---|---:|---:|---:|
| LOTES sem basemap, zoom relativo 4 | 124,44 ms | 124,96 ms | 6,43–9,12 ms |
| LOTES + tiles locais já disponíveis, zoom relativo 4 | 113,13 ms | 145,22 ms | 11,62 ms |

No primeiro cenário, HTTP + decode caiu de 186,43 ms para 75,18–90,32 ms. No segundo, de 183,18 ms para 75,24 ms. São amostras de smoke/A-B, não percentis estatísticos ou FPS garantido. A primeira construção pode custar mais; o ganho é em reutilizações da mesma câmera. Zooms com poucos polígonos continuam diretos. PNG/cópias/HTTP ainda consomem tempo; P3-W segue relevante.

## Verificação reproduzível

Na raiz do repositório, PowerShell:

```powershell
dotnet build GeoNex/GeoNex.csproj -c Release -p:GeoNexBuildNative=false --no-restore -v:q
dotnet build PerfTest/PerfTest.csproj -c Release -p:GeoNexBuildNative=false --no-restore -v:q
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --polygon-image-cache-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --production-map-metrics GeoNex/bin/Release/net10.0-windows10.0.19041.0/win-x64/GeoNex.dll C:\Users\Nicho\Downloads\LOTES.geojson --polygon-images
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --production-map-metrics GeoNex/bin/Release/net10.0-windows10.0.19041.0/win-x64/GeoNex.dll C:\Users\Nicho\Downloads\LOTES.geojson --delayed-basemap
```

`--delayed-basemap` usa HTTP local determinístico, não Google: atrasa tiles, muda câmera, verifica refinamento e preservação da imagem válida em 403/204/500. O modo `--polygon-images` compara cache desligado/construção/hits e exige ao menos um hit; RAM insuficiente ou dataset inelegível podem impedir esse benchmark, mas o renderer permanece funcional em fallback. Os ensaios usam o importador real e podem criar derivados no cache temporário normal.

Passaram também: render-path-cache, render-precision, resource-lease, raster, raster-overview, online-session, online-worker, telemetry, production-raster-refresh e `node PerfTest/MapEngineContracts.js`. Build do aplicativo: **0 erros, 191 avisos preexistentes**; PerfTest: 0 erros, 3 avisos GDAL gerados. Vulnerabilidades/dependências anteriormente reportadas (NU1903/NU1603) não foram corrigidas por esta fase.

As GeoAI Skills (`swe-devops-standards`) orientaram o recorte conservador, testes na DLL real, orçamento/disposal, validação de pixels e registro das limitações.

## Reversão e sequência

Definir `GEONEX_POLYGON_IMAGE_CACHE=0` no ambiente do processo e reiniciar o aplicativo desativa o recurso e mantém o desenho direto. Não há migração de projeto/dados para reverter.

Próximos recortes: revisão por camada (hoje uma edição invalida todos os vetores), cobertura de categorias/labels/grupos/blends, medição de navegação real e redução do custo de transporte/composição de quadros. Reconciliar mudanças pendentes de outra máquina antes de alterar o pipeline raster compartilhado.
