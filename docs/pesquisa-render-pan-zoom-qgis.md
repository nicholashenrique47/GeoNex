# Pesquisa: renderização PAN/ZOOM do QGIS e aplicação no GeoNex

Data da análise: 2026-11-06  
Código-fonte pesquisado: QGIS `final-4_2_0`.

## Como o QGIS mantém a imagem responsiva

### Durante o gesto

O QGIS desloca o conteúdo atual do canvas junto com o movimento do mouse. Ao terminar o pan, converte esse deslocamento em uma nova posição central e agenda uma renderização. O usuário vê o último quadro transformado enquanto a próxima renderização não termina. O GeoNex já segue a mesma ideia no navegador: aplica a transformação CSS imediatamente e solicita quadros interativos/finais em separado.

Fontes: [`QgsMapCanvas::panAction` e `panActionEnd`](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/gui/qgsmapcanvas.cpp), [`QgsMapCanvasMap::paint`](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/gui/qgsmapcanvasmap.cpp).

### Renderização e composição

O renderizador paralelo do QGIS prepara tarefas por camada, executa os trabalhos de camada em paralelo e compõe as imagens respeitando a ordem visual das camadas. O canvas pode apresentar a imagem parcial disponível enquanto outras camadas ainda estão trabalhando. A renderização de rótulos é tratada separadamente da composição normal.

Fontes: [`QgsMapRendererParallelJob`](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/maprenderer/qgsmaprendererparalleljob.cpp), [`QgsMapRendererJob`](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/maprenderer/qgsmaprendererjob.cpp).

### Cache de camadas e quadros parciais

O cache do QGIS guarda imagens por camada e os dados de extensão/mapa-para-pixel necessários para reposicioná-las. Uma correspondência exata permite ignorar o render daquela camada. Quando a extensão muda, o renderizador pode transformar a imagem anterior e usá-la como saída provisória; a interseção geográfica é desenhada na posição equivalente do novo quadro. Isso mantém pixels antigos visíveis enquanto o novo conteúdo chega.

O GeoNex já tem caches por camada para rasters, cache de geometria/imagens de vetores e cache global da composição. Seu cache de gesto faz rebase em precisão dupla e reaproveita a imagem enquanto a janela solicitada ainda cabe no quadro disponível. A diferença é que o QGIS tem um mecanismo geral de cache de imagem por camada para extensões parcialmente coincidentes; a composição global do GeoNex ainda é a via principal para a transição imediata.

Fontes: [`QgsMapRendererCache`](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/maprenderer/qgsmaprenderercache.cpp), [`QgsMapRendererJob::prepareJobs` e composição parcial](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/maprenderer/qgsmaprendererjob.cpp).

### Pré-renderização da vizinhança

Depois que o quadro principal termina, o canvas desktop do QGIS pode lançar trabalhos para as oito posições vizinhas de uma grade 3×3. Cada trabalho cobre aproximadamente um viewport, é assíncrono, não desenha rótulos e marca o contexto como preview. Antes de incluir cada camada, o QGIS consulta se o provedor aceita preview e usa o tempo de render anterior e um limite de tempo. Quando uma imagem vizinha fica pronta, ela é desenhada na sua posição geográfica; novos trabalhos são agendados em sequência. Um movimento maior, então, pode revelar conteúdo que já estava sendo preparado.

Essa estratégia evita que uma camada remota/lenta seja duplicada oito vezes, mas não promete que todas as camadas estejam prontas na vizinhança. O canvas QGIS conserva a transformação imediata do quadro anterior como fallback.

Fonte: [`QgsMapCanvas::startPreviewJobs`, `startPreviewJob` e `schedulePreviewJob`](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/gui/qgsmapcanvas.cpp), [`QgsMapCanvasMap::addPreviewImage` e `paint`](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/gui/qgsmapcanvasmap.cpp).

### Raster: blocos e apresentação progressiva

O iterador raster do QGIS divide leituras em blocos limitados pelo provedor e mantém a relação entre janela da fonte e destino. O renderer pode publicar saídas parciais; os callbacks são limitados por intervalo mínimo para evitar uma composição por cada notificação de bloco. O provedor GDAL também administra handles/datasets para leitura concorrente.

