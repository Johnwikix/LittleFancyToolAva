# AudioSR attribution

Model and original algorithms: Haohe Liu et al., https://github.com/haoheliu/versatile_audio_super_resolution (MIT; AudioSR.txt).
ONNX decomposition/export toolkit and numpy numerical references: Santiago Quiroz, https://github.com/santiquiroz/port-audiosr-onnx and https://github.com/santiquiroz/upflow (MIT; AudioSR-ONNX.txt).
The C# frontend, diffusion driver and graph adapter use these model contracts and numerical algorithms. The numpy regression reference and export toolkit retain the upstream MIT terms.

Pinned exporter revision: bc6a34b01cb3a7c28f27191090102a480d3e2842.
Pinned downloadable model release: models-v1.0. All eight assets, including ddpm.onnx.data, are SHA-256 pinned in Services/ModelManifest.cs.
Runtime inference is C# + ONNX Runtime/Windows ML. Python/PyTorch are only needed to reproduce exports; they are not application dependencies.
