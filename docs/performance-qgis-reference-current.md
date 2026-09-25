# Referência QGIS e hipóteses rejeitadas — 24/09/2026

O script `PerfTest/QgisRenderMetrics.py` agora aceita dimensões, escala física,
quantidade de amostras, largura da borda, cores nominais do GeoNex e desativação
explícita da simplificação. Os defaults antigos foram preservados. A comparação
`--compare-renderer-frames` mede diferenças RGBA premultiplicadas nas imagens
completas, sem presumir equivalência dos dois renderers.

## Fonte e método

LOTES.shp original, 77.146 feições, 16.065.048 bytes, SHA-256
`4C202E2D32E9F948AC1D236DE5F97ACA755BAC51EFBF39970DA6A0ECE504C2FC`.
Nenhuma alteração de geometria, atributos ou índices na fonte. Não comparar
estes números diretamente com os registros antigos de 4,9 milhões de feições.

QGIS instalado 4.2.0, imagem transparente 3712 × 2312, centro EPSG:3857
(-5408820,5; -2984029), fill (56,189,248,89), borda (14,165,233,89) com 2 pixels
e junções redondas, sem labels ou raster. Centro e escala efetivos do GeoNex
confirmados no novo log `HIGH_ZOOM frame_center_x=... frame_center_y=...`.
Escalas físicas 8/32/128 correspondem a CSS 4/16/64 no DPI 2 do GeoNex.

QGIS: sete jobs sequenciais sem cache de imagem entre jobs, mediana dos seis
últimos; gravação PNG fora do cronômetro. GeoNex: mediana de 18 renders finais,
três processos, primeiro frame excluído, pequenos pans com invalidação forçam
pintura; tempos excluem PNG/HTTP. Os protocolos de aquecimento não são idênticos.

| Escala CSS | GeoNex render | QGIS padrão | QGIS sem simplificação |
|---|---:|---:|---:|
| 4 | 30,43 ms | 116,06 ms | 117,86 ms |
| 16 | 18,63 ms | 75,71 ms | 72,96 ms |
| 64 | 18,82 ms | 67,59 ms | 64,53 ms |

Essa referência indica menor tempo de render do GeoNex nesses três casos.
Não comprova superioridade de UI, FPS, todos os SHPs ou qualidade equivalente.
QGIS pinta símbolos por feição; GeoNex agrega paths. Sobreposições, bordas e
antialiasing têm semânticas diferentes, mesmo com cores nominais iguais.

Comparação com QGIS sem simplificação, 8.582.144 pixels por frame:

| Escala CSS | Pixels diferentes | Pixels com diferença >1 | Delta máximo (também no alpha) |
|---|---:|---:|---:|
| 4 | 253.411 | 212.693 | 82 |
| 16 | 66.790 | 54.582 | 77 |
| 64 | 15.693 | 15.230 | 40 |

Não usar o QGIS como referência byte a byte do contrato de regressão 1/255
do GeoNex. Uma linha central no zoom 64 apresentou 3.705/3.712 pixels da mesma
cor plana nos dois programas; as diferenças restantes nessa linha eram bordas.
Isso não prova que todas as diferenças dos frames estejam apenas nas bordas.

Logs: `%TEMP%/GeoNex-qgis-current-<escala>.log` (sem simplificação),
`%TEMP%/GeoNex-qgis-default-<escala>.log`,
`%TEMP%/GeoNex-surface-ab-<rodada>-<escala>-before.log`.
Cada log QGIS contém a pasta temporária de PNGs, tamanho, extensão e versão.

## Hipóteses rejeitadas em produção

1. Reduzir o limiar do painter de 8.192 para 512 vértices: captura real do zoom
   4 (1.538 pontos) produziu delta 17/255. Limiar original restaurado antes de
   recompilar o aplicativo. Log `%TEMP%/GeoNex-low-vertex-probe.log`.
2. Remover a limpeza adicional de superfícies raster: o
   [contrato Skia](https://api.skia.org/namespaceSkSurfaces.html) garante pixels
   inicializados com zero na superfície alocada, e o
   [binding 3.119.2](https://github.com/mono/SkiaSharp/blob/v3.119.2/binding/SkiaSharp/SKSurface.cs)
   usa esse construtor. O diagnóstico `--raster-surface-metrics` ganhou
   1,4–2,9 ms isoladamente, com pixels idênticos. Porém, três rodadas em produção
   pioraram HTTP no zoom 4 de 78,99 para 81,03 ms; no zoom 16 ficaram em
   62,04 → 62,43 ms; apenas zoom 64 ganhou (37,17 → 34,81 ms). As duas remoções
   de Clear foram desfeitas. Não selecionar por zoom para favorecer essa amostra.

Painter restaurado: SHA-256
`5AB7DCE688B096AFC9DEA7DAA61BE6CE0E0DC18ED8702FE21076B5ACFD890A15`.
O aplicativo foi recompilado após restaurar os arquivos, evitando DLL incremental
com experiência antiga. Sete frames do zoom 4 ficaram idênticos à referência.

## Limitação confirmada da câmera

No zoom 64, pans CSS de 0, 4 e 8 produziram frames finais idênticos. A diferença
de centro é perdida em `SKPoint` mundial antes da matriz centrada: a .5 m por
ULP e escala 64, pequenos deslocamentos não sobrevivem ao float. `Rebase` da
prévia tem o mesmo problema. Corrigir somente a prévia criaria salto ao receber
o frame final. A solução deve preservar centro/resíduo em double no contrato
compartilhado por pintura, raster, cache e ferramentas de coordenadas; essa
mudança ainda não foi implementada nesta rodada.

## Reprodução QGIS

```powershell
$env:QT_QPA_PLATFORM = 'offscreen'
& 'C:/Program Files/QGIS 4.2.0/bin/python-qgis.bat' 'PerfTest/QgisRenderMetrics.py' 'C:/Users/Windows 10/Desktop/SHP S/LOTEAMENTOS_LOTES/LOTES.shp' --center-x -5408820.5 --center-y -2984029 --width 3712 --height 2312 --scale 128 --samples 7 --stroke-width 2 --style geonex --no-simplification
```
