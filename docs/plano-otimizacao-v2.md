# Plano Arquitetônico Supremo: GeoNex Ultra-Performance Engine V2 (Superando QGIS e ArcGIS Pro)

## 1. Visão Geral e Diagnóstico dos Gargalos Físicos Atuais

O GeoNex já possui uma fundação de altíssimo desempenho baseada em Memory-Mapped Files (MMF), SIMD (AVX2), índice espacial nativo CSR em C++ e VRT Bypass no GDAL.
Este plano estabelece as diretrizes para superar definitivamente os limites de renderização, pintura e I/O de vetores e rasters pesados, superando o QGIS e o ArcGIS Pro.

### Os 5 Gargalos Físicos Alvo

1. **Codificação PNG/WebP + Loopback HTTP a cada Frame:**
   - *Gargalo:* Comprimir em PNG/WebP no C# e descomprimir no Chromium via HTTP consome de 12ms a 35ms em 4K.
   - *Solução:* Zero-Copy Shared Memory (`CoreWebView2SharedBuffer`) + renderização/apresentação direta via WebGL2/WebGPU.

2. **Rasterização Skia 100% em CPU:**
   - *Gargalo:* Saturação de largura de banda de memória em CPU com centenas de milhares de polígonos.
   - *Solução:* Backend SkiaSharp acelerado por GPU (Direct3D 12 / Vulkan) com Hardware Tessellation e MSAA nativo.

3. **Lock Global Único no GDAL e Loop Escalar de Alpha:**
   - *Gargalo:* Contenção no lock global do GDAL e laço escalar for-pixel definindo canal Alpha.
   - *Solução:* GDAL Dataset Pool multi-thread + Raster Tile Cache (512x512) + SKAlphaType.Opaque / SIMD AVX2.

4. **Parsing Nativo Single-Thread por Camada e Page Faults:**
   - *Gargalo:* MicroGrid com 1 byte por pixel (8 MB em 4K) gerando cache thrashing no Cache L2; saltos em disco.
   - *Solução:* Hi-Z Sub-Pixel Bitmask (1 bit/pixel, 1 MB em 4K), prefetching DMA via Win32 `PrefetchVirtualMemory`, paralelismo nativo C++ (`ProcessShapeBatchParallelV5`) e kernels FMA3/AVX-512.

5. **Pressão no Garbage Collector (.NET Managed Heap):**
   - *Gargalo:* Structs com referências gerenciadas (`string`, `SKPath`) impedindo alocação unmanaged/blittable e forçando varreduras no GC Gen2.
   - *Solução:* Layout SoA (Structure of Arrays) 100% blittable, envelopes relativos quantizados e category ID compacto (`ushort`).

---

## 2. Os 6 Pilares Arquitetônicos

### Pilar 1: Hardware Governor (Auto-Tuning por Hardware)
- Detecção topológica de CPU (P-Cores vs E-Cores, tamanho de cache L1/L2/L3).
- Orçamento elástico de memória e VRAM via `GlobalMemoryStatusEx` e DXGI.
- Ajuste dinâmico de workers e blocos para caber no cache L2 por núcleo.

### Pilar 2: Memória Zero-GC e Layout SoA
- Eliminação de referências gerenciadas nas feições em massa.
- Redução de envelope e metadados para 16 bytes alinhados.
- Chamada antecipada de `PrefetchVirtualMemory` para carregar páginas do `.shp` em paralelo via DMA.

### Pilar 3: GeoNexNative V5 (C++ SIMD Extremo & Hilbert Curve)
- Kernels SIMD fundidos com FMA (`(coord - offset) * scale + trans`) e suporte a AVX-512.
- Grade de ocupação compactada em bitmask (1 bit por pixel), residindo 100% no cache L2.
- Pirâmides de geometria em background (`.gnx_lod`) ordenadas pela curva espacial de Hilbert.

### Pilar 4: Motor Raster Tile-Pool Assíncrono
- Pool de handles independentes do GDAL por camada para leitura multi-thread real em SSDs NVMe.
- Cache de tiles 512x512 em nível de pirâmide para pan instantâneo sem reler o frame completo.
- Otimização radiométrica e canal Alpha sem custo de loop escalar.

### Pilar 5: Renderização Híbrida GPU + CPU
- Superfícies Direct3D 12 no SkiaSharp para processamento em hardware de linhas, hachuras e composições.
- Fallback automático e transparente para CPU em máquinas sem GPU dedicada.

### Pilar 6: Transporte Zero-Copy para o Frontend
- Memória compartilhada com WebView2 eliminando encode/decode de imagens.
- WebGL2 no `mapa.js` para upload e apresentação a 60–120 FPS.

---

## 3. Fases de Execução

- **Fase 1 (Quick Wins):** Otimização de canal Alpha, SIMD AVX2/FMA, compressão de bitmask e prefetch de memória.
- **Fase 2 (SoA & C++ Multi-Thread):** Compactação de feições e paralelização nativa em C++.
- **Fase 3 (Raster Tile-Pool):** Pool GDAL por thread e cache de tiles 512x512.
- **Fase 4 (Zero-Copy & GPU):** Memória compartilhada WebView2 e pipeline Direct3D 12.
