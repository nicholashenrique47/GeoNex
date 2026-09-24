# Preservar navegação durante chegada de tiles — 22/09/2026

## Problema e mudança

A chegada de cada atualização raster removia o cache global. Em cenas com
LOTES, um gesto logo após essa invalidação voltava a consultar geometria e
pintar os polígonos, mesmo quando já existia uma composição útil da cena.
Além disso, frames com tiles pendentes/adiados não ofereciam cache de navegação.

`MapRenderingService` agora distingue a imagem disponível para interação da
imagem elegível como resultado final. Usa a mesma entrada de bitmap:

- Publicação final disponibiliza a imagem aos dois caminhos.
- Composição parcial/adiada disponibiliza somente a prévia.
- Atualização de pixels raster invalida a elegibilidade final, mantendo a
  imagem anterior para gestos dentro da cobertura existente.
- `RequestRedraw`/invalidação normal remove ambas as possibilidades. Edições,
  estilos e alterações da cena continuam conservadores.
- Publicação com revisão antiga é descartada pelo mesmo controle existente.

O endpoint usa a prévia somente na interação. Um pedido final continua
renderizando os pixels atuais e não pode confundir prévia com resultado final.
Ordem de camadas, transformações, precisão local, estilos e geometria não mudam.
Retém uma imagem de cena, não uma cópia persistente adicional por estado;
leases de imagens substituídas continuam válidas até seus leitores terminarem.
Agora essa memória também pode permanecer ocupada durante downloads pendentes.

## Validação e medição

Arquivo original LOTES.shp, 4.937.409 feições, EPSG:3857, 1600 × 900 CSS,
DPI 1, bitmap com overscan 2050 × 1350, escala 4 px/m. Mesma sequência de
movimentos 0/4/8 px; o benchmark chama a invalidação usada pelas publicações
raster antes de cada movimento, isolando ruído de rede.

| Seis movimentos com invalidação raster | DLL anterior | Corrigida |
|---|---:|---:|
| Mediana HTTP | 383 ms | 26 ms |
| Intervalo | 366–420 ms | 25–30 ms |
| Geometria | 46–65 ms | 0 ms |

Não é medição de FPS/WebView. Primeira imagem completa permanece perto de
450 ms nesta rodada. O ganho exige uma prévia cuja cobertura atenda à câmera;
não acelera construção integral nem mostra antecipadamente tiles não recebidos.

Contratos de produção: prévia chega com o renderer bloqueado, pixels e espaços
entre polígonos preservados, pan e DPI 1/2, escala 4/64; atualização raster
conserva a prévia mas invalida o final; edição remove ambos. O teste falha por
timeout na DLL anterior. Fixture online prova disponibilidade da cena parcial
durante tile retido, adiamento sem download, impedimento de cache final
prematuro e atualização final normal. Passaram também fila/cancelamento online,
cache de polígonos, identidade PNG e navegação JavaScript.

```powershell
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --high-zoom-preview-contracts GeoNex/bin/Release/net10.0-windows10.0.19041.0/win-x64/GeoNex.dll
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --online-progressive-production GeoNex/bin/Release/net10.0-windows10.0.19041.0/win-x64/GeoNex.dll
$env:GEONEX_BENCH_RASTER_REFRESH = '1'
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --production-map-metrics GeoNex/bin/Release/net10.0-windows10.0.19041.0/win-x64/GeoNex.dll 'C:/Users/Windows 10/Desktop/SHP S/LOTEAMENTOS_LOTES/LOTES.shp' --high-zoom
Remove-Item Env:GEONEX_BENCH_RASTER_REFRESH
```

## Experimento de pintura separado

No path real, preenchimento ~82 ms e bordas ~182 ms. Particionar a pintura
em faixas mudou pixels (delta máximo 148), inclusive mantendo a transformação
original. O experimento permanece somente em `PerfTest/Experiments` e não foi
integrado ao aplicativo. A otimização do custo de pintura integral continua
pendente, assim como validação visual de cenas mistas no WebView.
