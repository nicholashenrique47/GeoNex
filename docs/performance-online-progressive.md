# Mapa base progressivo — 21/09/2026

## Implementação

O motor online publica regiões prontas antes da imagem completa. Regiões de
512 pixels são ordenadas pela distância ao centro; até quatro leitores GDAL
independentes avançam em paralelo, respeitando o limite do provedor (OSM: 2)
e reduzindo para dois leitores com menos de 2 GiB disponíveis no orçamento.
Um tile lento pode ocupar uma fila sem impedir a publicação das outras.

Os snapshots são imutáveis, limitados pelo orçamento de memória e publicados no
máximo a cada 150 ms, além da primeira região e do fechamento. A fonte é lida
na resolução final mesmo quando o snapshot é reduzido, evitando baixar um
zoom inferior apenas para a prévia. O bitmap anterior preenche áreas ainda
não recebidas, com a transformação da câmera correspondente.

Resultados parciais passam pelas mesmas verificações de câmera, camada e
descarte do resultado final. O cache parcial nunca é marcado como completo.
Chegada de imagens não invalida caches vetoriais. Uma leitura da grade inteira
encerra o trabalho, preservando os pixels finais e a reprojeção existentes.

## Validação

Servidor local controlado, sem depender de serviços públicos:

- PNG parcial chegou ao endpoint de produção enquanto um pedido HTTP ficou retido.
- Endpoint manteve I/O de raster online em zero no gate da cena.
- Estado parcial/final e revisão dos caches vetoriais conferidos.
- Resultado final idêntico byte a byte ao leitor anterior em EPSG:3857, EPSG:4326
  e SIRGAS 2000 / UTM 22S (EPSG:31982) com origem local.
- Revisita sem novas chamadas HTTP; falha de região preserva as regiões recebidas.
- Publicação obsoleta descartada com liberação única; regressões de fila, sessão,
  zoom/DPI e câmera JavaScript passaram.

```powershell
dotnet build PerfTest/PerfTest.csproj -c Release -p:GeoNexBuildNative=false
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --online-progressive-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --online-progressive-production GeoNex/bin/Release/net10.0-windows10.0.19041.0/win-x64/GeoNex.dll
```

## Limites e próximos passos

Este incremento melhora o tempo até a primeira imagem, sem demonstrar redução
do tempo total nem equivalência de FPS ao QGIS. Ainda falta teste visual no
WebView com o projeto real. Impressão, rotação e fontes sem cache persistente
mantêm o caminho anterior. O último passe pode custar processamento adicional.

O incremento seguinte implementou conexões persistentes e compartilhamento de
pedidos em andamento por URL: veja [transporte de tiles](performance-online-transport.md).
Reaproveitar tiles de outros zooms como prévia continua sendo uma possibilidade.
Cancelamento nativo continua limitado: uma chamada HTTP em execução pode
precisar terminar antes da próxima câmera. A fila de renderização da cena
permanece independente.

Referências consultadas: [QGIS WMS/XYZ](https://github.com/qgis/QGIS/blob/master/src/providers/wms/qgswmsprovider.cpp),
[QGIS download manager](https://github.com/qgis/QGIS/blob/master/src/core/qgstiledownloadmanager.cpp),
[GDAL 3.12.1 WMS](https://github.com/OSGeo/gdal/blob/v3.12.1/frmts/wms/gdalwmsrasterband.cpp).
Implementação própria; nenhuma cópia do código QGIS.

Rollback: trocar a chamada progressiva em `LocalMapServer` pela chamada
`OnlineRasterSession.Read` anterior, sem restaurar correções anteriores de CRS,
vetores ou navegação. Alterações locais, sem commit/push.
