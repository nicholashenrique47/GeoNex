# Publicação do frame em zoom alto — 24/09/2026

## Correção do diagnóstico

A escala 0,25 usada na rodada anterior é uma visão ampla, apesar do nome
`--high-zoom` do comando. Esta rodada mede explicitamente as escalas 4, 16 e 64
px por unidade projetada, DPI 2, na mesma câmera e fonte. O LOTES.shp disponível
hoje contém 77.146 feições (16.065.048 bytes), e não os 4,9 milhões de feições de
capturas antigas. Não combinar esses resultados como se fossem o mesmo arquivo.

Fonte somente lida: `C:/Users/Windows 10/Desktop/SHP S/LOTEAMENTOS_LOTES/LOTES.shp`.
SHA-256: `4C202E2D32E9F948AC1D236DE5F97ACA755BAC51EFBF39970DA6A0ECE504C2FC`.
Nos três níveis, os paths reais têm respectivamente 1.538, 149 e 25 pontos.
O bypass de cache para paths pequenos já existia. Alterar esse limite ou repetir
os experimentos de particionamento não trata o custo dominante desses frames.

Os experimentos rejeitados anteriormente (bitmaps recortados, oito bandas,
transformação prévia e histogramas ponderados) não estão no painter. Seu hash
permanece `5AB7DCE688B096AFC9DEA7DAA61BE6CE0E0DC18ED8702FE21076B5ACFD890A15`.

## Alteração mantida

Após pintar, `LocalMapServer` copiava o snapshot inteiro com
`SKBitmap.FromImage` para publicar o cache de navegação. Em 3712 × 2312,
isso copiava e convertia 34.328.576 bytes por frame. O
[contrato da API](https://learn.microsoft.com/en-us/dotnet/api/skiasharp.skbitmap.fromimage?view=skiasharp-3.119)
confirma que `FromImage` copia os pixels.

`RasterFrameSnapshot` conserva a imagem imutável e cria uma bitmap que compartilha
seus pixels com `InstallPixels`. O callback do pixel-ref nativo mantém uma lease
da imagem até o último leitor ser liberado. O request também mantém sua própria
posse: invalidar o cache durante a codificação não libera os pixels em uso.
Superfícies sem pixels acessíveis continuam usando a cópia anterior.

As geometrias, matrizes, cores, antialiasing, limites de CPU/RAM e critérios de
validade do cache não mudam. A imagem retida ocupa o mesmo volume de pixels;
desaparece a alocação extra temporária para a cópia. O bitmap compartilhado é
imutável. A superfície pode ser descartada ou modificada (copy-on-write do Skia)
sem alterar a imagem publicada. Falhas na publicação liberam a bitmap local.

## Medição A/B

DLLs Release reais antes/depois, sem rede/raster, 1600 × 900 CSS, DPI 2, com
overscan (3712 × 2312 pixels). Três rodadas, ordem antes/depois alternada, sete
frames por processo. Medianas abaixo excluem o primeiro frame: 18 amostras por
versão e escala. Pequenos pans finais forçam pintura completa; nenhum resultado
é apresentado como FPS do WebView nem como comparação controlada com QGIS.

| Escala CSS | Render antes → depois | HTTP antes → depois |
|---|---:|---:|
| 4 | 38,42 → 31,52 ms | 86,54 → 79,63 ms |
| 16 | 25,96 → 17,94 ms | 68,90 → 60,76 ms |
| 64 | 26,74 → 18,77 ms | 45,07 → 36,71 ms |

Redução HTTP: aproximadamente 8%, 12% e 19%. O tempo de `DrawPath` permanece
essencialmente igual; o ganho vem da publicação do resultado pintado. No teste
isolado com a mesma dimensão, mediana de seis amostras: cópia 8,00 ms,
compartilhamento 0,007 ms. Não usar esse fator isolado como aceleração do frame.

Todos os 63 frames da versão nova nas rodadas A/B têm delta máximo zero em
todos os canais, incluindo overscan. Outros 21 frames cobrem visão ampla
(escala 0,25/DPI 2), escala 4/DPI 1 e escala 64/DPI 4, também sem diferenças.
Logs locais: `%TEMP%/GeoNex-zoom-ab-<rodada>-<escala>-<before|after>.log` e
`%TEMP%/GeoNex-zoom-coverage-<dpi>-<escala>-<before|after>.log`.

## Verificação e reprodução

`RasterFrameSnapshotContracts` confere pixels compartilhados, RGBA/BGRA,
alpha premultiplicado/opaco, composição, snapshot independente de mutação da
superfície, descarte antecipado, leitores nativos, aposentadoria concorrente e
liberação da imagem após o último leitor. Não afirma ausência de falhas em
cenários não testados.

Também passaram os contratos de pintura paralela, cache de polígonos,
geometria/projeção, recursos adaptativos, PNG em cache, prévia de zoom alto,
endpoint online progressivo e atualização de raster em produção.

```powershell
dotnet build GeoNex/GeoNex.csproj -c Release --no-restore -p:GeoNexBuildNative=false -clp:ErrorsOnly
dotnet build PerfTest/PerfTest.csproj -c Release --no-restore -p:GeoNexBuildNative=false -clp:ErrorsOnly
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --raster-frame-snapshot-contracts
$env:GEONEX_BENCH_DPI = '2'
$env:GEONEX_BENCH_SCALE = '64'
$env:GEONEX_BENCH_FINAL_PAN = '1'
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --production-map-metrics GeoNex/bin/Release/net10.0-windows10.0.19041.0/win-x64/GeoNex.dll 'C:/Users/Windows 10/Desktop/SHP S/LOTEAMENTOS_LOTES/LOTES.shp' --high-zoom
```

Rollback isolado: voltar apenas o bloco de snapshot/publicação de
`LocalMapServer` para `surface.Snapshot()`/`SKBitmap.FromImage(image)` e
recompilar. O backup anterior dessa unidade está em
`%TEMP%/GeoNex-zoom-fix-20260924-075320`. Não resetar o worktree.
