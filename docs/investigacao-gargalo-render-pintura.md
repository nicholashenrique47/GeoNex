# Investigação do gargalo de renderização

Data: 09/11/2026. Cenário da captura: `LOTES.shp` sobre `Guaratuba Ortho.ecw`.
O HUD da captura indicava 2618,6 ms.

## Conclusão

O custo dominante é a leitura do ECW na unidade de rede `X:`, não a pintura dos
polígonos. No teste aquecido com as duas camadas, GeoNex levou 236 ms p50 e
245 ms p95 do pedido HTTP completo, incluindo PNG e resposta. O QGIS levou
552 ms p50 e 730 ms p95 para renderizar a imagem em memória. Nesse quadro e
nessa configuração, GeoNex foi 2,34 vezes mais rápido no p50, apesar de também
codificar e transmitir o PNG.

O valor de 2618,6 ms da captura não tem um `Server-Timing` correspondente;
portanto não é possível atribuí-lo retrospectivamente a uma única etapa. A
leitura fria do compartilhamento variou de cerca de 5 a 6,6 segundos nas
rodadas iniciais, enquanto os acessos aquecidos variaram de 47 a 500 ms. Essa
variação da rede pode produzir latências altas sem que a pintura vetorial seja
o gargalo.

## Reprodução mista GeoNex × QGIS

Os dois medidores renderizaram a mesma ortofoto ECW de 17,1 GB e os mesmos
77.146 polígonos de `LOTES.shp`. A simbologia foi alinhada: preenchimento
transparente e contorno ciano fino. O enquadramento foi zoom 64, foco 50%/25%,
viewport CSS 1600×900, DPI 2, imagem de 3712×2312 pixels e seis pans aquecidos
de 32 pixels CSS. QGIS usou bilinear. O medidor GeoNex exercitou o servidor de
produção por HTTP.

Resultados de seis quadros aquecidos, excluindo o primeiro quadro de cada
processo:

- **QGIS 4.2.0 / GDAL 3.13.1:** render p50 552,3 ms; p95 730,1 ms.
- **GeoNex / GDAL 3.12.1:** HTTP p50 236,3 ms; p95 244,9 ms.
- **GeoNex, pan interativo com pré-visualização já semeada:** HTTP p50 71,8 ms;
  p95 82,7 ms; `io=0` em todos os seis quadros.

O número do QGIS termina após desenhar a imagem em memória. O número HTTP do
GeoNex também inclui encode PNG e envio do payload, então essa comparação não
favorece GeoNex por excluir essas etapas. A pré-visualização interativa do
GeoNex usa limite de DPI 1,25; esse número mede resposta ao gesto e não é uma
comparação de qualidade final na mesma resolução.

No render final aquecido do GeoNex, as medianas das etapas foram:

- leitura ECW (`io`): 52,8 ms;
- pintura (`draw`): 55,5 ms;
- geometria: abaixo de 1 ms;
- encode PNG: 42,0 ms;
- escrita HTTP: 56,7 ms.

Isso aponta a leitura remota como a principal fonte de variação. Em outro lote
de amostras, `io` chegou a 499 ms, enquanto `draw` permaneceu perto de 60 ms.

## Experimentos de pintura e formato

O compositor paralelo de `RasterFramePainter` já está ativo. No mesmo raster e
na mesma saída 3712×2312, a pintura paralela mediu aproximadamente 42 ms p50;
forçar a pintura serial com `GEONEX_RASTER_PARALLEL_COMPOSITE=0` elevou a
mediana para aproximadamente 324 ms. A pintura paralela reduz esse estágio em
cerca de 7,7 vezes, mantendo a imagem e o filtro atuais.

WebP reduziu o payload de aproximadamente 25,8 MB para 5,2 MB, mas a codificação
subiu de cerca de 40–48 ms para 1,1–2,2 segundos. Para esse ECW, PNG continua
sendo mais rápido no tempo total.

## Limites e próximo alvo

O benchmark mostra vantagem aquecida do GeoNex nesse cenário específico e
resposta interativa rápida com cache. Não prova superioridade em todos os
projetos, fontes locais, resoluções, estilos ou máquinas. A captura original
continua sem a decomposição do próprio quadro.

O primeiro acesso ao ECW na rede é o próximo alvo: medir e reduzir a latência
de leitura do compartilhamento ou permitir uma cópia/cache local controlada.
Copiar os 17,1 GB automaticamente não foi adotado, pois consumiria espaço local
considerável. As medições também confirmam que reduzir a pintura vetorial não
atacaria a parcela dominante do quadro frio.

## Medidores

