using System.Diagnostics;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Text.Json;
using FancyToolAva.Models;
using FancyToolAva.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SkiaSharp;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    /// <summary>Validates one exported candidate using the application's hardware selection and bitmap tiling.</summary>
    private static int RunOnnxCandidate(string workDir, string[] args)
    {
        string Argument(string name) => args[Array.IndexOf(args, name) + 1];
        string root = Path.GetFullPath(Argument("--candidate-root"));
        string name = Argument("--candidate-name");
        bool directMl = args.Contains("--candidate-dml");
        CandidateTensor.ForceFp32 = args.Contains("--candidate-fp32");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, args.Contains("--candidate-packaged") ? "packaged-manifest.json" : "manifest.json")));
        var model = manifest.RootElement.GetProperty("models").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        string path = Path.Combine(root, model.GetProperty("path").GetString()!);
        string rgbPath = Path.Combine(root, model.GetProperty("rgbPath").GetString()!);
        int size = model.GetProperty("referenceSize").GetInt32();
        int channels = model.GetProperty("channels").GetInt32();
        int nativeScale = model.GetProperty("nativeScale").GetInt32();
        var checks = new List<object>();
        var benchmarks = new List<object>();
        string? device = null, fallbackReason = null, error = null;
        bool passed = true;
        var timer = Stopwatch.StartNew();
        try
        {
            using (var runner = new CandidateTensor(path, size, size, channels, directMl))
            {
                device = runner.Device.Name;
                fallbackReason = runner.Device.FallbackReason;
                double setupMs = timer.Elapsed.TotalMilliseconds;
                Console.WriteLine($"DEVICE {device}; setup={setupMs:F0}ms; fallback={fallbackReason}");
                foreach (var fixture in model.GetProperty("cases").EnumerateArray())
                {
                    string label = fixture.GetProperty("name").GetString()!;
                    string prefix = Path.Combine(root, "references", name, label);
                    var input = ReadFloats(prefix + "-input.f32");
                    var expected = ReadFloats(prefix + "-output.f32");
                    timer.Restart();
                    float[] actual = runner.Run(input, default);
                    double ms = timer.Elapsed.TotalMilliseconds;
                    if (actual.Length != expected.Length) throw new InvalidOperationException("Output shape mismatch");
                    double max = 0, sum = 0;
                    for (int i = 0; i < actual.Length; i++)
                    {
                        if (!float.IsFinite(actual[i])) throw new InvalidOperationException("Non-finite GPU output");
                        double delta = Math.Abs(actual[i] - expected[i]);
                        max = Math.Max(max, delta); sum += delta;
                    }
                    double mae = sum / actual.Length;
                    bool match = max <= .02 && mae <= .001;
                    passed &= match;
                    checks.Add(new { label, maxAbsoluteError = max, meanAbsoluteError = mae, milliseconds = ms, passed = match });
                    Console.WriteLine($"CHECK {label}: max={max:G5} mae={mae:G5} pass={match}");
                }
                // A cancelled request must not execute another tile.
                using var cancelled = new CancellationTokenSource();
                cancelled.Cancel();
                try { runner.Run(new float[channels * size * size], cancelled.Token); throw new InvalidOperationException("Cancellation ignored"); }
                catch (OperationCanceledException) { checks.Add(new { label = "pre-cancelled", passed = true }); }
            }
            if ((!directMl || args.Contains("--candidate-benchmark-dml")) && passed)
            {
                int window = model.GetProperty("window").GetInt32();
                using var tensor = new CandidateTensor(rgbPath, window, window, channels == 15 ? 15 : 3, directMl);
                using var upscaler = channels == 15 ? null : new VideoFrameUpscaler(new CandidateTile(tensor), tensor.Device, window, nativeScale);
                var testSizes = channels == 15 ? new[] { (320, 180) } : new[] { (640, 360), (1280, 720), (1920, 1080) };
                foreach (var dimensions in testSizes)
                {
                    var frames = CreateCandidateFrames(dimensions.Item1, dimensions.Item2, channels == 15 ? 5 : 1);
                    try
                    {
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                        SKBitmap Run() => upscaler != null ? upscaler.Upscale(frames[0], 2, deadline.Token)
                            : UpscaleEdvrCandidate(tensor, frames, window, deadline.Token);
                        timer.Restart();
                        double coldMs;
                        using (var cold = Run())
                        {
                            coldMs = timer.Elapsed.TotalMilliseconds;
                            using var image = SKImage.FromBitmap(cold);
                            using var png = image.Encode(SKEncodedImageFormat.Png, 100);
                            using var stream = File.Create(Path.Combine(workDir, $"{name}-{dimensions.Item2}p.png"));
                            png.SaveTo(stream);
                        }
                        var times = new double[3];
                        long allocated = GC.GetTotalAllocatedBytes();
                        for (int i = 0; i < times.Length; i++)
                        {
                            timer.Restart();
                            using var output = Run();
                            times[i] = timer.Elapsed.TotalMilliseconds;
                            if (output.Width != dimensions.Item1 * 2 || output.Height != dimensions.Item2 * 2)
                                throw new InvalidOperationException("Bitmap output dimensions mismatch");
                        }
                        Array.Sort(times);
                        var result = new { width = dimensions.Item1, height = dimensions.Item2, outputScale = 2, nativeScale,
                            tileWindow = window, coldMs, warmMedianMs = times[1], warmFps = 1000 / times[1],
                            allWarmMs = times, managedAllocatedBytesPerFrame = (GC.GetTotalAllocatedBytes() - allocated) / 3,
                            processPeakWorkingSetMiB = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576d };
                        benchmarks.Add(result);
                        Console.WriteLine("BENCH " + JsonSerializer.Serialize(result));
                    }
                    finally { foreach (var frame in frames) frame.Dispose(); }
                }
                if (channels != 15)
                {
                    var ffmpeg = new FfmpegService(NullLogger<FfmpegService>.Instance);
                    if (!ffmpeg.ValidateAsync().GetAwaiter().GetResult()) throw new InvalidOperationException(ffmpeg.LastError);
                    string input = Path.Combine(workDir, "integration-input.avi");
                    string output = Path.Combine(workDir, name + "-integration.mp4");
                    WriteTestAvi(input, 160, 120, 7, 1);
                    var service = new VideoTranscodeService(NullLogger<VideoTranscodeService>.Instance,
                        new CandidateVideoService(rgbPath, window, nativeScale, directMl));
                    service.TranscodeAsync(input, output, new VideoTranscodeOptions
                    {
                        SuperResolutionEnabled = true, SuperResolutionScale = 2,
                        SuperResolutionBackend = VideoSrBackend.Gpu,
                        VideoCodec = VideoCodec.H264, AudioCodec = AudioCodec.Aac,
                        HardwareBackend = HardwareBackend.Software, Preset = PresetLevel.Ultrafast,
                        RateControl = RateControlMode.Crf, Crf = 18, HdrOutput = VideoHdrOutput.ForceSdr
                    }).GetAwaiter().GetResult();
                    var probe = service.ProbeAsync(output).GetAwaiter().GetResult()!;
                    int count = ReadVideoTimes(output).Count;
                    bool valid = probe.Width == 320 && probe.Height == 240 && count == 7 && probe.HasAudio
                        && Math.Abs(probe.DurationMs - 1000) <= 200;
                    checks.Add(new { label = "application-transcode", passed = valid, frames = count,
                        width = probe.Width, height = probe.Height, probe.DurationMs, probe.HasAudio });
                    passed &= valid;
                }
            }
        }
        catch (Exception ex) { error = ex.ToString(); passed = false; Console.WriteLine(error); }
        var record = new { name, backend = directMl ? "DirectML-strict" : CandidateTensor.ForceFp32 ? "OpenVINO-FP32-strict" : "ApplicationGpu-strict", device, fallbackReason,
            passed, error, checks, benchmarks, utc = DateTimeOffset.UtcNow,
            scope = "FP32 CPU-reference comparison; warm synthetic BGRA-to-bitmap path, no decoding/encoding; CPU EP fallback disabled" };
        File.WriteAllText(Path.Combine(workDir, name + (directMl ? "-dml" : "-gpu") + ".json"),
            JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }

    /// <summary>Reads a little-endian FP32 reference tensor.</summary>
    private static float[] ReadFloats(string path) => MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(path)).ToArray();

    /// <summary>Creates deterministic moving coloured edges and gradients for bitmap-path measurements.</summary>
    private static SKBitmap[] CreateCandidateFrames(int width, int height, int count)
    {
        var frames = new SKBitmap[count];
        for (int f = 0; f < count; f++)
        {
            var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
            var pixels = bitmap.GetPixelSpan();
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int index = y * bitmap.RowBytes + x * 4;
                pixels[index] = (byte)((x + y + f * 3) % 127 + 40);
                pixels[index + 1] = (byte)(y * 210 / height + 20);
                pixels[index + 2] = (byte)(x * 210 / width + 20);
                pixels[index + 3] = 255;
            }
            using var canvas = new SKCanvas(bitmap);
            using var paint = new SKPaint { Color = SKColors.Gold, IsAntialias = true, StrokeWidth = 2 };
            canvas.DrawCircle(width * .4f + f * 3, height * .5f, height * .18f, paint);
            paint.Color = SKColors.White;
            for (int j = 0; j < 12; j++) canvas.DrawLine(j * width / 12, height, width, j * height / 12, paint);
            frames[f] = bitmap;
        }
        return frames;
    }

    /// <summary>Measures EDVR with five distinct neighbours, using the application's padding and output assembly.</summary>
    private static SKBitmap UpscaleEdvrCandidate(CandidateTensor tensor, SKBitmap[] frames, int window, CancellationToken ct)
    {
        const int pad = 32;
        int tile = window - 2 * pad, channel = 3 * window * window;
        var input = new float[channel * 5];
        var tileOutput = new float[3 * window * window * 16];
        var result = new SKBitmap(new SKImageInfo(frames[0].Width * 2, frames[0].Height * 2, SKColorType.Bgra8888, SKAlphaType.Opaque));
        try
        {
            for (int y = 0; y < frames[0].Height; y += tile)
            for (int x = 0; x < frames[0].Width; x += tile)
            {
                ct.ThrowIfCancellationRequested();
                for (int f = 0; f < 5; f++) VideoFrameUpscaler.FillInput(frames[f], x, y, window, pad, input.AsSpan(f * channel, channel));
                tensor.Run(input, ct, tileOutput);
                VideoFrameUpscaler.PasteOutput(result, x, y, Math.Min(tile, frames[0].Width - x), Math.Min(tile, frames[0].Height - y), 2, window, pad, 4, tileOutput);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    /// <summary>Runs fixed-shape FP32 tensors with the same Windows ML factory and mandatory hardware execution.</summary>
    private sealed class CandidateTensor : IDisposable
    {
        internal static bool ForceFp32;
        private readonly InferenceSession _session;
        private readonly float[] _data;
        private readonly Float16[]? _halfData;
        private readonly OrtValue _value;
        private readonly Dictionary<string, OrtValue> _inputs;
        private readonly string[] _outputs;
        internal VideoSrDevice Device { get; }

        internal CandidateTensor(string path, int width, int height, int channels, bool directMl)
        {
            // Initialize the app-local Windows ML runtime before creating native
            // options, just as the normal vendor-provider path does.
            _ = OrtEnv.Instance();
            Console.WriteLine($"SESSION start: {(directMl ? "DirectML" : "ApplicationGpu")} {Path.GetFileName(path)} {width}x{height}");
            var dimensions = new Dictionary<string, long> { ["batch"] = 1, ["batch_size"] = 1,
                ["height"] = height, ["width"] = width, ["h"] = height, ["w"] = width };
            VideoSrDevice device;
            _session = ForceFp32 && !directMl ? CreateOpenVinoFp32(path, dimensions, out device)
                : directMl ? WindowsMlAccelerator.CreateDirectMlTensorSession(path, dimensions, default, out device, true)
                : WindowsMlAccelerator.CreateTensorSession(path, VideoSrBackend.Gpu, dimensions, default, out device, true);
            Device = device;
            try
            {
                var metadata = _session.InputMetadata.Single();
                _data = new float[channels * width * height];
                long[] shape = [1, channels, height, width];
                if (metadata.Value.ElementDataType == TensorElementType.Float16)
                {
                    _halfData = new Float16[_data.Length];
                    _value = OrtValue.CreateTensorValueFromMemory(_halfData, shape);
                }
                else if (metadata.Value.ElementDataType == TensorElementType.Float)
                    _value = OrtValue.CreateTensorValueFromMemory(_data, shape);
                else throw new NotSupportedException("Candidate requires FP16/FP32 I/O");
                _inputs = new Dictionary<string, OrtValue> { [metadata.Key] = _value };
                _outputs = [_session.OutputMetadata.Single().Key];
            }
            catch { _session.Dispose(); throw; }
        }

        internal float[] Run(float[] data, CancellationToken ct, float[]? destination = null)
        {
            ct.ThrowIfCancellationRequested();
            if (_halfData != null) TensorPrimitives.ConvertToHalf(data, MemoryMarshal.Cast<Float16, Half>(_halfData.AsSpan()));
            else data.CopyTo(_data, 0);
            using var options = new RunOptions();
            using var registration = ct.Register(() => options.Terminate = true);
            try
            {
                using var output = _session.Run(options, _inputs, _outputs);
                if (output[0].GetTensorTypeAndShape().ElementDataType == TensorElementType.Float16)
                {
                    var half = MemoryMarshal.Cast<Float16, Half>(output[0].GetTensorDataAsSpan<Float16>());
                    destination ??= new float[half.Length];
                    if (destination.Length != half.Length) throw new InvalidOperationException("Output tensor length mismatch");
                    TensorPrimitives.ConvertToSingle(half, destination);
                    return destination;
                }
                var result = output[0].GetTensorDataAsSpan<float>();
                if (destination == null) return result.ToArray();
                if (result.Length != destination.Length) throw new InvalidOperationException("Output tensor length mismatch");
                result.CopyTo(destination);
                return destination;
            }
            catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        }

        /// <inheritdoc />
        public void Dispose() { _value.Dispose(); _session.Dispose(); }

        /// <summary>Isolates precision-sensitive models without changing the application's provider policy.</summary>
        private static InferenceSession CreateOpenVinoFp32(string path, IReadOnlyDictionary<string, long> dimensions, out VideoSrDevice device)
        {
#if WINDOWS
            var env = OrtEnv.Instance();
            var catalog = Microsoft.Windows.AI.MachineLearning.ExecutionProviderCatalog.GetDefault();
            if (catalog != null) catalog.RegisterCertifiedAsync().AsTask().GetAwaiter().GetResult();
            var candidate = env.GetEpDevices().First(d => d.EpName == "OpenVINOExecutionProvider"
                && d.HardwareDevice.Type == OrtHardwareDeviceType.GPU);
            using var options = new SessionOptions { EnableMemoryPattern = false, EnableCpuMemArena = false,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            foreach (var dimension in dimensions) options.AddFreeDimensionOverrideByName(dimension.Key, dimension.Value);
            options.AddSessionConfigEntry("session.disable_cpu_ep_fallback", "1");
            options.AppendExecutionProvider(env, [candidate], new Dictionary<string, string>
                { ["load_config"] = "{\"GPU\":{\"INFERENCE_PRECISION_HINT\":\"f32\"}}" });
            device = new VideoSrDevice("GPU · Windows ML / OpenVINO (FP32 hint)", true);
            return new InferenceSession(path, options);
#else
            throw new PlatformNotSupportedException();
#endif
        }
    }

    /// <summary>Adapts a strict candidate session to the production bitmap tile runner.</summary>
    private sealed class CandidateTile(CandidateTensor tensor) : IVideoSrTileModel
    {
        /// <inheritdoc />
        public void Infer(float[] input, float[] output, CancellationToken ct) => tensor.Run(input, ct, output);
        /// <inheritdoc />
        public void Dispose() { }
    }

    /// <summary>Supplies an experimental RGB session to the unmodified application video pipeline.</summary>
    private sealed class CandidateVideoService(string path, int window, int nativeScale, bool directMl) : IVideoSuperResolutionService
    {
        /// <inheritdoc />
        public event Action<ModelDownloadProgress>? DownloadProgressChanged { add { } remove { } }
        /// <inheritdoc />
        public event Action<VideoSrDevice>? DeviceChanged { add { } remove { } }
        /// <inheritdoc />
        public bool IsModelAvailable(VideoSrModel model) => true;
        /// <inheritdoc />
        public Task EnsureModelAsync(VideoSrModel model, CancellationToken ct = default) => Task.CompletedTask;
        /// <inheritdoc />
        public Task InstallAccelerationAsync(CancellationToken ct = default) => throw new NotSupportedException();
        /// <inheritdoc />
        public Task<IVideoFrameUpscaler> CreateSessionAsync(VideoTranscodeOptions options, CancellationToken ct = default)
            => Task.FromResult<IVideoFrameUpscaler>(new OwnedCandidateFrame(path, window, nativeScale, directMl));
    }

    /// <summary>Owns both the experimental tensor session and production bitmap runner for a transcode.</summary>
    private sealed class OwnedCandidateFrame : IVideoFrameUpscaler
    {
        private readonly CandidateTensor _tensor;
        private readonly VideoFrameUpscaler _upscaler;
        /// <inheritdoc />
        public VideoSrDevice Device => _tensor.Device;
        internal OwnedCandidateFrame(string path, int window, int nativeScale, bool directMl)
        {
            _tensor = new CandidateTensor(path, window, window, 3, directMl);
            _upscaler = new VideoFrameUpscaler(new CandidateTile(_tensor), _tensor.Device, window, nativeScale);
        }
        /// <inheritdoc />
        public SKBitmap Upscale(SKBitmap source, int scale, CancellationToken ct) => _upscaler.Upscale(source, scale, ct);
        /// <inheritdoc />
        public void Dispose() { _upscaler.Dispose(); _tensor.Dispose(); }
    }
}
