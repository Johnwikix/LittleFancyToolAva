"""Archive small measurements and review figures, leaving all video files local."""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import shutil
import subprocess


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("destination", type=Path)
    args = parser.parse_args()
    directory, destination = args.directory.resolve(), args.destination.resolve()
    destination.mkdir(parents=True, exist_ok=True)
    performance = json.loads((directory / "performance.json").read_text(encoding="utf-8"))
    for case in performance["cases"]:
        case["input"] = Path(case["input"]).name
        case["output"] = f"runs/{case['Height']}p-{case['model']}/output.mp4"
    result = {
        "archivedUtc": datetime.now(timezone.utc).isoformat(),
        "hardware": subprocess.check_output(["nvidia-smi", "--query-gpu=name,driver_version,memory.total",
                                              "--format=csv,noheader"], text=True).strip(),
        "source": json.loads((directory / "source.json").read_text(encoding="utf-8")),
        "inputSha256": json.loads((directory / "input-hashes.json").read_text(encoding="utf-8")),
        "performance": performance,
        "quality": json.loads((directory / "quality.json").read_text(encoding="utf-8"))}
    if len(performance["cases"]) != 8 or len(result["quality"]) != 10:
        raise RuntimeError("Expected eight performance cases and ten quality cases including Lanczos")
    (destination / "measurements.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    for path in directory.glob("telemetry-*.csv"):
        shutil.copyfile(path, destination / path.name)
    for name in ["720p-face.png", "1080p-face.png", "1080p-city-texture.png", "1080p-window-boundary.png",
                 "1080p-scene-cut-96.png", "1080p-scene-cut-159.png", "temporal-residuals.png"]:
        shutil.copyfile(directory / name, destination / name)


if __name__ == "__main__":
    main()
