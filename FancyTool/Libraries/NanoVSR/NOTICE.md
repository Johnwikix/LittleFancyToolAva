# NanoVSR

Authors: Filip Pawlicki, Marcel Kańduła, Marcin Pucek, Kamil Dobies.

Source: https://github.com/filippawlicki/nanovsr
Pinned source commit: feab9d259977ace3be4fca659394c3043bcc34c5
Checkpoint: https://github.com/filippawlicki/nanovsr/releases/download/v1.0/nanovsr_644k.pth
License: MIT (LICENSE.txt).

The bundled ONNX is a derivative of the authors' 644k checkpoint, exported using
`FancyTool/_tools/NanoVsrExport/export_nanovsr.py`. RepVGG branches are fused;
input/output are FP16 RGB [1,5,3,H,W] / [1,5,3,4H,4W], opset 17. Spatial dimensions
are dynamic and specialized by Windows ML. Time is fixed at five adjacent frames.
This is a bounded bidirectional temporal window, not independent image inference.
Only the last incomplete window repeats its final frame. Chunk boundaries do not
share recurrent state. Both 2x (box-downsampled native 4x) and 4x output are supported.

The source paper's 15-frame, 320x180 Jetson benchmark does not describe this
integration's speed or guarantee real-time HD processing. See ADR 0007 for validation.
