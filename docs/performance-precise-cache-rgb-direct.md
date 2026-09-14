# Cache preciso e tiles RGB sem cópia intermediária

2026-09-13 — continuação de `fix-high-zoom-render-precision.md`.

## Implementação

- Cache vetorial existente agora identifica também a origem de desenho. Paths precisos usam cobertura local e só são reutilizados com a mesma origem; nunca podem ser confundidos com paths mundiais. Refinamentos e zooms compatíveis evitam consulta espacial/reprojeção/construção repetida. Mudança de origem, falta de cobertura, qualidade incompatível ou invalidação provocam reconstrução. Mantidos orçamento adaptativo compartilhado, LRU, snapshots COW e descarte.
- A simbologia UNICA em zoom alto consulta esse cache antes do índice. Bordas permanecem presentes. Com rótulos ativos, usa-se o caminho completo para não saltar sua renderização. A matriz original é restaurada após o desenho.
- No raster online RGB de mesmo CRS, GDAL escreve cada faixa diretamente nas linhas do bitmap final. Alpha inicializado em 255; removidas a alocação de bitmap e a cópia Skia de cada faixa. A mesma janela fracionária e interpolação bilinear foram mantidas. RGBA/reprojeção/borda da cobertura continuam com conversão anterior. Cancelamento e falhas descartam o resultado inteiro, preservando cache válido.
- Nenhuma alteração em dados, nível máximo dos provedores, simplificação, C++ ou qualidade final. GeoAI Skills orientaram os contratos de precisão, integridade e recursos limitados.

## Evidências

- Contratos do cache: identidade da origem, cobertura, qualidade, pixels, COW, orçamento e invalidação aprovados.
- Tiles: caminho direto comparado byte a byte ao caminho anterior com cópia Skia em quatro escalas, incluindo pixels fracionários e overzoom. Faixas versus leitura integral também idênticas. Níveis HTTP, cancelamento, deduplicação, propriedade dos recursos e retry limitado aprovados.
- LOTES compilado pelo importador: 57.454 feições / 324.191 vértices. Testes de produção de leitura, reprojeção, precisão e novo cache aprovados. Original GeoJSON não modificado.
- Câmera repetida em zoom relativo 1/4/16/64: pixels idênticos nas quatro amostras. Em zoom 16 e 64, as três repetições reutilizaram geometria (`geometry;dur=0.000`). Zoom 64: resposta HTTP de aproximadamente 22–25 ms e resposta+decode de 31–35 ms nesta execução. Não é FPS nem comparação estatística entre versões; máquina/sistema influenciam os tempos.
- Há gargalo remanescente de desenho no zoom relativo 4 (~110–151 ms). Cache elimina construção repetida, não o custo de rasterizar todos os contornos; não se afirma equivalência com QGIS/ArcGIS.
- Integração online com provedor local bloqueado e falhas HTTP 403/204/500 verifica que vetores não aguardam rede e cache válido não é substituído. Contratos de precisão em DPI/rotação e agendamento JavaScript aprovados.
- Release compilado: zero erros, 191 avisos preexistentes. PerfTest: zero erros, três avisos preexistentes.

## Operação e limites

Reabrir o executável Release para usar a implementação. Sem configuração nova. Em pressão de memória, o cache continua podendo ser reduzido/desativado. Pan que muda a origem ainda reconstrói: reutilização entre origens/blocos fica para etapa posterior com testes numéricos próprios. Raster local/offline não recebeu novo pipeline nesta etapa.

Reproduzir: build Release com `GeoNexBuildNative=false`; executar PerfTest com `--render-path-cache-contracts`, `--online-worker-contracts`, `--render-precision-contracts`, `--production-vector-smoke <GeoNex.dll> <LOTES.shp>` e `--production-map-metrics <GeoNex.dll> <LOTES.shp>` (também `--delayed-basemap`). Executar `node PerfTest/MapEngineContracts.js`.

Rollback técnico: voltar os pontos de consulta/publicação precisos ao comportamento sem cache da etapa anterior e desativar o caminho `directRgb` em `OnlineRasterFrameReader`. Não reverter em bloco as alterações anteriores do usuário.
