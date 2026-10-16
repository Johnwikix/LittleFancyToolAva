# Video frame interpolation

Video transcoding offers RIFE 4.25 Lite (default), RIFE 4.25, and FFmpeg motion
compensation. RIFE provides generated intermediate pictures; motion compensation
uses `minterpolate` with adaptive overlapped blocks and variable block sizes, and
requires no downloaded model. Both methods support only 2x, 4x, and 6x source
frame rate. These multipliers also apply to GIF. Enabling interpolation hides the
ordinary frame-rate and GIF frame-rate controls so they cannot independently
resample the output. Old saved custom-target selections migrate to 2x; new saved
selections retain all three multiplier tiers.

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
timestamps, including variable-rate input. Fractional source rates retain their
rational timing when multiplied. Original pictures are retained
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

Each converting file displays measured processing FPS and processed/total output
frames next to its percentage. The source probe preserves FFmpeg's `nb_frames`
when the container reports it. Complete non-GIF conversions without clipping,
frame-rate conversion, interpolation, or deinterlacing reuse that exact count;
spatial SR and resizing do not invalidate it. Other conversions use duration
multiplied by encoder frame rate as an explicitly estimated total; unknown
durations without a usable exact count show an unknown total. This avoids a
second full decode just to count frames. A source frame count is not an exact
count of generated output frames after temporal processing.
Completion replaces that estimate with the actual count. Two-pass jobs display
their pass number and reset the counters per pass. Statistics are posted to the
UI directly in producer order so the final actual count cannot be overwritten by
an older asynchronous progress callback.
The counter counts output frames submitted to the encoder, including generated
frames, against wall-clock time in approximately half-second windows. Decode,
filters, inference, encoding, and muxing contribute to this elapsed time; model
initialization before the conversion loop is excluded. Short jobs publish their
remaining sample on completion, and two-pass jobs restart measurement per pass.

## Vendor GPU interpolation

Windows has two additional methods, appended to preserve saved RIFE/motion mode
indices. AMD uses FFmpeg `frc_amf` on a D3D11 adapter, supporting 2x and two
cascaded GPU stages for 4x. The AMF interface exposes doubling only, so 6x is
disabled. NVIDIA uses `fruc_vulkan` and `VK_NV_optical_flow` with 2x/4x/6x output
rates. AMD motion search runs GPU shaders; NVIDIA uses its optical flow engine
and Vulkan synthesis shaders. These paths upload BGRA frames, generate frames
on the GPU, and download BGRA frames for the existing preview/SR/encoding path.
They do not imply GPU-only decode, color conversion, or encoding.
The hardware stage uses a microsecond time base so AMF's preserved input time
base can represent intermediate frames even with coarse AVI source timestamps.

The UI initially disables both choices. A background, process-cached probe
enumerates every physical DXGI adapter, checks the matching vendor and required
filter, then initializes the real device/filter graph and submits eight moving
test pictures at each supported multiplier, checking output count, pixel layout,
and strictly increasing timestamps. A vendor name alone never enables
the method. Successful probes are tied to the selected adapter and multiplier;
unavailable choices stay disabled with a localized reason. NVIDIA physical
devices are selected by name because Vulkan and DXGI use different indices.
The service repeats availability enforcement even for callers outside the UI.
Actual video dimensions are checked again when creating the conversion graph.
There is no CPU motion fallback for these methods. Scene protection controls are
hidden because these backends do not implement RIFE's protection policy. AMF is
configured to duplicate low-confidence frames rather than blend them. At EOF,
asynchronous GPU output is drained with cancellation and a bounded wait.

Read-only inspection on 2026-10-16 found `frc_amf`, `hwupload`, and `hwdownload`
in the bundled Windows FFmpeg, but no `fruc_vulkan`. Thus NVIDIA stays disabled
with this bundle even on a compatible GPU, until an ABI-compatible FFmpeg build
containing that filter is distributed. No DLLs were replaced. Vendor end-to-end
validation requires the corresponding physical GPU and has not been run for
this change; no build or smoke suite was run, as requested.

References: [FFmpeg AMF FRC](https://ffmpeg.org/ffmpeg-filters.html#frc_005famf),
[FFmpeg Vulkan FRUC](https://ffmpeg.org/ffmpeg-filters.html#fruc_005fvulkan),
[AMD AMF FRC interface](https://github.com/GPUOpen-LibrariesAndSDKs/AMF/blob/master/amf/public/include/components/FRC.h).

HDR input requires explicit SDR output for interpolation (GIF is already SDR).
The error explains this constraint. This avoids silently interpolating HDR as
8-bit SDR and claiming HDR preservation. SDR input can still use the existing
explicit SDR-to-HDR output mapping.

## Validation and performance

The native smoke tool supports `--interpolation-only`, `--interpolation-benchmark`,
`--interpolation-benchmark --full`, `--interpolation-order-benchmark`, and
`--editing-ui --interpolation-ui`. Regression coverage includes clocks, tails,
audio, 2x/4x/6x multipliers, non-aligned cuts, VFR, scene cuts, actual GPU/NPU/CPU
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
