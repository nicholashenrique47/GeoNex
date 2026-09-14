# Renderização online independente — 2026-09-13

Continuação de [LOTES: apresentação online](performance-lotes-online-presentation.md).
Esta etapa substitui a limitação anterior: o refinamento de mapas base
cadastrados pela UI não ocupa mais o gate de renderização dos vetores.

## Mudança

- `LatestRenderWorker`: um executor dedicado, até 16 camadas pendentes e
  somente a câmera mais recente por camada. Repetir a mesma câmera não
  reinicia o download. Resultados substituídos/cancelados são descartados.
- `OnlineRasterFrameReader`: abre uma instância GDAL independente a partir
  do XML original. RasterIO direto em CRS coincidente; Warp com bilinear e
  alpha para reprojeção. Dimensões seguem os limites existentes de pixels/RAM.
- O render HTTP desenha vetores e o último bitmap válido sem aguardar a
  rede. Quando o trabalho termina, a UI recebe uma notificação para apresentar
  a imagem refinada. Não há polling contínuo no navegador nem cópia de dados
  vetoriais para o executor online.
- A última imagem válida permanece disponível quando tiles falham; dados
  HTTP inválidos não são publicados como um fundo preto. Uma repetição
  automática após 3 segundos é permitida por trabalho. Pedidos idênticos
  respeitam cooldown; mudar a câmera permite outra tentativa.
- Cache incompleto não vira cache global final. A publicação valida a
  identidade do dataset, o CRS e a origem local. Remoção/troca de camada
  impede publicação na fonte substituta. Shutdown cancela trabalho pendente
  sem esperar uma chamada de rede nativa bloqueada.
- Na câmera exata, o bitmap final é desenhado diretamente em pixels físicos,
  evitando erro de arredondamento na ida/volta por coordenadas mundiais.

O XML é registrado ao adicionar mapas base. WMS externo sem esse registro,
render com rotação e impressão continuam pelo caminho síncrono existente.
Impressão não exporta intencionalmente uma prévia ainda esperando tiles.

## Teste reproduzível com o arquivo do usuário

`LOTES.geojson`: 57.454 feições, 324.191 vértices na representação compilada.
Arquivo original mantido intacto. Windows, i5-1235U, 12 threads lógicas,
7,7 GiB RAM total; ~0,6–1 GiB livres nas rodadas medidas. Release, GDAL
3.12.1, SkiaSharp 3.119.2, `DOTNET_TieredCompilation=0`.

O provedor local de teste recebe o pedido de tiles e **não o responde**
até dois zooms dos vetores terminarem. Não é apenas um teste com tiles já
em cache. A DLL real de produção atende os pedidos pelo HTTP local.

Resultados de uma rodada (incluindo HTTP e decode Skia, não FPS/WebView):

| Pedido | Tempo | I/O online na fila vetorial |
| --- | ---: | ---: |
| Zoom relativo 8, provedor bloqueado | 104 ms | 0 ms |
| Zoom relativo 32, provedor bloqueado | 69 ms | 0 ms |
| Refinamento após liberar o provedor | 84 ms | 0 ms |
| Zoom 64 com falha HTTP 403 | 71 ms | 0 ms |

Outra rodada validou HTTP 204 e 500: o identificador do cache válido
permaneceu igual após cada erro. Erros de GDAL exibidos nesse teste são
esperados, não falhas da suíte. Tempos variam com carga/energia; não usar
esses números como ganho percentual comparando máquinas/rodadas distintas.
Na rodada final, com outra carga da máquina, os dois zooms bloqueados
responderam em 164 e 90 ms; o primeiro quadro incluiu aquecimento do runtime.
O serviço Google real também refinou a imagem com sucesso. Em todos esses
pedidos, a etapa de I/O online da fila vetorial permaneceu em zero.

`--online-worker-contracts` cobre 500 pedidos substituídos, deduplicação,
descarte e liberação de resultados, cooldown, recuperação automática,
shutdown sem bloqueio, RasterIO EPSG:3857 e Warp EPSG:4326 com RGB/alpha.
As geometrias, atributos e algoritmos de simplificação não foram alterados.
Regressões de cache de paths, leases, geometria/PNG e câmera JavaScript
passaram. Builds Windows Release e Debug mantêm os avisos preexistentes.

```powershell
dotnet build PerfTest/PerfTest.csproj -c Release -p:GeoNexBuildNative=false
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --online-worker-contracts
$env:DOTNET_TieredCompilation='0'
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --production-map-metrics GeoNex/bin/Release/net10.0-windows10.0.19041.0/win-x64/GeoNex.dll 'C:/Users/Nicho/Downloads/LOTES.geojson' --delayed-basemap
# Opcional: serviço Google real, uma câmera, usando o cache de tiles existente.
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --production-map-metrics GeoNex/bin/Release/net10.0-windows10.0.19041.0/win-x64/GeoNex.dll 'C:/Users/Nicho/Downloads/LOTES.geojson' --basemap
```

## Invariantes e fontes

As skills GeoAI `swe-devops-standards` e `geo-data-engineering` orientaram
a separação de objetos GDAL, os limites da fila e os testes de falha.
O parser da versão implantada foi consultado: o valor `,` em
`ZeroBlockHttpCodes` forma uma lista não vazia de texto com zero códigos
permitidos. Diferentemente de omitir o elemento, também desativa o bloco
preto implícito para HTTP 204. O contrato é testado com a versão fixada e
deve ser repetido numa atualização do GDAL.

- [GDAL WMS: cache e tratamento de HTTP](https://gdal.org/en/stable/drivers/raster/wms.html).
- [Parser GDAL 3.12.1](https://github.com/OSGeo/gdal/blob/v3.12.1/frmts/wms/gdalwmsdataset.cpp).

## Limites e rollback

O custo CPU de desenhar os polígonos permanece; foi removida a espera de
rede que atrasava esses quadros. Não é equivalência demonstrada com QGIS
ou ArcGIS. Sem imagem anterior e sem rede, não há satélite para apresentar.
Regiões novas fora do último bitmap aguardam os tiles corretos. Um download
nativo já iniciado pode terminar antes de o próximo trabalho online começar,
mas não retém o gate vetorial.

Rollback do caminho assíncrono: não registrar o argumento `onlineXml` ao
publicar o raster; mantém-se o caminho anterior. Não apagar caches/fontes
nem reverter alterações anteriores indiscriminadamente.
