# LOTES: separar espera online da apresentação dos vetores

## Escopo e evidência — 2026-09-12

Teste com `C:\Users\Nicho\Downloads\LOTES.geojson`, 33.023.098 bytes,
57.454 feições e 324.191 vértices após a conversão existente. Original não
modificado. Windows, Intel i5-1235U (12 processadores lógicos), ~7,7 GiB RAM,
aproximadamente 1 GiB livre. GDAL 3.12.1, SkiaSharp 3.119.2, Release,
`DOTNET_TieredCompilation=0`. HEAD de referência: `0663bc9`, com alterações
locais anteriores preservadas. Não é comparação de desempenho com QGIS/ArcGIS.

O novo `--production-map-metrics` carrega **a DLL real**, importa pelo
ProjetoService, usa o índice/renderer de produção e solicita PNG pelo HTTP
local. Viewport 1600×900 CSS, DPI 1, overscan: 2050×1350 pixels. Testa
EPSG:3857 com reprojeção da camada, zoom relativo 1/4/16/64. Isola o log
relativo da importação numa pasta temporária. `--basemap` acrescenta Google
Satellite, na extensão dos lotes, em uma única câmera. Esse modo acessa a rede
e reutiliza o cache normal de tiles, sem limpá-lo.

### Medições observadas

- Conversão inicial GeoJSON→SHP: 2.907 ms; repetição com arquivos aquecidos:
  1.476 ms. Publicação/índice: 129–298 ms. **Não foram otimizados nesta etapa**.
- Antes da apresentação progressiva, Google + vetores: 5.637 ms HTTP,
  dos quais 5.173 ms eram I/O online; quadro seguinte, com cache: 113 ms.
- Depois: quadro inicial com vetores nítidos: 402 ms HTTP, **0 ms de I/O
  online**; prévia interativa: 70 ms, também zero I/O online. Refinamento:
  478 ms, com 238 ms I/O (tiles já aquecidos pela execução anterior).
  Não atribuir a diferença de tempo do refinamento à otimização: o cache mudou.
- Comparação de codecs no **mesmo bitmap dos lotes**, três amostras:
  PNG Sub/compressão 1: mediana 52,71 ms para codificar e 70,93 ms incluindo
  decodificação Skia; compressão 0: 16,26 / 26,31 ms. Pixels comparados byte
  a byte. Payload aumenta de 2,12 MB para 11,09 MB, somente no localhost.
- Em bitmap com satélite, codificação 142,49→17,93 ms (medianas, três amostras).
  Tempos variam com aquecimento, carga e energia do notebook; não comparar
  rodadas diferentes como se fossem um A/B controlado do motor inteiro.

## Implementação mantida

1. Atualizações de cena com vetor e mapa online solicitam `deferOnline=1`.
   A geometria mantém a qualidade final, mas usa apenas o bitmap online já
   disponível, quando existe. Sem cache, o fundo aguarda o refinamento.
2. O JS solicita o quadro completo **depois** de apresentar a prévia, a partir
   da câmera absoluta confirmada. Quadros obsoletos não iniciam refinamentos.
   Gestos em andamento continuam usando o agendamento de assentamento.
3. Prévia interativa não chama RasterIO/Warp em datasets online. O cache
   retém o referencial espacial e é transformado para a nova câmera na posição
   original da camada na pilha. Nenhuma nova thread compartilha objetos GDAL.
4. Quadros com online adiado **não publicam o cache global final**. O
   refinamento mantém resolução física, antialias, estilos e pipeline online
   existentes; impressão ignora o adiamento.
5. PNG adaptativo, sem perdas: amostragem limitada da variação de pixels
   seleciona compressão 0 apenas para imagens densas de navegação. Payload
   bruto limitado a 32 MiB e 1/32 da RAM livre informada pelo orçamento do
   runtime; quadros pequenos/simples e impressão continuam comprimidos.

## Verificação

- `--frame-encoding-contracts`: igualdade de pixels/alpha RGBA e BGRA,
  pressão de memória, cenas simples/densas; transformação do cache online
  em diferentes DPI, zoom, pan e rotações.
- `MapEngineContracts.js`: apresentar vetor antes de pedir refinamento,
  câmera preservada, prévia obsoleta descartada, demais contratos de gestos.
- `--production-map-metrics ... --basemap`: exige zero I/O nas duas prévias
  e diferença entre quadro sem tiles e refinamento (evita cache final contaminado).
- `--production-vector-smoke`: contagem GDAL, FIDs/atributos amostrados,
  reprojeção e igualdade dos pixels dos paths em três escalas.
- Builds Windows Debug e Release; avisos preexistentes de dependências/nullable
  permanecem. ABI C++ 4 reutilizada, sem alteração nativa nesta etapa.

## Limites / próximas prioridades

O gate GDAL/Skia ainda serializa renders completos. Um novo pedido pode
aguardar uma operação síncrona já iniciada quando não há cache global
compatível; isto **não** é I/O totalmente assíncrono por camada. Os dados
online continuam sujeitos à rede/provedor. Não houve benchmark da UI/WebView
nem medição de FPS. O decode acima é Skia, não o navegador.

A conversão intermediária para SHP continua com suas limitações de campos,
tipos e FIDs em relação ao GeoJSON. A validação de atributos desta etapa é
contra o SHP compilado, não uma prova de equivalência completa ao GeoJSON.
Próximos trabalhos: jobs online independentes, provedor vetorial sem
conversão obrigatória e perfil do custo de stroke em visões gerais densas.

Experimento descartado: restringir mais a reutilização do path após zoom-in
não trouxe ganho suficiente em LOTES e adicionou custo de reconstrução.
Não foi mantido. A seleção de trabalho e os testes de invariantes seguiram
as skills GeoAI `swe-devops-standards` e `geo-data-engineering`.

## Reproduzir

```powershell
dotnet build PerfTest/PerfTest.csproj -c Release -p:GeoNexBuildNative=false
$env:DOTNET_TieredCompilation='0'
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --production-map-metrics GeoNex/bin/Release/net10.0-windows10.0.19041.0/win-x64/GeoNex.dll 'C:/Users/Nicho/Downloads/LOTES.geojson'
# Opcional: uma câmera com tiles online e cache existente.
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --production-map-metrics GeoNex/bin/Release/net10.0-windows10.0.19041.0/win-x64/GeoNex.dll 'C:/Users/Nicho/Downloads/LOTES.geojson' --basemap
```
