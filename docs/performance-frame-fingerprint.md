# Validação de pixels do cache PNG — 25/09/2026

O cache PNG continua verificando todos os bytes visíveis, além de dimensões,
formato de cor, alpha e compressão. Para imagens a partir de 8 MiB, agrupa linhas
em aproximadamente 1 MiB, calcula SHA-256 de cada grupo e calcula SHA-256 da
concatenação ordenada desses digests. Não usa amostragem, câmera ou ponteiro
como identidade. Este digest composto é diferente do SHA-256 linear anterior;
é uma chave interna efêmera, sem mudança no PNG ou no protocolo público.

Os grupos dependem somente da largura/altura lógica: mudar o número de workers
ou o padding das linhas não altera a chave. Usa no máximo quatro workers,
limitados por `VectorRuntimeResources.Current.Workers`; com um worker,
executa os mesmos grupos sequencialmente.
Imagens menores preservam o hash linear. Limites de RAM do cache e de 128 MiB
para fingerprint permanecem. Não copia nem retém outra imagem; a matriz de
digests do frame de teste ocupa aproximadamente 1 KiB.

O token é verificado antes de cada grupo, durante linhas com padding e depois
da execução paralela. Nenhum digest parcial publica um payload. A proteção que
impede prévias de esperar pelo lock de outro encoder permanece ativa.

## Medições

Captura 3712 × 2312: hash anterior 15,752 ms; agrupado com 1/2/4 workers:
15,127 / 7,856 / 4,259 ms. Nove rodadas alternadas, primeira excluída.

A/B HTTP no LOTES atual (77.146 feições): 1600 × 900 CSS, DPI 2, pan de 64 CSS,
`GEONEX_BENCH_FINAL_PAN=1`. Três processos por versão/escala, ordem alternada,
sete frames por processo e primeiro excluído (18 amostras por célula).
As otimizações de buffers já estão ativas em ambos os lados da comparação.

| Escala CSS | HTTP antes → depois | Validação + PNG antes → depois |
|---|---:|---:|
| 4 | 73,955 → 62,625 ms | 45,306 → 34,241 ms |
| 16 | 56,270 → 44,685 ms | 41,355 → 30,029 ms |
| 64 | 52,990 → 42,350 ms | 38,077 → 26,818 ms |

Redução HTTP: aproximadamente 15,3%, 20,6% e 20,1%. Não é redução do `DrawPath`
nem medição de FPS. Os 63 frames novos tiveram delta zero em todos os canais,
incluindo overscan. Logs: `%TEMP%/GeoNex-frame-hash-{rodada}-{escala}-{before,after}.log`.

Em outro A/B no zoom 64, com prévias (`GEONEX_BENCH_FINAL_PAN=0`), as respostas
de identidade (samples 3/6, seis amostras por versão) caíram de 16,785 para
6,010 ms. Pans deslocados continuam evitando a consulta ao cache PNG. Os 21
frames novos dessa série também foram idênticos às referências. Logs:
`%TEMP%/GeoNex-preview-hash-{rodada}-{before,after}.log`.

## Verificação

- `--frame-pixel-fingerprint-contracts`: padding, 1/2/4 workers, todos os canais
  nos extremos de cada grupo e na cauda, ordem dos grupos, limiar de tamanho,
  imagens pequenas e cancelamento.
- `--encoded-frame-cache-contracts`: cache real acima do limiar, imagens
  independentes iguais, último pixel alterado, PNG idêntico ao encoder direto,
  metadados, limites, leases, concorrência e encerramento.
- `--high-zoom-preview-contracts` e `--vector-resource-contracts` passaram.
- Outros 14 frames finais reais passaram com `GEONEX_INDEX_WORKERS=1` e `2`,
  delta zero; logs `%TEMP%/GeoNex-frame-hash-workers-{1,2}.log`.
- SHP e pintor paralelo mantiveram seus hashes anteriores.

Release de GeoNex e PerfTest compilado nos dois workspaces com zero erros
(avisos preexistentes). Contratos de fingerprint, cache e HTTP repetidos com
sucesso no Desktop. Mais sete frames finais reais no zoom 64 tiveram delta
zero: `%TEMP%/GeoNex-frame-hash-desktop-final.log`. Ao todo, 105 frames novos
comparados nesta unidade (63 finais A/B, 21 prévias, 14 com limites de CPU e
sete no Desktop).

Rollback/A-B: `GEONEX_PARALLEL_FRAME_HASH=0` seleciona o hash linear anterior.
Para reproduzir a medição isolada:
`--frame-pixel-hash-metrics <PNG-capturado>`.
