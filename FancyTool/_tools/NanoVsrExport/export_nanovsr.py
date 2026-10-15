"""Export the pinned upstream NanoVSR checkpoint, fuse blocks, and check ONNX parity.

Requires torch, numpy, onnx and onnxruntime. Export runs on CPU; CUDA is not needed.
Clone https://github.com/filippawlicki/nanovsr at UPSTREAM_COMMIT first.
"""
import argparse
import hashlib
import inspect
import json
from pathlib import Path
import subprocess
import sys

import numpy as np
import onnx
import onnxruntime as ort
from onnxruntime.transformers.float16 import convert_float_to_float16
import torch

UPSTREAM_COMMIT = "feab9d259977ace3be4fca659394c3043bcc34c5"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--upstream", type=Path, required=True)
    parser.add_argument("--checkpoint", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--references", type=Path, required=True)
    args = parser.parse_args()
    commit = subprocess.check_output(["git", "-C", str(args.upstream), "rev-parse", "HEAD"], text=True).strip()
    if commit != UPSTREAM_COMMIT:
        raise RuntimeError(f"Expected upstream {UPSTREAM_COMMIT}, got {commit}")
    sys.path.insert(0, str(args.upstream.resolve()))
    from models.nanovsr import NanoVSR

    torch.manual_seed(710)
    torch.set_num_threads(4)
    checkpoint = torch.load(args.checkpoint, map_location="cpu", weights_only=True)
    state = checkpoint
    for key in ("params_ema", "params", "model_state_dict"):
        if key in checkpoint:
            state = checkpoint[key]
            break
    features = state["feat_extract.rbr_dense.0.weight"].shape[0]
    blocks = max(int(key.split(".")[1]) for key in state if key.startswith("forward_net.")) + 1
    model = NanoVSR(num_feat=features, num_blocks=blocks).eval()
    model.load_state_dict(state, strict=True)
    sample = torch.rand(1, 5, 3, 12, 16)
    with torch.no_grad():
        original = model(sample).numpy()
        model.switch_to_deploy()
        expected = model(sample).numpy()
    np.testing.assert_allclose(expected, original, atol=2e-5, rtol=2e-5)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    fp32_path = args.output.with_suffix(".fp32.onnx")
    extra = {"dynamo": False} if "dynamo" in inspect.signature(torch.onnx.export).parameters else {}
    torch.onnx.export(model, sample, str(fp32_path), opset_version=17,
                      input_names=["lr"], output_names=["sr"],
                      dynamic_axes={"lr": {3: "height", 4: "width"},
                                    "sr": {3: "height_x4", 4: "width_x4"}}, **extra)
    fp32 = onnx.load(fp32_path)
    model_fp16 = convert_float_to_float16(fp32, keep_io_types=False)
    # Some ORT converter versions append Resize's FP32 precision casts after their
    # consumers. Restore a valid dependency order before checking/serializing.
    available = {value.name for value in model_fp16.graph.input} | {value.name for value in model_fp16.graph.initializer}
    pending, ordered = list(model_fp16.graph.node), []
    while pending:
        ready = [node for node in pending if all(not name or name in available for name in node.input)]
        if not ready:
            raise RuntimeError("FP16 conversion produced an unresolved graph dependency")
        for node in ready:
            ordered.append(node)
            available.update(node.output)
            pending.remove(node)
    del model_fp16.graph.node[:]
    model_fp16.graph.node.extend(ordered)
    onnx.checker.check_model(model_fp16)
    onnx.save(model_fp16, args.output)
    options = ort.SessionOptions()
    options.intra_op_num_threads = 4
    actual = ort.InferenceSession(str(args.output), options, providers=["CPUExecutionProvider"]).run(
        None, {"lr": sample.numpy().astype(np.float16)})[0].astype(np.float32)
    np.testing.assert_allclose(actual, expected, atol=0.006, rtol=0.006)
    # Exercise another spatial shape; time remains exactly five in the unrolled graph.
    second = torch.rand(1, 5, 3, 9, 13)
    with torch.inference_mode():
        second_expected = model(second).numpy()
    second_actual = ort.InferenceSession(str(args.output), options, providers=["CPUExecutionProvider"]).run(
        None, {"lr": second.numpy().astype(np.float16)})[0].astype(np.float32)
    np.testing.assert_allclose(second_actual, second_expected, atol=0.006, rtol=0.006)
    args.references.mkdir(parents=True, exist_ok=True)
    sample.numpy().tofile(args.references / "input.f32")
    expected.tofile(args.references / "output.f32")
    report = {"upstreamCommit": commit, "checkpointSha256": hashlib.sha256(args.checkpoint.read_bytes()).hexdigest(),
              "onnxSha256": hashlib.sha256(args.output.read_bytes()).hexdigest(), "features": features, "blocks": blocks,
              "maxAbsError": float(np.max(np.abs(actual - expected))),
              "secondShapeMaxAbsError": float(np.max(np.abs(second_actual - second_expected))),
              "inputShape": list(sample.shape), "outputShape": list(expected.shape), "temporalFrames": 5,
              "opset": 17, "precision": "float16"}
    (args.references / "export-report.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))
    fp32_path.unlink()


if __name__ == "__main__":
    main()
