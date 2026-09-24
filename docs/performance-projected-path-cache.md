# Reuse projected polygon coordinates during high-zoom pan

Validated on 2026-09-22 with LOTES.shp (4,937,409 features), the Release application
DLL, 1600x900 CSS pixels, DPI 1, scale 4 px/m and a 2050x1350 navigation image.

Previously, changing the local camera origin invalidated the precise SKPath even
inside its retained spatial coverage. Each final frame repeated the spatial query
and GDAL projection. The renderer now optionally retains the projected **double**
coordinates and ring order and converts them directly to the new local origin.
It preserves every vertex and ring; preview simplification remains separate.

The cache shares the existing adaptive RAM budget and invalidation. Extra storage
is capped at one quarter of that budget, at most 32 MiB per capture. If allocation,
budget, coordinate validity or oversized feature handling prevents capture, the
existing renderer and SKPath cache remain available. Print uses the existing path.
Camera coverage and world offsets must match; large zoom changes rebuild normally.

## Measurements and limits

- Previous final pan: geometry 45–54 ms. Reused doubles: 6.8–8.6 ms after the first
  reuse (13.4 ms). Returning to the original camera reuses SKPath directly.
- Final HTTP frames remained around 294–304 ms in the measured warm sequence;
  polygon painting still costs approximately 266–270 ms. This does not solve the
  first-paint bottleneck or establish QGIS-equivalent frame rates.
- Capturing doubles added about 8.8 MB of managed allocations and approximately
  10–15 ms to the first geometry build in this case. Capture is bounded and optional.
- All displayed pixels matched the previous build for seven tested frames. The
  outer overscan differed in 19–34 pixels, at x=0–71 (the visible area starts at
  x=225). Fresh spatial queries include different offscreen contours, affecting
  Skia's edge coverage. Full padded-image byte identity is **not** claimed.
- Translating cached float geometry was investigated first. Retaining doubles
  avoids cumulative float rounding and reproduces fresh vertex conversion exactly.

## Reproduce

Build Release with `-p:GeoNexBuildNative=false`. Run the same PerfTest harness once
against the previous DLL, setting `GEONEX_BENCH_FRAME_DIRECTORY` to a temporary
directory, then against the current DLL with `GEONEX_BENCH_REFERENCE_DIRECTORY`
pointing to that directory. Set `GEONEX_BENCH_FINAL_PAN=1` for both runs:

```powershell
dotnet PerfTest/bin/Release/net10.0/PerfTest.dll --production-map-metrics <GeoNex.dll> <LOTES.shp> --high-zoom
```

The reference comparison fails on any changed displayed pixel and reports padded
image differences separately. The benchmark invalidates only raster presentation
between requests so the final polygon renderer runs despite whole-frame caching.

Contracts passed: projected geometry and path cache (vertices, winding, duplicates,
DPI, rotation, input ownership, coverage, cancellation, edit invalidation and RAM
fallback); render precision; polygon image cache; production high-zoom preview;
production SHP/GDAL smoke on 32 source features; JavaScript navigation contracts.
Both application and PerfTest Release builds completed without errors.

## Packed storage follow-up

Projected doubles now occupy bounded blocks instead of two arrays per ring. Ring
records store block offsets; rebuilding rents one point buffer for the entire path.
Allocated block capacity and ring-table growth count toward the same RAM limit.
Block boundaries never split a contour, including rings larger than a default block.

On the same LOTES frame, measured first-frame managed allocations decreased from
9,553,432 to 6,809,592 bytes (29%). Warm pan geometry rebuilding decreased from
6.8–8.6 ms to 2.3–2.7 ms; first reuse was 7.1 ms. No first-paint latency reduction
is claimed: painting remained approximately 273–280 ms in this run.
Seven complete padded images matched the previous double-cache implementation
byte for byte after decoding. Added contracts cover exact block fills, subsequent
blocks, oversized rings, mixed open/closed contours and builder ownership.