Fontes: [`QgsRasterIterator`](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/raster/qgsrasteriterator.cpp), [`QgsRasterLayerRenderer` e feedback parcial](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/raster/qgsrasterlayerrenderer.cpp), [`QgsGdalProvider`](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/providers/gdal/qgsgdalprovider.cpp).

## O que foi aplicado nesta iteração

O GeoNex mantinha o preview interativo em até 1,5× DPI para qualquer cena. No benchmark anterior do ECW real, esse caminho cacheado levou p50 62,3 ms e produziu cerca de 19,3 MB por PNG; a referência anterior em 1× levou p50 33,7 ms e produziu cerca de 8,6 MB. A diferença é um compromisso real entre detalhe transitório e latência/transferência local.

O preview agora seleciona DPI pelo tipo de conteúdo: até 1,25× em cenas com raster e até 1,5× em cenas apenas vetoriais. Quadros finais continuam na DPI solicitada. A decisão é somente de amostragem transitória: não altera fonte, geometria, escala, CRS ou pixels dos quadros finais. O limite de raster pode ser ajustado de 1× a 2× pelo ambiente `GEONEX_INTERACTIVE_RASTER_PREVIEW_DPI`.

O A/B foi repetido no ECW de produção `Guaratuba Ortho.ecw` (17,1 GB), zoom 75, viewport 1600×900 CSS, DPI final 2×, padding 128 CSS px, pan de 32 CSS px e 20 amostras aquecidas por configuração. Ambos os previews fizeram zero leituras GDAL. A 1,5×: p50 63,7 ms, p95 98,3 ms e 19.342.816 bytes por PNG. A 1,25×: p50 33,8 ms, p95 59,3 ms e 13.432.578 bytes. Neste teste, 1,25× reduziu p50 em 47%, p95 em 40% e payload em 31%; o quadro final permanece em 2×. Em cenas com raster, o cache do gesto parte do último quadro final de alta resolução sempre que ele ainda cobre a área visível; só usa a imagem rebased do gesto quando o pan sai desse limite. Vetores continuam usando o cache reduzido do gesto para manter rebases baratos. O vetor real `LOTES.shp` (77.146 feições) foi reavaliado no zoom 64: após a primeira rebase (52,9 ms), os cinco previews aquecidos ficaram entre 10,9 e 12,2 ms HTTP completo; a regra de DPI vetorial continua em 1,5×.

## Diferenças restantes e caminho de engenharia

1. Avaliar a nitidez transitória no WebView ao lado da latência medida; o usuário pode comparar 1,25× e 1,5× via variável de ambiente sem alterar a DPI final.
2. O maior próximo recurso inspirado no QGIS é pré-render de vizinhança em baixa prioridade, filtrado pelo tempo medido de cada camada. Precisa de orçamento explícito de RAM, cancelamento por revisão da cena e prioridade ao quadrante na direção do pan.
3. Para preservar a ordem cartográfica, as imagens de vizinhança precisam ser armazenadas por camada ou por grupos entre camadas raster; uma imagem vetorial transparente não pode ser simplesmente colocada sobre a composição se houver raster acima dela.
4. Raster ECW em unidade de rede não deve ser lido oito vezes em background. As primeiras candidatas para vizinhança são vetores rápidos e rasters locais já cobertos por cache; provedores lentos devem ser omitidos até que uma política de preview seja medida.
5. O canvas QGIS mostra imagens nativas. O GeoNex precisa transportar e decodificar um PNG entre o renderer local e o WebView; comparações de render isolado não são equivalentes a tempo até a apresentação. Os benchmarks existentes relatam render, encode, payload e HTTP separadamente quando disponíveis.

## Leitura da captura enviada

Os lotes continuam visiveis sobre uma ortofoto bastante pixelada. Minha leitura e que o browser esta ampliando o ultimo quadro raster durante a transicao; a imagem sozinha nao confirma se foi capturada durante o gesto ou depois do quadro final. Para o caminho interativo raster, o servidor volta a amostrar do quadro final em alta resolucao sempre que ele ainda cobre a area pedida, evitando reduzir a origem do gesto a cada pan curto. Quando o usuario sai da cobertura original, mantem o fallback rebased para que o pan continue respondendo.

## Limites das conclusões

