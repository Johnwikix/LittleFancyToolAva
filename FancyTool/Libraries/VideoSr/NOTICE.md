# Bundled video enhancement models

These are fixed-weight format conversions, not newly trained models. Native output scale is 2×. The application implements 4× by running the same 2× model twice.

| Graphs | Weights / license | Pinned source |
|---|---|---|
| Anime4K Upscale CNN S / M, FP32 and FP16 | bloc97, MIT | [Anime4K](https://github.com/bloc97/Anime4K/tree/7684e9586f8dcc738af08a1cdceb024cc184f426) |
| FSRCNNX s / m, FP32 and FP16 | igv trained weights, GPL-3.0; bundled upstream by TianZerL | [FSRCNNX parameter header](https://github.com/TianZerL/Anime4KCPP/blob/fe187e020c8e57c6885511344a8705c292a92612/core/internal/AC/Core/Internal/Model/Param/FSRCNNX.p) |
| RAVU Lite r2 / r3, FP32 | bjin trained weights, LGPL-3.0 | [mpv-prescalers source branch](https://github.com/bjin/mpv-prescalers/tree/6b9ba842a05a113a75e5a2ac3d0a7f00f21ffbae) |

Conversion scripts: [rigaya/HWEnc-onnx-models](https://github.com/rigaya/HWEnc-onnx-models/tree/909fc391b5584702892081adb98f93ea2db16e73), MIT. The conversion-script license does not replace the weight licenses. Full license texts and upstream provenance statements are in `Licenses`.

FancyTool modifications (2026-10-16): FSRCNNX and RAVU receive an ONNX RGB adapter using full-range BT.709 luma, bilinear chroma reconstruction and luma residual addition with output clipping. Optional Anime4K / FSRCNNX graphs convert weights and supported operations to FP16 using ONNX Runtime's converter; unsupported operations retain the converter's casts. Tensor scheduling is unchanged. RAVU r2 uses DirectML FP32; r3 uses a vendor GPU provider in FP32. Anime4K / FSRCNNX use vendor GPU providers for both precisions. Backend selection is automatic and CPU fallback is disabled.

Corresponding conversion source and reproduction commands are distributed in this repository at `FancyTool/_tools/OnnxCandidateValidation`: `fetch.ps1` pins original source commits and validates weights, `prepare.py` exports and adapts the models, and `package.py` produces these exact packaged graph families. `models.json` records SHA-256 values for all ten graphs. Dependency versions are recorded in `FancyTool/docs/data/onnx-vsr-validation-2026-10-16/python-lock.txt`. Original source and weight locations above remain the authoritative upstream provenance.
