# Buffer de pixels para prévias — 25/09/2026

O pan inteiro já copiava pixels sem reamostragem, mas alocava um bitmap de
aproximadamente 33 MiB a cada prévia HiDPI. `RasterPixelBuffer` mantém uma
alocação disponível para o próximo pan. A cópia e a limpeza das margens são
as mesmas de `CachedPreviewImage`; identidade, zoom fracionário, rotação e
formatos incompatíveis preservam seus caminhos anteriores.

O bitmap temporário que produz o `SKImage` instala os pixels com um callback
de liberação. Somente a última referência nativa devolve a alocação ao buffer.
O encoder, shaders e outros leitores não compartilham uma área mutável com o
próximo frame. `Stop` libera imediatamente a alocação ociosa; leitores ativos
mantêm seus pixels até terminar. Devolução duplicada é idempotente.

## CPU e RAM

- Nenhum worker adicional. Uma única alocação ociosa por servidor.
- Orçamento: `min(64 MiB, VectorRuntimeResources.Current.CacheBytes / 8)`.
- O orçamento inclui alocações emprestadas. Se não couber, usa o caminho
  anterior; não espera por um leitor nem sobrescreve seus pixels.
- Pressão de RAM remove a alocação ociosa na próxima manutenção de prévia;
  alocações ativas são liberadas ao devolver. Resize descarta o buffer incompatível.
- `GEONEX_PREVIEW_BUFFER=0` desativa o reaproveitamento para comparação/rollback.

## Medição no LOTES atual

77.146 feições, EPSG:3857, 1600 × 900 CSS, DPI 2, imagem 3712 × 2312.
Três processos por versão/escala, ordem alternada. Sete requests por processo:
frame final inicial, pans de 64/128 CSS, identidade, pans de 64/128, identidade.
As medianas abaixo incluem os quatro pans deslocados (12 amostras por célula),
inclusive a primeira alocação de cada processo. Não incluem os hits de identidade.

| Escala CSS | HTTP antes → depois | Cópia da prévia antes → depois |
|---|---:|---:|
| 4 | 39,705 → 38,225 ms | 8,006 → 7,098 ms |
| 16 | 35,515 → 31,990 ms | 7,999 → 4,997 ms |
| 64 | 32,135 → 28,940 ms | 8,108 → 5,031 ms |

Redução HTTP aproximada: 3,7%, 9,9% e 9,9%. É uma otimização de prévia durante
o arrasto, não uma aceleração do `DrawPath`, nem comprovação de FPS na WebView.
Os 63 frames novos foram idênticos às referências anteriores em todos os canais,
incluindo overscan (delta máximo 0; limite 1/255).
Logs: `%TEMP%/GeoNex-preview-buffer-{1,2,3}-{4,16,64}-{before,after}.log`.

## Contratos

- `--preview-pixel-buffer-contracts`: reutilização por endereço, leitores ativos,
  shader nativo, subconjuntos, RAM, resize, dimensões extremas, descarte duplicado
  e devoluções concorrentes com pressão de memória/encerramento.
- `--cached-preview-image-contracts`: todos os pixels premultiplicados, alpha
  0–255, formatos RGBA/BGRA, source mutável/imutável, padding, pans positivos e
  negativos, fallback fracionário/escala/rotação e cancelamento.
- `--high-zoom-preview-contracts`: HTTP, contenção de ambos os gates, cancelamento
  de frames antigos, zoom/DPI, troca e invalidação de cache, pixels exatos.

Release de GeoNex e PerfTest compilado sem erros nos dois workspaces (avisos
preexistentes). Os três contratos passaram novamente em `Desktop/GeoNex-main`.
Outros sete frames reais do zoom 64 no Desktop tiveram delta zero; log
`%TEMP%/GeoNex-preview-buffer-desktop-final.log` (70 frames novos comparados).
O contrato de raster online progressivo também passou. SHP e pintor paralelo
mantiveram seus hashes anteriores.

As experiências com máscara Alpha8 (inclusive expansão SIMD), shader constante
e preenchimento por faixas foram descartadas: sem ganho ou com delta acima de 1.
A seleção de algoritmos AA por formato de path/clip é visível no
[código do Skia](https://skia.googlesource.com/skia/+/4135cf0b57c2ef71b60ecf973d93eab37032f4f7/src/core/SkScan_AAAPath.cpp);
essa referência orientou a investigação, mas os critérios de adoção foram os
testes do binário SkiaSharp 3.119.2 instalado no projeto.
