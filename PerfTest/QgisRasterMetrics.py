"""Comparable QGIS raster or raster-plus-vector render benchmark for GeoNex.

Run with the matching QGIS Python launcher, for example:
  python-qgis.bat QgisRasterMetrics.py image.ecw --zooms 64 --dpi 2
Pass --vector lots.shp to overlay polygons on the raster. The benchmark uses
the same 1600x900 CSS viewport, 7 pans of 32 CSS px,
focus fractions, and supports matching the renderer's resampling method.
Use --padding-css 0 to render exactly the visible viewport without overscan.
"""

import argparse
import gc
import json
import os
import statistics
import time

from osgeo import gdal
from qgis.PyQt.QtCore import QByteArray, QBuffer, QIODevice, QSize
from qgis.PyQt.QtGui import QColor
from qgis.core import (
    Qgis,
    QgsApplication,
    QgsMapRendererParallelJob,
    QgsMapSettings,
    QgsRasterDataProvider,
    QgsRasterLayer,
    QgsFillSymbol,
    QgsSingleSymbolRenderer,
    QgsVectorLayer,
    QgsRectangle,
)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("raster")
    parser.add_argument("--vector", help="overlay a polygon Shapefile above the raster")
    parser.add_argument("--zooms", default="1,8,64")
    parser.add_argument("--seed-zoom", type=int, default=0,
                        help="render this zoom once before target zooms to reproduce a zoom transition")
    parser.add_argument("--focus-x", type=float, default=0.5)
    parser.add_argument("--focus-y", type=float, default=0.5)
    parser.add_argument("--dpi", type=int, default=2)
    parser.add_argument("--visible-width", type=int, default=1600)
    parser.add_argument("--visible-height", type=int, default=900)
    parser.add_argument("--padding-css", type=int,
                        help="CSS pixels of render padding; defaults to the benchmark's overscan")
    parser.add_argument("--samples", type=int, default=7)
    parser.add_argument("--output-dir")
    parser.add_argument("--measure-png", action="store_true")
    parser.add_argument("--resampling", choices=("bilinear", "cubic-spline"), default="cubic-spline")
    args = parser.parse_args()
    if not 0 <= args.focus_x <= 1 or not 0 <= args.focus_y <= 1:
        parser.error("focus fractions must be in [0, 1]")
    if (args.dpi < 1 or args.dpi > 4 or args.samples < 2 or args.visible_width < 1 or
            args.visible_height < 1 or (args.padding_css is not None and args.padding_css < 0)):
        parser.error("dpi must be 1..4, viewport dimensions positive, and samples at least 2")
    if args.seed_zoom < 0 or args.seed_zoom > 1024:
        parser.error("seed zoom must be 0..1024")

    if args.padding_css is None:
        padding = min(min(args.visible_width, args.visible_height) // 4, int(256 / args.dpi))
        while padding > 0 and (args.visible_width + 2 * padding) * (args.visible_height + 2 * padding) * args.dpi**2 > 16_777_216:
            padding //= 2
    else:
        padding = args.padding_css
        if (args.visible_width + 2 * padding) * (args.visible_height + 2 * padding) * args.dpi**2 > 16_777_216:
            parser.error("requested padding exceeds the benchmark pixel budget")
    css_width = args.visible_width + 2 * padding
    css_height = args.visible_height + 2 * padding
    output_width, output_height = css_width * args.dpi, css_height * args.dpi

    app = QgsApplication([], False)
    prefix = os.environ.get("QGIS_PREFIX_PATH")
    if prefix:
        QgsApplication.setPrefixPath(prefix, True)
    app.initQgis()
    layer = vector = provider = settings = job = image = None
    try:
        started = time.perf_counter()
        layer = QgsRasterLayer(os.path.abspath(args.raster), "raster", "gdal")
        if not layer.isValid():
            raise RuntimeError(f"QGIS could not load raster: {args.raster}")
        provider = layer.dataProvider()
        if args.vector:
            vector = QgsVectorLayer(os.path.abspath(args.vector), "benchmark", "ogr")
            if not vector.isValid():
                raise RuntimeError(f"QGIS could not load vector: {args.vector}")
            vector.setRenderer(QgsSingleSymbolRenderer(QgsFillSymbol.createSimple({
                "style": "no",
                "color": "0,255,255,0",
                "outline_color": "0,255,255,255",
                "outline_width": "0.20",
                "outline_width_unit": "MM",
            })))
        resampling = {
            "bilinear": QgsRasterDataProvider.ResamplingMethod.Bilinear,
            "cubic-spline": QgsRasterDataProvider.ResamplingMethod.CubicSpline,
        }[args.resampling]
        provider.setZoomedInResamplingMethod(resampling)
        provider.setZoomedOutResamplingMethod(resampling)
        open_ms = (time.perf_counter() - started) * 1000
        extent = layer.extent()
        scene_width, scene_height = extent.width(), extent.height()
        zooms = [int(item) for item in args.zooms.split(",")]
        if any(zoom < 1 or zoom > 1024 for zoom in zooms):
            parser.error("zooms must be in [1, 1024]")
        if args.output_dir:
            os.makedirs(args.output_dir, exist_ok=True)

        print(json.dumps({
            "event": "source",
            "qgis": Qgis.QGIS_VERSION,
            "gdal": gdal.VersionInfo("RELEASE_NAME"),
            "driver": provider.name(),
            "raster": os.path.abspath(args.raster),
            "vector": os.path.abspath(args.vector) if args.vector else None,
            "vector_features": vector.featureCount() if vector else 0,
            "size": [provider.xSize(), provider.ySize()],
            "bands": provider.bandCount(),
            "extent": [extent.xMinimum(), extent.yMinimum(), extent.xMaximum(), extent.yMaximum()],
            "renderer": layer.renderer().type(),
            "resampling": args.resampling,
            "open_ms": round(open_ms, 3),
            "pixels": [output_width, output_height],
            "visible_css": [args.visible_width, args.visible_height],
            "padding_css": padding,
            "focus": [args.focus_x, args.focus_y],
            "zoom_one_uses_scene_center": True,
        }, separators=(",", ":")), flush=True)

        settings = QgsMapSettings()
        settings.setLayers([vector, layer] if vector else [layer])
        settings.setDestinationCrs(layer.crs())
        settings.setOutputSize(QSize(output_width, output_height))
        settings.setOutputDpi(96 * args.dpi)
        settings.setBackgroundColor(QColor(0, 0, 0, 0))
        settings.setFlag(QgsMapSettings.Antialiasing, False)

        def extent_for(zoom, pan_css=0):
            # Match the GeoNex harness: zoom 1 fits the whole scene, while a
            # requested focus fraction selects the center for zoomed frames.
            focus_x = 0.5 if zoom == 1 else args.focus_x
            focus_y = 0.5 if zoom == 1 else args.focus_y
            center_x = extent.xMinimum() + scene_width * focus_x
            center_y = extent.yMaximum() - scene_height * focus_y
            scale = min(args.visible_width / scene_width, args.visible_height / scene_height) * 0.8 * zoom
            frame_center_x = center_x - pan_css / scale
            frame_width, frame_height = css_width / scale, css_height / scale
            return QgsRectangle(
                frame_center_x - frame_width / 2,
                center_y - frame_height / 2,
                frame_center_x + frame_width / 2,
                center_y + frame_height / 2,
            )

        if args.seed_zoom:
            settings.setExtent(extent_for(args.seed_zoom))
            job = QgsMapRendererParallelJob(settings)
            started = time.perf_counter()
            job.start()
            job.waitForFinished()
            image = job.renderedImage()
            seed_ms = (time.perf_counter() - started) * 1000
            if image.isNull() or image.width() != output_width or image.height() != output_height:
                raise RuntimeError("QGIS returned an invalid seed frame")
            print(json.dumps({
                "event": "seed", "zoom": args.seed_zoom,
                "render_ms": round(seed_ms, 3),
            }, separators=(",", ":")), flush=True)
            image = None
            job = None

        for zoom in zooms:
            render_ms, encode_ms, render_png_ms = [], [], []
            for sample in range(args.samples):
                pan_x = sample * 32
                settings.setExtent(extent_for(zoom, pan_x))
                job = QgsMapRendererParallelJob(settings)
                started = time.perf_counter()
                job.start()
                job.waitForFinished()
                image = job.renderedImage()
                render_ms.append((time.perf_counter() - started) * 1000)
                if image.isNull() or image.width() != output_width or image.height() != output_height:
                    raise RuntimeError("QGIS returned an invalid frame")
                visible_left = padding * args.dpi
                visible_top = padding * args.dpi
                visible_width = args.visible_width * args.dpi
                visible_height = args.visible_height * args.dpi
                sample_columns, sample_rows = 64, 36
                covered = 0
                sampled_colors = set()
                for gy in range(sample_rows):
                    y = visible_top + int((gy + 0.5) * visible_height / sample_rows)
                    for gx in range(sample_columns):
                        x = visible_left + int((gx + 0.5) * visible_width / sample_columns)
                        color = image.pixelColor(x, y)
                        if color.alpha() == 0:
                            continue
                        covered += 1
                        sampled_colors.add((color.red(), color.green(), color.blue()))
                coverage = covered / (sample_columns * sample_rows)
                if coverage < 0.05 or len(sampled_colors) < 64:
                    raise RuntimeError(
                        "QGIS focus has insufficient visible raster content: "
                        f"coverage={coverage:.1%}, sampled_colors={len(sampled_colors)}; "
                        "choose another focus fraction"
                    )
                if sample > 0 and args.output_dir:
                    image.save(os.path.join(args.output_dir, f"zoom-{zoom}-frame-{sample}.png"), "PNG")

                record = {
                    "event": "frame", "zoom": zoom, "sample": sample,
                    "render_ms": round(render_ms[-1], 3),
                    "visible_alpha_coverage": round(coverage, 4),
                    "sampled_colors": len(sampled_colors),
                }
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
                    encode_ms.append((time.perf_counter() - started) * 1000)
                    render_png_ms.append(render_ms[-1] + encode_ms[-1])
                    record["png_ms"] = round(encode_ms[-1], 3)
                print(json.dumps(record, separators=(",", ":")), flush=True)
                image = None
                job = None

            warm_render = render_ms[1:]
            summary = {
                "event": "summary", "zoom": zoom,
                "render_p50_ms": round(statistics.median(warm_render), 3),
                "render_p95_ms": round(sorted(warm_render)[-1], 3),
            }
            if args.measure_png:
                warm_encode = encode_ms[1:]
                warm_render_png = render_png_ms[1:]
                summary["png_p50_ms"] = round(statistics.median(warm_encode), 3)
                summary["png_p95_ms"] = round(sorted(warm_encode)[-1], 3)
                summary["render_plus_png_p50_ms"] = round(statistics.median(warm_render_png), 3)
                summary["render_plus_png_p95_ms"] = round(sorted(warm_render_png)[-1], 3)
            print(json.dumps(summary, separators=(",", ":")), flush=True)
    finally:
        image = job = settings = provider = vector = layer = None
        gc.collect()
        app.exitQgis()


if __name__ == "__main__":
    main()
