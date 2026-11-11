# Relatório de Retomada e Otimizações de Desempenho (GeoNex vs QGIS)

Data: 10 de Novembro de 2026 (Atualizado em 11 de Novembro de 2026)

## 1. Contexto da Retomada
Continuação do trabalho iniciado pelo Codex para otimizar os componentes críticos de renderização do **GeoNex** visando superar o QGIS em throughput e latência perceptível, preservando exatidão estrita de pixels e conformidade com todos os contratos de `PerfTest`.

O Codex havia implementado:
- Expansão do pintor paralelo de polígonos para até 8 bandas/workers.
- Aumento adaptativo do teto transitório de memória para 512 MiB em [`GeoNex/Services/VectorResourcePolicy.cs`](file:///C:/Users/Windows%2010/Desktop/GeoNex-main/GeoNex/Services/VectorResourcePolicy.cs).

---

## 2. Otimizações Realizadas

### 2.1 Vetores Pesados (Polígonos e Linhas)
- **[`GeoNex/Services/ParallelPolygonPainter.cs`](file:///C:/Users/Windows%2010/Desktop/GeoNex-main/GeoNex/Services/ParallelPolygonPainter.cs)**:
  - **Composição Paralela de Bandas (`CopyRegion`)**: Paralelização da cópia final das fatias renderizadas (`Parallel.For(0, bands, ...)`), garantindo que o buffer de destino (`destination`) permaneça imutável se qualquer worker sofrer cancelamento ou falha.
  - **Remoção de Divisões em Loop Crítico (`Boundaries`)**: Pré-cálculo de `invDimBuckets = (double)buckets / dimension` fora do laço de vértices.
  - **Eliminação de Checagens Redundantes de NaN (`Partition`)**: Substituição de `Math.Min` e `Math.Max` por condicionais diretas em vértices já validados como finitos, otimizando o pipeline vetorial da CPU.
- **[`PerfTest/VectorResourceContracts.cs`](file:///C:/Users/Windows%2010/Desktop/GeoNex-main/PerfTest/VectorResourceContracts.cs) & [`PerfTest/ParallelPolygonContracts.cs`](file:///C:/Users/Windows%2010/Desktop/GeoNex-main/PerfTest/ParallelPolygonContracts.cs)**:
  - Ajuste das asserções de limite de alocação transitória (`<= 512 * mb`).
  - Expansão da matriz de testes com workers `[2, 4, 8]`, cobrindo resoluções em múltiplos DPIs, orientações paisagem/retrato ímpares e regras de winding/buracos.

### 2.2 Raster Local (ECW / GeoTIFF / Imagens Monocromáticas e Paletizadas)
- **[`GeoNex/Services/RasterDatasetPolicy.cs`](file:///C:/Users/Windows%2010/Desktop/GeoNex-main/GeoNex/Services/RasterDatasetPolicy.cs)**:
  - **Expansão Otimizada de Pixels**: `ExpandGrayInPlace`, `ExpandGrayAlphaInPlace` e `ExpandPaletteInPlace` anotados com `[MethodImpl(MethodImplOptions.AggressiveOptimization)]`.
  - **Escrita Empacotada de 32-bit com Loop Unrolling 4x**: Conversão direta para ARGB através de máscaras bitwise (`0xFF000000u | (gray * 0x00010101u)`), minimizando acessos a byte isolado.
  - **Lookup Table Stack-Allocated**: Para rasters paletizados de 8-bit, uso de `stackalloc uint[256]` pré-calculado com canais empacotados.

### 2.3 WebTiles (XYZ / WMS / Basemaps Online)
- **[`GeoNex/Services/OnlineTileTransport.cs`](file:///C:/Users/Windows%2010/Desktop/GeoNex-main/GeoNex/Services/OnlineTileTransport.cs)**:
  - **Cache em Memória Lock-Free e Zero-Allocation**: `CachedTile` convertido em classe selada imutável que referencia o `Tile ResponseTile` pronto, com `LastAccess` sincronizado via `Volatile.Write`. O caminho quente `TryGetValidatedTile` tornou-se livre de locks (`lock (source.Gate)`) e de alocações na leitura de hits.
  - **Montagem de URLs Sem Alocação Intermediária (`BuildTileUrl`)**: Substituição de múltiplas cadeias de `.Replace()` por montagem via `ValueStringBuilder` em buffer de pilha (`stackalloc char[512]`).
  - **Validação Rápida de Assinatura de Mídia (`ContentType`)**: Validação de magic bytes e delimitadores de container (PNG, JPEG, WebP, GIF) diretamente no array de bytes antes de instanciar decodificadores nativos (`SKData`/`SKCodec`).
  - **Cache Seguro de Criação de Diretórios (`Source.CreatedDirs`)**: Evita chamadas de I/O em disco desnecessárias para checar ou criar subpastas hexadecimais de cache.
- **[`GeoNex/Services/ProgressiveOnlineRaster.cs`](file:///C:/Users/Windows%2010/Desktop/GeoNex-main/GeoNex/Services/ProgressiveOnlineRaster.cs) & [`GeoNex/Services/OnlineRasterFrameReader.cs`](file:///C:/Users/Windows%2010/Desktop/GeoNex-main/GeoNex/Services/OnlineRasterFrameReader.cs)**:
  - Eliminação de chamadas redundantes a `XDocument.Parse(xml)` na verificação de viabilidade de mosaico completo e na configuração de pipelines com transporte compartilhado.

---

## 3. Caixa de Ferramentas de Geoprocessamento (Fases 1 e 2)
Integrada e sincronizada através de [`GeoNex/Components/Pages/Home.Geoprocessing.cs`](file:///C:/Users/Windows%2010/Desktop/GeoNex-main/GeoNex/Components/Pages/Home.Geoprocessing.cs) e [`GeoNex/Services/GeoprocessingEngine.cs`](file:///C:/Users/Windows%2010/Desktop/GeoNex-main/GeoNex/Services/GeoprocessingEngine.cs), cobrindo 9 categorias e 24+ algoritmos espaciais validados com Shapefiles reais:

1. **Superposição Topológica (Overlay)**: Recortar (Clip), Intersecção com fusão de tabelas, Diferença (Erase), Diferença Simétrica (XOR), União Plena e Atualização (Patch).
2. **Agregação e Fusão**: Dissolver Geometrias (global e por atributo).
3. **Geometrias Construtivas**: Zonas de Amortecimento (Buffer) com terminações retas/curvas, Envoltória Convexa (Convex Hull), Centroides e Polo de Inacessibilidade (Polylabel).
4. **Proximidade e Relevo**: Triangulação de Delaunay (TIN), Polígonos de Voronoi (Thiessen) e Vizinho Mais Próximo (Clark-Evans).
5. **Generalização Cartográfica**: Douglas-Peucker topológico, Visvalingam-Whyatt e Suavização de Chaikin.
6. **Decomposição e Conversão**: Explodir Multipartes, Agrupar por Atributo, Polígonos para Linhas, Poligonização de Linhas (Polygonizer) e Extração Analítica de Vértices.
7. **Segmentação Linear e Partição**: Quebra em nós/intersecções, Estaqueamento por comprimento e Partição de polígonos por linhas delimitadoras.
8. **Malhas Espaciais e Amostragem**: Grades Cartesianas (Grids), Grades Hexagonais (Hexbins), Amostragem Poisson Disk e Amostragem Regular.
9. **Estatísticas Zonais & Joins**: Contagem e soma de pontos em polígonos, Densidade linear e Junção Espacial Avançada (Spatial Join).

- **Validação de Testes (`GeoprocessingCore`)**: **18 módulos e 386 asserções com 100% de sucesso**.

---

## 4. Resultados dos Contratos e Testes de Regressão

Todos os testes foram executados na compilação `Release` (`net10.0` / `win-x64`) com 0 erros:
- `--vector-resource-contracts`: **PASS** (210 perfis de hardware validados)
- `--parallel-polygon-contracts`: **PASS** (fidelidade exata de pixels em 2, 4 e 8 workers)
- `--polygon-image-cache-contracts`: **PASS**
- `--projection-batch-contracts`: **PASS** (1.310.735 pontos transformados; paralelo 4x com ganho de ~3.5x no throughput)
- `--raster-contracts` & `--raster-frame-painter-contracts`: **PASS** (192 casos, fidelidade visual delta <= 1)
- `--online-transport-contracts` & `--online-progressive-contracts`: **PASS** (zero requisições HTTP redundantes no cache revisitado)
- `--frame-encoding-contracts`: **PASS**

---

## 5. Binário Executável Gerado

- **Caminho do Executável**: [`GeoNex/bin/Release/net10.0-windows10.0.19041.0/win-x64/GeoNex.exe`](file:///C:/Users/Windows%2010/Desktop/GeoNex-main/GeoNex/bin/Release/net10.0-windows10.0.19041.0/win-x64/GeoNex.exe)
- **Modo**: `Release` (WinUI / .NET MAUI unpackaged, win-x64)
- **Status da Compilação**: 0 Erros
