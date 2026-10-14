# Model storage and HDR output

Settings owns the default model root. `ModelStorageDirectory` is saved immediately;
`ApplicationHostService.LoadState` resolves it before constructing model consumers.
The active root is captured for the lifetime of the process, so changing settings
cannot redirect an in-flight download or invalidate an inference session. Changing
the directory requires restart. Existing models are never moved or deleted.
Image and video models share this root; default lyrics models use `Qwen3Lyrics`.
Old lyrics queues that stored the default path explicitly follow the new root.
Explicit lyrics overrides and `FANCYTOOL_LYRICS_MODELS` remain supported.

The settings GPU toggle is removed. Image acceleration defaults to NPU priority
and enables GPU fallback, followed by CPU. The old serialized DirectML toggle is
ignored; it cannot leave an invisible CPU-only setting. Memory tiers also apply
to software inference and remain selectable on other platforms.

Video AI enhancement controls are children of the Quality tab. HDR output is a
persistent three-way selection, resolved independently for each source in a batch.
PQ and HLG source flags are read from FFmpeg color-transfer metadata. Same-as-source
keeps the dynamic-range class; forcing HDR produces BT.2020/PQ with a supported
10-bit pixel format, and forcing SDR produces BT.709 with actual tone mapping.
An incompatible encoder/profile fails instead of silently reducing HDR to 8-bit.
GIF output is always tone-mapped to SDR.

SDR-to-HDR uses ITU-R BT.2446-1 Method A, section 4.2 / Table 4, implemented as a
locally generated 65³ tetrahedral LUT. It converts nonlinear BT.2020 SDR RGB into
linear RGB normalized to a 1000-nit mastering target, before PQ/HLG encoding.
The cached LUT lives under the selected model root in `ColorLuts` and needs no
neural weights. HDR-to-SDR uses float linear-light conversion and FFmpeg Mobius
tone mapping before gamut/transfer conversion and dithering. Generated HDR carries
1000-nit mastering/MaxCLL metadata; MaxFALL is left unknown rather than invented.
Source HDR mastering metadata is retained when no range expansion is performed;
HDR metadata is removed from SDR outputs and stale dynamic metadata is discarded.

Existing super-resolution models operate on SDR images. HDR inputs are tone-mapped
before neural enhancement and mapped back when HDR output is requested; this is
not a lossless HDR round trip. Source/progress previews are tone-mapped for the SDR
UI. Inverse tone mapping expands highlights but cannot guarantee recovery of
information already clipped from SDR material.

Validation: `_tools/FfmpegSmokeTest --hdr-only` checks PQ/HLG, source matching,
both conversion directions, actual encoded highlight luminance, encoder limits,
HDR previews, GIF and enhancement composition. `--settings-only` checks startup,
persistence/rollback, defaults, model consumers, legacy queues and native layouts.

Sources: [ITU-R BT.2446-1](https://www.itu.int/dms_pub/itu-r/opb/rep/R-REP-BT.2446-1-2021-PDF-E.pdf),
[FFmpeg filters](https://ffmpeg.org/ffmpeg-filters.html),
[ITM-LUT research implementation, MPL-2.0 code](https://github.com/AndreGuo/ITMLUT).
ITM-LUT is an alternative research model, not bundled in this implementation.
