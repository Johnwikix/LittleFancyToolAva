"""Full-raster fidelity and motion-compensated temporal residuals for the real-video SR test.

Requires ffmpeg, numpy, OpenCV and matplotlib. Scores describe the encoded delivery
files; they are not estimates of subjective quality or a proof of flicker absence.
"""
import argparse
from functools import lru_cache
import json
import math
from pathlib import Path
import re
import subprocess

import cv2
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np

MODELS = ["Lanczos", "NanoVsr644K", "SpanClearRealityV1", "LiveActionSpanX2", "RealEsrganGeneralX4V3"]
LABELS = ["Lanczos", "NanoVSR", "ClearReality SPAN", "LiveAction SPAN", "RealESRGAN general"]


def run(command, **kwargs):
    return subprocess.run(command, check=True, **kwargs)


def crop(height):
    # The master has an active 3840x1714 picture, padded to 3840x2160.
    # Ignore letterboxing and a four-pixel guard on each edge of the active image.
    top = int(math.ceil((222 * height / 2160 + 4) / 2) * 2)
    bottom = int(math.floor(((222 + 1714) * height / 2160 - 4) / 2) * 2)
    return f"{height * 16 // 9 - 8}:{bottom - top}:4:{top}"


def baseline(directory, height):
    case = directory / "runs" / f"{height}p-Lanczos"
    case.mkdir(parents=True, exist_ok=True)
    output = case / "output.mp4"
    if not output.exists():
        run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-i", str(directory / f"input{height}.mp4"),
             "-an", "-vf", f"scale={height * 32 // 9}:{height * 2}:flags=lanczos,setsar=1",
             "-c:v", "h264_nvenc", "-preset", "p4", "-rc", "constqp", "-qp", "18", "-pix_fmt", "yuv420p",
             "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "tv",
             "-y", str(output)])


def fidelity(case, reference, output_height):
    if not (case / "ssim.log").exists():
        graph = (f"[0:v]crop={crop(output_height)}[d];"
                 f"[1:v]crop={crop(output_height)},split=2[r1][r2];"
                 "[d][r1]psnr=stats_file=psnr.log:stats_version=2[p];"
                 "[p][r2]ssim=stats_file=ssim.log[s]")
        with (case / "quality-ffmpeg.log").open("w", encoding="utf-8") as log:
            run(["ffmpeg", "-hide_banner", "-threads", "4", "-i", str(case / "output.mp4"),
                 "-threads", "4", "-i", str(reference), "-filter_complex_threads", "4", "-filter_complex", graph,
                 "-map", "[s]", "-an", "-f", "null", "-"], cwd=case, stdout=log, stderr=subprocess.STDOUT)
    psnr = [dict(re.findall(r"(\w+):([^\s]+)", line)) for line in (case / "psnr.log").read_text().splitlines()
            if line.startswith("n:")]
    ssim = [dict(re.findall(r"(\w+):([^\s]+)", line)) for line in (case / "ssim.log").read_text().splitlines()
            if line.startswith("n:")]
    if len(psnr) != 192 or len(ssim) != 192:
        raise RuntimeError(f"Frame count mismatch in quality metrics: {case}")
    mse = float(np.mean([float(p["mse_y"]) for p in psnr]))
    return {"frames": len(psnr), "psnrYDb": 10 * math.log10(255 ** 2 / mse),
            "ssimY": float(np.mean([float(p["Y"]) for p in ssim])),
            "ssimAll": float(np.mean([float(p["All"]) for p in ssim]))}


def small_luma(path, output_height):
    # Decode all frames once, preserving frame order. Area resampling suppresses
    # subpixel flow noise; temporal scores are measured at 512x228, in 8-bit luma.
    result = run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-threads", "4", "-i", str(path),
                  "-an", "-vf", f"crop={crop(output_height)},scale=512:228:flags=area,format=gray",
                  "-f", "rawvideo", "-pix_fmt", "gray", "-"], stdout=subprocess.PIPE)
    frames = np.frombuffer(result.stdout, np.uint8).reshape(-1, 228, 512)
    if len(frames) != 192:
        raise RuntimeError(f"Expected 192 frames: {path} -> {len(frames)}")
    return frames


