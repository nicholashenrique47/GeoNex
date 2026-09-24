# Google/OSM: nitidez e alinhamento — 21/09/2026

O relato do usuário foi esclarecido: imagem borrada ou desalinhada. Os dois
provedores responderam HTTP 200 com JPEG/PNG válido; o defeito reproduzido
estava na apresentação das imagens.

## Correções

- A composição das matrizes da prévia perdia precisão com coordenadas grandes.
  Na mesma câmera, centro (-5400000, 3000000), DPI 2 e escala 3,7, o teste
  reproduziu deslocamento de 3,9999 pixels. Preservar o centro original e compor
  os deslocamentos em double eliminou esse erro. Também cobre o cache global.
- O limite fixo de 4 MP introduzido no incremento anterior reduzia prévias HiDPI
  mesmo com memória disponível. Agora se aplica o orçamento de RAM existente;
  o contrato de 2560×1920 preserva todos os pixels da prévia nesta máquina.
- A reprojeção online usa `gdalwarp -et 0`: transformação exata, sem o
  aproximador cujo resultado pode variar com a divisão em regiões.
  Resolução, extensão e algoritmo bilinear permanecem explícitos.

## Verificação

PASS: identidade e pan com coordenadas Mercator grandes em três escalas;
reprojeção/SIRGAS; zoom fracionário e HiDPI com os níveis reais pedidos ao
provedor local; falhas e publicação obsoleta; endpoint real; 60 verificações
de impressão; navegação JavaScript. A imagem final é conferida contra uma
leitura da grade inteira. As regressões preexistentes de coordenadas e PNG
também passaram.

Google Satellite e OpenStreetMap foram lidos pelo GDAL 3.12.1 usando seus URLs
configurados, em Guaratuba, tanto EPSG:3857 quanto SIRGAS/UTM 22S. Não houve
falha HTTP nesses testes. Tempos de rede variaram; não são uma medição de FPS
nem prova de alinhamento absoluto de cartografia de fornecedores diferentes.
O teste visual do projeto do usuário no WebView continua pendente.

```powershell
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --online-alignment-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --online-projected-zoom-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --online-progressive-contracts
# Diagnóstico manual; estes comandos acessam a internet e gravam PNGs em TEMP.
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --online-provider-diagnostics Google EPSG:31982
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --online-provider-diagnostics OSM EPSG:3857
```

Fontes: [GDAL Warp: aproximação e -et 0](https://gdal.org/en/stable/programs/gdalwarp.html#approximate-transformation),
[GDAL WMS/TMS: resolução, cache e conexões](https://gdal.org/en/stable/drivers/raster/wms.html).

Limites: a resolução máxima e a qualidade da imagem de origem continuam sendo
as fornecidas pelo serviço. Memória insuficiente ainda pode limitar a dimensão
de renderização. Transformação exata pode custar mais CPU em projetos reprojetados.
Nenhum dado do usuário foi modificado. Para rollback, revisar apenas as alterações
em MapCoordinateSpace, NavigationFramePolicy, ProgressiveOnlineRaster e
OnlineRasterFrameReader; preservar as correções anteriores.
