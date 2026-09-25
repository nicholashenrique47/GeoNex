# Buffer da imagem final — 25/09/2026

O custo de alocar e liberar uma superfície HiDPI era relevante mesmo com poucos
polígonos visíveis. `RasterRenderTarget` permite pintar uma vez em pixels
reutilizáveis e terminar o frame com uma imagem imutável. `Finish()` encerra
a superfície gravável antes de transferir os pixels; um segundo `Finish()` ou
acesso a `Surface` é rejeitado. O fundo continua sendo limpo integralmente.

O `RasterFrameSnapshot` recebe essa imagem e conserva o protocolo de leases do
cache global. O próximo frame não reutiliza pixels retidos por cache, encoder
ou shader. Cancelamento antes de `Finish` devolve a alocação; cancelamento após
a publicação mantém as regras existentes de descarte de respostas obsoletas.

O buffer antes chamado `PreviewPixelBuffer` foi generalizado para
`RasterPixelBuffer`. O servidor tem instâncias separadas: a prévia mantém seu
limite anterior de 64 MiB; o frame final usa até
`min(128 MiB, VectorRuntimeResources.Current.CacheBytes / 4)`, incluindo imagens
ainda emprestadas ao cache. Cada instância retém no máximo uma alocação ociosa.
Não acrescenta workers. Sem orçamento, usa a superfície Skia original.
Impressão e requests sem `nav=1` não usam o buffer final.

## A/B na renderização completa

LOTES atual: 77.146 feições, EPSG:3857, 1600 × 900 CSS, DPI 2, 3712 × 2312 pixels.
Três processos por versão/escala, ordem alternada, sete frames por processo,
primeiro excluído das medianas (18 amostras por célula).
`GEONEX_BENCH_FINAL_PAN=1` invalida o cache de imagem final a cada frame;
`GEONEX_BENCH_PAN_STEP=64` provoca deslocamentos reais. Todos os requests são
finais, com nova pintura; estas medidas não são hits de prévia.

| Escala CSS | Render antes → depois | HTTP antes → depois |
|---|---:|---:|
| 4 | 30,365 → 25,110 ms | 77,500 → 72,285 ms |
| 16 | 18,410 → 12,305 ms | 61,270 → 55,345 ms |
| 64 | 19,226 → 13,312 ms | 59,975 → 54,960 ms |

Redução do estágio de renderização: 17,3%, 33,2% e 30,8%. Redução HTTP:
6,7%, 9,7% e 8,4%. O custo isolado de `DrawPath` praticamente não mudou;
o ganho vem do gerenciamento dos pixels ao preparar e publicar a imagem.
PNG e transporte continuam incluídos somente na medida HTTP. Não são números
de FPS da WebView nem uma comparação global com o QGIS.

Os 63 frames novos foram idênticos às referências em todos os canais,
incluindo overscan (delta máximo 0, limite 1/255). Artefatos locais:
`%TEMP%/GeoNex-frame-buffer-{1,2,3}-{4,16,64}-{before,after}.log` e diretórios
com os mesmos nomes, contendo PNGs e geometria capturada.

## Verificação e rollback

- `--raster-render-target-contracts`: 32 frames exatos, formatos/alpha/espaço
  de cor, encerramento da pintura, cache e leitores nativos, cancelamento e RAM.
- `--raster-frame-snapshot-contracts`: publicação sem cópia, COW, leases e descarte.
- `--raster-pixel-buffer-contracts` (alias anterior preservado): alocações,
  retenção por shader, limites, pressão, resize e encerramento concorrente.
- Contratos de prévia, raster online progressivo e refresh de raster passaram.
- SHP e pintor paralelo mantiveram os hashes anteriores.

Release de GeoNex e PerfTest compilado nos dois workspaces com zero erros e
somente os avisos preexistentes. Os contratos de target, snapshot, buffer e
prévia passaram novamente na cópia Desktop. Mais sete frames finais reais do
zoom 64 tiveram delta zero no Desktop (70 frames novos comparados ao todo),
registrados em `%TEMP%/GeoNex-final-frame-buffer-desktop.log`.
O contrato de pintura paralela também passou.

`GEONEX_FRAME_BUFFER=0` desativa apenas este reaproveitamento. As otimizações
anteriores de pintura, snapshot e prévia continuam ativas. Não usar reset/clean
do worktree para reverter esta unidade.
