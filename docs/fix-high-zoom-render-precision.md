# Zoom alto: precisão vetorial e resolução online

2026-09-13. Continuação sobre o worker online independente; alterações anteriores preservadas.

## Causas e implementação

- Com mapa base primeiro, o projeto usa EPSG:3857 e origem zero. Converter metros de magnitude milionária para `float` antes de construir o SKPath quantiza vértices. Na primeira feição do LOTES, erro máximo observado: **0,12425 m**. Subtraindo uma origem próxima em `double` antes da conversão: **0,00000057556 m**, mantendo os cinco vértices e sua ordem.
- `RenderPrecisionPolicy` aciona origem de desenho próxima à câmera quando o passo do float ultrapassa 1/8 pixel físico. O caminho de simbologia UNICA (polígonos/linhas, nativo e reprojetado) recebe offsets em double e matriz centrada. Consulta espacial permanece em coordenadas mundiais double; geometria e índices originais não são alterados.
- Nessa condição, caches de paths em coordenadas antigas e reprojeção do frame global são ignorados. Bordas deixam de ser omitidas na prévia. A matriz de projeto é restaurada após o desenho. Custo: geometria visível é reconstruída, sem cache de origem móvel nesta etapa.
- A janela online passa a ser calculada próxima da origem, evitando arredondar suas bordas em metros mundiais. Dentro da cobertura, RasterIO recebe janela fracionária double e produz faixas de 128 linhas na grade física final; não há segunda interpolação do mosaico intermediário. Scratch limitado a uma faixa, com orçamento para dois frames RGBA + 8 MiB. Reprojeção e borda do mundo mantêm orçamento conservador anterior.
- Com 768 MiB disponíveis, o orçamento direto preserva 3840×2160; solicitações enormes continuam limitadas por memória/dimensão. Não se promete resolução integral sob qualquer pressão de RAM.

## Verificação

Release compilado: zero erros, 191 avisos preexistentes. PerfTest: zero erros, três avisos preexistentes.

- Regressão sintética: dois polígonos separados por 1/64 m que colapsavam em float mundial; equivalência exata de pixels após rebasing em DPI 1/1,25/2 e rotações 0/17/90.
- LOTES: leitor de produção, 57.454 feições, 324.191 vértices, contagem/FIDs extremos/quantidade de atributos; primeira geometria reprojetada comparada com GDAL. Insumo: SHP temporário produzido anteriormente pelo importador do LOTES.geojson; original não modificado. Não é validação de todos os valores dos atributos ou de todos os vértices contra um motor independente.
- Servidor de tiles local registra níveis HTTP: zoom fracionário, DPI 2 e ampliação além de z20 selecionam o nível esperado. Não foi encontrada seleção incorreta nesses cenários.
- Faixas comparadas byte a byte com leitura integral na mesma grade: escalas de amostragem 0,125/1/1,75/4,25; padrão bidimensional, sem diferenças nos pixels.
- Integração HTTP real do motor com LOTES e tiles bloqueados: frames continuam respondendo; refinamento posterior aparece; HTTP 403/204/500 não substitui último cache válido. Métricas desta execução: zoom relativo 8 ≈138 ms e 32 ≈34 ms enquanto tiles bloqueados; não representam FPS, comparação controlada antes/depois nem equivalência QGIS/ArcGIS.
- Contratos de câmera, cache vetorial, orçamento online e agendamento JavaScript aprovados.

## Limites e continuidade

O provedor Google configurado continua limitado a z20. Ampliar além disso não cria detalhe inexistente. Disponibilidade real varia por região/provedor; os testes de qualidade usaram provedor local controlado, não certificam a imagem Google de cada localização.

Simbologia categorizada pré-compilada, pontos, edição/seleção e coordenadas da câmera ainda têm caminhos float legados; não foram convertidos integralmente nesta etapa. Próxima evolução: origem por bloco cacheável e câmera double compartilhada entre renderização e ferramentas, com testes de alinhamento e picking.

As GeoAI Skills orientaram a preservação dos dados e testes de precisão/memória, sem limpeza ou simplificação destrutiva. Nenhuma alteração C++ nesta etapa (ABI 4 existente).

Referência consultada: [GDAL RasterIO e seleção de overviews](https://gdal.org/en/stable/doxygen/classGDALRasterBand.html). A confirmação dos níveis veio também dos pedidos HTTP reais do fixture.

## Reproduzir

`dotnet build PerfTest/PerfTest.csproj -c Release --no-restore -p:GeoNexBuildNative=false`

Executar `PerfTest/bin/Release/net10.0/PerfTest.dll` via dotnet com `--render-precision-contracts`, `--online-worker-contracts`, `--online-basemap-contracts`, `--coordinate-contracts` e `--render-path-cache-contracts`.

Integração: `--production-vector-smoke <GeoNex.dll Release> <LOTES compilado.shp>` e `--production-map-metrics <GeoNex.dll Release> <LOTES compilado.shp> --delayed-basemap`. JavaScript: `node PerfTest/MapEngineContracts.js`.
