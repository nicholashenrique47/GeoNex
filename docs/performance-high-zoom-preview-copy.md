# Reutilização de pintura em zoom alto — 24/09/2026

`CachedPreviewImage` reduz o trabalho do endpoint de prévia durante gestos.
O resultado final já pintado continua sujeito aos mesmos critérios de câmera,
revisão, DPI, dimensões, overscan e lifetime por lease. Não altera a geometria,
a pintura de polígonos nem o orçamento adaptativo de CPU/RAM.

Para uma imagem RGBA premultiplicada sem perfil de cor e matriz identidade,
`SKImage.FromBitmap` compartilha os pixels quando a bitmap é imutável. Para
translações inteiras, copia somente a interseção e zera as margens descobertas.
Não faz blending nem reamostragem de pixels já prontos. O custo temporário é
uma bitmap do mesmo tamanho da antiga superfície; a identidade dispensa esse
buffer. O resultado possui os pixels mesmo após o descarte da bitmap.

Pan fracionário, escala, rotação, perspectiva e outros formatos continuam no
mesmo caminho Skia anterior. A otimização está apenas em `TryServeCachedPreviewCore`;
não muda a autorização para reutilizar imagens finais ou prévias incompletas.

## Medição real

LOTES.shp atual: 77.146 feições, SHA-256
`4C202E2D32E9F948AC1D236DE5F97ACA755BAC51EFBF39970DA6A0ECE504C2FC`.
Mesma máquina, Release, sem rede/raster, 1600 × 900 CSS, DPI 2, com overscan
3712 × 2312. Três rodadas antes/depois em ordem alternada; sete requests por
processo, primeiro excluído. O estado anterior já inclui o snapshot compartilhado
documentado em `performance-high-zoom-snapshot.md`.

O benchmark aceita `GEONEX_BENCH_PAN_STEP`. Esta rodada usa 64 pixels CSS para
que o pan realmente desloque a câmera mesmo em zoom 64: a representação float
do centro mundial pode quantizar o antigo passo de 4 pixels para zero. Não
confundir tempo de identidade com tempo de imagem deslocada. Medianas HTTP:

| Escala CSS | Pan deslocado antes → depois (12 amostras/versão) | Redução | Identidade antes → depois (6 amostras/versão) |
|---|---:|---:|---:|
| 4 | 48,15 → 41,17 ms | 14,5% | 47,76 → 32,03 ms |
| 16 | 43,12 → 37,62 ms | 12,7% | 42,89 → 27,60 ms |
| 64 | 42,10 → 33,35 ms | 20,8% | 40,19 → 23,81 ms |

Os 63 frames novos têm delta máximo zero em todos os canais, incluindo
overscan. Logs: `%TEMP%/GeoNex-preview-ab-<rodada>-<escala>-<before|after>.log`.
Mais 21 frames de cobertura (DPI 1/zoom 4, DPI 4/zoom 16, DPI 2/zoom 0,25 com
pan fracionário) também têm delta máximo zero em toda a imagem. Logs:
`%TEMP%/GeoNex-preview-coverage-<dpi>-<escala>-<before|after>.log`.

O teste isolado de composição 3712 × 2312 deu aproximadamente 16,2 → 8,6 ms
para deslocamento (8,8), e 16,3 → 0,002 ms para identidade. Não são FPS de UI.
O span de telemetria `draw` da nova prévia inclui sua preparação/alocação;
o antigo span abrangia apenas `DrawBitmap`. Comparar HTTP completo entre versões,
não apresentar esses spans diferentes como aceleração da mesma operação.

## Contratos e alternativas descartadas

`--cached-preview-image-contracts` compara pixels premultiplicados com o caminho
antigo, incluindo alpha 0..255, RGBA/BGRA, bitmap mutável/imutável, stride com
padding, pan positivo/negativo, limites, frações, escala, rotação, cancelamento
e imagem sobrevivendo ao descarte da origem. Os contratos de prévia em zoom alto
e endpoint online progressivo verificam as leases e invalidações em produção.
Também passaram pintura paralela, recursos adaptativos, snapshot compartilhado
e cache de PNG. Builds Release do aplicativo e PerfTest passaram sem erros nos
dois workspaces (há avisos preexistentes). A cópia Desktop foi sincronizada por
hash e validada novamente: contratos de composição/prévia e sete frames reais
no zoom 64, todos com delta zero.

Os diagnósticos `--device-space-polygon-metrics` e `--polygon-blitter-metrics`
ficam exclusivamente em PerfTest/Experiments. Captura real de zoom 4, 1.538
pontos/257 contornos, 3712 × 2312, escala física 8, stroke local 0,25:

- Transformar o path antes de pintar: delta até 6/255 no stroke, mesmo corrigindo
  a largura do stroke para unidades físicas. A experiência anterior que não
  escalava a largura não era uma comparação válida.
- Pintura BGRA: delta 4/255; fill com Src em superfície transparente: delta 9/255.
- Divisão por clips em regiões disjuntas: delta 7/255 em linhas, 11/255 em colunas.

Todas excedem a tolerância 1/255 e foram rejeitadas. Nenhuma entrou no renderer.
O painter paralelo anterior e seus ganhos permanecem intactos. A otimização
entregue reduz a composição da prévia; não acelera o `DrawPath` final. A precisão
do centro da câmera em pans muito pequenos e o custo da codificação continuam
sendo pontos a investigar. Não há comparação controlada que comprove superar QGIS.

## Reprodução

```powershell
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --cached-preview-image-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --high-zoom-preview-contracts GeoNex/bin/Release/net10.0-windows10.0.19041.0/win-x64/GeoNex.dll
$env:GEONEX_BENCH_DPI = '2'
$env:GEONEX_BENCH_SCALE = '64'
$env:GEONEX_BENCH_FINAL_PAN = '0'
$env:GEONEX_BENCH_PAN_STEP = '64'
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --production-map-metrics GeoNex/bin/Release/net10.0-windows10.0.19041.0/win-x64/GeoNex.dll 'C:/Users/Windows 10/Desktop/SHP S/LOTEAMENTOS_LOTES/LOTES.shp' --high-zoom
```

Rollback isolado: restaurar somente o bloco de composição da prévia em
LocalMapServer e recompilar. Backup anterior desta unidade:
`%TEMP%/GeoNex-preview-copy-baseline/LocalMapServer.cs`. Não resetar o worktree.
