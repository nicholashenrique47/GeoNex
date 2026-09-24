# Parallel polygon painting

Validated on 2026-09-22 using the Release GeoNex DLL and LOTES.shp (4,937,409
features), at 4 px/m, DPI 1, with a 2050x1350 navigation image.

The polygon image cache can paint on two or four CPU workers. Each receives the
complete contours whose bounds can touch its region, including stroke/miter padding.
Every worker uses the original full-size bitmap clip, matrix, contour order, fill
rule and paints. The final image copies disjoint regions only after all workers succeed.
No vertex simplification, deduplication, opacity changes or GPU backend is involved.

Clipping canvases directly to bands was rejected: it changed Skia edge coverage.
Filtering complete contours with the original clip produced identical pixels.
That result concerns the original captures below; the later overview investigation
found a row-partition defect and quantization differences, documented at the end.

## Limits and fallback

- Worker count follows the existing CPU/RAM policy, capped at four.
- Persistent image retention stays capped at 64 MiB. Temporary bitmaps, conservative
  path storage and pooled contour buffers share an adaptive working limit: half of
  the vector RAM budget, at most 256 MiB, or the existing image budget if larger.
  The output bitmap and existing cached layers are included. Temporary storage is
  released before publication. Larger frames or memory pressure may stay sequential.
- Small paths, curves, open contours, inverse fills, rotated/perspective matrices,
  special paint effects and insufficient memory use the existing sequential path.
- Partitioning declines a band retaining over 85% of source vertices, avoiding
  repeated painting of large contours that span most of the frame.
- Cancellation or allocation failure publishes no partial image. Workers own their
  paths, paints and bitmaps; the destination is written after successful completion.
- Skia internal rasterization scratch, as in the existing renderer, is not a hard
  process-RSS limit. The policy bounds owned storage and concurrent workers.

## Evidence

Production painting decreased from approximately 273–280 ms to 119–132 ms in the
measured sequence (one loaded sample reached 151 ms). First server response time
decreased from approximately 415 ms to 254 ms. Seven full decoded padded images
matched the previous sequential implementation byte for byte. These are local
measurements of one view, not a universal frame-rate or QGIS-equivalence claim.

`--parallel-polygon-contracts` compares exact RGBA output with sequential drawing:
two/four workers, three DPIs, winding/even-odd, overlapping contours and holes,
fractional translation, hairline/wide/miter strokes, fill-only and border-only.
It also checks CPU/RAM/rotation/curve/open-contour fallbacks, unhelpful spanning
geometry and cancellation without destination mutation.

Polygon image cache contracts, production high-zoom preview, online progressive
production and JavaScript navigation contracts passed. The old sequential frame
captures can be compared with `--production-map-metrics ... --high-zoom`, setting
`GEONEX_BENCH_FINAL_PAN=1` and `GEONEX_BENCH_REFERENCE_DIRECTORY` to those captures.

## HiDPI follow-up, 2026-09-23

The current LOTES.shp is now 16,065,048 bytes with 77,146 features. It cannot be
compared against the earlier 4.9-million-feature frame captures. Fresh baseline
frames from the preceding DLL and the current file match the updated DLL exactly
at DPI 2, across seven complete decoded frames.

To exercise heavy painting without modifying that source, a separate benchmark
reconstructs closed linear contours from the earlier local XY capture: 173,376
points / 29,888 rings, 3712x2312 pixels, scale 8 px/m and 0.25 m stroke width.
The previous 64 MiB total limit declined parallel painting; times were
931/911/919 ms. With the adaptive 256 MiB working limit on this machine, parallel
times were 430/433/428 ms. All decoded pixels were identical between both modes.
This result concerns the captured heavy workload, not the current smaller SHP.

```powershell
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --captured-polygon-metrics <path-0.xy> 3712 2312 8 0.25
```

Use `GEONEX_BENCH_DPI=2` with production map metrics for HiDPI frame comparisons.
Resource contracts cover 210 hardware profiles, pressure, user overrides and the
256 MiB transient ceiling. Cache retention limits and CPU worker limits are unchanged.

