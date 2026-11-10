"""Read-only QGIS renderer reference; run with the installed python-qgis.bat."""

import argparse
import json
import math
import statistics
import tempfile
import time
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("--center-x", type=float)
    parser.add_argument("--center-y", type=float)
    parser.add_argument("--width", type=int, default=2050)
    parser.add_argument("--height", type=int, default=1350)
    parser.add_argument("--scale", type=float, help="Physical pixels per projected map unit")
    parser.add_argument("--crs", help="Destination CRS; defaults to the source layer CRS")
    parser.add_argument("--fit-zoom", type=float,
                        help="Fit the source extent to 1600x900 CSS pixels, then apply this zoom")
    parser.add_argument("--pan-step", type=float, default=32, help="CSS pixels of pan between frames")
    parser.add_argument("--pan-repeat", type=int, default=0,
                        help="repeat a cycle of N pan positions, matching a cached navigation sequence")
    parser.add_argument("--samples", type=int, default=3)
    parser.add_argument("--measure-png", action="store_true",
                        help="also time in-memory PNG encoding and report render-plus-PNG totals")
    parser.add_argument("--stroke-width", type=float, default=1, help="Physical pixels")
    parser.add_argument("--marker-size", type=float, default=8, help="Point symbol diameter in physical pixels")
    parser.add_argument("--style", choices=("cyan", "geonex", "outline"), default="cyan")
    parser.add_argument("--no-simplification", action="store_true")
    args = parser.parse_args()
    if not (0 < args.width <= 32768 and 0 < args.height <= 32768 and
            args.width * args.height <= 128 * 1024 * 1024 and 1 <= args.samples <= 100 and
            (args.scale is None or (math.isfinite(args.scale) and args.scale > 0)) and
            (args.fit_zoom is None or (math.isfinite(args.fit_zoom) and args.fit_zoom > 0)) and
            math.isfinite(args.pan_step) and args.pan_step >= 0 and
            0 <= args.pan_repeat <= 100 and
            math.isfinite(args.stroke_width) and args.stroke_width >= 0 and
            math.isfinite(args.marker_size) and args.marker_size > 0 and
            (args.center_x is None or math.isfinite(args.center_x)) and
            (args.center_y is None or math.isfinite(args.center_y))):
        parser.error("Invalid viewport, scale, stroke or sample count")

    from qgis.core import (
        Qgis, QgsApplication, QgsCoordinateReferenceSystem, QgsCoordinateTransform, QgsFillSymbol,
        QgsLineSymbol, QgsMapRendererSequentialJob, QgsMapSettings, QgsMarkerSymbol,
        QgsProject, QgsRectangle, QgsSingleSymbolRenderer, QgsVectorLayer, QgsWkbTypes,
    )
    from qgis.PyQt.QtCore import QByteArray, QBuffer, QIODevice, QSize
    from qgis.PyQt.QtGui import QColor

    app = QgsApplication([], False)
    app.initQgis()
    started = time.perf_counter()
    layer = QgsVectorLayer(str(args.source.resolve()), "LOTES", "ogr")
    if not layer.isValid():
        raise ValueError("QGIS could not open the vector source")
    print(json.dumps({"qgis": Qgis.QGIS_VERSION, "features": layer.featureCount(),
                      "source_crs": layer.crs().authid(), "source_extent": layer.extent().toString(10),
                      "load_ms": (time.perf_counter() - started) * 1000}), flush=True)
    fill_color = "0,0,0,0" if args.style == "outline" else (
        "56,189,248,89" if args.style == "geonex" else "0,255,255,25")
    line_color = "0,0,0,255" if args.style == "outline" else (
        "14,165,233,89" if args.style == "geonex" else "0,255,255,200")
    geometry_type = QgsWkbTypes.geometryType(layer.wkbType())
    if geometry_type == QgsWkbTypes.PointGeometry:
        symbol = QgsMarkerSymbol.createSimple({
            "name": "circle", "color": fill_color, "outline_color": line_color,
            "outline_width": str(args.stroke_width), "outline_width_unit": "Pixel",
            "size": str(args.marker_size), "size_unit": "Pixel",
        })
    elif geometry_type == QgsWkbTypes.LineGeometry:
        symbol = QgsLineSymbol.createSimple({
            "line_color": line_color, "line_width": str(args.stroke_width),
            "line_width_unit": "Pixel", "joinstyle": "round", "capstyle": "round",
        })
    else:
        symbol = QgsFillSymbol.createSimple({
            "color": fill_color, "outline_color": line_color,
            "outline_width": str(args.stroke_width), "outline_width_unit": "Pixel", "joinstyle": "round",
        })
    layer.setRenderer(QgsSingleSymbolRenderer(symbol))
    layer.setLabelsEnabled(False)
    if args.no_simplification:
        method = layer.simplifyMethod()
        method.setSimplifyHints(Qgis.VectorRenderingSimplificationFlag.NoSimplification)
        layer.setSimplifyMethod(method)
    settings = QgsMapSettings()
    settings.setLayers([layer])
    destination_crs = QgsCoordinateReferenceSystem(args.crs) if args.crs else layer.crs()
    if not destination_crs.isValid():
        raise ValueError(f"Invalid destination CRS: {args.crs}")
    settings.setDestinationCrs(destination_crs)
    settings.setOutputSize(QSize(args.width, args.height))
    settings.setOutputDpi(96)
    settings.setBackgroundColor(QColor(0, 0, 0, 0))
    layer_extent = layer.extent()
    scale = args.scale
    if scale is None:
        scale = (min(1600 / layer_extent.width(), 900 / layer_extent.height()) * 0.8 * args.fit_zoom
                 if args.fit_zoom is not None else 4.0)
    center_x = args.center_x if args.center_x is not None else (
        (layer_extent.xMinimum() + layer_extent.xMaximum()) / 2 if args.fit_zoom is not None else -5412144)
    center_y = args.center_y if args.center_y is not None else (
        (layer_extent.yMinimum() + layer_extent.yMaximum()) / 2 if args.fit_zoom is not None else -2984383)
    half_width, half_height = args.width / scale / 2, args.height / scale / 2
    settings.setExtent(QgsRectangle(center_x - half_width, center_y - half_height,
                                   center_x + half_width, center_y + half_height))
    visible_extent = settings.visibleExtent()
    transform = QgsCoordinateTransform(layer.crs(), destination_crs, QgsProject.instance())
    projected_layer_extent = transform.transformBoundingBox(layer.extent())
    if not projected_layer_extent.intersects(visible_extent):
        raise ValueError(
            "The benchmark viewport does not intersect the vector layer after CRS transformation: "
            f"layer={projected_layer_extent.toString(10)}, viewport={visible_extent.toString(10)}, "
            f"source_crs={layer.crs().authid()}, destination_crs={destination_crs.authid()}"
        )
    print(json.dumps({"width": args.width, "height": args.height, "physical_scale": scale,
                      "center": [center_x, center_y], "crs": destination_crs.authid(), "style": args.style,
                      "stroke_pixels": args.stroke_width, "simplification_disabled": args.no_simplification,
                      "marker_size_pixels": args.marker_size,
                      "extent": visible_extent.toString(10),
                      "transformed_layer_extent": projected_layer_extent.toString(10)}), flush=True)
    output = Path(tempfile.mkdtemp(prefix="GeoNex-qgis-reference-"))
    render_times, png_times, render_png_times = [], [], []
    for sample in range(args.samples):
        pan_x = (sample % args.pan_repeat if args.pan_repeat else sample) * args.pan_step
        frame_center_x = center_x - pan_x / scale
        settings.setExtent(QgsRectangle(frame_center_x - half_width, center_y - half_height,
                                       frame_center_x + half_width, center_y + half_height))
        started = time.perf_counter()
        job = QgsMapRendererSequentialJob(settings)
        job.start()
        job.waitForFinished()
        elapsed = (time.perf_counter() - started) * 1000
        render_times.append(elapsed)
        errors = [error.message for error in job.errors()]
        if errors:
            raise RuntimeError(errors)
        image = job.renderedImage()
        # Inspect a small thumbnail outside the timed render region so that a
        # CRS/style mistake cannot be reported as an exceptionally fast frame.
        thumbnail = image.scaled(80, 50)
        visible_pixels = 0
        sampled_colors = set()
        for y in range(thumbnail.height()):
            for x in range(thumbnail.width()):
                color = thumbnail.pixelColor(x, y)
                if color.alpha() > 0:
                    visible_pixels += 1
                    sampled_colors.add((color.red(), color.green(), color.blue(), color.alpha()))
        if visible_pixels < 4 or len(sampled_colors) < 2:
            raise RuntimeError(
                "QGIS produced an empty/near-empty vector benchmark frame: "
                f"sample={sample}, visible_thumbnail_pixels={visible_pixels}, "
                f"distinct_colors={len(sampled_colors)}"
            )
        record = {"sample": sample, "pan_x": pan_x, "render_ms": round(elapsed, 3),
                  "width": image.width(), "height": image.height(),
                  "visible_thumbnail_pixels": visible_pixels,
                  "distinct_thumbnail_colors": len(sampled_colors)}
        if args.measure_png:
            started = time.perf_counter()
            encoded = QByteArray()
            buffer = QBuffer(encoded)
            if not buffer.open(QIODevice.OpenModeFlag.WriteOnly):
                raise RuntimeError("QGIS PNG buffer could not be opened")
            try:
                if not image.save(buffer, "PNG"):
                    raise RuntimeError("QGIS PNG encoding failed")
            finally:
                buffer.close()
            png_elapsed = (time.perf_counter() - started) * 1000
            png_times.append(png_elapsed)
            render_png_times.append(elapsed + png_elapsed)
            record["png_ms"] = round(png_elapsed, 3)
            record["render_plus_png_ms"] = round(elapsed + png_elapsed, 3)
        if image.isNull() or not image.save(str(output / f"frame-{sample}.png")):
            raise RuntimeError("Missing QGIS reference image")
        print(json.dumps(record), flush=True)
        del job
    warm_render = render_times[1:]
    summary = {"event": "summary", "render_p50_ms": round(statistics.median(warm_render), 3),
               "render_p95_ms": round(sorted(warm_render)[-1], 3)}
    if args.measure_png:
        warm_png = png_times[1:]
        warm_total = render_png_times[1:]
        summary["png_p50_ms"] = round(statistics.median(warm_png), 3)
        summary["png_p95_ms"] = round(sorted(warm_png)[-1], 3)
        summary["render_plus_png_p50_ms"] = round(statistics.median(warm_total), 3)
        summary["render_plus_png_p95_ms"] = round(sorted(warm_total)[-1], 3)
    print(json.dumps(summary), flush=True)
    scope = "QGIS layer render; no UI, PNG encoding excluded"
    if args.measure_png:
        scope = "QGIS layer render plus in-memory PNG; no UI"
    print(json.dumps({"output": str(output), "scope": scope + "; per-feature styling differs from GeoNex batches"}), flush=True)
    del settings, layer, symbol
    app.exitQgis()


if __name__ == "__main__":
    main()
