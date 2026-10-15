using FancyToolAva.AudioEnhancement;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace FancyToolAva.Services;

/// <summary>Installs pinned AudioSR graphs through the shared downloader and bounds active model memory.</summary>
public sealed class AudioSrModelService(ModelDownloadService downloader, ILogger<AudioSrModelService> logger)
{
    private readonly ILogger<AudioSrModelService> _logger = logger;
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    /// <summary>Reports download, validation and graph loading progress.</summary>
    public event Action<ModelDownloadProgress>? DownloadProgressChanged;
    /// <summary>Reports the actual execution provider for each neural graph.</summary>
    public event Action<VideoSrDevice>? DeviceChanged;
    /// <summary>Reports completed diffusion steps within the current window and channel.</summary>
    public event Action<int, int>? InferenceProgressChanged;
    /// <summary>Gets the managed model directory, resolved when a storage lease is held.</summary>
    public static string DirectoryPath => Environment.GetEnvironmentVariable("FANCYTOOL_AUDIOSR_MODELS")
        ?? Path.Combine(AppPaths.ModelsDirectory, "AudioSR", "models-v1.0");
    /// <summary>Gets whether all required files exist; creation additionally verifies their hashes.</summary>
    public bool IsModelAvailable => ModelManifest.AudioSr.All(entry => File.Exists(Path.Combine(DirectoryPath, entry.FileName)));

    /// <summary>Downloads and verifies the complete FP32 model pack without creating inference sessions.</summary>
    public Task EnsureModelAsync(CancellationToken ct = default) => Task.Run(() => downloader.UseModelsAsync(
        ModelManifest.AudioSr, () => DirectoryPath, _ => true, DownloadProgress(), ct), ct);

    /// <summary>Installs the same Windows ML hardware providers used by image and video enhancement.</summary>
    public Task InstallAccelerationAsync(CancellationToken ct = default) => WindowsMlAccelerator.InstallAsync(
        name => DownloadProgressChanged?.Invoke(new(name, ModelDownloadStage.Connecting, 0, null,
            LocalizationRegistry.Get("VideoTranscode.Sr_InstallingHardware") + " · " + name)), ct);

    internal IAudioUpsamplingProcessor CreateProcessor(AudioUpsamplingRequest request, CancellationToken ct)
    {
        var options = request.Inference ?? new AudioUpsamplingInferenceOptions();
        AudioSrPipeline.Timesteps(options.Steps);
        if (!Enum.IsDefined(options.Acceleration)) throw new ArgumentOutOfRangeException(nameof(request));
        _inferenceGate.Wait(ct);
        try
        {
            return downloader.UseModelsAsync<IAudioUpsamplingProcessor>(ModelManifest.AudioSr, () => DirectoryPath, root =>
            {
                double[] basis = AudioSrDsp.LoadNpy(Path.Combine(root, "mel_basis.npy"), 256 * 1025);
                double[] alphas = AudioSrDsp.LoadNpy(Path.Combine(root, "alphas_cumprod.npy"), 1000);
                if (alphas.Any(v => v < 0 || v >= 1) || alphas.Take(999).Any(v => v == 0)) throw new InvalidDataException("Invalid AudioSR schedule.");
                var graphs = new Graphs(root, options, this, ct);
                try
                {
                    return new AudioSrUpsamplingBackend.Processor(request.Source.Channels, graphs, basis, alphas, options,
                        () => _inferenceGate.Release(), (done, total) => InferenceProgressChanged?.Invoke(done, total));
                }
                catch { graphs.Dispose(); throw; }
            }, DownloadProgress(), ct).GetAwaiter().GetResult();
        }
        catch { _inferenceGate.Release(); throw; }
    }

    private IProgress<ModelDownloadProgress> DownloadProgress() => new Progress<ModelDownloadProgress>(
        item => DownloadProgressChanged?.Invoke(item));

