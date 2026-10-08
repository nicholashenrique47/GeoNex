# Exact palette PNG: adaptive index staging

## Change and bounds

`ExactPalettePngEncoder` previously read RGBA twice: palette validation,
then palette lookup and scanline encoding. Dense parcel frames now retain
the exact one-byte indexes during validation and compress those bytes in
64 KiB blocks. No colors are reduced or approximated.

The existing 16-row probe enables staging only when its average constant
run is shorter than 128 pixels. Sparse high-zoom frames retain the original
streaming path. Frames exceeding 256 exact colors still use native PNG.
The complete palette is validated before either path starts compression.

The staging allocation is native, scoped to the encode, and included in
the existing adaptive budget (cache budget / 16, capped at 16 MiB). It is
used only if at least 1 MiB remains for bounded compressed output. Otherwise
the existing one-row path remains available. The compressed stream's
capacity and returned SKData are accounted for separately. Cancellation is
checked per validation row and compression block; all buffers are disposed
on cancellation, fallback or failure. `GEONEX_PALETTE_PNG=0` still disables
the indexed encoder. Print/export encoding is unchanged.

## Measurement, 2026-09-29

Baseline: commit `9857888`, including the previously implemented exact
palette encoder. LOTES.shp: 77,146 features, original SHA-256
`4C202E2D32E9F948AC1D236DE5F97ACA755BAC51EFBF39970DA6A0ECE504C2FC`.
Viewport 3712 x 2312 physical pixels, DPI 2, full final repaint, pan step
64 CSS pixels. Three separate process runs per variant and scale, alternating
variant order, excluding the initial frame: 18 observations per cell.

| CSS scale | HTTP before / after, ms | Encode before / after, ms |
| --- | --- | --- |
| 4 | 67.775 / 57.070 | 38.0365 / 27.4345 |
| 16 | 48.730 / 47.330 | 32.0840 / 31.5755 |
| 64 | 30.250 / 29.150 | 14.3925 / 13.6765 |

These are medians for this fixture, not general FPS or QGIS equivalence.
The substantial improvement is at scale 4 (about 16% HTTP, 28% encode).
Scale 16 rejects the palette; sparse scale 64 retains streaming. Their
small timing differences should be treated as measurement variability.
Every one of the 126 full frames in the alternating comparison matched
the reference with maximum channel delta zero.

Logs: `%TEMP%/GeoNex-palette-adaptive-{1,2,3}-{4,16,64}-{before,after}.log`.
Set `GEONEX_BENCH_DPI=2`, `GEONEX_BENCH_FINAL_PAN=1`,
`GEONEX_BENCH_PAN_STEP=64`, `GEONEX_BENCH_SCALE` to the scale, and invoke
`PerfTest --production-map-metrics <GeoNex.dll> <LOTES.shp> --high-zoom`.

## Verification

- Palette contracts compare native PNG against both 16 MiB and 3 MiB
  policies: RGBA/BGRA, padded rows, alpha 0..255, 1/2/255/256 colors.
- Independent chunk CRC, zlib, scanline and palette-index validation;
  unsampled 257th color, compressed-output budget, dense-noise rejection,
  unsupported formats and cancellation.
- Encoded-frame cache, existing frame encoding and HTTP preview contracts.
- Qt's independent PNG decoder compared 21 actual captured frames with
  native reference frames: identical premultiplied RGBA.

Run `--exact-palette-png-contracts`, `--encoded-frame-cache-contracts`,
`--frame-encoding-contracts`, and
`--high-zoom-preview-contracts <GeoNex.dll>` through PerfTest.