Esta análise descreve o código-fonte desktop do QGIS 4.2.0 e mecanismos concretos encontrados nele. Não significa que todo provider, projeto ou gesto use todos os caminhos. Os benchmarks mostram que 1,25× melhora a latência do preview raster neste ECW e que vetores aquecidos respondem em 10,9–12,2 ms HTTP. Isso não prova superioridade universal do canvas interativo: o QGIS transforma a imagem dentro da UI, enquanto o GeoNex mede também o encode e o transporte local em PNG. A aparência do preview de 1,25× ainda deve ser conferida no WebView durante zooms grandes; o usuário pode voltar a 1,5× pela variável de ambiente.

## Refresh raster quando o cache perde resolução

A captura do usuário mostra limites de lotes nítidos sobre uma ortofoto ampliada em blocos. O caminho rápido estava reutilizando o quadro final mesmo quando o zoom novo exigia mais pixels do que a imagem antiga podia fornecer. Além disso, um refresh que chegava ao renderizador completo continuava usando a DPI final e podia codificar um PNG de 25,8 MB.

O GeoNex agora calcula a resolução efetiva do cache com base no zoom e nos pixels físicos de origem/destino. Enquanto o cache final tem detalhe suficiente, o preview continua sem leitura GDAL. Quando um zoom-in ultrapassa essa margem, o pedido faz um render interativo na DPI de preview configurada, dentro do orçamento de memória; o resultado é retido como nova imagem base de gesto. A DPI final completa continua sendo usada quando a câmera assenta. Isso evita ampliar indefinidamente um bitmap já reduzido e permite que os PANs seguintes voltem ao caminho cacheado.

No ECW real, com viewport 1600×900 CSS, DPI final 2×, transição de zoom 8× para 64× e pan de 32 CSS px, o refresh raster novo respondeu em 194 ms HTTP, leu GDAL por 125 ms e enviou 10.074.909 bytes. Os cinco PANs seguintes não fizeram novas leituras GDAL e tiveram p50 37,7 ms e p95 56,3 ms, com 13.432.578 bytes por quadro. Uma execução anterior sem a DPI interativa no render completo respondeu em 687 ms, com leitura de 545 ms e 25.790.393 bytes; o cache de disco/rede do ECW estava mais aquecido na execução nova, portanto a diferença de tempo total não é um A/B controlado. A queda no tamanho do PNG e no tempo de encode reflete a redução de DPI; o primeiro refresh ainda depende da leitura do ECW na unidade X:.

O lote real `LOTES.shp` (77.146 feições, zoom 64, DPI 2×) permaneceu estável: quadro inicial HTTP 131 ms e previews aquecidos com mediana aproximada de 11,7 ms. Os contratos de pixels do preview e de uso do cache raster passaram.

Este mecanismo segue a ideia do QGIS de manter o quadro antigo como fallback enquanto o render atualiza a vista. Ainda falta reproduzir o trabalho de vizinhança 3×3; para isso, o GeoNex precisa guardar imagens parciais por camada ou por grupos contíguos entre rasters, além de controlar prioridade, cancelamento e memória. O refresh atual é deliberado quando o cache perde detalhe e a primeira leitura de uma região nova ainda pode ficar limitada pela latência do ECW/SMB.

## Revisão do código-fonte e correção do A/B — 2026-11-06

Revisei diretamente a implementação do QGIS 4.2.0. O caminho de vizinhança funciona assim:

1. Ao terminar o frame principal, o canvas guarda o tempo de render por camada e agenda o primeiro preview após `PREVIEW_JOB_DELAY_MS = 250` ms.
2. O trabalho cobre um viewport deslocado em uma grade 3×3. A posição central é ignorada; as oito posições são processadas em sequência. O próximo preview só começa quando o anterior termina.
3. Cada preview usa o renderizador sequencial, desativa rótulos e sinaliza `RenderPreviewJob`. Camadas explicitamente marcadas `rendering/noPreviewJobs` são ignoradas; as demais só entram se o provedor aceitar o orçamento de preview. O provedor base aceita quando o último render da camada levou no máximo `MAXIMUM_LAYER_PREVIEW_TIME_MS = 250` ms.
4. A imagem de vizinhança é desenhada primeiro, na sua extensão geográfica. O frame principal antigo é pintado por cima e continua sendo o fallback nas áreas que ele cobre. Uma nova navegação cancela os previews sem bloquear a interface.
5. No frame principal, trabalhos de camada podem rodar em paralelo; rótulos são uma fase separada. Isso é diferente dos previews de vizinhança, que são sequenciais e sem rótulos.