def detect_cuts(path):
    result = run(["ffmpeg", "-hide_banner", "-threads", "4", "-i", str(path), "-vf",
                  "scale=320:-2,select=gt(scene\\,0.05),showinfo", "-an", "-f", "null", "-"],
                 stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
    return sorted({96} | {round(float(value) * 24) for value in re.findall(r"pts_time:([\d.]+)", result.stderr)})


def reference_motion(reference, cuts):
    y, x = np.mgrid[:228, :512].astype(np.float32)
    motion = []
    for index in range(1, len(reference)):
        previous, current = reference[index - 1], reference[index]
        flow = cv2.calcOpticalFlowFarneback(current, previous, None, .5, 3, 21, 4, 7, 1.5, 0)
        map_x, map_y = x + flow[:, :, 0], y + flow[:, :, 1]
        warped = cv2.remap(previous.astype(np.float32), map_x, map_y, cv2.INTER_LINEAR)
        valid = ((map_x >= 1) & (map_x < 510) & (map_y >= 1) & (map_y < 226)
                 & (np.abs(current.astype(np.float32) - warped) < 15))
        # Exclude both the artificial splice and cuts detected in the source.
        excluded = index in cuts or valid.mean() < .2
        motion.append((map_x, map_y, valid, excluded))
    return motion


def temporal(reference, output, motion):
    error = output.astype(np.float32) - reference.astype(np.float32)
    transitions = []
    for index, (map_x, map_y, valid, excluded) in enumerate(motion, 1):
        previous = cv2.remap(error[index - 1], map_x, map_y, cv2.INTER_LINEAR)
        score = None if excluded else float(np.abs(error[index] - previous)[valid].mean())
        transitions.append({"frame": index, "windowBoundary": index % 5 == 0,
                            "validFraction": float(valid.mean()), "residualMae": score})
    scores = [p["residualMae"] for p in transitions if p["residualMae"] is not None]
    boundary = [p["residualMae"] for p in transitions if p["residualMae"] is not None and p["windowBoundary"]]
    other = [p["residualMae"] for p in transitions if p["residualMae"] is not None and not p["windowBoundary"]]
    return {"meanResidualMae": float(np.mean(scores)), "p95ResidualMae": float(np.percentile(scores, 95)),
            "boundaryMean": float(np.mean(boundary)), "otherMean": float(np.mean(other)),
            "boundaryRatio": float(np.mean(boundary) / np.mean(other)),
            "excludedTransitions": [p["frame"] for p in transitions if p["residualMae"] is None],
            "transitions": transitions}


@lru_cache(maxsize=64)
def frame(path, index):
    # OpenCV random seeking into these lossless MKVs returns an incorrect frame.
    # Select by decoded frame index, matching the metric streams exactly.
    result = run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-threads", "2", "-i", str(path),
                  "-vf", f"select=eq(n\\,{index})", "-frames:v", "1", "-f", "image2pipe", "-c:v", "png", "-"],
                 stdout=subprocess.PIPE)
    pixels = cv2.imdecode(np.frombuffer(result.stdout, np.uint8), cv2.IMREAD_COLOR)
    if pixels is None:
        raise RuntimeError(f"Cannot read frame {index}: {path}")
    return cv2.cvtColor(pixels, cv2.COLOR_BGR2RGB)


def contact(directory, height, reference, name, index, rectangle):
    scale = height * 2 / 2160
    x, y, w, h = [int(round(v * scale)) for v in rectangle]
    paths = [reference] + [directory / "runs" / f"{height}p-{model}" / "output.mp4" for model in MODELS]
    fig, axes = plt.subplots(2, 3, figsize=(12, 8.5 if w == h else 5.4), layout="constrained")
    for ax, path, label in zip(axes.flat, paths, ["Reference"] + LABELS):
        pixels = frame(path, index)[y:y+h, x:x+w]
        ax.imshow(pixels, interpolation="nearest")
        ax.set_title(label, fontsize=12)
        ax.axis("off")
    fig.suptitle(f"{height}p to {height * 2}p | frame {index} | {name} | native-pixel crops", fontsize=14)
    fig.text(.01, .004, "Tears of Steel / Blender Foundation / CC BY 3.0 | Downsampled + H.264 CRF23 input; NVENC QP18 output", fontsize=8)
    fig.savefig(directory / f"{height}p-{name}.png", dpi=140)
    plt.close(fig)


def boundary_contact(directory, height, reference, index):
    paths = [reference] + [directory / "runs" / f"{height}p-{m}" / "output.mp4" for m in ("NanoVsr644K", "SpanClearRealityV1")]
    scale = height * 2 / 2160
    x, y, w, h = [int(round(v * scale)) for v in (2160, 360, 720, 720)]
    fig, axes = plt.subplots(3, 4, figsize=(12, 9.5), layout="constrained")
    for row, (path, label) in enumerate(zip(paths, ["Reference", "NanoVSR", "ClearReality SPAN"])):
        for col, n in enumerate(range(index - 2, index + 2)):
            ax = axes[row, col]
            ax.imshow(frame(path, n)[y:y+h, x:x+w], interpolation="nearest")
            ax.set_title(f"{label} / f{n}" + (" (new window)" if n % 5 == 0 else ""), fontsize=9)
            ax.axis("off")
    fig.suptitle(f"{height}p temporal-window boundary: consecutive native-pixel crops")
    fig.text(.01, .003, "Tears of Steel / Blender Foundation / CC BY 3.0", fontsize=8)
    fig.savefig(directory / f"{height}p-window-boundary.png", dpi=140)
    plt.close(fig)


