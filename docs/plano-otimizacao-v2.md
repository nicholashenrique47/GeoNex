# Plano Arquitetônico Supremo: GeoNex Ultra-Performance Engine V2 (Superando QGIS e ArcGIS Pro)

## 1. Visão Geral e Diagnóstico dos Gargalos Físicos Atuais

O GeoNex já possui uma fundação de altíssimo desempenho baseada em Memory-Mapped Files (MMF), SIMD (AVX2), índice espacial nativo CSR em C++ e VRT Bypass no GDAL.
Este plano estabelece as diretrizes para superar definitivamente os limites de renderização, pintura e I/O de vetores e rasters pesados, superando o QGIS e o ArcGIS Pro.

### Os 5 Gargalos Físicos Alvo

1. **Codificação PNG/WebP + Loopback HTTP a cada Frame:**
   - *Gargalo:* Comprimir em PNG/WebP no C# e descomprimir no Chromium via HTTP consome de 12ms a 35ms em 4K, além de alocações no LOH ao escrever payloads grandes em `HttpResponseStream`.
   - *Solução:* Escrita HTTP chunked via `ArrayPool<byte>` (zero alocação no LOH), cache de frames codificados (`EncodedFrameCache`) e compressão adaptativa.

2. **Rasterização Skia 100% em CPU:**
   - *Gargalo:* Saturação de largura de banda de memória em CPU com centenas de milhares de polígonos.
   - *Solução:* Particionamento espacial multi-thread balanceado (`ParallelPolygonPainter`), deduplicação de geometrias idênticas (`ShapefileRenderCatalog`) e reuso de superfícies (`CachedPreviewImage`).

3. **Lock Global Único no GDAL, Resampling Software Single-Thread e Loop Escalar de Alpha:**
   - *Gargalo:* Ao pedir `GRIORA_Bilinear` no `ReadRaster` de datasets ECW/JP2ECW ou GeoTIFF com pirâmides (`.ovr`), o GDAL desvia do sintetizador wavelet nativo do SDK ERDAS ECW e faz resampling bilinear single-thread em software sobre blocos extras.
   - *Solução:* `RasterDatasetPolicy.SupportsDirectSkiaFilteredRead` (leitura direta na pirâmide/wavelet exata via `NCScbmSetFileView` / blocos `.ovr` + filtragem bilinear nativa no Skia em múltiplos núcleos via `RasterFramePainter`), `GDAL_ECW_CACHE_MAXMEM` e `HardwareGovernor.FillOpaqueAlphaSimd`.

