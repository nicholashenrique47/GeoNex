# P3-W — reuso do quadro já decodificado

Data: 14/09/2026. Complementa o [cache de PNG](performance-encoded-frame-cache.md); não implementa deltas espaciais.

## Correção após relato de regressão nos tiles

O transporte fetch/Blob agora é **experimental, desligado por padrão**. Quando a imagem muda, ele espera o PNG completo virar Blob antes de iniciar Image.decode; isso acrescenta uma barreira ausente no carregador Image direto. Restaurado o carregador nativo para não impor esse custo no refinamento dos tiles. Não foi comprovada causalidade/ganho em milissegundos no WebView real. Resolução física, PNG sem perdas, seleção de zoom e caches GDAL não mudam.

Para comparar explicitamente: `window.GEONEX_FRAME_REUSE = true`; para retornar: `false` ou recarregar a página. Cache de PNG do servidor permanece independente. Contratos JS cobrem padrão sem fetch/Blob, apresentação do quadro refinado na resolução física e modo experimental com identidade/fallback. A validação manual de latência até nitidez continua pendente.

Validação desta correção: `node PerfTest/MapEngineContracts.js` passou; build Release incremental 0 erros/3 avisos preexistentes; `git diff --check` passou. Skill GeoAI SWE orientou a reversão pontual e contratos de regressão, sem alteração dos dados.

## Protocolo e segurança de estado

- O cache gera um ID opaco de 32 caracteres para cada payload retido. Hits mantêm o ID; mudança de qualquer pixel/metadado, troca ou descarte gera outro. A identidade continua baseada no SHA-256 integral, não na câmera ou em amostras.
- Resposta normal: 200 + PNG + `X-GeoNex-Payload-Id`. Cliente anuncia somente uma imagem já apresentada, ainda retida, do mesmo endpoint, com `?reuse=ID`.
- Após renderizar/verificar o quadro atual, o servidor pode retornar **204 sem corpo**, com `X-GeoNex-Reused: 1` e ID coincidente. Não é HTTP 304 nem cache HTTP persistente. 204 de cancelamento, sem marcador, nunca autoriza reuso.
- ID desconhecido, cache desativado/expulso, preview, impressão ou conteúdo diferente: imagem completa normal. O ID não permite consultar outro quadro nem pula a validação dos pixels atuais.
- Cliente usa `fetch` sem cache/credenciais; headers expostos por CORS no localhost. Decodifica o Blob uma vez e libera sua URL temporária. Se o ID divergir ou a base desaparecer, solicita o quadro completo uma única vez.
- Reuso executa o commit normal de câmera/DPI/layout e o swap A/B, usando a mesma imagem decodificada. Não reaproveita transformação antiga nem copia o canvas visível com overlays. Guards de epoch/frame/request permanecem.
- Fetch anterior é abortado ao chegar um novo; reset elimina a referência e aborta o request. Resposta/decodificação obsoleta não repopula o cache.
- Até uma imagem apresentada retida e 16 MiB de pixels; imagens maiores são exibidas sem retenção. Buffers de PNG, imagens em processamento e memória interna do navegador não estão incluídos nesse teto.
- Preview (`i=1`) e impressão (`c=1`) conservam `Image.src`. Host sem fetch/AbortController ou com TypeError de CORS/rede volta ao caminho anterior; falha de transporte fica memorizada por endpoint até reset, evitando duplicar requests continuamente.

## Resultado e limites

DLL real com LOTES: identidade válida resultou em 204 e **zero bytes de corpo**; identidade desconhecida retornou 200 com PNG. Contratos JS confirmaram que reuso não cria outra Image/decode e ainda atualiza a câmera. Isso elimina o corpo PNG e o decode **somente em hits**; permanecem HTTP/headers, renderização/verificação SHA-256 e composição no canvas. Não é promessa de FPS nem redução de trabalho para quadros diferentes.

PNG original e tolerâncias de composição P1-C não foram alterados. Contadores existentes registram corpo zero em reuso; telemetria JS registra decode zero. Não houve medição manual de rede/GPU no WebView real nesta etapa.

## Verificação

- `--encoded-frame-cache-contracts`: ID estável em hit e diferente após mudança de pixel; leases, metadados e RAM continuam válidos.
- `--production-map-metrics ... --polygon-images`: DLL real, 204 vazio identificado, fallback 200, igualdade de pixels, impressão/rotação/preview.
- `node PerfTest/MapEngineContracts.js`: full→reuse sem novo decode, commit de câmera, ID divergente, troca de servidor, reset durante decode, CORS/fallback persistente, imagem acima do teto, além das regressões A/B e stale frames.
- `--delayed-basemap`: ocultação/reexibição, refinamento e preservação da imagem válida em 403/204/500 passaram.
- Build Release: 0 erros, 191 avisos preexistentes; PerfTest 0 erros/3 avisos GDAL gerados. Dependências com avisos anteriores não foram atualizadas.

A GeoAI Skill SWE/DevOps orientou o fallback, validação de identidade, limites e testes na DLL real. Reversão do transporte no cliente: `window.GEONEX_FRAME_REUSE = false` (até recarregar a página); servidor: `GEONEX_ENCODED_FRAME_CACHE=0`. Clientes antigos sem `reuse` continuam recebendo PNG completo. Sem migração de projeto ou modificação do GeoJSON original.

Próximo recorte: medir hits/misses no WebView; só depois introduzir deltas de regiões com identidade explícita de frame base, teste de alpha/bordas/DPI e fallback completo. Não substituir este protocolo por simples comparação de câmera/revisão.