- GeoNex misto: `PerfTest --production-mixed-metrics <GeoNex.dll> <ECW> <LOTES.shp>`.
- QGIS misto: `QgisRasterMetrics.py <ECW> --vector <LOTES.shp> --zooms 64 --focus-x 0.5 --focus-y 0.25 --dpi 2 --visible-width 1600 --visible-height 900 --samples 7 --resampling bilinear`.

## Reutilização do raster em pan assentado — 10/11/2026

O cache raster por camada já guardava a janela ECW do quadro anterior, mas o
render final só a usava quando a câmera era idêntica. Agora, no mesmo zoom e DPI,
sem rotação, o GeoNex reaproveita essa janela se o pan for um deslocamento inteiro
em pixels físicos e o quadro visível continuar coberto pelo cache. A composição
usa faixas paralelas e a qualidade de pintura alta. Zoom, movimento fracionário,
rotação, cache interativo de baixa resolução e pan fora da área coberta continuam
no render normal do GDAL. Defina `GEONEX_SETTLED_RASTER_PIXEL_PAN=0` para desligar
o caminho.

No mesmo cenário misto, com `LOTES.shp`, a ortofoto ECW, viewport 1600×900 CSS,
DPI 2, zoom 64 e pans de 32 CSS px:

- **GeoNex sem reutilização:** HTTP p50 258,0 ms; p95 706,8 ms.
- **GeoNex com reutilização automática:** HTTP p50 206,2 ms; p95 326,0 ms.
- **QGIS 4.2.0 / GDAL 3.13.1:** render em memória p50 224,1 ms; p95 238,0 ms.

GeoNex ficou 8% mais rápido no p50 que o QGIS mesmo incluindo encode PNG e
resposta HTTP. O p95 ainda foi maior: um pan que ultrapassou o overscan de 128 CSS
px corretamente caiu de volta no GDAL, e o acesso à rede oscilou. Em seis pans
aquecidos, cinco puderam reutilizar o quadro sem leitura (`io=0`). O benchmark
de GeoNex inclui PNG e HTTP; o de QGIS termina no render em memória.

A apresentação reutilizada manteve o mesmo enquadramento visual, mas não é
idêntica byte a byte ao render novo: a reamostragem raster difere ligeiramente.
Na área visível do quadro de referência, o erro RGB médio foi 4,9/255, a mediana
1/255 e o percentil 95 foi 29/255; o canal alpha não mudou. O PNG cresceu de
25,8 MB para 34,4 MB (+33%), então esse ganho depende de a latência da leitura
ECW evitada superar o custo adicional de codificar e enviar a imagem. A comparação
visual em 3712×2312 manteve as parcelas alinhadas; o deslocamento residual medido
por correlação ficou abaixo de 0,23 pixel físico.

O primeiro acesso frio ao compartilhamento ainda pode levar 5–6 segundos. O
cache de pan evita leituras repetidas em movimentos cobertos; ele não elimina a
latência inicial do UNC. Reduzir o overscan para 96/64 CSS px diminuiu o PNG, mas
reduziu a cobertura dos últimos pans, então o padrão permaneceu em 128 px. Forçar
`GDAL_FORCE_CACHING=YES` também foi rejeitado: alterou pixels e elevou o custo de
pintura, sem resultado estável.

O cache VSI do GDAL foi comparado em 8 MiB e 64 MiB com deslocamentos de 256 CSS
px. A primeira rodada de 8 MiB teve leitura p50 de 1,49 s; 64 MiB marcou 270 ms,
com pixels exatamente iguais. Porém, repetindo 8 MiB depois que o compartilhamento
e o cache do sistema já estavam aquecidos, o p50 também caiu para 263 ms (io de
91–102 ms). Portanto, não há evidência isolada de que aumentar VSI_CACHE reduza
o tempo; o fator dominante é o estado de cache/rede. O valor padrão permanece.

## Prévia raster inicial mais rápida — 10/11/2026

O benchmark progressivo agora mede o primeiro pedido interativo sem semear um
quadro final no cache e, depois, solicita o quadro final na mesma câmera. No
cenário misto de zoom 64, viewport 1600×900 CSS, DPI solicitado 2, foco 50%/25%,
ECW remoto e 77.146 lotes, a prévia padrão anterior (limite 1,25 DPI) produziu
imagem de 2320×1445 e PNG de 10,07 MB. Duas execuções marcaram 541 e 552 ms de
HTTP, com `io` de 378 e 388 ms.