## Column partition and overview quality, 2026-09-23

Strokes wider than one physical pixel now use columns, including portrait frames.
Hairlines and one-pixel strokes keep rows, preserving the earlier zoom performance.
Workers still paint complete contours with the original full-image clip, and copy
only their owned pixels. There are no source-coordinate, opacity or memory-limit changes.

The current 77,146-feature LOTES overview at scale 0.25 and DPI 2 exposed a defect
in the previous row partition: its default style differed from sequential painting
by up to 17/255 per channel (38/255 with the cyan diagnostic style). Columns reduce
the difference to at most 1/255 across all seven full padded production frames:
680–691 changed channels out of 34,328,576, RMS 0.00445–0.00449 on the 0–255 scale.
Default-style painting is 134–144 ms in the measured sequence, versus 291–310 ms
sequentially and 167–177 ms in the preceding row implementation's earlier run.
Measurements are local runs subject to system load, not a universal guarantee.

Analytic antialiasing chooses scanlines using segment endpoints and intersections
([Skia's algorithm description](https://raw.githubusercontent.com/google/skia/main/src/core/SkScan_AAAPath.cpp)).
The measured one-level fill variation after contour filtering is accepted explicitly
as 8-bit coverage quantization; it is not described as byte-identical output.
Painting the full original fill eliminated that variation, but increased the old
DPI-1 workload from about 110 ms to 163–195 ms, so that alternative was rejected.

The earlier heavy DPI-2 capture now paints in 265–286 ms, versus approximately
408–433 ms with rows; all its RGBA bytes match the independent sequential reference.
The old DPI-1 capture remains at 109–112 ms warm, also byte-identical.
No source-file size or feature-count comparison is inferred from those captures.

Validation now includes synthetic clipped, rotated parcels in portrait/landscape,
two/four workers, default translucent colors, and physical stroke widths
0/1/1.005/2/3.5. Maximum accepted difference there is one 8-bit level. The preceding
synthetic contracts continue to require exact bytes. Real capture checks also cover
two/four workers and 0/1/2/3.5-pixel strokes; all stayed within that same limit.

New `.gpath` diagnostic captures retain Move/Line/Close verbs and the fill rule.
Legacy `.xy` dumps cannot reliably distinguish implicit closures on the current
overview. No shapefile is rewritten to create these local diagnostic captures.

Both benchmark commands stay strict by default. For the explicitly documented
quantization comparison set `GEONEX_BENCH_PIXEL_TOLERANCE=1`; no higher tolerance
is accepted. Production comparison then checks the full image, including overscan.
Use `GEONEX_BENCH_SCALE=0.25`, `GEONEX_BENCH_DPI=2`, and
`GEONEX_BENCH_FINAL_PAN=1` for the current overview. Generate the sequential reference
with `GEONEX_VECTOR_CACHE_MB=128`, then unset that variable for the parallel run.
For captured geometry use `GEONEX_BENCH_STYLE=default` to reproduce the production
colors, and `GEONEX_BENCH_VERTICAL=0/1` only to diagnose rows versus columns.

## Balanced columns and buffer initialization, 2026-09-23

Equal-width columns left the overview workers with 36,887 / 40,926 / 42,368 /
16,843 vertices. A 256-bin histogram of physical X positions now chooses vertex
quantiles, giving 34,407 / 34,350 / 34,173 / 33,590 vertices in that capture.
This is O(vertices + bins), with one bulk `SKPath.GetPoints` read into a pooled
array, returned before contour partitioning. The existing scratch reservation
covers either the histogram input (at most 16 bytes per vertex with pool rounding)
or the growing contour buffer (24 bytes per vertex); these allocations do not
coexist. The bulk API is available in the pinned
[SkiaSharp 3.119.2 implementation](https://github.com/mono/SkiaSharp/blob/v3.119.2/binding/SkiaSharp/SKPath.cs).

Thin strokes retain their existing row partition. Degenerate quantiles use equal
widths. If quantiles duplicate too many spanning contours or exceed the path
budget, the painter retries equal widths before reducing workers or going
sequential. Worker limits, image retention and transient RAM limits are unchanged.

Each worker now clears only its owned integer pixel region, then restores the
full image clip before either DrawPath call. The supported SrcOver paints have
no filters/shaders or neighboring-pixel reads. Pixels outside that region are
never copied or published. Four workers therefore initialize one image's worth
of pixels instead of four, while retaining the same full-size drawing surfaces.
There is still no destination mutation until all workers finish successfully.

### Measurements against the preceding parallel version

Release, same machine, four workers, 256 MiB total working budget. Two launches
of each version per scene in before/after/after/before order; discard round zero
from each launch. Each value below is the median of four warm measurements,
including allocation, partitioning, painting and copying. Every produced bitmap
was compared against independently drawn sequential pixels.

| Captured workload | Previous parallel | Balanced + regional clear | Reduction | Max channel delta vs sequential |
| --- | ---: | ---: | ---: | ---: |
| Current LOTES overview, 3712×2312, scale 0.5, default style | 116.97 ms | 96.73 ms | 17.3% | 1/255 |
| Earlier heavy capture, 3712×2312, scale 8, cyan style | 270.86 ms | 228.16 ms | 15.8% | 0 |
| Earlier heavy capture, 2050×1350, scale 4, cyan style | 108.24 ms | 105.37 ms | 2.7% | 0 |

The small DPI-1 difference is best treated as preserved performance, not a strong
speedup claim. These are local painting measurements, not browser FPS or proof
of QGIS equivalence. The current source has 77,146 features; earlier heavy
captures remain separate workloads, not altered versions of the current SHP.

The production overview passed all seven complete 34,328,576-channel frames,
including overscan, against the sequential references: maximum delta 1/255.
The first production validation recorded warm draw times of 105.9–110.8 ms.
PNG encoding remained around 84–93 ms in that run and remains a separate cost.
After the tight-budget fallback was added, the final production run again passed
all seven frames, with warm draw times of 102.2–116.0 ms (median 109.30 ms).
The preceding compiled Desktop version measured 122.6–144.2 ms (median 134.08 ms)
in the immediately preceding production run. Full HTTP response medians were
241.89 ms before and 218.75 ms after, excluding each cold first frame.

Validation also includes 16 real-capture configurations: 2/4 workers, default
and cyan styles, physical strokes 0/1/2/3.5; all passed the 1/255 ceiling. Synthetic
contracts exercise dense clusters, empty/degenerate histograms, exact region
coverage, equal-width fallback under a tight path budget, RAM downgrade to two
workers, and cancellation after a worker finishes without publishing any pixels.
The preceding winding/holes, fill/border, clipping, DPI, portrait/landscape and
effect fallback contracts remain active. Image cache, 210 resource profiles,
render path cache, high-zoom preview and online progressive production passed.

An alternative with 16 bands queued on four reusable worker buffers was rejected:
it slowed the heavy captures and changed overview channels by up to 6/255. It is
not installed in production.

`GEONEX_BENCH_ROUNDS=7` optionally extends captured benchmarks; SUMMARY lines
exclude the first round and report each budget/mode separately. The budget label
alone does not imply sequential execution: always inspect `parallel=True/False`.
Local logs for this run are in `%TEMP%\GeoNex-balanced-paint-validation`, and
production frames in `%TEMP%\GeoNex-balanced-paint-production`. The preceding
sources and PerfTest binaries were saved in `%TEMP%\GeoNex-paint-optimization-before`.

Actual input: `C:\Users\Windows 10\Desktop\SHP S\LOTEAMENTOS_LOTES\LOTES.shp`.
SHA-256: `4C202E2D32E9F948AC1D236DE5F97ACA755BAC51EFBF39970DA6A0ECE504C2FC`.
No dataset coordinates, source files, styles, dependencies or native libraries
were changed. To isolate/roll back this optimization, restore only the preceding
`ParallelPolygonPainter.cs` and rebuild; do not reset the unrelated working tree.

## Lossless frame delivery follow-up, 2026-09-23

Painting is unchanged from the balanced-column version above. Removing redundant
closing vertices from the temporary worker paths produced no consistent gain and
was discarded. The next measured saving is in delivery of the painted frame.

For untagged RGBA/BGRA images of at least one megapixel, compressed PNG now samples
16 scattered 256-pixel windows (16 KiB total). It encodes only this small sample
with None and Sub filters and chooses None only when its sample is over 12.5%
smaller. Small/narrow images, color-managed images, unsupported formats and
level-zero PNG retain the prior path. Compression levels, retention limits and
CPU/RAM policy are unchanged. Sampling is deterministic and adds no full-frame
buffer. It changes lossless PNG filtering only, never drawing, alpha or colors.

Two runs per version, in before/after/after/before order, used the current LOTES
overview at DPI 2 and scale 0.25. Twelve warm samples per version, excluding each
first frame, measured median encoding (including the existing pixel hash) of
86.49 → 78.29 ms and full localhost HTTP responses of 223.44 → 213.69 ms.
Painting medians were 110.63 / 108.61 ms; no painting speedup is attributed to this
change. Isolated PNG encoding, including selection, was about 64 ms versus 70 ms
with Sub. Frame 0 decreased from 3,165,200 to 2,602,548 bytes (17.8% smaller);
frames 1 and 2 showed the same reduction.

All seven complete production frames were byte-identical after decoding to the
preceding balanced painter output: zero changed channels out of 34,328,576.
The earlier maximum 1/255 comparison against sequential painting remains valid.
Contracts cover large odd dimensions, padded row storage, RGBA/BGRA,
premultiplied/unpremultiplied/opaque alpha, dense parcels, gradients, color-space
fallback, cache identity/leases/budgets and concurrent disposal. Smooth gradient
fixtures retained Sub. Online progressive production and JavaScript navigation
contracts also passed. These results do not imply a universal filter speedup or
browser FPS gain; the decision is a bounded content heuristic.

Reproduce the isolated measurement with:

```powershell
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --captured-frame-encoding <frame.png>
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --frame-encoding-contracts
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --encoded-frame-cache-contracts
```

The capture benchmark now correctly expects zero payload-cache hits when a stored
PNG exceeds the unchanged 32 MiB limit. This changes the benchmark assertion,
not the cache policy. Logs are in `%TEMP%\GeoNex-filter-validation`; new frames
are in `%TEMP%\GeoNex-filter-production`. Rollback of this delivery optimization
only requires the preceding `MapFrameEncoding.cs` and a rebuild.

## Raster worker initialization, 2026-09-23

The worker bitmap is now initialized with a bounded native zero-fill of only its
owned rows or columns. The previous implementation used a Skia save/clip/clear/
restore sequence. Both approaches leave the full canvas clip intact for path
rasterization; only the owned region is copied after all workers finish. The
paint itself remains unchanged, and the destination is still untouched until the
parallel operation succeeds.

On the same captured overview (3712×2312, default style, four workers, 256 MiB)
the seven-round benchmark produced a warm median of 98.36 ms (range 94.36–112.03)
with direct clearing, compared with 102.49 ms (95.25–105.12) using the Skia clip
clear in the immediately preceding A/B run. The heavy 3712×2312 HiDPI capture
measured 226.55 ms (223.40–554.89; one system-load outlier) versus 228.76 ms
(223.03–234.52). The result is treated as a small local gain, not a universal
benchmark promise. Removing the per-worker raster `Flush` was also measured;
it remains omitted because raster writes are observed only after the worker loop,
all exact contracts pass, and no production frame regressed.

The seven real production frames still stayed at maximum delta 1/255 against the
sequential reference: 689–758 changed channels out of 34,328,576, with no change
to geometry, alpha policy, worker count, memory limits or fallback behavior.
`--parallel-polygon-contracts`, frame encoding, encoded-frame cache and the real
production high-zoom run passed after this change. The source SHP remains read-only.
