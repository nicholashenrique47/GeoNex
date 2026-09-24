# Transporte de tiles — 21/09/2026

`OnlineTileTransport` mantém conexões HTTP abertas entre leituras GDAL e
compartilha pedidos simultâneos da mesma URL. Um adaptador HTTP em loopback
recebe somente coordenadas de fontes TMS previamente registradas. O limite
de downloads vale para todos os leitores: Google/ESRI 4, OSM 2.

O GDAL continua responsável por seleção de zoom, RasterIO e reprojeção.
Prévia e imagem final mantêm as dimensões e os pixels anteriores. O transporte
é usado na navegação online com cache persistente; fontes não suportadas
continuam no transporte nativo.

O cache usa a URL original e a mesma hierarquia MD5 do GDAL 3.12.1, preservando
o conteúdo existente entre reinícios. Respeita `no-store`, `no-cache`,
`max-age`, `Expires` e `Age`; gravação atômica, tamanho limitado e descarte
dos arquivos mais antigos. Respostas HTTP inválidas ou imagens incompletas
não entram no cache. Timeouts e concorrência permanecem limitados.

## Evidência

Contratos com servidor local: oito pedidos concorrentes geram um download;
novo transporte reaproveita o cache; GDAL lê arquivos gravados pelo adaptador
e vice-versa; máximo de duas conexões no fixture OSM; falhas, validade HTTP,
imagem truncada e encerramento com download pendente verificados.

O teste progressivo cobre 36 URLs distintas com 36 downloads. Uma região
independente chega enquanto um tile de borda permanece retido. A imagem final
é idêntica à leitura integral em Mercator, coordenadas geográficas e SIRGAS/UTM.
Endpoint de produção, estado parcial/final, zoom fracionário, DPI 2,
alinhamento, sessão, fila e navegação JavaScript também passaram.

Medições pontuais em Guaratuba (-48.5747, -25.8828), SIRGAS/UTM 22S,
1024 × 768, área de 1400 × 1050 m, diretório de cache local novo por execução:

| Caminho | Primeira prévia | Imagem final |
|---|---:|---:|
| Google, leitura integral com HTTP nativo | — | 12,123 s |
| Google, progressivo com transporte compartilhado | 0,513 s | 0,863 s |
| OSM, progressivo com transporte compartilhado | 0,698 s | 1,359 s |

O cronômetro encerra antes da exportação PNG. São amostras de rede, não um
benchmark controlado de ganho percentual: execução sequencial, possível
aquecimento do servidor remoto e compilação concorrente na amostra nativa.
Não medem FPS nem substituem validação visual no WebView com o projeto real.
Revisita progressiva medida: Google 0,362 s; OSM 0,323 s.

Cancelamento de câmera ainda espera a chamada GDAL ativa retornar; fechamento
do transporte cancela o HTTP. Uma região que depende de um tile lento precisa
aguardá-lo. Não há pré-download de áreas externas à leitura solicitada.

```powershell
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --online-transport-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --online-progressive-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --online-progressive-production GeoNex/bin/Release/net10.0-windows10.0.19041.0/win-x64/GeoNex.dll
# Diagnósticos manuais acessam os provedores e gravam PNGs em TEMP.
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --online-provider-diagnostics Google EPSG:31982 direct
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --online-provider-diagnostics Google EPSG:31982
```

Referências: [GDAL WMS](https://gdal.org/en/stable/drivers/raster/wms.html),
[HTTP do WMS 3.12.1](https://github.com/OSGeo/gdal/blob/v3.12.1/frmts/wms/gdalhttp.cpp),
[cache do WMS 3.12.1](https://github.com/OSGeo/gdal/blob/v3.12.1/frmts/wms/gdalwmscache.cpp).
