# Medição PAN/ZOOM: GeoNex e QGIS

Data: 2026-11-06

## Ambiente e método

- QGIS 4.2.0 (Belém do Pará), GDAL 3.13.1; GeoNex Release x64, GDAL 3.12.1.
- Medições locais e sequenciais na mesma máquina, com os harnesses em `PerfTest/`.
- Raster sintético GeoTIFF tiled, 4096×4096, RGB, 5 overviews e alta entropia. Vetor sintético com 12.000 polígonos EPSG:3857.
- Todas as métricas aquecidas excluem o primeiro frame. Os comparadores usam o mesmo viewport CSS, centro, zoom, pan e resolução física em cada par.
- A rota HTTP do GeoNex mede o tempo completo de resposta, incluindo PNG. O harness QGIS mede renderização e codificação PNG separadamente; o total combinado é somado por frame antes de calcular p50/p95.

## Resultados

### Vetor: 12.000 polígonos, pan de 4 CSS px, viewport 1600×900, escala 4

| Motor | Medição aquecida | p50 | p95 |
| --- | --- | ---: | ---: |
| QGIS | Render da camada, sem codificação PNG | 82,8 ms | 87,3 ms |
| GeoNex Release | Preview interativo, HTTP completo com PNG | 15,5 ms | 21,6 ms |

Neste fixture, o preview do GeoNex respondeu cerca de 5,4× mais rápido que a etapa de render do QGIS, embora a medição do GeoNex inclua codificação e resposta HTTP. O primeiro quadro final do GeoNex Release levou 131 ms. O estilo foi aproximado, não idêntico: o harness QGIS usa estilo por feição e o GeoNex usa pintura em lote.

### Raster sintético: GeoTIFF RGB 4096×4096 (baseline anterior, preview 1×)

Os dois pares usam o mesmo viewport e pan de 32 CSS px. Para o preview, ambos produzem 1600×900 pixels. Para o quadro final, ambos produzem 3200×1800 pixels.

| Zoom | QGIS 1× render+PNG p50 | GeoNex preview 1× HTTP p50 / p95 | QGIS 2× render+PNG p50 | GeoNex final 2× HTTP p50 / p95 |
| ---: | ---: | ---: | ---: | ---: |
| 1× | 62,2 ms | 35,6 / 46,6 ms | 218,0 ms | 212,5 / 240,7 ms |
| 8× | 68,0 ms | 23,7 / 39,7 ms | 249,5 ms | 153,2 / 163,1 ms |
| 64× | 62,9 ms | 23,6 / 43,8 ms | 220,9 ms | 129,6 / 148,1 ms |

Este baseline antigo limitava o preview a 1× físico e mantinha o tamanho CSS. A versão atual usa até 1,5× no preview durante gestos e conserva a DPI solicitada no quadro final.

### Raster ECW real do projeto: comparação direta no QGIS

- Fonte: `Guaratuba Ortho.ecw`, driver ECW, 195.943×387.566, 4 bandas e 10 níveis de overview; armazenada na unidade de rede X:.
- QGIS 4.2.0 / GDAL 3.13.1 e GeoNex Release x64 / GDAL 3.12.1, mesma máquina, zoom 75, viewport visível de 1600×900 CSS, DPI 2, padding de 128 CSS px e pan de 32 CSS px por quadro. Ambos produziram 3712×2312 pixels e usaram bilinear.
- Cinco quadros por motor; p50/p95 calculados sobre os quatro quadros aquecidos. QGIS mede renderização e PNG em etapas separadas; GeoNex mede a resposta HTTP completa.

| Motor | Render p50 / p95 | PNG p50 / p95 | Total por quadro p50 / p95 |
| --- | ---: | ---: | ---: |
| QGIS | 429,9 / 458,2 ms | 209,5 / 230,5 ms | 637,7 / 669,1 ms |
| GeoNex Release | — | — | 339,3 / 403,2 ms (HTTP completo) |

No ensaio controlado, GeoNex concluiu o quadro final 1,88× mais rápido no p50 e 1,66× no p95, embora sua rota inclua composição, codificação PNG, padding e resposta HTTP. A primeira resposta variou com o estado do cache da unidade de rede: uma execução levou 1.572 ms no quadro frio; outra, após leituras aquecidas, levou 729 ms. Esse custo de leitura ECW/SMB ainda domina o primeiro quadro.

