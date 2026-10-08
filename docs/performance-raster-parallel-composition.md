# Raster composition: ECW and TIFF

## Implemented, 2026-09-29

`RasterFramePainter` divides the final raster composition into disjoint physical
scanline bands. Each worker retains the original full destination rectangle,
CSS-to-physical matrix, alpha blending and Medium/High sampling. The image is
marked immutable after GDAL and the temporary canvas finish writing it, so
workers can share source pixels. Destination pixels belong exclusively to the
current HTTP request and are not published until workers finish.

The native surface wrappers share the destination allocation; no full-frame
scratch image is allocated per worker. Concurrency follows the existing adaptive
resource policy, capped at four workers. Frames below one megapixel, one-worker
budgets, nonrectangular clipping and surfaces without CPU pixel access use the
serial path. Rotated map composition retains its existing path.

This helper requires a writable request surface before any snapshot is taken.
Production does not apply antialiased clipping to that surface. Cancellation
is checked before work and after all workers join; canceled partial frames
are never published. Set `GEONEX_RASTER_PARALLEL_COMPOSITE=0` to select serial
composition while retaining the same source freezing and sampling.

Removing the High filter for a nominal 1:1 draw was tested and rejected:
it changed channels by up to 47 levels in the synthetic alpha fixture.
The adopted change preserves filtering.

## Controlled measurement

Actual production DLL and HTTP endpoint, 1600x900 CSS viewport, DPI 2,
3712x2312 physical pixels including overscan, final quality, zoom multiplier 64.
Each request pans 32 CSS pixels to avoid whole-frame and layer-cache hits.
Three process runs per variant, alternating order, excluding each initial
frame: 18 observations per cell. Both variants use the new helper; only
parallel composition differs. These are warm-run medians, not cold-storage
throughput or end-to-end browser FPS.

| Source | Paint serial / parallel, ms | HTTP serial / parallel, ms | IO serial / parallel, ms |
| --- | --- | --- | --- |
| TIFF Ilha | 255.260 / 140.8805 | 562.1075 / 448.7015 | 1.911 / 1.9485 |
| ECW Compress2 over SMB | 258.919 / 143.746 | 753.5785 / 646.8285 | 140.2295 / 145.398 |

Painting improved about 44%; HTTP improved about 20% and 14%, respectively.
Encoding remains material: roughly 252 ms for TIFF and 285–289 ms for ECW
in these focused frames. Network and GDAL cache timing vary independently.

Sources opened with `GA_ReadOnly`, GDAL PAM disabled, no overview creation:

- `Desktop/ORTO/GUARATUBA_ILHA_ORTOFOTO_2022.tif`: 1,892,340,356 bytes,
  42810x41763, four bands, 256x256 blocks, eight overviews.
- `X:/SETOR DE TOPOGRAFIA E GEOPROCESSAMENTO/SIG/ORTOFOTO/ITTI_2022/Compress2.ecw`:
  1,366,084,754 bytes, 100286x180795, three bands, nine internal levels.

The TIFF's bounding-box center is outside useful imagery. Its reproducible
focus is x=0.2, y=0.5 of the source extent; ECW uses x=0.5, y=0.5.
The harness rejects wholly empty focus frames instead of timing them as
representative high-zoom raster painting.

### GDAL read-thread check at zoom 8

Compared the default six GDAL threads with `GEONEX_GDAL_THREADS=1` on both
sources, using seven panned frames at 1600x900 CSS and DPI 1. The first cold
frame is excluded. Warm HTTP median was 348 ms vs 351 ms for TIFF and 91 ms
vs 92 ms for ECW; median RasterIO was 230 ms vs 229 ms for TIFF and 59 ms vs
60 ms for ECW. The pixel output and response sizes were identical within each
source. Thread count did not improve these direct reads, so the adaptive
default remains unchanged.

### GDAL generic block-cache path

`GDAL_FORCE_CACHING=YES` switches driver-specific `RasterIO` to generic
block-by-block reads. On the TIFF zoom 8/64 frames it preserved every decoded
pixel, but six-frame HTTP p50 increased from about 623 to 640 ms at zoom 8 and
105 to 114 ms at zoom 64. On ECW zoom 64, three alternating process pairs
measured 266 ms without the option and 271 ms with it; RasterIO p50 was 141
and 148 ms. A single ECW zoom 8 pair looked faster with the option, but the
zoom 64 repetitions did not confirm a benefit. Because the result is mixed by
format/scale and TIFF regresses, this global option is not enabled.

## Reproduction and validation

Run `PerfTest --production-raster-metrics <GeoNex.dll> <raster>` with:

