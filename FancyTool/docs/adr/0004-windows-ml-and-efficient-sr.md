# Shared Windows ML acceleration and efficient super-resolution

> 图片模型选择已由 [ADR 0015：移除非商业图片模型](0015-remove-noncommercial-image-models.md) 部分取代：UltraSharp 与 LiveAction 已从当前功能和下载清单移除。下文保留历史决策与实验数据。

Windows builds use `Microsoft.Windows.AI.MachineLearning` 2.4.89, including ONNX Runtime 1.27.1 and DirectML. Image and video inference share provider discovery, hardware selection and reusable tile runners. The old separately bundled OpenVINO C API, DLLs and licenses are removed. Vendor OpenVINO is acquired and registered by Windows ML; it is not another app-owned inference runtime.

Windows builds target `net10.0-windows10.0.19041.0` with minimum OS build 18362. Linux remains `net10.0` with CPU ONNX Runtime. Publish scripts, smoke projects and MSIX content paths use the corresponding TFM. Do not install another ONNX native/managed package alongside Windows ML in the Windows build. Initialize the app-local `OrtEnv` before WinRT provider registration: otherwise Windows ML may resolve the older inbox ONNX Runtime and fail during provider registration.

The app registers already installed certified providers without downloading on startup. Both enhancement screens have an explicit hardware installer with status, cancellation and a fifteen-minute timeout; it downloads compatible providers through Microsoft's catalog. Hardware providers require Windows 11 24H2 (26100) or later. Older systems and GPUs retain DirectML. NVIDIA's vendor provider requires RTX 30-series or later; AMD's MIGraphX provider requires RDNA 3 or later and a compatible driver. The direct DirectML adapter fallback selects the physical adapter with most dedicated memory. AMD and NVIDIA vendor execution were not validated on this Intel machine.

GPU sessions prefer vendor providers and then DirectML. NPU preference selects an explicit NPU and then permits the application's configured GPU/CPU fallbacks. OpenVINO AUTO devices are excluded. `session.disable_cpu_ep_fallback=1` prevents the vendor provider from silently compiling on CPU after a hardware failure. All known model input dimension symbols (`batch`, `batch_size`, `height`, `width`, `in_height`, `in_width`) are fixed, including the RealESRGAN v3 denoise input. Merely selecting an NPU while leaving those dimensions dynamic can produce a CPU fallback and a misleading hardware badge.

ClearReality SPAN is the new default, available to videos as well as images. Existing image model selections are preserved. LiveAction V1 SPAN adds native 2× output, avoiding 4× inference/downsampling, and is intended for compressed live-action footage. It is restricted to 2× in the UI and service contract and licensed CC BY-NC-SA 4.0; its source weights are downloaded on demand rather than committed. The manifest pins the author's immutable commit and SHA-256. ClearReality remains Apache-2.0. RealESRGAN and UltraSharp remain selectable. An architecture's age alone does not establish perceptual quality; these speed tests do not establish a universal quality ranking. TSPANv2 is a promising temporal model but needs a separate multi-frame pipeline and has not been integrated or benchmarked here.

Video tile windows follow the same memory/speed preference as images (192/288/384/576). Opaque BGRA images reuse the video tile/tensor path rather than allocating bitmap and tensor wrappers for every tile. FP16/FP32 conversion uses SIMD tensor primitives. Transparent images retain alpha restoration. A single serialized session per video reuses buffers across frames; image sessions are released after a batch. Native 4× models still require 4× inference for a 2× result; only the native 2× model avoids that work.

Measurements on Intel Core Ultra 7 255H / Arc 140T, input 640×360, output 1280×720, synthetic circles, median of three warm complete bitmap runs, excluding initial model/provider compilation:

| Model | Previous DirectML image / video | Windows ML vendor GPU image / video |
| --- | ---: | ---: |
| RealESRGAN x4plus | 6613 / 6924 ms | 3909 / 3876 ms |
| RealESRGAN general v3 | 926 / 788 ms | 340 / 394 ms |
| ClearReality SPAN | 492 / 376 ms | 198 / 205 ms |
| LiveAction SPAN native 2× | not previously available | 166 / 167 ms |

These include tile overlap, tensor conversion, GPU readback and bitmap assembly; they are not decoder/encoder throughput or real-time video claims. New-model output quality and video flicker need content-specific assessment. Cold initialization can take seconds. Increasing tile size is not faster for every image size or model.

Reproduce with a separate model/settings directory:

```powershell
$env:FANCYTOOL_DATA_DIRECTORY = '<workspace>/.qa/video-sr'
dotnet run --project FancyTool/_tools/FfmpegSmokeTest -- '<workspace>/.qa/video-sr/benchmark' --sr-benchmark
dotnet run --project FancyTool/_tools/FfmpegSmokeTest -- '<workspace>/.qa/video-sr/tests' --sr-only
dotnet run --project FancyTool/_tools/FfmpegSmokeTest -- '<workspace>/.qa/video-sr/ui' --editing-ui --sr-ui
```

Windows ML provider tests require a normal user process with device/package access; a restricted sandbox can hide installed providers. Source benchmark JSON is written to the specified work directory. No user models, images or videos are altered by the harness.

Sources: [Windows ML deployment](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/distributing-your-app), [supported providers and hardware requirements](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/supported-execution-providers), [provider selection](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/select-execution-providers), [ClearReality author model card](https://huggingface.co/Kim2091/ClearRealityV1), [LiveAction author weights, training and license](https://github.com/jcj83429/upscaling/tree/9332e7d5b07747ff347e5abdc43f8144364de9f7/2xLiveActionV1_SPAN), [TSPANv2](https://github.com/Kim2091/TSPANv2).
