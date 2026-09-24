"""Read-only QGIS renderer reference; run with the installed python-qgis.bat."""

import argparse
import json
import math
import tempfile
import time
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("--center-x", type=float, default=-5412144)
    parser.add_argument("--center-y", type=float, default=-2984383)
    parser.add_argument("--width", type=int, default=2050)
    parser.add_argument("--height", type=int, default=1350)
    parser.add_argument("--scale", type=float, default=4, help="Physical pixels per projected map unit")
    parser.add_argument("--samples", type=int, default=3)
    parser.add_argument("--stroke-width", type=float, default=1, help="Physical pixels")
    parser.add_argument("--style", choices=("cyan", "geonex"), default="cyan")
    parser.add_argument("--no-simplification", action="store_true")
    args = parser.parse_args()
    if not (0 < args.width <= 32768 and 0 < args.height <= 32768 and
            args.width * args.height <= 128 * 1024 * 1024 and 1 <= args.samples <= 100 and
            math.isfinite(args.scale) and args.scale > 0 and
            math.isfinite(args.stroke_width) and args.stroke_width >= 0 and
            math.isfinite(args.center_x) and math.isfinite(args.center_y)):
        parser.error("Invalid viewport, scale, stroke or sample count")

    from qgis.core import (
        Qgis, QgsApplication, QgsCoordinateReferenceSystem, QgsFillSymbol,
        QgsMapRendererSequentialJob, QgsMapSettings, QgsRectangle, QgsVectorLayer,
    )
    from qgis.PyQt.QtCore import QSize
    from qgis.PyQt.QtGui import QColor

    app = QgsApplication([], False)
    app.initQgis()
    started = time.perf_counter()
    layer = QgsVectorLayer(str(args.source.resolve()), "LOTES", "ogr")
    if not layer.isValid():
        raise ValueError("QGIS could not open the vector source")
    print(json.dumps({"qgis": Qgis.QGIS_VERSION, "features": layer.featureCount(),
                      "load_ms": (time.perf_counter() - started) * 1000}), flush=True)
    symbol = QgsFillSymbol.createSimple({
        "color": "56,189,248,89" if args.style == "geonex" else "0,255,255,25",
        "outline_color": "14,165,233,89" if args.style == "geonex" else "0,255,255,200",
        "outline_width": str(args.stroke_width), "outline_width_unit": "Pixel", "joinstyle": "round",
    })
    layer.renderer().setSymbol(symbol)
    layer.setLabelsEnabled(False)
    if args.no_simplification:
        method = layer.simplifyMethod()
        method.setSimplifyHints(Qgis.VectorRenderingSimplificationFlag.NoSimplification)
        layer.setSimplifyMethod(method)
    settings = QgsMapSettings()
    settings.setLayers([layer])
    settings.setDestinationCrs(QgsCoordinateReferenceSystem("EPSG:3857"))
    settings.setOutputSize(QSize(args.width, args.height))
    settings.setOutputDpi(96)
    settings.setBackgroundColor(QColor(0, 0, 0, 0))
    half_width, half_height = args.width / args.scale / 2, args.height / args.scale / 2
    settings.setExtent(QgsRectangle(args.center_x - half_width, args.center_y - half_height,
                                   args.center_x + half_width, args.center_y + half_height))
    print(json.dumps({"width": args.width, "height": args.height, "physical_scale": args.scale,
                      "center": [args.center_x, args.center_y], "style": args.style,
                      "stroke_pixels": args.stroke_width, "simplification_disabled": args.no_simplification,
                      "extent": settings.visibleExtent().toString(10)}), flush=True)
    output = Path(tempfile.mkdtemp(prefix="GeoNex-qgis-reference-"))
    for sample in range(args.samples):
        started = time.perf_counter()
        job = QgsMapRendererSequentialJob(settings)
        job.start()
        job.waitForFinished()
        elapsed = (time.perf_counter() - started) * 1000
        errors = [error.message for error in job.errors()]
        if errors:
            raise RuntimeError(errors)
        image = job.renderedImage()
        if image.isNull() or not image.save(str(output / f"frame-{sample}.png")):
            raise RuntimeError("Missing QGIS reference image")
        print(json.dumps({"sample": sample, "render_ms": elapsed,
                          "width": image.width(), "height": image.height()}), flush=True)
        del job
    print(json.dumps({"output": str(output), "scope": "QGIS layer render; no UI, PNG encoding excluded; per-feature styling differs from GeoNex batches"}), flush=True)
    del settings, layer, symbol
    app.exitQgis()


if __name__ == "__main__":
    main()