- `GEONEX_BENCH_DPI=2`;
- `GEONEX_BENCH_RASTER_ZOOMS=64` (default `1,8,64`);
- `GEONEX_BENCH_RASTER_FOCUS_X=0.2` for TIFF, `0.5` for ECW;
- `GEONEX_BENCH_FRAME_DIRECTORY` to save PNGs;
- `GEONEX_BENCH_REFERENCE_DIRECTORY` to compare every decoded channel exactly.

Baseline captures: `%TEMP%/GeoNex-raster-ilha-focus-before` and
`%TEMP%/GeoNex-raster-ecw-before`. First comparisons cover 21 frames each
at zooms 1/8/64. Controlled A/B logs are
`%TEMP%/GeoNex-raster-paint-{ilha,ecw}-{1,2,3}-{0,1}.log`, covering 84 more
frames. All 126 comparisons are pixel-exact against the original renderer.

`--raster-frame-painter-contracts` covers 192 combinations: 1/2/4 workers,
four DPIs including fractional scales, preview/final sampling, reduced raster
resolution, alpha blending over an existing background, integer clips,
empty worker bands, nonrectangular-clip fallback, and cancellation.
The synthetic threshold is one channel level; real reference comparisons
require zero changed channels.

## QGIS comparison on the available ECW and TIFF (2026-10-08)

QGIS 4.2.0 / GDAL 3.13.1 and the GeoNex Release build / GDAL 3.12.1 used the
same 1600x900 CSS viewport, DPI 2, 3712x2312 output, zoom 64, focus fractions,
and six warm pans of 32 CSS pixels. QGIS used cubic-spline resampling; GeoNex
used the medium Skia filter in its direct raster path. The GeoNex HTTP time includes local
request/response and PNG encoding; QGIS reports render and in-memory PNG
encoding separately.

| Source | QGIS render p50, ms | QGIS PNG p50, ms | GeoNex render p50, ms | GeoNex PNG p50, ms | GeoNex HTTP p50, ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| TIFF Ilha | 558.477 | 1215.286 | 39.994 | 209.856 | 264.595 |
| ECW Compress2 over SMB | 541.288 | 5397.841 | 206.327 | 318.328 | 546.227 |

Sample PNG sizes were 4,094,098 bytes QGIS / 10,200,826 bytes GeoNex for TIFF,
and 14,732,228 / 14,666,730 bytes for ECW. Across the six warm frames, decoded
RGB comparison against QGIS measured RMSE/mean absolute error of 2.951/1.809
for TIFF and 4.369/2.896 for ECW (0..255 channel scale). A 4x downsample search
on the first warm frame found no whole-pixel shift. These are visual-difference
metrics, not pixel equality; the GDAL versions and resampling filters differ.

The comparison was extended to zooms 1 and 8 with two warm pans at each level.
The QGIS harness now mirrors the production fit behavior: zoom 1 centers the
full scene, while the focus fraction applies at zooms 8 and 64. A center-pixel
transparency check was replaced with a sampled frame-coverage check, because
the TIFF scene center can legitimately be transparent.

| Zoom | TIFF full-frame RMSE / MAE | TIFF visible-area MAE | ECW full-frame RMSE / MAE | ECW visible-area MAE |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 9.21 / 0.71 | 7.06 | 9.52 / 1.65 | 12.15 |
| 8 | 10.63 / 4.99 | 6.66 | 17.41 / 6.91 | 6.91 |
| 64 | 2.96 / 1.82 | 1.82 | 4.36 / 2.88 | 2.89 |

RMSE and MAE are on decoded RGB channels from 0 to 255. Full-frame values
include transparent pixels; visible-area MAE weights only the overlap where
both renderers produced alpha. The contact sheets show similar scene coverage.
The automated shift search found no whole-pixel offset on the zoom 8 frames
for either source, and on the first warm zoom 64 frame. Zoom 1 was checked by
alpha bounds and contact sheets, without an automated shift search. The zoom 1
benchmark correction is specific to matching the renderer’s full-scene fit,
not a raster-renderer change.

An experimental floating-point GDAL read window on the direct raster path was
compared against the existing integer-window path. On ECW zoom 8 it reduced
visible-area MAE by about 0.22 channel levels across two QGIS frames; on TIFF
zoom 8 it increased MAE by about 0.09. Warm HTTP medians were effectively the
same (about 298 ms ECW, 820 ms TIFF). Because the quality change was small and
not consistent across formats, the experiment was rejected and the integer
window path remains in production. Switching GeoNex bilinear/cubic-spline
resampling produced byte-identical ECW zoom 8 frames, consistent with the
driver selecting an internal overview at that scale.

