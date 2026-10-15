# Video frame interpolation

Video transcoding offers RIFE 4.25 Lite (default), RIFE 4.25, and FFmpeg motion
compensation. RIFE provides generated intermediate pictures; motion compensation
uses `minterpolate` with adaptive overlapped blocks and variable block sizes, and
requires no downloaded model. Both methods support 2x, 4x, and a custom target
rate. GIF uses its own target frame rate. Enabling interpolation hides the ordinary
frame-rate filter so the two controls cannot independently resample the output.

## Model distribution and acceleration

The RIFE ONNX v2 exports consume one FP32 tensor `[1, 7, height, width]`: two RGB
pictures and a timestep plane. Output is `[1, 3, height, width]`. Models contain
their own padding. An axis of 64 pixels or less is first extended to 128 by
repeating its edge and cropped back after inference, because the upstream
reflection padding is invalid at those sizes. Models run on complete frames;
independent tile interpolation would break optical flow at tile boundaries. The initial implementation limits
the inference frame to 3840 x 2160 pixels by area. A reducing output scale is
applied before interpolation when super-resolution is disabled.

Downloads pin a repository revision and SHA-256 for each model. Verification and
session creation hold the same storage gate used by model-directory migration.
The selected root's `Interpolation` subdirectory is resolved at each operation.
Provenance, hashes and licensing are in
[`Libraries/InterpolationModels/NOTICE.md`](../../Libraries/InterpolationModels/NOTICE.md).
Weights are downloaded on demand and are not bundled with the app.

Sessions reuse the existing Windows ML provider catalog and DirectML fallback.
Automatic selection tries GPU, then NPU. NPU priority tries NPU, then GPU. Explicit
GPU and CPU choices are also available; software fallback after hardware failure
requires the user's checkbox. CPU graph fallback is disabled for hardware RIFE
sessions so a reported GPU/NPU session cannot silently execute unsupported nodes
on CPU. Intel GPU uses the provider's FP16 precision hint while retaining the
original FP32 interchange model. There is no separate bundled Intel NPU runtime.

Native initialization and inference run on worker threads, outside the STA UI
thread. Session input memory and the previous/current/generated bitmap buffers
are reused. Session creation, model downloads and frame generation support
cancellation, although cancellation cannot interrupt every provider's native
model compilation immediately.

## Timeline and processing order

The output uses a rational clock and samples each source pair by presentation
timestamps, including variable-rate input. Typed NTSC rates 23.976, 29.97, 59.94
and 119.88 map to their corresponding /1001 rates. Original pictures are retained
at matching timestamps. Single-frame and trimmed tails hold the last picture to
the clip end; non-frame-aligned trims begin at zero without shifting later source
timestamps. Audio retains its existing trimmed timeline and duration.

Scene-cut protection holds the previous picture across a detected cut instead of
generating a transition. Repeated identical pictures skip inference. Protection
is a sampled luminance/histogram heuristic and can be disabled.

Crop, deinterlacing, denoising and SDR conversion run once before frame generation.
Super-resolution and interpolation can be explicitly ordered. Auto uses SR first
for RIFE when the enhanced picture is at most 1920 x 1080 pixels by area, avoiding
SR work on every generated picture. Larger pictures and CPU motion compensation
use interpolation first to keep optical-flow dimensions smaller. This threshold
is a heuristic, not a universal performance guarantee. Output scaling, encoding
and audio muxing follow the combined processing pipeline. Live progress previews
show processed pictures, including generated frames, through the existing bounded
preview path.

HDR input requires explicit SDR output for interpolation (GIF is already SDR).
The error explains this constraint. This avoids silently interpolating HDR as
8-bit SDR and claiming HDR preservation. SDR input can still use the existing
explicit SDR-to-HDR output mapping.

## Validation and performance

The native smoke tool supports `--interpolation-only`, `--interpolation-benchmark`,
`--interpolation-benchmark --full`, `--interpolation-order-benchmark`, and
`--editing-ui --interpolation-ui`. Regression coverage includes clocks, tails,
audio, fractional rates, non-aligned cuts, VFR, scene cuts, actual GPU/NPU/CPU
inference, strict DirectML graph execution, cancellation, GIF, two-pass encoding,
HDR rejection/tone mapping, downscaling, and both SR orders. Settings regression
also covers moving interpolation models and resolving the new root through an
already-created service. Existing editing, HDR and SR checks remain available.

On this machine (Core Ultra 7 255H, Arc 140T), warmed RIFE Lite GPU inference took
about 73 ms at 720p, 163 ms at 1080p, and 1978 ms at 4K: approximately 13.8, 6.2
and 0.5 newly generated pictures per second. These are short two-iteration inference
measurements, excluding decode, encode and session initialization, not output FPS
or real-time guarantees. Standard RIFE measured about 75/208/2293 ms respectively.
Small-frame end-to-end inference also passed on Intel NPU and CPU; large-frame
NPU performance has not been established.

For 160 x 120 input, 2x frame rate and 2x SR, processing SR before interpolation
took about 66 ms per source interval versus 113 ms for the opposite order with
General x4 v3, and 44 ms versus 64 ms with SPAN ClearReality. Other sizes, models
and devices may favor a different order; the manual control remains available.

NVIDIA and AMD use the shared Windows ML/vendor-provider or DirectML path but have
not been physically tested here. Dedicated vendor optical-flow engines are not
part of this implementation. High-resolution RIFE remains an offline processing
feature on this machine.