Com limite de 1,0 DPI, duas execuções produziram imagem de 1856×1156 e PNG de
6,45 MB. O HTTP marcou 264 e 277 ms, com `io` de 133 e 139 ms. A mediana das
duas medições caiu de 546,5 para 270,5 ms (50,5%); o payload caiu 36%. O
viewport visível manteve 100% de cobertura raster. A inspeção lado a lado
mostrou o mesmo enquadramento e uma prévia um pouco mais suave; após ela, o
quadro final voltou à resolução solicitada e os pixels finais foram idênticos
ao baseline nas comparações.

O padrão agora limita a 1,0 DPI somente as prévias interativas raster. O valor
pode ser ajustado entre 0,5 e 2,0 com `GEONEX_INTERACTIVE_RASTER_PREVIEW_DPI`.
O contrato `--raster-contracts` passou e os builds Release de PerfTest e GeoNex
foram concluídos sem erros. As execuções de prévia começaram em processos novos,
mas o cache SMB/sistema já estava aquecido; elas não medem a primeira leitura
fria da máquina. A leitura fria do compartilhamento, que pode levar 5–6 s,
permanece como gargalo aberto.

Para repetir a sequência progressiva, defina
`GEONEX_BENCH_PROGRESSIVE_STARTUP=1` e
`GEONEX_BENCH_RASTER_ZOOMS=64` antes de chamar
`PerfTest --production-mixed-metrics <GeoNex.dll> <ECW> <LOTES.shp>`. A variável
`GEONEX_INTERACTIVE_RASTER_PREVIEW_DPI` alterna o limite sem recompilar.

Na mesma sequência, variar `GEONEX_GDAL_THREADS` entre 1, 2, 4, 6 e 8 não
reduziu a leitura: a prévia ficou entre 293 e 311 ms e `io` entre 135 e 145 ms
(uma execução por valor, com o cache do compartilhamento já aquecido). O padrão
dinâmico de threads foi mantido.

Também medi JPEG e WebP no quadro final capturado de 3712×2312. JPEG95 gerou
5,93 MB, mas levou p50 de 279 ms para codificar e 116 ms para decodificar; a
imagem não passou na verificação de opacidade, então o formato ainda exigiria
decidir como achatar transparências. WebP70 e WebP95 levaram cerca de 1,27 s e
1,05 s para codificar. O PNG da rota de produção codificou em cerca de 44 ms no
mesmo cenário, portanto a redução de bytes não compensou o custo de codec.

## Primeiro quadro sem margem de navegação — 10/11/2026

A abertura do projeto agora marca apenas a prévia inicial com `startup=1` e
renderiza o viewport visível sem os 128 px de overscan. O quadro final e os
pedidos de gesto continuam com a margem normal. No mesmo enquadramento 1600×900
CSS, DPI solicitado 2 e foco 60%/40%, o quadro antigo media 1856×1156 e
6.448.227 bytes; a prévia inicial sem margem mediu 1600×900 e 4.327.947 bytes
(32,9% menos). Ambas cobriram 100% do viewport. A inspeção do recorte visível
mostrou o mesmo enquadramento; o erro RGB médio entre os recortes foi 24,1/255
por diferenças de reamostragem, com alpha idêntico. O quadro final permaneceu
pixel a pixel igual ao baseline.

O pedido sem margem no mesmo ponto foi executado depois do baseline, com blocos
ECW já aquecidos; por isso, os tempos de 2,30 s e 0,30 s não são uma comparação
válida de latência fria. Em outra área sem margem, a prévia levou 1,34 s, com
`io` de 1,20 s. Durante leituras em novos pontos, os contadores SMB mostraram
tráfego de aproximadamente 2–12 MB/s. Uma leitura direta de 64 MiB no mesmo
compartilhamento levou 5,99 s (10,7 MiB/s). `io` cobre a chamada GDAL/ECW e não
separa rede de descompressão; a primeira leitura fria ainda depende da rede.

Para medir a variante sem overscan, use também
`GEONEX_BENCH_STARTUP_NO_PADDING=1` junto com as variáveis do benchmark
progressivo. O quadro inicial não traz margem para pan, mas o assentamento e os
gestos seguintes voltam à cobertura normal.

No mesmo ponto, a prévia sem margem a 0,75 DPI mediu 1200×675 e 3.245.979
bytes, contra 1600×900 e 4.327.947 bytes a 1,0 DPI (25% menos payload). Ambas
mantiveram cobertura completa e o quadro final foi idêntico ao baseline. A
inspeção mostrou mais suavidade em 0,75 DPI. Os tempos HTTP isolados dessas duas
execuções não são um A/B de leitura fria, pois os blocos do ECW já estavam
aquecidos.

## Limite dedicado para a prévia de abertura

