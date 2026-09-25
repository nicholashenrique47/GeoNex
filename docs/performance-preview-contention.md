# Prévia durante contenção — 25/09/2026

Quando `_previewGate` estava ocupado por uma prévia anterior, `Wait(0)` retornava
false e enviava a nova requisição para `_renderGate`. Uma pintura ou leitura
nativa demorada podia bloquear essa nova prévia, apesar de existir imagem válida
no cache. O contrato reproduziu timeout de 2 segundos na versão anterior,
mantendo o renderizador bloqueado mesmo após liberar o gate da prévia.

`TryServeCachedPreviewAsync` agora espera assincronamente no gate de prévia,
com o token da requisição. Requisições substituídas são canceladas durante a
espera. Ao obter o gate, consulta novamente a validade do cache. Pedidos finais,
impressão e formatos incompatíveis com a via rápida continuam fora dessa espera.
Mantém apenas uma prévia em execução, sem aumentar workers, bitmaps ou limites
de RAM. Não muda geometria, câmera, antialiasing ou pixels.

## Verificação

`--high-zoom-preview-contracts <GeoNex.dll>` mantém ambos os gates ocupados,
substitui uma requisição pendente (HTTP 204), libera somente o gate de prévia e
exige resposta válida com pixels exatos. Exercita DPI 1/2 e escalas CSS 4/64.
Nas execuções locais, a resposta e comparação completaram em 2–17 ms após liberar
o gate da prévia, ainda com o renderizador bloqueado. Isso mede recuperação sob
contenção controlada; não é benchmark de FPS nem tempo total de pintura.
Um pedido final também completa enquanto o gate da prévia está ocupado.

`--encoded-frame-cache-contracts` e `--online-progressive-production` passaram.
O benchmark HTTP do LOTES atual comparou 21 imagens de 3712 × 2312 pixels,
incluindo overscan, nos zooms 4/16/64, DPI 2 e pan de 64 CSS. Todos os canais
foram idênticos às referências anteriores (delta máximo 0, tolerância 1/255).
Logs: `%TEMP%/GeoNex-preview-queue-{4,16,64}.log`.

Contratos de polígonos paralelos também passaram. Aplicativo e PerfTest em
Release: zero erros nos dois workspaces, com avisos preexistentes. Fontes e
contrato sincronizados com `Desktop/GeoNex-main`, onde o contrato de contenção
foi executado novamente e passou. SHA-256 do SHP e do pintor paralelo inalterados.

Uma experiência separada com máscara Alpha8 de preenchimento foi descartada:
respeitou delta 1 nos três captures, mas aumentou o tempo total de pintura.
Nenhuma alteração experimental ficou no pintor ou no SHP.

Rollback isolado: trocar a espera assíncrona pelo antigo `Wait(0)` e remover
o `await` da chamada. Não reverter o cache PNG ou outras alterações do workspace.
