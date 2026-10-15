# AudioSR ONNX export reproduction

Vendored MIT toolkit from santiquiroz/port-audiosr-onnx, revision bc6a34b01cb3a7c28f27191090102a480d3e2842. See LICENSE.txt and Libraries/AudioSR/NOTICE.md. This is a developer tool, not a Python runtime shipped with FancyTool.

Requires Python 3.11 for the upstream dependency pins. Run from this directory:

```powershell
pwsh -File toolkit/setup-env.ps1
.venv/Scripts/python.exe toolkit/export_components.py
.venv/Scripts/python.exe toolkit/validate_ort.py --require-dml
```

The exporter loads the official AudioSR basic checkpoint, exports the conditioner, UNet, decoder and vocoder, and writes numpy constants plus PyTorch reference tensors. UNet uses opset 18/dynamo; the other graphs use opset 17. Generated artifacts and environments are ignored by Git.

FancyTool currently consumes the already-exported models-v1.0 pack. Do not claim a fresh local export was performed merely because the application can run that pack. New exports require parity validation and new pinned SHA-256 entries in ModelManifest before distribution.