A prévia da primeira apresentação do projeto usa agora 0,75 DPI por padrão,
separado do limite de 1,0 DPI usado durante pan/zoom. O limite inicial pode ser
ajustado entre 0,5 e 2,0 com `GEONEX_STARTUP_RASTER_PREVIEW_DPI`; o limite dos
gestos continua ajustável por `GEONEX_INTERACTIVE_RASTER_PREVIEW_DPI`. No ponto
medido, 0,75 DPI reduziu a imagem inicial de 1600×900 para 1200×675 e o PNG em
25%, preservando 100% de cobertura visível. O quadro final continua sendo
renderizado na resolução solicitada e permaneceu pixel a pixel igual ao
baseline. A prévia fica visivelmente mais suave; esse compromisso vale somente
para o primeiro quadro. Como os blocos ECW estavam aquecidos, os dados
demonstram menor trabalho de rasterização e transferência, mas não quantificam
ganho de latência na primeira leitura fria do compartilhamento.

## Reutilização de pan sem perder cobertura

O benchmark de 7 passos revelou que salvar cada prévia deslocada como nova base
acumulava uma faixa transparente de 32 px por passo. Removi essa publicação do
caminho de reuso: a imagem-base agora permanece estável até o movimento sair da
área que ela cobre; então o servidor lê e publica um novo quadro. Isso manteve
100% de cobertura visível em todos os 20 passos do cenário de zoom 64.

Para reduzir a frequência dessas leituras em arrastos longos, a margem dos
quadros de gesto usa até 225 px CSS em 1600×900/DPI 2, enquanto a margem do
quadro assentado continua em 128 px. O limite pode ser reduzido com
`GEONEX_GESTURE_PADDING_CSS` (0–256), ou herdado de
`GEONEX_NAVIGATION_PADDING_CSS`. Em 20 passos de 32 px, a margem de 128 px
precisou atualizar a base 3 vezes; 225 px precisou 2. O PNG de cache aumentou
de 8,60 MB para 11,09 MB (+29%). Uma execução mais aquecida mediu p50/p95 de
35/89 ms com 225 px e 34/77 ms com 128 px; portanto, o ganho observado foi
menos releituras e cobertura completa, não menor latência de cada resposta em
cache. Os tempos das releituras variaram bastante conforme o estado do cache
SMB; ainda não há uma comparação fria controlada que prove menor latência total.

## Composição raster e transporte HTTP — 10/11/2026

Repeti o quadro final misto (ECW + 77.146 lotes, 3712×2312, zoom 64) para separar o custo do compositor e da gravação local. Todos os quadros finais passaram na comparação pixel a pixel com a referência.

- Com o paralelismo atual, `draw` variou aproximadamente entre 33 e 53 ms nos testes por quantidade de workers. Forçar `GEONEX_RASTER_PARALLEL_COMPOSITE=0` levou a fase `draw` a 176–180 ms. O ganho é real e o padrão paralelo deve permanecer.
- Variar 2, 4, 6 e 8 workers produziu medianas de `draw` de 91,2; 47,1; 40,7 e 38,3 ms, respectivamente (cinco quadros por grupo). O padrão adaptativo atual já opera próximo de oito workers nesta máquina; não há ganho consistente que justifique fixar um número diferente.
- A escrita HTTP usa buffer de 256 KiB. A alternativa de 1 MiB reduziu a mediana de `write` de 68,9 para 68,5 ms em cinco quadros por grupo, dentro da variação. O buffer maior aumentou a alocação medida em cerca de 0,8 MB por resposta; foi descartado.

No quadro final atual, `io` de ECW continua em aproximadamente 271–337 ms, acima de `draw` (~38 ms), encode PNG (~45 ms) e escrita local (~69 ms). Assim, o próximo ganho relevante continua sendo evitar ou reduzir leituras no compartilhamento; chamadas de escrita e ajuste fino de workers não atacam essa parcela. Estas amostras foram feitas com caches já aquecidos e não medem o primeiro acesso frio ao UNC.
O encoder BMP foi sondado no quadro opaco de referência; o Skia desta distribuição retornou resultado nulo, então o formato não pode substituir o PNG. A política de codificação permanece inalterada.
## Validação do build e da abertura real — 10/11/2026

Depois do build Release, o primeiro quadro no cenário misto saiu em 204,3 ms: 1200×675, 3.245.979 bytes, cobertura raster visível de 100% e `io=86,8 ms`. O quadro final saiu em 467,5 ms: 3712×2312, 25.790.393 bytes, `io=290,0 ms`, `draw=54,8 ms`, `encode=44,7 ms`; os pixels finais foram idênticos à referência. É uma execução com cache do compartilhamento aquecido, não uma mediana nem uma medição fria.