"""Deterministic HTTP XYZ tile fixture for matching GeoNex/QGIS benchmarks."""

from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from io import BytesIO
from PIL import Image, ImageDraw
import sys
from threading import Lock


image = Image.new("RGB", (256, 256), (73, 116, 41))
draw = ImageDraw.Draw(image)
for x in range(0, 256, 16):
    draw.rectangle((x, 0, x + 7, 255), fill=(125, 154, 76))
for y in range(0, 256, 14):
    draw.rectangle((0, y, 255, y + 2), fill=(41, 90, 21))
encoded = BytesIO()
image.save(encoded, format="PNG")
tile = encoded.getvalue()
log_lock = Lock()


class Handler(BaseHTTPRequestHandler):
    # Mirror the persistent connections used by OnlineTileTransport and QGIS.
    protocol_version = "HTTP/1.1"

    def do_GET(self):
        self.send_response(200)
        self.send_header("Content-Type", "image/png")
        self.send_header("Content-Length", str(len(tile)))
        self.send_header("Cache-Control", "public, max-age=86400")
        self.end_headers()
        self.wfile.write(tile)

    def log_message(self, format_string, *args):
        with log_lock:
            print(self.path, flush=True)


ThreadingHTTPServer(("127.0.0.1", int(sys.argv[1])), Handler).serve_forever()