No mesmo ECW, o novo preview rebaseado a 1,5× respondeu em p50 62,3 ms / p95 68,1 ms, sem leitura GDAL (`io_ms=0`) e com PNGs de 19,3 MB. A referência anterior, com preview 1× e sem amostragem linear, era p50 33,7 ms / p95 46,8 ms e cerca de 8,6 MB; o novo filtro e a resolução maior acrescentam detalhe ao zoom, à custa de cerca de 29 ms na mediana neste perfil.

O canvas do QGIS também pode transformar o último quadro durante o gesto. A medição QGIS acima cobre render + PNG, não esse caminho de transformação ao vivo. Portanto, os dados demonstram vantagem do GeoNex para concluir quadros finais neste ECW e resolução; a comparação isolada não prova que toda transição visual supera o QGIS.

### Vetor LOTES real do projeto

- Fonte: `LOTES.shp`, 77.146 feições, CRS EPSG:31982; viewport projetado em EPSG:3857, centro e escala idênticos, PAN de 32 CSS px, zoom 64.
- Preview: GeoNex e QGIS produziram 2784×1734 pixels. O preview GeoNex inclui transformação/rebase, PNG e HTTP; o QGIS reporta render puro e, separadamente, uma codificação PNG em memória.
- Quadro final: ambos produziram 3712×2312 pixels. GeoNex mede HTTP completo; QGIS separa pintura e PNG.

| Caminho | Render p50 / p95 | PNG p50 / p95 | Total p50 / p95 |
| --- | ---: | ---: | ---: |
| QGIS preview 1,5× | 5,5 / 6,2 ms | 132,5 / 144,8 ms | 137,4 / 151,0 ms |
| GeoNex preview 1,5×, após primeira rebase | — | — | 13,5 / 15,1 ms (HTTP completo) |
| QGIS quadro final 2× | 8,9 / 12,6 ms | 233,9 / 251,1 ms | 243,4 / 263,7 ms |
| GeoNex quadro final 2× | — | — | 37,5 / 41,0 ms (HTTP completo) |

O primeiro preview GeoNex após a imagem final levou 53,0 ms; depois que a imagem foi rebaseada, os previews aquecidos ficaram entre 13,3 e 15,1 ms. A pintura vetorial do QGIS é mais rápida em termos puros; a resposta HTTP completa do GeoNex é mais rápida porque QGIS não precisa serializar a tela para PNG na interface real. Esse teste não mede nem substitui o canvas interativo do QGIS, que também transforma o último quadro sem PNG. Portanto, não declaro vantagem geral do GeoNex sobre QGIS para vetores.

### Codificação

WebP foi medido e descartado para interação neste raster: levou cerca de 400–660 ms por frame, apesar de reduzir os dados de aproximadamente 34 MB para 0,2–1,3 MB. PNG lossless levou aproximadamente 42–64 ms no quadro completo. Neste perfil, o custo de CPU do WebP não compensou a economia de bytes.

## Implementação e validação

- A política de preview limita a DPI física a 1,5× durante PAN/ZOOM, sem reduzir a resolução dos quadros finais; o reaproveitamento do bitmap usa amostragem linear em zoom e pan fracionário.
- O preview cacheado é rebased após cada gesto e publicado num cache separado. O cache final de resolução total continua disponível para o assentamento da câmera e outras solicitações.
- `ProductionRasterMetrics` mede p50/p95 aquecidos no caminho HTTP de preview real com `GEONEX_BENCH_GLOBAL_PREVIEW=1`; o p50 usa a mediana entre as duas amostras centrais quando o total é par.
- `QgisRasterMetrics.py` agora permite `--padding-css 0` e calcula p50/p95 do render mais PNG por frame, para comparar a mesma área e contagem de pixels.
- `QgisRenderMetrics.py` aceita `--measure-png` para medir também a codificação PNG em memória, mantendo separado o tempo puro de render.
- O experimento de deslocar pixels vetoriais e redesenhar só faixas foi removido: o contrato detectou diferença de até 17/255 nas bordas antialias. O caminho mantido reutiliza o buffer e repinta a camada para preservar os pixels.
- Contratos passaram: `--cached-preview-image-contracts`, `--high-zoom-preview-contracts`, `--polygon-image-cache-contracts`, `--raster-frame-painter-contracts`, `--render-precision-contracts`, `--raster-contracts`, `--render-contracts`, `--vector-display-contracts` e `--vector-resource-contracts`. As métricas de produção incluem o ECW real e `LOTES.shp` com 77.146 feições.
- O assembly Release x64 foi usado nas medições GeoNex deste relatório. Builds passaram sem erros: `GeoNex.csproj` Debug (0 avisos), `PerfTest.csproj` Debug (15 avisos) e GeoNex Release x64 (210 avisos). O executável Release está em `GeoNex/bin/Release/net10.0-windows10.0.19041.0/win-x64/GeoNex.exe`.

