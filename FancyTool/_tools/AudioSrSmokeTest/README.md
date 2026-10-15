# AudioSR regression

No Python or PyTorch is needed by the application. Python is used here only as an independent numerical oracle. dsp_reference.py is the MIT numpy reference from santiquiroz/upflow; attribution is in Libraries/AudioSR/NOTICE.md.

Choose a writable QA root. The native test downloads/verifies the pinned pack under <qa>/models-fp32 through ModelDownloadService; use a dedicated QA directory, because fixtures and output files are rewritten.

```powershell
dotnet run --project _tools/AudioSrSmokeTest -c Release -p:PublishAot=false -- <qa> --real --backend=Gpu --steps=50 --multiplier=4 --input=D:/audioTest/example.mp3
```

The real-input path decodes the file and tests a one-second stereo segment. Without --input it uses a short mono signal. --cancel triggers cancellation after the first real DDIM step and checks temporary-file cleanup and preservation of a dedicated target. --backend=Cpu tests the software path. Native tests are smoke/contract tests, not perceptual quality measurements or steady-state benchmarks.

Generate independent numerical vectors after the model constants exist:

```powershell
python -m pip install -r _tools/AudioSrSmokeTest/requirements-reference.txt
python _tools/AudioSrSmokeTest/generate_vectors.py <qa>
dotnet run --project _tools/AudioSrSmokeTest -c Release -p:PublishAot=false -- <qa>
```

Numerical tests cover normalization, STFT, mel, Butterworth/resample preprocessing, low-band anchoring, DDIM, schedules, legacy video tensor dimensions, manifest completeness, silent-input bypass, stereo coherence and resource disposal. Runtime download and native tests additionally exercise actual model hashes, providers, codec output and cancellation. Tests do not establish bit parity between the full stochastic AudioSR pipeline and PyTorch.
