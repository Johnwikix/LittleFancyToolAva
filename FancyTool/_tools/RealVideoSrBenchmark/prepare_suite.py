"""Acquire two CC BY 3.0 shots and splice the exact eight-second test fixture."""
import argparse
import json
from pathlib import Path
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    directory = args.directory.resolve()
    directory.mkdir(parents=True, exist_ok=True)
    acquire = Path(__file__).with_name("acquire_sample.py")
    shots = []
    for name, start in [("faces", 25), ("city", 43)]:
        shot = directory / name
        if not (shot / "reference4k.mkv").exists():
            subprocess.run([sys.executable, str(acquire), str(shot), "--start", str(start), "--seconds", "4"], check=True)
        shots.append(shot / "reference4k.mkv")
    subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-t", "4", "-i", str(shots[0]),
                    "-t", "4", "-i", str(shots[1]), "-filter_complex",
                    "[0:v]setpts=PTS-STARTPTS[a];[1:v]setpts=PTS-STARTPTS[b];[a][b]concat=n=2:v=1:a=0[v]",
                    "-map", "[v]", "-an", "-c:v", "libx264", "-preset", "veryfast", "-crf", "0",
                    "-pix_fmt", "yuv420p", "-colorspace", "bt709", "-color_primaries", "bt709",
                    "-color_trc", "bt709", "-color_range", "tv", "-y", str(directory / "reference4k.mkv")], check=True)
    source = json.loads((directory / "faces" / "source.json").read_text(encoding="utf-8"))
    source.pop("probe", None)
    source.pop("sourceStartSeconds", None)
    source.update(seconds=8, segmentsSeconds=[[25, 29], [43, 47]], hardCutOutputFrame=96)
    (directory / "source.json").write_text(json.dumps(source, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