## Atualizacao de preview raster — 2026-11-06

O resultado de preview a 1,5x acima e historico. O padrao atual usa 1,25x em cenas com raster, mantendo 1,5x em cenas apenas vetoriais e 2x (DPI solicitada) no quadro final. No A/B com `Guaratuba Ortho.ecw` (17,1 GB), zoom 75, viewport 1600x900 CSS, DPI final 2x, padding 128 CSS px, pan 32 CSS px e 20 amostras aquecidas por configuracao, os dois caminhos fizeram zero leituras GDAL:

| DPI do preview raster | p50 HTTP | p95 HTTP | Payload por PNG |
| ---: | ---: | ---: | ---: |
| 1,5x | 63,7 ms | 98,3 ms | 19.342.816 bytes |
| 1,25x | 33,8 ms | 59,3 ms | 13.432.578 bytes |

Neste A/B, 1,25x reduziu p50 em 47%, p95 em 40% e o payload em 31%, sem alterar a resolucao final. `GEONEX_INTERACTIVE_RASTER_PREVIEW_DPI=1.5` restaura o nivel anterior para comparar a nitidez em cada tela; valores validos ficam entre 1 e 2. Em cenas com raster, o preview parte do cache final de alta resolucao enquanto ele cobre a tela; vetores continuam usando o cache reduzido do gesto para manter rebases baratos. O benchmark vetorial real de `LOTES.shp` (77.146 feicoes, zoom 64, DPI 2) teve primeiro preview de 52,9 ms e cinco previews HTTP aquecidos entre 10,9 e 12,2 ms.

## Limites

Este ensaio mede renderização e codificação no harness do QGIS, não a transformação instantânea que o canvas do QGIS pode aplicar ao último frame durante um gesto. O vetor QGIS mede render sem PNG enquanto GeoNex mede uma resposta HTTP completa, o que torna esse resultado conservador para GeoNex. Os dados são sintéticos, e os estilos não são perfeitamente iguais. Os números sustentam vantagem do caminho medido do GeoNex neste perfil; não demonstram superioridade universal nem substituem testes com os arquivos, estilos e fluxo de produção do usuário.

## Refresh de raster em zoom-in — 2026-11-06

O cache agora avalia se os pixels do quadro disponível ainda resolvem o zoom pedido. Se o zoom-in tornaria o bitmap insuficiente, o servidor renderiza uma imagem interativa no zoom atual com DPI limitada pela política de preview e pelo orçamento de memória. O quadro gerado se torna a nova base para os movimentos seguintes; o render final continua em DPI completa depois que a câmera assenta.

Teste de transição no `Guaratuba Ortho.ecw`, viewport 1600×900 CSS, DPI final 2×, quadro assentado em zoom 8× e gesto para zoom 64×, pans de 32 CSS px. O primeiro refresh novo respondeu em 193,6 ms HTTP, leu GDAL por 124,5 ms e produziu 10.074.909 bytes. Os cinco pans seguintes fizeram zero leituras GDAL: p50 37,7 ms, p95 56,3 ms e 13.432.578 bytes por quadro. No mesmo cenário, o vetor real `LOTES.shp` (77.146 feições) manteve previews aquecidos em torno de 11,7 ms HTTP p50.

Antes de aplicar a DPI interativa também ao renderizador completo, uma execução separada desse salto mediu 686,7 ms HTTP, 544,7 ms de leitura GDAL e 25.790.393 bytes. A leitura do ECW na unidade X: estava mais aquecida na execução posterior, então o tempo total não é uma comparação A/B controlada. O tamanho do PNG caiu 61%; encode caiu de 35,5 ms para 16,2 ms. O custo variável de leitura do ECW permanece no primeiro refresh de regiões novas.