The adaptive raster PNG path uses the fast lossless setting: on the TIFF frame,
level 3 reduced the sample from 10.20 MB to 9.28 MB but raised median encoding
from about 270 ms to 745 ms; level 6 took about 2.71 s and produced 8.82 MB.
The measured QGIS PNG encoder took 1.22 s on the same TIFF frame, so stronger
compression was not adopted.

This comparison covers the available 1.89 GB TIFF and 1.37 GB ECW only.
Projected/rotated scenes, browser presentation latency, and the separate
17 GB ECW / 43 GB TIFF sources remain unmeasured.

### High Skia filter experiment at zoom 8

The direct raster painter accepts an interaction flag but currently uses the
medium filter in both modes. Temporarily selecting the high filter for final
frames changed TIFF visible-area MAE against QGIS by only -0.005 levels across
two frames (6.676 to 6.671) and raised warm draw time from roughly 28–33 ms to
45–60 ms. ECW MAE worsened on both frames (7.104/6.712 to 8.278/7.912), while
RMSE improved slightly. The filter change was rejected: it costs more and does
not improve both sources. Production remains on the medium filter.

### GDAL RasterIO resampling verification

The environment override already allowed `cubic`, `lanczos`, `average`, and
`mode`, but the direct-read path only translated bilinear and cubic-spline;
other selected methods silently fell back to nearest-neighbour. The mapping is
now complete for every algorithm accepted by the policy.

At zoom 8, DPI 2, TIFF warm HTTP p50 was 821 ms with cubic-spline, 749 ms with
bilinear, 620 ms with average, and 929 ms with Lanczos. Against the same two
QGIS frames, visible-area MAE was 6.676/6.647 for cubic-spline,
6.934/6.905 for bilinear, 7.174/7.145 for average, and 7.651/7.621 for
Lanczos. The faster filters reduce quality relative to the QGIS cubic-spline
reference, so cubic-spline remains the default.

On ECW zoom 8, cubic-spline, Lanczos, and average produced byte-identical
frames across six matching pans. This confirms the resampling choice does not
change those ECW pixels at this scale; timing differences were within the
observed source and SMB variation. The earlier Lanczos trial was invalidated
by the missing enum mapping and is not used as evidence.

A direct TIFF comparison of GDAL `cubic` with the existing `cubicspline` used
six warm zoom 8 pans and the same two QGIS reference frames. HTTP p50 was
603.2 ms for cubic and 604.6 ms for cubic-spline, while visible-area MAE
increased from 6.66 to 7.34. The 1.4 ms timing difference is within run
variation and does not justify the measurable quality loss; cubic-spline stays
the final-render default.

### Lossless PNG for large opaque raster frames

On the TIFF frames (3712x2312, DPI 2), PNG level 0 reduced zoom 8 warm HTTP
p50 from 821 ms to 603 ms and encode p50 from about 281 ms to 45 ms. Payload
grew from 13.7 MB to 34.4 MB. At zoom 64, HTTP p50 fell from 261 ms to 116 ms,
encode p50 from about 205 ms to 39 ms, and payload grew from 10.2 MB to
25.8 MB. All seven decoded frames at both zooms were pixel-exact against their
compressed baselines. Large raster frames now use level 0 when the complete
uncompressed payload fits the transient-memory budget; otherwise they retain
the compressed path. Raster frames get a 48 MB budget ceiling, still bounded
by one thirty-second of currently available memory. Non-raster frames retain
the 32 MB ceiling.

I also measured PNG level 1 on a captured opaque TIFF zoom 64 frame. It was
pixel-exact but took about 265 ms and produced 10.15 MB, versus 47 ms and
25.79 MB at level 0. The production endpoint's level 0 response is about
100–120 ms warm on this host, so level 1 would add more encoding time than it
saves in local transfer; it remains unselected. On constrained or remote
clients, end-to-end transfer may change this tradeoff and is not measured by
the local HTTP harness.

### ECW SDK cache initialization

`ECW_CACHE_MAXMEM` was previously applied after GDAL driver registration, so
the ECW SDK could initialize its cache before seeing GeoNex's memory budget.
Runtime configuration now runs after the native/plugin paths are ready and
before `Gdal.AllRegister()`. An explicit positive environment value is kept;
otherwise the limit is derived from physical and currently available memory.

On the 1.37 GB `Compress2.ecw` over the network share, six warm zoom 8 renders
had HTTP p50 about 293 ms with a 2 GiB cache, 259 ms with 4 GiB, and 268 ms with
8 GiB. At zoom 64 the corresponding p50 values were about 512 ms, 249 ms, and
253 ms. The 4 GiB cap improved these medians by about 12% at zoom 8 and 51% at
zoom 64 compared with 2 GiB; 8 GiB did not improve on 4 GiB. The adaptive
default on this 32.6 GiB machine is about 4.3 GiB, close to the measured
optimum. Network-share timings vary, so the result is specific to this file
and host; the memory-based cap avoids forcing a fixed multi-gigabyte allocation
on smaller machines.

