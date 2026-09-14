# Polígonos reprojetados e enquadramento progressivo — 11/09/2026

## Diagnóstico e escopo

Foram encontrados e lidos, sem alteração, os três GeoJSONs em `C:/Users/Nicho/Desktop/geojson/geojson`. O maior, `PDDU_Mancha_Inundacao_modelo.geojson`, tem 44.088.471 bytes, 8 MultiPolygons, 27.494 anéis e 1.078.031 vértices. Quantidade de feições não representa a complexidade de desenho dessa camada.

A DLL Release local estava datada de 07/09, enquanto as correções de enquadramento haviam sido compiladas em Debug em 11/09. Não foi possível confirmar qual executável o usuário abriu. A Release foi recompilada nesta etapa.

## Alterações

- Enquadramento após importação e pelo botão usa uma requisição identificada, com watchdog/retries. Apresenta uma prévia e solicita qualidade final somente depois de apresentar essa prévia. Evita cancelar a prévia com um refinamento prematuro.
- Importação não solicita mais um frame na extensão antiga ao sincronizar a hierarquia, imediatamente antes de enquadrar.
- Caminho reprojetado ganha redução radial somente na prévia, em coordenadas de tela (0,85 px CSS). Mantém pontos inicial/final, fechamento e sinal de área dos anéis; usa o anel original se houver colapso/inversão. Não é validação topológica completa entre anéis e não se aplica a exportação ou qualidade final.
- Contorno interativo considera também complexidade do path (>50 mil pontos), não apenas >500 feições. A qualidade final continua incluindo contornos.
- MultiPolygons que excedem o lote de reprojeção passam a enviar anéis completos que cabem no buffer por `AddPoly`, em vez de chamadas `MoveTo/LineTo` por vértice. Anéis individuais acima de 262.144 vértices continuam no caminho de chunks, sem fechamentos artificiais.
- O caminho final normal e seu buffer reutilizado foram mantidos. A primeira versão experimental apresentou overhead durante aquecimento; não se adotou essa substituição no caminho final normal.
- Falhas de importação agora são propagadas ao chamador, em vez de anunciar sucesso para uma camada que não foi publicada.

Essas alterações atingem SHP e os formatos convertidos pelo importador (GeoJSON/KML), quando precisam de reprojeção. Não foi introduzida conversão destrutiva nem alterado o arquivo original. A conversão existente para SHP e suas limitações de atributos permanecem; ainda não há cache persistente de conversão/reprojeção.

## Teste de CPU com os dados encontrados

Preparação separada: leitura do GeoJSON e conversão matemática de lon/lat para Web Mercator. Kernel de envio/draw em Skia, 1920×1080, sem margem extra, sem GDAL, tiles online, WebView ou tempo completo de abertura. Tiered compilation desabilitado no processo do benchmark para reduzir viés de aquecimento; build medido por mediana de três passes após um passe inicial, draw por um passe. Prévia e final têm qualidades distintas, não são equivalentes.

| Arquivo | Vértices originais → prévia | Draw completo → prévia |
|---|---:|---:|
| Inundacao_modelo | 1.078.031 → 324.375 | ~210 → ~47 ms |
| Mare_1_6m | 95.914 → 40.841 | ~39 → ~15 ms |
| Mare_2_5m | 57.229 → 30.725 | ~27 → ~10 ms |

As imagens finais dos três arquivos passaram na comparação pixel a pixel contra o caminho de referência. A construção final do arquivo maior ficou da mesma ordem (~16 ms), sem ganho significativo comprovado. A redução de tempo é principalmente do desenho da **prévia**. Não representa FPS do aplicativo, velocidade de importação ou paridade com QGIS/ArcGIS.

## Reprodução

```powershell
dotnet build PerfTest/PerfTest.csproj -c Release -p:GeoNexBuildNative=false --no-restore
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --transformed-ring-contracts
node PerfTest/MapEngineContracts.js
$env:DOTNET_TieredCompilation = '0'
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --geojson-render-metrics 'CAMINHO/arquivo.geojson'
```

O benchmark aceita apenas fixtures Polygon/MultiPolygon RFC 7946 sem SRC alternativo. Não modifica os dados. Contratos adicionais: fechamento/buracos, preservação dos vértices finais, contornos finais, cancelamento e transição prévia→final correlacionada à câmera.

## Pendente

Validar visualmente a Release atualizada no fluxo mapa base → GeoJSON → pan → aproximar camada. Medir o aplicativo completo com tiles frios/quentes e separar conversão OGR, reprojeção, geração de paths, GDAL raster, PNG e apresentação. Só então definir a próxima otimização de abertura. Não há comparação controlada com QGIS. Dependências vulneráveis já sinalizadas pelo build continuam pendentes de atualização.
