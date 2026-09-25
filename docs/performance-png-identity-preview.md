# PNG de prévia sem deslocamento — 24/09/2026

O endpoint rápido de gestos já reutilizava a pintura, mas codificava novamente
o mesmo PNG mesmo quando a matriz era exatamente identidade. Agora consulta
o `EncodedFrameCache` existente nesse caso. A igualdade continua sendo validada
com SHA-256 de todos os pixels e metadados; câmera, amostras ou ponteiros não
substituem essa verificação. Pan e zoom com transformação continuam no caminho
anterior, evitando acrescentar hashing a cada imagem nova de um gesto.

`TryEncodePreview` usa `Monitor.TryEnter`: se o encoder estiver ocupado com
outro frame, retorna imediatamente para a codificação independente anterior.
Não amplia os limites de RAM, o número de entradas ou as condições para usar
uma prévia como imagem final. PNG permanece lossless. Cancelamento, leases e
descarte continuam sob os contratos existentes.

## A/B no LOTES atual

77.146 feições, mesma câmera EPSG:3857, 1600 × 900 CSS, DPI 2, imagem
3712 × 2312. Três processos por versão/escala, ordem alternada, sete frames,
primeiro excluído. Passo de pan 64 CSS: samples 1/2/4/5 deslocam de fato a câmera;
samples 3/6 retornam à identidade. Seis amostras de identidade por versão/escala.

| Escala CSS | HTTP da identidade antes → depois | Redução |
|---|---:|---:|
| 4 | 32,30 → 17,78 ms | 45,0% |
| 16 | 28,06 → 17,37 ms | 38,1% |
| 64 | 23,87 → 16,69 ms | 30,1% |

Não apresentar esses ganhos como aceleração do pan ou do `DrawPath`. As medianas
dos pans deslocados foram 40,96 → 39,05; 36,64 → 37,62; 32,98 → 32,65 ms,
respectivamente; esse caminho não usa a nova consulta ao cache.

Os 63 frames novos tiveram delta máximo zero, incluindo overscan. Após a
proteção contra contenção, outros sete frames no zoom 64 também tiveram delta
zero, com requests de identidade em 17,44 e 16,69 ms. Logs locais:
`%TEMP%/GeoNex-png-ab-<rodada>-<escala>-<before|after>.log` e
`%TEMP%/GeoNex-png-final.log`.

## Verificação

- `--encoded-frame-cache-contracts`: igualdade de todos os pixels, alteração
  no último pixel, alpha/cores, RAM, cancelamento, concorrência, shutdown e
  retorno imediato quando outro encoder mantém o lock.
- `--high-zoom-preview-contracts <GeoNex.dll>`: hit de PNG em identidade,
  bypass em pan, imagem substituída na mesma câmera/revisão sem servir PNG
  antigo, precisão existente, DPI 1/2, zoom 4/64, manutenção e invalidações.
- `--online-progressive-production <GeoNex.dll>`: prévia responde com renderer
  ocupado; raster parcial não vira cache final e os limites de conexões permanecem.

Release do aplicativo e PerfTest compilados nos dois workspaces sem erros
(avisos preexistentes). Contratos de PNG/prévia repetidos na cópia Desktop.
Mais sete frames reais no zoom 64, com `GEONEX_ENCODED_FRAME_CACHE=0`, tiveram
delta zero em todos os canais; log `%TEMP%/GeoNex-png-desktop-disabled.log`.
O SHP manteve o SHA-256 registrado em `performance-qgis-reference-current.md`.

```powershell
$env:GEONEX_BENCH_DPI = '2'
$env:GEONEX_BENCH_SCALE = '64'
$env:GEONEX_BENCH_FINAL_PAN = '0'
$env:GEONEX_BENCH_PAN_STEP = '64'
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --production-map-metrics GeoNex/bin/Release/net10.0-windows10.0.19041.0/win-x64/GeoNex.dll 'C:/Users/Windows 10/Desktop/SHP S/LOTEAMENTOS_LOTES/LOTES.shp' --high-zoom
```

Rollback isolado: remover a habilitação `cacheIdentityPreview` na chamada do
endpoint de prévia. Não reverter as outras otimizações nem limpar o worktree.