def cut_contact(directory, height, reference, cut=96):
    paths = [reference] + [directory / "runs" / f"{height}p-{m}" / "output.mp4" for m in ("NanoVsr644K", "SpanClearRealityV1")]
    fig, axes = plt.subplots(3, 4, figsize=(14, 6.5), layout="constrained")
    for row, (path, label) in enumerate(zip(paths, ["Reference", "NanoVSR", "ClearReality SPAN"])):
        for col, index in enumerate(range(cut - 2, cut + 2)):
            axes[row, col].imshow(frame(path, index), interpolation="hanning")
            axes[row, col].set_title(f"{label} / f{index}" + (" (cut)" if index == cut else ""), fontsize=9)
            axes[row, col].axis("off")
    fig.text(.01, .003, "Tears of Steel / Blender Foundation / CC BY 3.0 | Full frames reduced for cut inspection", fontsize=8)
    fig.savefig(directory / f"{height}p-scene-cut-{cut}.png", dpi=140)
    plt.close(fig)


def comparison_clip(directory, height, reference):
    # A synchronized native-pixel face crop makes playback comparison practical.
    output = directory / f"{height}p-face-comparison-2x2.mp4"
    if output.exists():
        return
    scale = height * 2 / 2160
    x, y, size = [int(round(v * scale / 2) * 2) for v in (2160, 360, 720)]
    paths = [reference] + [directory / "runs" / f"{height}p-{m}" / "output.mp4" for m in ("NanoVsr644K", "SpanClearRealityV1", "LiveActionSpanX2")]
    args = ["ffmpeg", "-hide_banner", "-loglevel", "error"]
    for path in paths:
        args += ["-threads", "2", "-t", "4", "-i", str(path)]
    graph = ";".join(f"[{i}:v]crop={size}:{size}:{x}:{y},setsar=1[c{i}]" for i in range(4))
    graph += ";[c0][c1][c2][c3]xstack=inputs=4:layout=0_0|w0_0|0_h0|w0_h0[v]"
    run(args + ["-filter_complex_threads", "4", "-filter_complex", graph, "-map", "[v]", "-an",
                "-c:v", "libx264", "-preset", "fast", "-crf", "16", "-pix_fmt", "yuv420p", "-y", str(output)])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("directory", type=Path)
    parser.add_argument("--visuals-only", action="store_true")
    parser.add_argument("--metrics-only", action="store_true")
    args = parser.parse_args()
    directory = args.directory.resolve()
    if args.visuals_only:
        for height in [720, 1080]:
            reference_path = directory / ("reference1440.mkv" if height == 720 else "reference4k.mkv")
            contact(directory, height, reference_path, "face", 50, (2160, 360, 720, 720))
            contact(directory, height, reference_path, "city-texture", 145, (1300, 850, 1280, 640))
            boundary_contact(directory, height, reference_path, 50)
            for cut in detect_cuts(reference_path):
                cut_contact(directory, height, reference_path, cut)
            comparison_clip(directory, height, reference_path)
        return
    records = []
    cuts = detect_cuts(directory / "reference4k.mkv")
    fig, axes = plt.subplots(2, 1, figsize=(12, 7), layout="constrained")
    for axis, height in zip(axes, [720, 1080]):
        reference_path = directory / ("reference1440.mkv" if height == 720 else "reference4k.mkv")
        baseline(directory, height)
        reference = small_luma(reference_path, height * 2)
        motion = reference_motion(reference, cuts)
        for model, label in zip(MODELS, LABELS):
            case = directory / "runs" / f"{height}p-{model}"
            print(f"Analyze {height}p {model}", flush=True)
            record = {"height": height, "model": model, "fidelity": fidelity(case, reference_path, height * 2)}
            output = small_luma(case / "output.mp4", height * 2)
            record["temporal"] = temporal(reference, output, motion)
            (case / "quality.json").write_text(json.dumps(record, indent=2), encoding="utf-8")
            records.append(record)
            series = record["temporal"]["transitions"]
            axis.plot([p["frame"] / 24 for p in series], [p["residualMae"] for p in series], label=label, lw=1)
        for cut in cuts:
            axis.axvline(cut / 24, color="black", ls="--", lw=1)
        axis.set(title=f"{height}p to {height * 2}p (scene cuts excluded)", xlabel="Time (s)", ylabel="Temporal residual MAE (lower)")
        axis.legend(ncol=3, fontsize=8)
        if not args.metrics_only:
            for name, index, rect in [("face", 50, (2160, 360, 720, 720)), ("city-texture", 145, (1300, 850, 1280, 640))]:
                contact(directory, height, reference_path, name, index, rect)
            boundary_contact(directory, height, reference_path, 50)
            for cut in cuts:
                cut_contact(directory, height, reference_path, cut)
            comparison_clip(directory, height, reference_path)
        (directory / "quality.json").write_text(json.dumps(records, indent=2), encoding="utf-8")
    fig.savefig(directory / "temporal-residuals.png", dpi=140)
    plt.close(fig)


if __name__ == "__main__":
    main()
