# Video super-resolution and Intel NPU inference

Video enhancement uses the same pinned ONNX model files and SHA-256 installer as image enhancement. The default is `realesr-general-x4v3_fp16`: Real-ESRGAN's lightweight general-scene model. Anime 6B and x4plus remain optional quality choices. This is frame-by-frame restoration, not a recurrent temporal model, so it does not promise temporal consistency or new source detail. The neural input/output is SDR BGRA/RGB at 8-bit precision; it is not an HDR-preserving path.

The pipeline is time trim → crop/deinterlace/denoise/FPS → fixed-shape tiled inference → output scaling/pixel conversion → encoding/muxing. Audio follows the existing sample-accurate trim path. The inference stage holds only the current decoded and enhanced frame; FFmpeg's filters and codecs keep their own buffers, including the existing GIF palette buffering. A 192×192 padded inference window, 32-pixel overlap, serial enhanced-video scheduling, and a maximum of 8192 pixels per dimension / 33,554,432 total output pixels bound inference memory. Both encoding passes reuse one inference session.

Hardware preference is explicit: automatic selects a physical DirectML GPU with the largest dedicated memory, then Intel NPU; NPU preference tries Intel NPU then GPU. CPU fallback can be disabled. CPU selection is explicit. DirectML covers Intel, NVIDIA and AMD GPUs, and requires sequential execution with memory-pattern optimization disabled. Intel NPU uses OpenVINO's native C API, separate from ONNX Runtime, avoiding replacement of `onnxruntime.dll` in the DirectML distribution. Every model input is reshaped to fixed dimensions, including the general model's denoise-strength scalar. Compiled NPU models are cached under the shared model directory.

OpenVINO 2025.4.1's Windows x64 NPU runtime is bundled under `Libraries/OpenVINO`, with archive provenance, hashes and licenses in that directory. It requires the system's Intel AI Boost driver. It does not provide AMD or Qualcomm NPU support. Linux retains CPU inference. Image enhancement exposes an independent NPU preference and reports actual accelerator/fallback status; alpha is restored through the existing image path. Shared image sessions are serialized to satisfy DirectML's same-session concurrency restriction. Installer operations are serialized per destination file, preventing image/video installers from overwriting the same partial download.

Verification commands (use a test data directory so installed user models/settings are untouched):

```powershell
$env:FANCYTOOL_DATA_DIRECTORY = '<workspace>/.qa/video-sr'
dotnet run --project FancyTool/_tools/FfmpegSmokeTest -- '<workspace>/.qa/video-sr' --sr-only
dotnet run --project FancyTool/_tools/FfmpegSmokeTest -- '<workspace>/.qa/video-editing' --editing-only
dotnet run --project FancyTool/_tools/FfmpegSmokeTest -- '<workspace>/.qa/video-editing' --editing-ui
```

Sources: [Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN), [DirectML requirements](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html), [Intel OpenVINO hardware support](https://onnxruntime.ai/docs/reference/execution-providers/OpenVINO-ExecutionProvider.html), and [OpenVINO C API headers](https://github.com/openvinotoolkit/openvino/tree/2025.4.1/src/bindings/c/include/openvino/c).