Fontes primárias: [`QgsMapCanvas::startPreviewJob`, `previewJobFinished` e cancelamento](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/gui/qgsmapcanvas.cpp), [`QgsMapCanvasMap::paint`](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/gui/qgsmapcanvasmap.cpp), [`QgsDataProvider::renderInPreview`](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/providers/qgsdataprovider.cpp), [`Qgis::PREVIEW_JOB_DELAY_MS` e limite de camada](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/qgis.cpp) e [`QgsMapRendererParallelJob`](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/maprenderer/qgsmaprendererparalleljob.cpp).

O renderer raster normal do QGIS também separa leitura e apresentação em blocos. `QgsRasterIterator` limita cada bloco à dimensão máxima informada pelo provider e avança por linhas/colunas; o feedback pode solicitar uma imagem parcial, renderizada novamente com `previewOnly`, no máximo uma vez a cada 250 ms. O código-fonte ainda marca como trabalho futuro redesenhar somente a área do bloco recém-chegado, então o preview parcial pode redesenhar a camada inteira. No GeoNex, o caminho de ECW local atualmente solicita a janela visível com uma única chamada `RasterIO` e publica o PNG quando o frame está pronto. Isso evita costurar muitos blocos e mantém os pixels exatos, mas não expõe imagem parcial durante os ~373 ms de leitura observados. O caminho de basemap online do GeoNex já tem carregamento progressivo por tiles, separado do ECW local.

Fontes raster: [`QgsRasterIterator` (limite e sequência dos blocos)](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/raster/qgsrasteriterator.cpp), [`QgsRasterLayerRendererFeedback` (preview parcial e intervalo de 250 ms)](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/raster/qgsrasterlayerrenderer.cpp) e [`QgsRasterLayerRendererFeedback` (intervalo configurado)](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/raster/qgsrasterlayerrenderer.h).

### Vetores: consulta por extensão, atributos mínimos e simplificação em pixels

O QGIS prepara um renderer e um `QgsVectorLayerFeatureSource` por camada. Para cada render, monta um `QgsFeatureRequest` filtrado pelo retângulo visível (com buffer/extensão ampliada quando o renderer pede), solicita somente os atributos necessários ao renderer/rótulos e passa um `QgsFeedback` para interrupção. Se a simplificação estiver habilitada, calcula a tolerância em unidades de mapa a partir de pixels e da resolução atual; quando há reprojeção, ajusta a tolerância ao tamanho da transformação. Os rótulos são registrados e resolvidos pelo trabalho de labeling separado. A camada pode ser pulada por completo quando há imagem válida no cache, e saídas parciais podem partir da imagem anterior transformada.

