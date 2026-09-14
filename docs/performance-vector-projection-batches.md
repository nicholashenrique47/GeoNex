# Reprojeção adaptativa de metadados vetoriais

Continuação de P0/P2, 12/09/2026. Não altera o desenho, os vértices, a ordem das feições, os atributos nem a conversão de formatos. Não implementa GPU ou equivalência de desempenho com QGIS/ArcGIS.

## Entregue

`ProjetoService.ReprojetarMetadadosEmLotes` agora usa `ProjectionBatchRunner`. Continua enviando exatamente os mesmos cinco pontos por feição ao GDAL: quatro cantos do envelope e o centroide existente. O cálculo dos envelopes e a conversão final dos centroides para float permanecem iguais. A limitação preexistente de estimar envelopes reprojetados por quatro cantos permanece; não resolve descontinuidades de projeção/antimeridiano.

- Menos de 131.072 feições: execução serial para evitar overhead de inicialização.
- Acima disso: lotes fixos de até 32.768 feições, sem sobreposição de escritas. Limite recebido da política existente de CPU/RAM e teto de 16 workers.
- Cada worker cria e descarta suas próprias referências espaciais e transformação GDAL. Nenhuma transformação ou buffer de coordenadas é usado simultaneamente por dois workers.
- Buffers X/Y/Z dimensionados por `min(feicoes, 32768) × 5`, com pool e devolução em `finally`. Para oito feições: três arrays de 64 doubles (1,5 KiB), em vez dos três arrays de 262.144 doubles usados anteriormente (6 MiB), considerando o arredondamento observado do pool. Isso descreve buffers temporários, não a RAM total da camada.
- Cancelamento entre lotes e ao terminar; falhas liberam os recursos nativos. Resultados não finitos são rejeitados antes de atualizar aquele lote. A camada somente é publicada depois de toda a preparação terminar; em erro nessa fase, o mapeamento SHP é descartado.
- Log próprio com feições, workers e duração dos metadados.

Esta integração não acrescenta cancelamento à UI de importação; o token está disponível no executor para futuras integrações. O limite de workers é por operação, não um escalonador global de todos os subsistemas.

## Evidências

Máquina de teste: notebook Intel Core i5-1235U, 10 núcleos/12 processadores lógicos; Windows, .NET 10, GDAL 3.12.1, Release. Teste sintético com 262.147 feições / 1.310.735 pontos; transformação EPSG:4326→3857, seis amostras aquecidas por configuração, ordem serial/paralela alternada, tiered compilation desativada. Inclui alocação de saída e criação das transformações. Medianas da última execução: **185,41 ms serial / 68,69 ms com quatro workers**, aproximadamente **2,7× nessa etapa isolada**, não na abertura completa ou no FPS.

Comparação byte a byte das coordenadas com 1/2/4/6 workers, em EPSG:3857 e EPSG:31982: aprovada. Testes adicionais: quantidade processada, ausência de uso concorrente do mesmo handle, descarte dos handles, buffers pequenos, entrada vazia, limites inteiros, cancelamento inicial/final e falha injetada.

Smoke carregando a DLL real: integração de `ProjetoService`, envelopes e centroides da primeira/última feição comparados com GDAL serial, nos arquivos de inundação (8 feições) e LOTES (57.454). Ambos ficam no caminho serial por tamanho; o paralelismo foi exercitado pelo teste sintético. Os testes de geometria final/caches também passaram, com 1.078.031 e 324.191 vértices respectivamente, incluindo pixels em três escalas. Regressões: cache, orçamento, câmera, render nativo, SHX, leases e JavaScript do mapa.

Debug/Release compilados sem erros; os avisos preexistentes e alertas de dependências permanecem. Nenhum C++ alterado; DLL nativa ABI 4 reutilizada. Não houve teste interativo da UI nesta etapa.

```powershell
dotnet build PerfTest/PerfTest.csproj -c Release -p:GeoNexBuildNative=false --no-restore
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --projection-batch-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --production-vector-smoke <GeoNex.dll> <arquivo.shp>
```

## Experimentos rejeitados para preservar a qualidade

Foram testadas seleção de contornos fora da tela e rasterização por faixas independentes. Ambas apresentaram diferenças de pixels e **foram removidas antes da entrega**, sem deixar opções experimentais ativas. A primeira mostrou 1.296 bytes diferentes num caso sintético; a segunda falhou com rotação e no arquivo de inundação. Não interpretar a rejeição destes protótipos como impossibilidade de paralelismo futuro.

O [código de antialiasing do Skia](https://skia.googlesource.com/skia/+/83739ee0da1e/src/core/SkScan_AntiPath.cpp) mostra que propriedades do path participam da seleção do algoritmo de AA. Isso explica por que equivalência geométrica não basta para garantir pixels idênticos; é uma hipótese fundamentada para o primeiro experimento, não um rastreamento do binário utilizado. O isolamento das instâncias de GDAL segue sua [documentação de multithreading](https://gdal.org/en/stable/user/multithreading.html).

## Continuidade e reversão

Os gargalos de desenho e apresentação PNG/WebView continuam pendentes. Para comparar serial/paralelo, definir `GEONEX_INDEX_WORKERS=1` antes de iniciar o aplicativo; essa configuração também afeta os workers de leitura/indexação já existentes. Não usar `git reset` para desfazer a etapa: há alterações anteriores no workspace. A reversão específica consiste em restaurar a chamada serial de metadados preservando as outras otimizações e as verificações de falha.
