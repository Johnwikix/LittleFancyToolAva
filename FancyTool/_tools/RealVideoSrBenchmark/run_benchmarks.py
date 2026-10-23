"""Run the actual application transcode service, serially, with 200 ms NVML telemetry."""
import argparse
import csv
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import os
import subprocess
import threading
import time

import numpy as np

MODELS = ["NanoVsr644K", "SpanClearRealityV1", "RealEsrganGeneralX4V3"]
FIELDS = ["timestamp", "utilization.gpu", "utilization.memory", "utilization.encoder", "utilization.decoder",
          "memory.used", "power.draw", "clocks.current.sm", "temperature.gpu"]


def utc():
    return datetime.now(timezone.utc).isoformat()


def epoch(value):
    return datetime.fromisoformat(value).timestamp()


def prepare(directory):
    for height in (720, 1080, 1440):
        output = directory / (f"reference{height}.mkv" if height == 1440 else f"input{height}.mp4")
        if output.exists():
            continue
        command = ["ffmpeg", "-hide_banner", "-loglevel", "error", "-i", str(directory / "reference4k.mkv"),
                   "-an", "-vf", f"scale=-2:{height}:flags=lanczos,setsar=1", "-c:v", "libx264", "-preset", "medium",
                   "-crf", "0" if height == 1440 else "23", "-pix_fmt", "yuv420p",
                   "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
                   "-y", str(output)]
        subprocess.run(command, check=True)
    files = [directory / name for name in ("reference4k.mkv", "reference1440.mkv", "input720.mp4", "input1080.mp4")]
    (directory / "input-hashes.json").write_text(json.dumps({p.name: hashlib.file_digest(p.open("rb"), "sha256").hexdigest()
                                                           for p in files}, indent=2), encoding="utf-8")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("--runner", type=Path, required=True)
    parser.add_argument("--models-directory", type=Path, required=True)
    parser.add_argument("--models", nargs="+", default=MODELS)
    parser.add_argument("--heights", nargs="+", type=int, default=[720, 1080])
    args = parser.parse_args()
    directory = args.directory.resolve()
    prepare(directory)
    telemetry_path = directory / f"telemetry-{int(time.time())}.csv"
    samples = []
    monitor = subprocess.Popen(["nvidia-smi", "--query-gpu=" + ",".join(FIELDS), "--format=csv,noheader,nounits", "-lms", "200"],
                               stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, bufsize=1,
                               creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)

    def collect():
        with telemetry_path.open("w", newline="", encoding="utf-8") as stream:
            writer = csv.writer(stream)
            writer.writerow(["receivedUtc"] + FIELDS)
            for line in monitor.stdout:
                row = next(csv.reader([line]))
                if len(row) != len(FIELDS):
                    continue
                stamp = utc()
                writer.writerow([stamp] + [x.strip() for x in row])
                stream.flush()
                samples.append((epoch(stamp), [float(x) for x in row[1:]]))

    collector = threading.Thread(target=collect, daemon=True)
    collector.start()
    env = os.environ.copy()
    # The existing fixture models and the new bundled model are isolated from user settings.
    env["FANCYTOOL_DATA_DIRECTORY"] = str(args.models_directory.resolve().parent)
    records = []
    try:
        time.sleep(3)
        baseline = samples[-10:]
        for height in args.heights:
            for model in args.models:
                case = directory / "runs" / f"{height}p-{model}"
                case.mkdir(parents=True, exist_ok=True)
                record_path = case / "result.json"
                if not record_path.exists():
                    print(f"{utc()} START {height}p {model}", flush=True)
                    with (case / "console.log").open("w", encoding="utf-8") as log:
                        subprocess.run([str(args.runner.resolve()), str(case), "--real-video-sr", "--input",
                                        str(directory / f"input{height}.mp4"), "--model", model],
                                       stdout=log, stderr=subprocess.STDOUT, env=env, check=True, timeout=1800)
                record = json.loads(record_path.read_text(encoding="utf-8"))
                start, end = epoch(record["steadyStartUtc"]), epoch(record["lastInferenceUtc"])
                selected = np.asarray([value for stamp, value in samples if start <= stamp <= end])
                if len(selected):
                    record["gpu"] = {
                        "samples": len(selected), "scope": "whole adapter; excludes setup and first inference window",
                        "busyMeanPercent": float(selected[:, 0].mean()), "busyP95Percent": float(np.percentile(selected[:, 0], 95)),
                        "busyMaxPercent": float(selected[:, 0].max()), "memoryBusyMeanPercent": float(selected[:, 1].mean()),
                        "encoderMeanPercent": float(selected[:, 2].mean()), "decoderMeanPercent": float(selected[:, 3].mean()),
                        "vramPeakMiB": float(selected[:, 4].max()), "powerMeanW": float(selected[:, 5].mean()),
                        "powerPeakW": float(selected[:, 5].max()), "smClockMeanMHz": float(selected[:, 6].mean()),
                        "temperaturePeakC": float(selected[:, 7].max()), "telemetryFile": telemetry_path.name}
                    record_path.write_text(json.dumps(record, indent=2), encoding="utf-8")
                records.append(record)
                print(f"{utc()} DONE {height}p {model}: total={record['endToEndFps']:.2f}fps "
                      f"warm inference={record['warmInferenceFps']:.2f}fps gpu={record.get('gpu', {}).get('busyMeanPercent', 'previous run')}", flush=True)
                (directory / "performance.json").write_text(json.dumps({"idleBaseline": baseline, "cases": records}, indent=2), encoding="utf-8")
                time.sleep(2)
    finally:
        monitor.terminate()
        collector.join(timeout=5)
    if not samples:
        raise RuntimeError("GPU telemetry capture failed")


if __name__ == "__main__":
    main()