Fontes primárias: [`QgsVectorLayerRenderer` (feature source, requests e simplificação)](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/vector/qgsvectorlayerrenderer.cpp), [`QgsSimplifyMethod`](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/qgssimplifymethod.cpp), [`QgsMapRendererJob` (cache por camada e saída parcial)](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/maprenderer/qgsmaprendererjob.cpp) e [`QgsMapRendererParallelJob` (camadas paralelas e fase de rótulos)](https://raw.githubusercontent.com/qgis/QGIS/final-4_2_0/src/core/maprenderer/qgsmaprendererparalleljob.cpp).

O GeoNex já atende a mesma família de custos por outro caminho: compila geometrias SHP em buffers mapeados, mantém índice espacial nativo e caminhos de desenho em cache com overscan, além de imagens de polígonos e cache global de frame. Isso evita repetir leitura/compilação da geometria em pans próximos, enquanto o QGIS consulta os candidatos da extensão a cada novo job e reaproveita imagens de camada quando a chave espacial permite. Para vetores em lote, o caminho do GeoNex tem oportunidade de ganhar em custo amortizado; para o frame inicial, o QGIS ainda pode ganhar por não transportar PNG pela interface.

### Consequência para a implementação GeoNex

O GeoNex já transforma no cliente o último frame enquanto uma câmera nova é processada, e reaproveita imagens cobertas pelo padding/cache. Esse caminho é o análogo direto da resposta imediata do canvas QGIS. A vizinhança 3×3 deve ser uma segunda camada opcional, nunca um trabalho que bloqueie o render prioritário. O servidor atual serializa renders completos por um gate e serializa leituras dos datasets GDAL; uma pré-leitura ECW iniciada antes do próximo gesto poderia aumentar a latência do quadro que o usuário realmente pediu. Além disso, uma imagem transparente de vetor precisa respeitar a ordem de composição com rasters acima e abaixo. A implementação segura precisa de fila ociosa cancelável, limite por camada baseado no tempo observado, chave por revisão/estilo/câmera e cache de imagens espaciais com composição em ordem.

Por isso, a reprodução mais promissora para arquivos pesados não é lançar oito leituras de raster em paralelo. Primeiro, o frame atual e sua transformação continuam prioritários. Depois de 250 ms sem nova navegação, podem ser preenchidos tiles vizinhos sob orçamento estrito, começando por camadas rápidas/cacheadas; um raster ECW/SMB sem cache deve ser excluído até que se prove que sua pré-leitura não compete com o próximo frame. Isso preserva a vantagem do GeoNex de medir/limitar DPI de gesto e acrescenta pré-render oportunista apenas quando há capacidade disponível.

### Errata do raster ECW

Os números anteriores de `Guaratuba Ortho.ecw` neste documento usavam foco central `0,5; 0,5`. A área útil da ortofoto é diagonal dentro do retângulo geográfico; essa câmera produziu um quadro visualmente branco. Portanto, os tempos e payloads ECW anteriores não são evidência de desempenho e ficam substituídos pelos resultados da seção de correção no relatório. Os harnesses agora rejeitam quadros com cobertura de conteúdo insuficiente e menos de 64 cores amostradas na parte visível; o foco válido deste arquivo foi `0,5; 0,25`.

Na medição válida da transição de zoom 8×→64×, com DPI interativa 1,25×, o primeiro refresh respondeu em 438,8 ms (372,8 ms de leitura GDAL, 10.074.909 bytes); os pans cacheados seguintes tiveram p50 44,2 ms e p95 62,0 ms. A 1,5×, o refresh foi 448 ms e os pans cacheados tiveram p50 62,9 ms/p95 87,5 ms. O preview 1,25× enviou cerca de 31% menos bytes e melhorou a mediana dos pans cacheados em aproximadamente 30%. O ganho é de quadro interativo do GeoNex no teste local; a transformação nativa do canvas QGIS não foi medida por esse harness.

## Validação desta revisão

Build Release x64 do GeoNex e build Release do PerfTest concluídos com zero avisos e zero erros. Passaram os contratos de `--raster-contracts`, `--cached-preview-image-contracts`, `--high-zoom-preview-contracts` e `--production-raster-refresh-contracts`. O último exercita o assembly de produção com VRT/fonte sintética, barreira de frames, leases, invalidação de cache, comparação exata de pixels e proteções contra dataset/CRS/arquivo obsoletos.

## Próxima sequência de implementação

1. Para o gargalo raster observado, experimentar leitura da janela em faixas/tile com uma primeira imagem parcial limitada a 250 ms e refinamento progressivo. Comparar tempo até o primeiro pixel, tempo até o frame final, custo total de GDAL e igualdade de pixels. Se dividir o `RasterIO` piorar o total ou criar costuras, manter uma leitura única para aquele driver/overview.
2. Adicionar pré-render de vizinhança somente depois que o frame atual assentar. Começar por vetores e camadas que tenham render observado abaixo do orçamento de 250 ms; cancelar entre tiles quando a revisão/câmera mudar. Excluir ECW/SMB da fila até que uma medição prove que não aumenta a latência do próximo gesto.
3. Evoluir o cache para imagens por camada/tiles com composição cartograficamente ordenada, orçamento de memória e invalidação por estilo/dados/CRS/escala. Essa estrutura permite reaproveitar a parte comum entre o frame antigo e o novo sem exigir que um único cache global cubra toda a janela.

Os dois primeiros valores de 250 ms são pontos de partida compatíveis com o QGIS analisado, não parâmetros finais garantidos para o hardware do usuário. O objetivo do A/B seguinte deve ser reduzir o tempo até a imagem útil sem piorar p95, qualidade final, uso de memória ou fluidez durante gestos rápidos.
