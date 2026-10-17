"""汇总实际转码结果与设备级 NVIDIA 遥测，并复制可审查的原始证据。"""
import argparse
import csv
import json
import re
import shutil
import statistics
from datetime import datetime, timedelta, timezone
from pathlib import Path


def number(value):
    return float(re.search(r"[-\d.]+", value).group())


def summarize(source, label, destination):
    target = destination / label
    target.mkdir(parents=True, exist_ok=True)
    records = json.loads((source / "measurements.json").read_text(encoding="utf-8-sig"))
    for name in ("measurements.json", "gpu.csv"):
        shutil.copyfile(source / name, target / name)
    shutil.copyfile(source / "run.log", target / "run.txt")
    with (source / "gpu.csv").open(encoding="utf-8-sig", newline="") as stream:
        telemetry = [{k.strip(): v.strip() for k, v in row.items()} for row in csv.DictReader(stream)]
    for row in telemetry:
        row["utc"] = datetime.strptime(row["timestamp"], "%Y/%m/%d %H:%M:%S.%f").replace(
            tzinfo=timezone(timedelta(hours=8))).astimezone(timezone.utc)
    enriched = []
    for record in records:
        profile = source / f"{record['mode']}-{record['iteration']}"
        files = sorted(profile.glob("pipeline-*.csv"), key=lambda p: p.stat().st_mtime)
        with files[-1].open(encoding="utf-8-sig", newline="") as stream:
            phases = list(csv.DictReader(stream))
        wall = float(next(row["totalMs"] for row in phases if row["phase"] == "Wall"))
        end = datetime.fromisoformat(record["endUtc"])
        start = end - timedelta(milliseconds=wall)
        samples = [row for row in telemetry if start <= row["utc"] <= end]
        gpu = {}
        for key, result in (("utilization.gpu [%]", "gpuPercent"), ("power.draw [W]", "powerWatts"),
                            ("clocks.current.sm [MHz]", "smMHz"), ("memory.used [MiB]", "deviceMemoryMiB")):
            gpu[result] = statistics.mean(number(row[key]) for row in samples) if samples else None
        gpu["samples"] = len(samples)
        enriched.append({**record, "pipelineMs": wall, "pipelineFps": record["outputFrames"] * 1000 / wall,
                         "phases": phases, "telemetry": gpu, "telemetryStartUtc": start.isoformat()})
        shutil.copyfile(files[-1], target / f"{record['mode']}-{record['iteration']}.csv")
    summaries = []
    for mode in sorted({record["mode"] for record in records}):
        warm = [row for row in enriched if row["mode"] == mode and row["iteration"] > 0]
        summaries.append({"variant": label, "mode": mode, "repeats": len(warm),
                          "endToEndFps": statistics.median(row["outputFps"] for row in warm),
                          "pipelineFps": statistics.median(row["pipelineFps"] for row in warm),
                          "gpuPercent": statistics.median(row["telemetry"]["gpuPercent"] for row in warm),
                          "powerWatts": statistics.median(row["telemetry"]["powerWatts"] for row in warm),
                          "peakWorkingSetMiB": max(row["peakWorkingSetMiB"] for row in warm)})
    return {"variant": label, "measurements": enriched, "summary": summaries}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    result = [summarize(args.baseline, "baseline", args.output), summarize(args.candidate, "candidate", args.output)]
    with (args.output / "measurements.json").open("w", encoding="utf-8", newline="\r\n") as stream:
        json.dump(result, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    print(json.dumps([variant["summary"] for variant in result], ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