4. **Parsing Nativo Single-Thread por Camada, SMB2 Lease Caching e Page Faults:**
   - *Gargalo:* Abrir arquivos `.shp`/`.dbf` em rede SMB (`X:\`) com `FileShare.ReadWrite | FileShare.Delete` impede que o cliente SMB2 do Windows (`mrxsmb20.sys`) obtenha `SMB2_LEASE_READ_CACHING | SMB2_LEASE_HANDLE_CACHING`, forçando transferência pela rede a cada leitura (`6,8+ s` em 100 Mbps).
   - *Solução:* Detecção automática de rede/UNC (`FastShapeReader.GetOptimalFileShare`) ativando `FileShare.Read` em drives de rede (cache SMB2 em RAM local: **`8.116 ms` → `63,9 ms`**, **127x mais rápido**), `PrefetchVirtualMemory` contíguo, particionamento por blocos contíguos e `ProcessShapeBatchParallelV4`.

5. **Pressão no Garbage Collector (.NET Managed Heap) e Transporte de Tiles Web:**
   - *Gargalo:* Validação de tiles web (`OnlineTileTransport.ContentType`) copiando bytes (`SKData.CreateCopy`) e alocando `new SKBitmap(256, 256)` (256 KB por tile), além de `new Uri(_prefix)` e `MD5` com arrays por tile.
   - *Solução:* Validação zero-allocation com ponteiro fixo (`SKData.Create((IntPtr)ptr, length)`) + buffer `[ThreadStatic]` reutilizável, hash `MD5.HashData` via `stackalloc`, e `ArrayPool<byte>` no transporte HTTP.

---

## 2. Os 6 Pilares Arquitetônicos

### Pilar 1: Hardware Governor (Auto-Tuning por Hardware)
- Detecção topológica de CPU (P-Cores vs E-Cores, tamanho de cache L1/L2/L3 via `GetLogicalProcessorInformationEx`).
- Orçamento elástico de memória via `GCMemoryInfo` e escalonamento adaptativo de caches (`VectorResourcePolicy`).
- Ajuste dinâmico de workers e lotes (`OptimalVectorBatchSize`) para caber no cache L2/L3.

### Pilar 2: Memória Zero-GC, SMB2 Lease Caching e Layout Contíguo
- Detecção inteligente de compartilhamento (`GetOptimalFileShare`) para ativar `SMB2_LEASE_READ_CACHING` em shapefiles de rede (`X:\`) mantendo escrita concorrente em discos locais.
- Interning de categorias no `FastShapeReader`, particionamento contíguo de registros e reutilização de `RasterPixelBuffer` em pans inteiros, pans fracionários e zooms (`CachedPreviewImage` + `RasterRenderTarget`).
- Extração de offsets zero-virtual-dispatch (`NativeFeatureQuery.CopyOffsets` + `CollectionsMarshal.AsSpan`).
- Chamada antecipada de `PrefetchVirtualMemory` (`HardwareGovernor.PrefetchMappedMemory`) para carregar páginas do `.shp` e `.dbf` via DMA.

### Pilar 3: GeoNexNative V4/V5 (C++ SIMD Extremo & Índice CSR Otimizado)
- Kernels SIMD com loop unrolling 4x (1 cache line de 64 bytes por iteração em `ParseAvx2.cpp`).
- Processamento nativo multi-thread (`ProcessShapeBatchParallelV4`) particionado entre P-Cores.
- Varredura de `microGrid` em C# acelerada por SIMD (`IndexOfAnyExcept((byte)0)`).
- Fast-path de célula única no `QueryShapeSpatialIndex` (elimina leitura de `firstCells` em zoom profundo) e ponteiros restritos (`__restrict`).

### Pilar 4: Motor Raster (ECW/GeoTIFF/Web Tiles) & Projeção Rápida
- Bypass de resampling software do GDAL (`RasterDatasetPolicy.SupportsDirectSkiaFilteredRead`): aciona diretamente o decodificador wavelet multi-resolução do SDK ERDAS ECW (`NCScbmSetFileView`) e as pirâmides nativas do GeoTIFF (`.ovr`), delegando a interpolação bilinear para o compositor multi-thread `RasterFramePainter` (até 8 P-Cores).
- Orçamento automático de cache para ECW (`GDAL_ECW_CACHE_MAXMEM`), `GTIFF_DIRECT_IO` e `GTIFF_VIRTUAL_MEM_IO`.
- Preenchimento SIMD `Vector<uint>` para canal Alpha (`HardwareGovernor.FillOpaqueAlphaSimd`).
- Paralelismo em `ProjectionBatchRunner` a partir de `16.384` feições (acelerando em **3x** a reprojeção de metadados de lotes pesados).
- Unrolling 4x em `ProjectedPathGeometry` e `TransformedRingWriter` e aritmética de ponteiros em `WkbSkiaParser.BuildBatchPathWithProjectedCache`.

### Pilar 5: Pipeline de Pintura Categorizada & Reuso de Superfícies
- Catálogo de deduplicação binária de polígonos (`ShapefileRenderCatalog`) ativado a partir de `50.000` feições com hash `ulong` vetorizado.
- Hoisting de `canvas.SetMatrix` e redução de overhead de cancelamento nos loops de chunks categorizados (`LocalMapServer`).
- Zero alocação em `NavigationFramePolicy.TryReuse` via `stackalloc SKPoint[4]`.

### Pilar 6: Transporte & Codificação de Quadros e Web Tiles Ultra-Rápida
- Checagem de opacidade de frame com `Vector<uint>` 4x unrolled (`MapFrameEncoding.HasTransparentPixels`).
- Hash criptográfico one-shot zero-allocation (`SHA256.HashData` em `EncodedFrameCache` e `MD5.HashData` com `stackalloc` em `OnlineTileTransport`).
- Validação de tiles web sem alocação de `SKBitmap` (`OnlineTileTransport.ContentType` com buffer `[ThreadStatic]` e `ArrayPool<byte>`).
- Lookup de paleta indexada com registrador MRU de 1 ciclo (`ExactPalettePngEncoder`).
- Escrita HTTP chunked (`WriteStreamChunked` com `ArrayPool<byte>` de 256 KB) no `LocalMapServer`, eliminando alocações de **67,2 MB no LOH** por frame em rasters 4K/Retina.

---

## 3. Fases de Execução e Status

- **Fase 1 (Concluída — Hardware Governor & DMA/SIMD Foundation):** Topologia de CPU P/E-Cores, `PrefetchVirtualMemory`, `FillOpaqueAlphaSimd`, budgets adaptativos e string interning.
- **Fase 2 (Concluída — C++ Multi-Thread & AVX2 Cache-Line Unrolling):** `ProcessShapeBatchParallelV4`, AVX2 4-point unrolling e varredura SIMD de `microGrid`.
- **Fase 3 (Concluída — Projeção Vetorial & Pintura Paralela):** Unrolling 4x em `ProjectedPathGeometry` e `TransformedRingWriter`, cópia contígua de blocos em `CachedPreviewImage` e `ParallelPolygonPainter`.
- **Fase 4 (Concluída — Codificação de Frame & Transporte HTTP Zero-Copy):** `Vector<uint>` 4x unrolled em `MapFrameEncoding`, MRU em `ExactPalettePngEncoder`, `SHA256.HashData` e escrita otimizada no `LocalMapServer`.
- **Fase 5 (Concluída — Zoom & Arrasto Extremo / Zero-Alloc Navigation):** Reuso de `RasterPixelBuffer` via `RasterRenderTarget` em `CachedPreviewImage.Create`, `stackalloc SKPoint[4]` em `NavigationFramePolicy.TryReuse`, `NativeFeatureQuery.CopyOffsets` e fast-path de célula única em `QueryShapeSpatialIndex`.
- **Fase 6 (Concluída — Otimização Extrema em Produção: Vetores Pesados, ECW 17 GB, GeoTIFF 43 GB e Web Tiles em `X:\`):**
  - **Vetores Pesados (`LOTES_LOTEAMENTO.shp` — 79.891 feições, 17,0 MB `.shp`, 75,9 MB `.dbf` em `X:\`):**
    - `FastShapeReader.GetOptimalFileShare`: ativação de `SMB2_LEASE_READ_CACHING` em caminhos de rede/UNC (`X:\`), reduzindo a leitura completa de `.shp` + `.dbf` após o 1º carregamento de **`8.116,56 ms` para `63,94 ms` (127x mais rápido)**.
    - `ProjectionBatchRunner`: redução do limiar paralelo de `65.536` para `16.384` feições, acelerando `ReprojetarMetadadosEmLotes` de **`227,15 ms` (`workers=1`) para `76,52 ms` (`workers=4`, 3x mais rápido)**.
    - Renderização em zoom alto e múltiplos níveis (`--production-map-metrics`): resposta quente (`response_ms`) entre **`1,76 ms` e `19,66 ms`** com **`alloc_bytes = 0 B`**.
  - **Rasters Pesados ECW (`Guaratuba Ortho.ecw` — 17,1 GB, `195943x387566`, 4 bandas em `X:\`):**
    - `RasterDatasetPolicy.SupportsDirectSkiaFilteredRead` + `GDAL_ECW_CACHE_MAXMEM`: bypass do resampler software single-thread do GDAL, reduzindo o `io_ms` frio em `zoom=8` de **`2.347,92 ms` para `288,84 ms` (8,1x mais rápido)** e o `io_ms` quente para **`42,55–57,93 ms`** (`zoom=1` quente em **`6,85–7,70 ms`**).
    - `LocalMapServer.WriteStreamChunked`: redução de alocações gerenciadas (`alloc_bytes`) por frame de **`67.268.944 B` (67,2 MB no LOH) para `121.368 B` (~120 KB — redução de 550x)**.
  - **Rasters Pesados GeoTIFF (`Guaratuba Ortho.tif` — 43,1 GB, `195943x387566`, 11 overviews em `X:\`):**
    - Leitura direta nas pirâmides `.ovr` com interpolação bilinear multi-thread no Skia: `io_ms` quente em `zoom=8` caiu de **`366,83–571,18 ms` para `55,77–65,07 ms` (6,5x mais rápido)**, e em `zoom=64` caiu para **`55,34–65,58 ms`**.
  - **Web Tiles (`OnlineTileTransport`):**
    - Eliminação de `SKData.CreateCopy`, `new SKBitmap(256, 256)` (256 KB/tile), `new Uri(_prefix)` e arrays de `MD5` a cada tile recebido, substituídos por decodificação direta em buffer `[ThreadStatic]`, `ArrayPool<byte>` e `stackalloc` (`MD5.HashData`).
