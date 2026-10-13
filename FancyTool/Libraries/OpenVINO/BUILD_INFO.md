# Bundled Intel NPU runtime

OpenVINO 2025.4.1, build 20426, Windows x64. These native libraries are extracted from Intel's official PyPI wheel; Python is not required by the application.

- Source: https://files.pythonhosted.org/packages/64/c2/582bd6d01fc6588bfe35c0f363d294a54daa4587cafe30e932fa51420d04/openvino-2025.4.1-20426-cp312-cp312-win_amd64.whl
- Wheel SHA-256: `C50293D1463698012EAA526DCC83F841B85A3F4952EEA4C9445C83E0346F8E80`
- Included: `openvino.dll`, `openvino_c.dll`, `openvino_onnx_frontend.dll`, `openvino_intel_npu_plugin.dll`, `tbb12.dll`.
- Excluded: Python bindings, CPU/GPU OpenVINO plugins, development libraries, and unrelated model frontends.
- License: Apache-2.0; accompanying Intel runtime, oneDNN and oneTBB third-party notices are preserved and copied to the published application.

Extracted DLL SHA-256:

| File | SHA-256 |
| --- | --- |
| openvino.dll | `28FC34988E653D3710A75C654BE1A8A18F26C00C35809DB3846EBE5083335082` |
| openvino_c.dll | `DF1CC77E10B97FCB2CB86CACA4450B81D9F2CD6D4A0C7594B83FF166E6A100B3` |
| openvino_onnx_frontend.dll | `287080FD413711E645667F4A3A56172A639FD999DD10CA700CE4C2A82D1449B7` |
| openvino_intel_npu_plugin.dll | `D550E983C7F993C643A4908B6E41369FF57FE1EE0FBEF9E227AAB5E4DD4FA5A7` |
| tbb12.dll | `18866358D94D8657FEABF3CD05E205904B6CEE2DA99A00A5E08F82EB7C41AD02` |

The Intel AI Boost driver is supplied by the system. The application only advertises NPU execution after OpenVINO enumerates an NPU and compiles the selected model for that device. NVIDIA, AMD and Intel GPUs use the existing ONNX Runtime DirectML distribution independently of these libraries.
