# Vetores: recursos adaptativos e reuso entre escalas

Implementado em 12/09/2026. Etapa incremental de [P0/P2 do plano](arquitetura-vetorial-qgis-arcgis.md), não conclusão de todo o plano. Alterações anteriores preservadas; raster, compositor e fontes originais não foram modificados.

## Implementação

- `VectorResourcePolicy`: orçamento de cache limitado ao menor entre RAM física/32, RAM disponível/8 e 512 MiB. Abaixo de 256 MiB disponíveis, desativa retenção. Consulta memória física do Windows a cada dois segundos, durante acessos; fallback conservador baseado no limite do GC em outras plataformas.
- `RenderPathCache`: ajusta o orçamento em leituras/escritas e libera prévias antes dos resultados finais. Snapshots em uso continuam válidos via copy-on-write. Substitui ordenação LINQ na expulsão por varredura sem alocações. O orçamento estima armazenamento de paths; não limita RSS, índices, buffers de desenho, seleção ou outros caches do processo. Não faz coleta forçada do GC.
- CPU: elimina a regra específica de um modelo de processador. Leitura de metadados e índice C++ usam limite conservador por operação, baseado em CPUs disponíveis ao processo, RAM livre e quantidade de feições. Padrão de aproximadamente metade dos processadores, teto 16; reserva um processador quando possível; fontes pequenas usam um worker. Não é um autotuner nem coordena todos os subsistemas do aplicativo.
- Reuso entre escalas: somente paths finais reprojetados completos, sem LOD/compactação, podem ser reutilizados em escalas diferentes. Cobertura espacial continua obrigatória; saltos além de 4× ou abaixo de 0,25× provocam reconstrução. Prévias e paths nativos simplificados continuam vinculados à escala exata. A geometria é redesenhada na nova escala, não se amplia uma imagem rasterizada.
- `ShapefileIndexReader`: lê SHX em blocos de 64 KiB, removendo o buffer temporário proporcional ao arquivo inteiro. O array de offsets continua necessário. Em 7,8 milhões de registros, o buffer eliminado teria aproximadamente 59,5 MiB antes do arredondamento do pool; esse tamanho é uma estimativa, não um dataset medido nesta etapa. A leitura preserva ordem/FIDs e valida limites, comprimentos e sobreposição de registros. SHX danificado gera erro, em vez de publicar silenciosamente só parte das feições. Falhas nessa fase liberam os mapeamentos.

Configuração opcional: `GEONEX_VECTOR_CACHE_MB=0` desativa retenção; valores positivos continuam sujeitos ao teto de memória. `GEONEX_INDEX_WORKERS` permanece disponível, agora limitado por CPU/RAM e tamanho da tarefa. Configuração de raster/GDAL permanece intacta.

## Verificação

- Debug e Release: compilados, zero erros, 191 avisos existentes em cada build completo. Persistem alertas de dependências (`NU1603`, `NU1903` para Newtonsoft.Json/SQLite); não foram corrigidos nesta etapa.
- Contratos: 210 combinações simuladas de CPU/RAM; pressão, recuperação, configuração inválida, LRU, concorrência, snapshots, 30 reusos entre escalas e isolamento da qualidade.
- SHX: 20.003 registros, travessia de blocos, truncamento, sobreposição e offsets maiores que 2 GiB.
- Regressões aprovadas: render nativo, índice, leases, câmera, coordenadas/DPI, geometrias grandes, anéis transformados e JavaScript do mapa.
- Teste carregando a DLL real do aplicativo: leitor abriu a cópia SHP de inundação (8 feições) e LOTES (57.454 feições). Contagens comparadas com GDAL; IDs, existência de geometria e número de atributos verificados na primeira e última feição. Não equivale a validar todos os valores de atributos.
- Na inundação, o construtor de produção com GDAL reprojetou 1.078.031 vértices. Cache da própria DLL versus reconstrução completa: pixels idênticos em três escalas, incluindo preenchimento com transparência e borda.
- Ensaio isolado com o GeoJSON original, 1.078.031 vértices: 30/30 hits de zoom; lookup p50 0,0003 ms, p95 0,0071 ms; armazenamento estimado 24,88 MiB; três escalas pixel a pixel. São tempos somente de consulta ao cache, não FPS nem latência de apresentação. O desenho final continuou custoso (328,76 ms na amostra), e não foi acelerado por esta mudança. A preparação foi medida separadamente de importação/GDAL/WebView/rede.

Reprodução principal:

```powershell
dotnet build PerfTest/PerfTest.csproj -c Release -p:GeoNexBuildNative=false --no-restore
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --vector-resource-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --shx-stream-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --render-path-cache-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --production-vector-smoke <GeoNex.dll-compilado> <arquivo.shp>
```

O smoke de cache requer RAM livre suficiente para reter o path. Nenhum C++ foi alterado; as builds reutilizaram a DLL nativa ABI 4 existente. Não houve teste interativo da UI nem comparação controlada com QGIS/ArcGIS nesta etapa.

## Próximas etapas

P1 continua pendente: provider vetorial sem conversão obrigatória para SHP, preservando tipos de atributos, IDs e geometrias. As melhorias comuns alcançam os formatos atualmente convertidos, mas **não removem o custo/perdas potenciais da conversão GeoJSON→SHP**. P2 ainda exige índices por componente, blocos reprojetados persistentes e orçamento compartilhado mais amplo. P3/P4 continuam necessários para reduzir o desenho de polígonos e a apresentação PNG/WebView; nenhuma promessa de equivalência de desempenho com QGIS/ArcGIS foi demonstrada.