### Large SMB TIFF and ECW comparison (2026-10-08)

The second source pair in `X:/SETOR DE TOPOGRAFIA E GEOPROCESSAMENTO/SIG/ORTOFOTO/IBRF_2025`
contains a 43.10 GB COG TIFF and a 17.14 GB ECW, both 195943x387566 with 256x256
blocks. The TIFF has 11 overviews and JPEG quality 90; the ECW has 10 internal
levels. QGIS 4.2.0 / GDAL 3.13.1 and GeoNex Release / GDAL 3.12.1 used the same
3712x2312 output, DPI 2, focus x=0.85/y=0.2, and seven pans of 32 CSS pixels.
Warm medians exclude the first frame.

| Source / zoom | GeoNex HTTP p50, ms | QGIS render p50, ms | QGIS PNG p50, ms | Visible RGB MAE |
| --- | ---: | ---: | ---: | ---: |
| TIFF / 8 | 422.9 | 356.5 | 1075.7 | 10.31 |
| TIFF / 64 | 463.1 | 846.5 | 3513.2 | 6.35 |
| ECW / 8 | 210.0 | 155.2 | 1082.3 | 9.65 |
| ECW / 64 | 230.0 | 345.1 | 3804.7 | 6.81 |

GeoNex HTTP includes its local request, renderer, lossless PNG encode, and
response; QGIS render and PNG encode are measured separately. GeoNex is faster
than the combined QGIS render-plus-encode path at all four points. QGIS alone
renders the zoom 8 scenes faster, before PNG encoding. RGB MAE is computed only
where both frames are opaque; exact colors differ across GDAL versions and
renderers. A +/-2 pixel shift search found zero shift best on the inspected
zoom 8 frames for both sources.

The lossless raster fast-encode threshold was later lowered from 4 MP to 2 MP
after a full-HD online raster measurement. These 3712x2312 TIFF/ECW frames were
already above the old threshold, so the table's timings and pixel comparisons
are unchanged.

The four-band ECW has `Undefined` color interpretation on band 4. QGIS selects
RGB bands 1/2/3 and no alpha band. GeoNex previously treated band 4 as alpha,
which hid valid imagery. Raster rendering now includes an alpha channel only
when GDAL marks a band as `AlphaBand`; otherwise RGB is made opaque. The ECW
zoom 64 opaque-pixel coverage now matches QGIS on the inspected frames, visible
MAE improved from 7.45 to 6.81, and the lossless frame payload fell from 34.39
MB to 25.79 MB. Warm HTTP p50 remained within measurement noise (about 228 ms
before and 230 ms after). The TIFF's band 4 is explicitly `AlphaBand`; all
seven zoom 64 frames remained pixel-exact after the change.

On this 43 GB TIFF, one GDAL thread versus six changed every pixel by zero and
warm zoom 64 HTTP p50 from 463 ms to 457 ms. A 4 GiB block cache also preserved
all pixels and measured about 457 ms. Both differences are within run noise,
so the adaptive thread and memory budgets remain unchanged.

### LIBERTIFF TIFF reader trial

GDAL 3.12.1 includes the read-only, thread-safe LIBERTIFF driver, but it must
be selected explicitly and ignores TIFF sidecars. On the 1.89 GB Ilha TIFF,
seven warm zoom 8 frames measured about 644 ms p50 through GTiff and 1051 ms
through LIBERTIFF with six decode threads. The first LIBERTIFF frame also
failed exact decoded-pixel comparison. The trial was rejected; production
continues to use GTiff. See the [GDAL LIBERTIFF documentation](https://gdal.org/en/stable/drivers/raster/libertiff.html).

### CRS and source independence of raster PNG fast path

The encoder selects the lossless fast path from the rendered frame's pixel
count and available-memory budget. It does not inspect dataset format, CRS, or
geographic coordinates, so the same rule applies after any successfully
rendered raster reprojection. It does not add GDAL drivers or make an invalid
source/CRS readable.

As a source check, the same 2050x1350 zoom 8 viewport was rendered from the
large Guaratuba GTiff and ECW. Comparing compressed and stored PNG encoding of
each captured frame gave maximum decoded-channel delta 0 for both. Median encode
time fell from 97.4 to 14.4 ms for TIFF and 108.6 to 13.5 ms for ECW; payloads
grew from 4.49/5.31 MB to 11.09 MB. These measurements verify the two drivers
and alpha-bearing frames, not every CRS or GDAL format.