Validação atual: build Release x64 do GeoNex e Release do PerfTest sem erros; `--raster-contracts`, `--high-zoom-preview-contracts`, `--cached-preview-image-contracts` e `--production-raster-refresh-contracts` passaram. O último contrato foi ajustado para fornecer os argumentos opcionais na chamada refletida e comparar o VRT bilinear com a referência bilinear da política atual. A pré-renderização QGIS das oito vistas vizinhas ainda não foi portada; continua exigindo composição que respeite a ordem de rasters e vetores, limite de memória e cancelamento por versão da cena.

## Errata e comparação ECW com conteúdo válido — 2026-11-06

### Correção do enquadramento

As medições ECW acima que usaram o foco padrão `0,5; 0,5` são inválidas para `Guaratuba Ortho.ecw`: esse ponto fica na área vazia do footprint diagonal. Os PNGs tinham fundo branco, apesar de alfa opaco; um teste inicial que contava somente alfa aceitou esses quadros. Esses resultados ECW anteriores ficam substituídos abaixo. Os resultados dos fixtures sintéticos e do shapefile não dependem desse foco e continuam válidos.

Os harnesses `ProductionRasterMetrics.cs` e `QgisRasterMetrics.py` agora verificam cobertura e diversidade de cor na região visível central, ignorando o padding. A validação exige ao menos 5% de cobertura alfa e 64 cores distintas amostradas. No foco inválido `0,5; 0,5`, ambos os harnesses rejeitam a imagem; no foco válido `0,5; 0,25`, os quadros têm 100% de cobertura e mais de 2.280 cores amostradas. A verificação do GeoNex também considera o tamanho físico do preview reduzido.

### Render final válido

Arquivo `Guaratuba Ortho.ecw` (195.943×387.566; footprint diagonal), zoom 64, viewport 1600×900 CSS, DPI 2, padding 128 CSS px, pan de 32 CSS px e 7 amostras por motor. Cada execução aqueceu seis amostras e mediu conteúdo válido. Os engines usam QGIS 4.2.0/GDAL 3.13.1 e GeoNex/GDAL 3.12.1. O GeoNex HTTP inclui codificação e transferência PNG; o QGIS reporta o render separado da codificação PNG do harness.

| Ordem | QGIS render p50 / p95 | QGIS PNG p50 / p95 | QGIS render + PNG p50 / p95 | GeoNex HTTP p50 / p95 |
| --- | ---: | ---: | ---: | ---: |
| QGIS primeiro, depois GeoNex | 486,0 / 825,9 ms | 2.021,7 / 2.038,5 ms | 2.497,9 / 2.848,1 ms | 211,3 / 234,0 ms |
| GeoNex primeiro, depois QGIS | 162,8 / 169,6 ms | 2.011,3 / 2.042,8 ms | 2.174,1 / 2.212,5 ms | 191,7 / 211,0 ms |

O forte efeito da ordem e do cache impede uma afirmação de vencedor absoluto a partir desse A/B. No segundo bloco, o render puro do QGIS foi menor que o tempo HTTP do GeoNex; em ambos, a codificação PNG do harness QGIS custou cerca de 2 segundos e não representa o canvas nativo, que pinta `QImage` diretamente. Assim, esses valores de PNG/HTTP são úteis para diagnosticar transporte, mas não medem tempo até a apresentação de cada interface.

### Transição interativa real

Com foco válido `0,5; 0,25`, cache assentado em zoom 8×, gesto para zoom 64×, viewport 1600×900 CSS, DPI final 2× e pan de 32 CSS px:

| Preview GeoNex | Primeiro refresh | Leitura GDAL | Bytes do refresh | PANs cacheados p50 / p95 | Bytes por PNG cacheado |
| --- | ---: | ---: | ---: | ---: | ---: |
| 1,25× | 438,8 ms | 372,8 ms | 10.074.909 | 44,2 / 62,0 ms | aproximadamente 10 MB |
| 1,5× | 448,0 ms | 373,8 ms | 14.507.566 | 62,9 / 87,5 ms | aproximadamente 14,5 MB |

