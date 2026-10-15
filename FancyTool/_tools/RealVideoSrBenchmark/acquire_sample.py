"""Extract eight seconds of the CC BY 3.0 Tears of Steel 4K master.

Blender wraps its MOV in a ZIP_STORED archive. A loopback range proxy exposes the
MOV without downloading the entire 6.7 GB archive. No media is uploaded.
"""
import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
from pathlib import Path
import re
import struct
import subprocess
import threading
import requests

SOURCE = "https://download.blender.org/demo/movies/ToS/tearsofsteel_4k.mov.zip"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("--start", type=float, default=43)
    parser.add_argument("--seconds", type=float, default=8)
    args = parser.parse_args()
    args.directory.mkdir(parents=True, exist_ok=True)
    response = requests.get(SOURCE, headers={"Range": "bytes=0-511"}, timeout=30)
    response.raise_for_status()
    data = response.content
    header = struct.unpack("<I5H3I2H", data[:30])
    if header[0] != 0x04034B50 or header[3] != 0:
        raise RuntimeError("Expected an uncompressed ZIP member")
    name_size, extra_size = header[-2:]
    offset = 30 + name_size + extra_size
    extra = data[30 + name_size:offset]
    movie_size = header[8]
    while extra:
        kind, length = struct.unpack("<HH", extra[:4])
        if kind == 1:
            movie_size = struct.unpack("<Q", extra[4:12])[0]
        extra = extra[4 + length:]

    class Proxy(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_GET(self):
            match = re.fullmatch(r"bytes=(\d+)-(\d*)", self.headers.get("Range", "bytes=0-"))
            if not match:
                self.send_error(416)
                return
            start = int(match[1])
            end = min(int(match[2]) if match[2] else movie_size - 1, movie_size - 1)
            try:
                with requests.get(SOURCE, headers={"Range": f"bytes={offset + start}-{offset + end}"},
                                  stream=True, timeout=(20, 60)) as upstream:
                    upstream.raise_for_status()
                    content_range = upstream.headers.get("Content-Range", "")
                    if not content_range.startswith(f"bytes {offset + start}-"):
                        raise RuntimeError(f"Unexpected upstream range: {content_range}")
                    self.send_response(206)
                    self.send_header("Content-Type", "video/quicktime")
                    self.send_header("Accept-Ranges", "bytes")
                    self.send_header("Content-Range", f"bytes {start}-{end}/{movie_size}")
                    self.send_header("Content-Length", str(end - start + 1))
                    self.end_headers()
                    for block in upstream.iter_content(65536):
                        self.wfile.write(block)
            except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Proxy)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    reference = args.directory / "reference4k.mkv"
    try:
        subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "warning", "-ss", str(args.start),
                        "-i", f"http://127.0.0.1:{server.server_port}/master.mov", "-t", str(args.seconds),
                        "-map", "0:v:0", "-an", "-vf", "pad=3840:2160:0:222:color=black,setsar=1",
                        "-c:v", "libx264", "-preset", "veryfast", "-crf", "0", "-pix_fmt", "yuv420p",
                        "-y", str(reference)], check=True, timeout=240)
    finally:
        server.shutdown()
    probe = json.loads(subprocess.check_output(["ffprobe", "-v", "error", "-count_frames", "-show_streams",
                                               "-show_format", "-of", "json", str(reference)]))
    if int(probe["streams"][0]["nb_read_frames"]) < args.seconds * 20:
        raise RuntimeError("Incomplete sample acquisition")
    (args.directory / "source.json").write_text(json.dumps({
        "source": SOURCE, "movie": "Tears of Steel", "author": "Blender Foundation",
        "license": "CC BY 3.0", "licenseUrl": "https://creativecommons.org/licenses/by/3.0/",
        "sourceStartSeconds": args.start, "seconds": args.seconds,
        "masterRaster": [3840, 1714], "paddedRaster": [3840, 2160], "activeRect": [0, 222, 3840, 1714],
        "probe": probe}, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
