# RIFE interpolation models

The application downloads Practical-RIFE 4.25 Lite and 4.25 on demand. No Python,
PyTorch, standalone OpenVINO runtime, or model weights are bundled in the app.

- Author: Zhewei Huang (hzwer), https://github.com/hzwer/Practical-RIFE
- License: MIT for the upstream code and the linked trained-model content.
- ONNX conversion: https://github.com/AmusementClub/vs-mlrt/releases/tag/external-models
- Upstream archives: `rife_v4.25_lite.7z` and `rife_v4.25.7z`.
- Each downloaded file is the unmodified `rife_v2` graph from its upstream archive.
- Single-file download mirror: https://huggingface.co/notaneimu/onnx-image-models
- Pinned mirror revision: `f7bf1c91e94ef516900f68456528fa781e2e7174`.

| Installed file | Upstream file | SHA-256 |
| --- | --- | --- |
| `rife_v4.25_lite_v2.onnx` | `rife_v2/rife_v4.25_lite.onnx` | `610b5de57cdcfbcce9914c23e60a1cd357779a6f9582a1bcfcb035f8eb38509b` |
| `rife_v4.25_v2.onnx` | `rife_v2/rife_v4.25.onnx` | `65c57a5e4abb17ad67faf35054291ac53affab0506d509d2e66698f6ecd75584` |

The graph accepts `[1, 7, height, width]` FP32: first RGB frame, second RGB
frame, and a full-resolution timestep plane. Output is `[1, 3, height, width]`
RGB. The graph handles its own padding; video frames are not split into tiles.

Models are installed under `AppPaths.ModelsDirectory/Interpolation` and participate
in the application's shared verification and directory migration workflow.