No caminho cacheado, 1,25× reduziu a latência p50 em cerca de 30% e o payload em cerca de 31% contra 1,5×. O primeiro frame novo continua dominado pela leitura do trecho ECW ainda não coberto. A amostra QGIS de render final acima não mediu a transformação do último frame que o QGIS faz durante o gesto; portanto não sustenta uma alegação de que o GeoNex já supera o QGIS nas transições visuais.

### Pesquisa reproduzível do preview QGIS

O código QGIS 4.2.0 agenda o primeiro trabalho de vizinhança 250 ms após o frame, em uma grade 3×3 com o centro omitido. Renderiza os oito vizinhos um por vez, sem rótulos; filtra camadas pelo último tempo de render (limite padrão 250 ms) e cancela os jobs sem bloquear quando a cena muda. Cada imagem é pintada sob o frame base antigo, que cobre as regiões ainda não prontas. O frame normal distribui camadas em tarefas paralelas e processa rótulos separadamente. Fontes: [`QgsMapCanvas`](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/gui/qgsmapcanvas.cpp), [`QgsMapCanvasMap`](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/gui/qgsmapcanvasmap.cpp), [`QgsDataProvider`](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/providers/qgsdataprovider.cpp), [`QGIS constants`](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/qgis.cpp) e [`QgsMapRendererParallelJob`](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/maprenderer/qgsmaprendererparalleljob.cpp).

O GeoNex já mantém a transformação imediata e o cache de gesto, mas ainda não tem o preenchimento de vizinhança. Portar o recurso inteiro antes de uma fila de baixa prioridade poderia competir com leituras GDAL não interrompíveis e piorar o frame seguinte. O próximo passo seguro é implementar tiles vizinhos canceláveis com orçamento de memória e tempo por camada, priorizar a direção do gesto e excluir ECW/SMB lento até que a medição demonstre custo marginal aceitável. A ordem de composição raster/vetor deve ser preservada; uma imagem transparente única de vetores não pode ser sobreposta arbitrariamente ao mapa.

Validação desta revisão: `dotnet build PerfTest/PerfTest.csproj -c Release --no-restore` e build Release x64 do GeoNex concluíram com 0 avisos e 0 erros. Passaram os contratos `--raster-contracts`, `--cached-preview-image-contracts`, `--high-zoom-preview-contracts` e `--production-raster-refresh-contracts`. O executável Release foi atualizado em `GeoNex/bin/Release/net10.0-windows10.0.19041.0/win-x64/GeoNex.exe`.

## Revisão do caminho vetorial QGIS/GeoNex

No código QGIS 4.2.0, cada camada vetorial recebe um trabalho independente, com `QgsFeatureRequest` limitado pela extensão visível, subset dos atributos usados e feedback de cancelamento. A simplificação `OptimizeForRendering` calcula tolerância a partir do tamanho de pixel atual e compensa a reprojeção. As imagens de camada podem ser reaproveitadas; a etapa de rótulos fica separada. Isso combina índice espacial do provider, simplificação perceptual e cache de imagem por camada.

O GeoNex compila os SHPs e mantém índice espacial nativo, caminhos `SKPath` com overscan, cache de imagens de polígonos e cache de frames. No `LOTES.shp` (77.146 feições), a medição mais recente do render puro QGIS ficou em p50 9,4 ms/p95 10,8 ms; o custo `draw` do GeoNex ficou perto de 9,1 ms, enquanto sua resposta HTTP final aquecida ficou em torno de 22,6 ms. A diferença inclui PNG/HTTP no GeoNex e não mede a pintura nativa QGIS; não há evidência de que o GeoNex seja mais rápido no desenho vetorial puro. Nos pans cacheados, o GeoNex reaproveita geometria e frame e responde em dezenas baixas de milissegundos com PNG; o próximo ganho para camadas realmente pesadas deve vir de cache espacial em tiles/quadtrees persistentes e política de simplificação por erro em pixels, sem reduzir precisão dos dados/edição.

Fontes: [`QgsVectorLayerRenderer`](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/vector/qgsvectorlayerrenderer.cpp), [`QgsSimplifyMethod`](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/qgssimplifymethod.cpp), [`QgsMapRendererJob`](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/maprenderer/qgsmaprendererjob.cpp) e [`QgsMapRendererParallelJob`](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/maprenderer/qgsmaprendererparalleljob.cpp).