    private sealed class Graphs : IAudioSrGraphs
    {
        private readonly Dictionary<string, InferenceSession> _sessions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string[]> _outputs = new(StringComparer.Ordinal);
        private bool _disposed;
        private readonly AudioSrModelService _owner;
        private readonly AudioUpsamplingInferenceOptions _options;
        private readonly HashSet<string> _software = new(StringComparer.Ordinal);
        internal Graphs(string directory, AudioUpsamplingInferenceOptions options, AudioSrModelService owner, CancellationToken ct)
        {
            _owner = owner;
            _options = options;
            try
            {
                foreach (string name in new[] { "vae_feature_extract", "ddpm", "vae_decoder", "vocoder" })
                {
                    ct.ThrowIfCancellationRequested();
                    owner.DownloadProgressChanged?.Invoke(new(name, ModelDownloadStage.Connecting, 0, null,
                        LocalizationRegistry.Get("AudioTranscode.Sr_LoadingGraph", name)));
                    var preferred = options.Acceleration switch
                    {
                        VideoSrBackend.PreferNpu => new[] { VideoSrBackend.PreferNpu, VideoSrBackend.Gpu },
                        VideoSrBackend.Auto => new[] { VideoSrBackend.Gpu, VideoSrBackend.PreferNpu },
                        _ => new[] { options.Acceleration }
                    };
                    var backends = options.AllowCpuFallback ? preferred.Append(VideoSrBackend.Cpu).Distinct() : preferred;
                    var failures = new List<string>();
                    foreach (var backend in backends)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var dimensions = new Dictionary<string, long> { ["batch"] = 1, ["batch_size"] = 1, ["t"] = 64, ["frames"] = 512 };
                            var session = WindowsMlAccelerator.CreateTensorSession(Path.Combine(directory, name + ".onnx"),
                                backend, dimensions, ct, out var selected);
                            _sessions.Add(name, session);
                            if (backend == VideoSrBackend.Cpu) _software.Add(name);
                            _outputs.Add(name, session.OutputNames.ToArray());
                            var device = selected with { Name = name + " · " + selected.Name,
                                FallbackReason = failures.Count > 0 ? string.Join("; ", failures) : null };
                            owner.DeviceChanged?.Invoke(device);
                            owner._logger.LogInformation("AudioSR graph {Graph}: {Device}; fallback {Fallback}", name, selected.Name, device.FallbackReason);
                            break;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error)
                        {
                            failures.Add(error.Message);
                            owner._logger.LogWarning(error, "AudioSR {Graph} provider {Backend} unavailable", name, backend);
                        }
                    }
                    if (!_sessions.ContainsKey(name)) throw new InvalidOperationException("AudioSR " + name + ": " + string.Join("; ", failures));
                }
                ct.ThrowIfCancellationRequested();
            }
            catch { Dispose(); throw; }
        }

        public float[] Run(string graph, CancellationToken ct, params AudioSrTensor[] inputs)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            var values = new Dictionary<string, OrtValue>(StringComparer.Ordinal);
            try
            {
                foreach (var input in inputs)
                    values.Add(input.Name, input.Data switch
                    {
                        float[] f => OrtValue.CreateTensorValueFromMemory(f, input.Shape),
                        long[] l => OrtValue.CreateTensorValueFromMemory(l, input.Shape),
                        _ => throw new NotSupportedException("Unsupported AudioSR tensor type.")
                    });
                using var run = new RunOptions();
                using var cancellation = ct.Register(() => run.Terminate = true);
                try
                {
                    float[] Execute()
                    {
                        using var result = _sessions[graph].Run(run, values, _outputs[graph]);
                        ct.ThrowIfCancellationRequested();
                        return result[0].GetTensorDataAsSpan<float>().ToArray();
                    }
                    try { return Execute(); }
                    catch (OnnxRuntimeException error) when (!ct.IsCancellationRequested && _options.AllowCpuFallback && !_software.Contains(graph))
                    {
                        // Some providers compile successfully but reject an operator or allocation at Run.
                        // Retry this graph once on CPU; a prohibited fallback remains an explicit failure.
                        _sessions[graph].Dispose();
                        using var access = ModelStorage.Acquire(ct);
                        var session = WindowsMlAccelerator.CreateTensorSession(Path.Combine(DirectoryPath, graph + ".onnx"),
                            VideoSrBackend.Cpu, new Dictionary<string, long> { ["batch"] = 1, ["batch_size"] = 1, ["t"] = 64, ["frames"] = 512 }, ct, out _);
                        _sessions[graph] = session;
                        _software.Add(graph);
                        _owner.DeviceChanged?.Invoke(new(graph + " · CPU", false, error.Message));
                        _owner._logger.LogWarning(error, "AudioSR {Graph} fell back to CPU after inference failure", graph);
                        return Execute();
                    }
                }
                catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
            }
            finally { foreach (var value in values.Values) value.Dispose(); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Exception? failure = null;
            foreach (var session in _sessions.Values)
            {
                try { session.Dispose(); } catch (Exception error) { failure ??= error; }
            }
            _sessions.Clear(); _outputs.Clear(); _software.Clear();
            if (failure != null) throw failure;
        }
    }
}
