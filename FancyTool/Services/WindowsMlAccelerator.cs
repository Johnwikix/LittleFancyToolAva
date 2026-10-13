using Microsoft.ML.OnnxRuntime;
#if WINDOWS
using Microsoft.Windows.AI.MachineLearning;
#endif

namespace FancyToolAva.Services;

/// <summary>Shares Windows ML provider discovery and explicit GPU/NPU selection between images and videos.</summary>
internal static class WindowsMlAccelerator
{
#if WINDOWS
    private static readonly SemaphoreSlim CatalogGate = new(1, 1);
    private static readonly Lazy<Task> Registration = new(() => Task.Run(RegisterInstalledAsync));

    private static async Task RegisterInstalledAsync()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) return;
        await CatalogGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _ = OrtEnv.Instance();
            var catalog = ExecutionProviderCatalog.GetDefault();
            if (catalog != null) await catalog.RegisterCertifiedAsync();
        }
        catch (Exception) { /* Offline, older drivers or restricted processes still have DirectML. */ }
        finally { CatalogGate.Release(); }
    }
#endif

    /// <summary>Creates a fixed-shape session using an installed vendor provider, with DirectML GPU fallback.</summary>
    internal static InferenceSession CreateSession(string path, VideoSrBackend backend, int window, CancellationToken ct, out VideoSrDevice device)
    {
#if WINDOWS
        Registration.Value.WaitAsync(ct).GetAwaiter().GetResult();
        var env = OrtEnv.Instance();
        var kind = backend == VideoSrBackend.PreferNpu ? OrtHardwareDeviceType.NPU : OrtHardwareDeviceType.GPU;
        var failures = new List<string>();
        if (backend != VideoSrBackend.Cpu)
        {
            // Prefer vendor-optimized providers. DirectML remains available for older AMD/NVIDIA cards.
            var candidates = env.GetEpDevices().Where(d => d.HardwareDevice.Type == kind && d.EpName is "NvTensorRtRtxExecutionProvider" or "MIGraphXExecutionProvider" or "OpenVINOExecutionProvider" or "VitisAIExecutionProvider" or "QNNExecutionProvider")
                .OrderBy(d => d.EpName switch { "NvTensorRtRtxExecutionProvider" => 0, "MIGraphXExecutionProvider" => 1, "OpenVINOExecutionProvider" => 2, _ => 3 });
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var options = CreateOptions(window);
                    // OpenVINO reads this flag for its own compile-time CPU fallback too.
                    // Let the application perform and report hardware fallback explicitly.
                    options.AddSessionConfigEntry("session.disable_cpu_ep_fallback", "1");
                    options.AppendExecutionProvider(env, [candidate], new Dictionary<string, string>());
                    var session = new InferenceSession(path, options);
                    device = new VideoSrDevice($"{kind} · Windows ML / {candidate.EpName.Replace("ExecutionProvider", "")}", true);
                    return session;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { failures.Add(ex.Message); }
            }
        }
        if (backend == VideoSrBackend.PreferNpu)
            throw new PlatformNotSupportedException("Windows ML NPU provider unavailable. " + string.Join("; ", failures));
#endif
        using var fallbackOptions = CreateOptions(window);
        if (backend == VideoSrBackend.Cpu)
        {
            fallbackOptions.AppendExecutionProvider_CPU(0);
            device = new VideoSrDevice("CPU", false);
        }
        else
        {
            var gpu = VideoSrGpuSelector.Select();
            fallbackOptions.AppendExecutionProvider_DML(gpu.Index);
            device = new VideoSrDevice("GPU · " + gpu.Name + " / Windows ML DirectML", true);
        }
        return new InferenceSession(path, fallbackOptions);
    }

    private static SessionOptions CreateOptions(int window)
    {
        var options = new SessionOptions
        {
            EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            EnableCpuMemArena = false, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        // Static shapes allow provider compilation and reuse instead of re-specializing every tile.
        options.AddFreeDimensionOverrideByName("batch_size", 1);
        options.AddFreeDimensionOverrideByName("batch", 1);
        options.AddFreeDimensionOverrideByName("height", window);
        options.AddFreeDimensionOverrideByName("width", window);
        options.AddFreeDimensionOverrideByName("in_height", window);
        options.AddFreeDimensionOverrideByName("in_width", window);
        return options;
    }

    /// <summary>Installs compatible certified vendor providers through Microsoft's catalog when explicitly requested.</summary>
    internal static async Task InstallAsync(Action<string>? progress, CancellationToken ct)
    {
#if WINDOWS
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
            throw new PlatformNotSupportedException("Windows ML hardware providers require Windows 11 24H2 or later.");
        await CatalogGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _ = OrtEnv.Instance();
            var catalog = ExecutionProviderCatalog.GetDefault() ?? throw new InvalidOperationException("Windows ML catalog unavailable");
            var providers = catalog.FindAllProviders() ?? throw new InvalidOperationException("Windows ML provider discovery failed");
            foreach (var provider in providers.Where(p => p.Name is "OpenVINOExecutionProvider" or "NvTensorRtRtxExecutionProvider" or "MIGraphXExecutionProvider" or "VitisAIExecutionProvider" or "QNNExecutionProvider"))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke(provider.Name.Replace("ExecutionProvider", ""));
                var result = await provider.EnsureReadyAsync().AsTask(ct).ConfigureAwait(false);
                if (result.Status != ExecutionProviderReadyResultState.Success)
                    throw new InvalidOperationException($"Windows ML {provider.Name}: {result.Status}, {result.ExtendedError?.Message} {result.DiagnosticText}");
                if (!provider.TryRegister()) throw new InvalidOperationException($"Windows ML {provider.Name}: registration failed");
            }
        }
        finally { CatalogGate.Release(); }
#else
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("Windows ML requires Windows");
#endif
    }
}
