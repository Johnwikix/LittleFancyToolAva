using Microsoft.ML.OnnxRuntime;
#if WINDOWS
using Microsoft.Windows.AI.MachineLearning;
#endif

namespace FancyToolAva.Services;

/// <summary>Shares Windows ML provider discovery and explicit GPU/NPU selection between images, video and audio.</summary>
internal static class WindowsMlAccelerator
{
    // Catalog/EP ABI spelling differs from the older built-in provider's casing.
    internal static bool IsTensorRtProvider(string name) =>
        name.Equals("NvTensorRTRTXExecutionProvider", StringComparison.OrdinalIgnoreCase);

    private static bool IsVendorProvider(string name) => IsTensorRtProvider(name)
        || name is "MIGraphXExecutionProvider" or "OpenVINOExecutionProvider" or "VitisAIExecutionProvider" or "QNNExecutionProvider";
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
        => CreateSession(path, backend, window, window, ct, out device);

    /// <summary>Creates a session specialized for one rectangular video frame size.</summary>
    internal static InferenceSession CreateSession(string path, VideoSrBackend backend, int width, int height,
        CancellationToken ct, out VideoSrDevice device, bool requireHardware = false, bool? useFp16 = null, bool useCudaGraph = true)
        => CreateTensorSession(path, backend, VideoDimensions(width, height), ct, out device, requireHardware, useFp16, useCudaGraph);

    /// <summary>Creates an inference session for explicit named tensor dimensions, independently of media type.</summary>
    internal static InferenceSession CreateTensorSession(string path, VideoSrBackend backend,
        IReadOnlyDictionary<string, long> dimensions, CancellationToken ct, out VideoSrDevice device, bool requireHardware = false, bool? useFp16 = null, bool useCudaGraph = true)
    {
        _ = OrtEnv.Instance();
        if (backend == VideoSrBackend.DirectMl)
            return CreateDirectMlTensorSession(path, dimensions, ct, out device, requireHardware);
#if WINDOWS
        Registration.Value.WaitAsync(ct).GetAwaiter().GetResult();
        var env = OrtEnv.Instance();
        var kind = backend == VideoSrBackend.PreferNpu ? OrtHardwareDeviceType.NPU : OrtHardwareDeviceType.GPU;
        var failures = new List<string>();
        if (backend != VideoSrBackend.Cpu)
        {
            // Prefer vendor-optimized providers. DirectML remains available for older AMD/NVIDIA cards.
            var candidates = env.GetEpDevices().Where(d => d.HardwareDevice.Type == kind && IsVendorProvider(d.EpName))
                .OrderBy(d => IsTensorRtProvider(d.EpName) ? 0 : d.EpName switch { "MIGraphXExecutionProvider" => 1, "OpenVINOExecutionProvider" => 2, _ => 3 });
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var options = CreateOptions(dimensions);
                    // OpenVINO reads this flag for its own compile-time CPU fallback too.
                    // Let the application perform and report hardware fallback explicitly.
                    options.AddSessionConfigEntry("session.disable_cpu_ep_fallback", "1");
                    // RIFE's FP32 interchange graph can use Intel GPU FP16 compute
                    // without rewriting its weights or changing its tensor contract.
                    var providerOptions = (requireHardware || useFp16.HasValue) && kind == OrtHardwareDeviceType.GPU && candidate.EpName == "OpenVINOExecutionProvider"
                        ? new Dictionary<string, string> { ["load_config"] = useFp16 == false
                            ? "{\"GPU\":{\"INFERENCE_PRECISION_HINT\":\"f32\"}}" : "{\"GPU\":{\"INFERENCE_PRECISION_HINT\":\"f16\"}}" }
                        : new Dictionary<string, string>();
                    if (IsTensorRtProvider(candidate.EpName))
                    {
                        string cache = Path.Combine(AppPaths.ModelsDirectory, "Cache", "TensorRT-RTX");
                        Directory.CreateDirectory(cache);
                        providerOptions["nv_runtime_cache_path"] = cache;
                        providerOptions["enable_cuda_graph"] = useCudaGraph ? "1" : "0";
                    }
                    options.AppendExecutionProvider(env, [candidate], providerOptions);
                    var session = new InferenceSession(path, options);
                    device = new VideoSrDevice($"{kind} · Windows ML / {candidate.EpName.Replace("ExecutionProvider", "")}" +
                        (useFp16.HasValue ? useFp16.Value ? " · FP16" : " · FP32" : ""), true)
                    {
                        UsesTensorRt = IsTensorRtProvider(candidate.EpName), UsesCudaGraph = IsTensorRtProvider(candidate.EpName) && useCudaGraph,
                        TensorRtDeviceId = IsTensorRtProvider(candidate.EpName)
                            && candidate.EpOptions.Entries.TryGetValue("device_id", out var id) && int.TryParse(id, out int ordinal) ? ordinal : null
                    };
                    return session;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { failures.Add(ex.Message); }
            }
        }
        if (backend == VideoSrBackend.PreferNpu)
            throw new PlatformNotSupportedException("Windows ML NPU provider unavailable. " + string.Join("; ", failures));
        if (backend == VideoSrBackend.VendorGpu)
            throw new PlatformNotSupportedException(LocalizationRegistry.Get("VideoTranscode.Sr_VendorGpuRequired") + " " + string.Join("; ", failures));
#endif
        if (backend == VideoSrBackend.VendorGpu) throw new PlatformNotSupportedException("A vendor GPU provider requires Windows ML.");
        if (backend != VideoSrBackend.Cpu)
        {
            var session = CreateDirectMlTensorSession(path, dimensions, ct, out device, requireHardware);
#if WINDOWS
            if (failures.Count > 0) device = device with { FallbackReason = string.Join("; ", failures) };
#endif
            return session;
        }
        using var fallbackOptions = CreateOptions(dimensions);
        fallbackOptions.AppendExecutionProvider_CPU(0);
        device = new VideoSrDevice("CPU", false);
        return new InferenceSession(path, fallbackOptions);
    }

    internal static bool CanUseGpuPixels(CancellationToken ct)
    {
#if WINDOWS
        Registration.Value.WaitAsync(ct).GetAwaiter().GetResult();
        return OrtEnv.Instance().GetEpDevices().Any(d => d.HardwareDevice.Type == OrtHardwareDeviceType.GPU && IsTensorRtProvider(d.EpName));
#else
        return false;
#endif
    }

    /// <summary>Creates the common DirectML GPU path used when vendor providers are unavailable.</summary>
    internal static InferenceSession CreateDirectMlSession(string path, int width, int height, CancellationToken ct,
        out VideoSrDevice device, bool requireHardware = false)
        => CreateDirectMlTensorSession(path, VideoDimensions(width, height), ct, out device, requireHardware);

    /// <summary>Creates a DirectML session for non-image tensor shapes.</summary>
    internal static InferenceSession CreateDirectMlTensorSession(string path, IReadOnlyDictionary<string, long> dimensions,
        CancellationToken ct, out VideoSrDevice device, bool requireHardware = false)
    {
        ct.ThrowIfCancellationRequested();
        _ = OrtEnv.Instance();
#if WINDOWS
        Registration.Value.WaitAsync(ct).GetAwaiter().GetResult();
#endif
        using var options = CreateOptions(dimensions);
        options.IntraOpNumThreads = 1;
        options.InterOpNumThreads = 1;
        if (Environment.GetEnvironmentVariable("FANCYTOOL_SR_DIAGNOSTICS") == "1")
        {
            options.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_VERBOSE;
            options.LogVerbosityLevel = 1;
        }
        if (requireHardware) options.AddSessionConfigEntry("session.disable_cpu_ep_fallback", "1");
        var gpu = VideoSrGpuSelector.Select();
#if WINDOWS
        var env = OrtEnv.Instance();
        var candidate = env.GetEpDevices().FirstOrDefault(d => d.EpName == "DmlExecutionProvider"
            && d.HardwareDevice.Type == OrtHardwareDeviceType.GPU
            && d.HardwareDevice.VendorId == gpu.VendorId && d.HardwareDevice.DeviceId == gpu.DeviceId);
        if (candidate != null) options.AppendExecutionProvider(env, [candidate], new Dictionary<string, string>());
        else
#endif
        options.AppendExecutionProvider_DML(gpu.Index);
        device = new VideoSrDevice("GPU · " + gpu.Name + " / Windows ML DirectML", true);
        return new InferenceSession(path, options);
    }

    private static IReadOnlyDictionary<string, long> VideoDimensions(int width, int height) => new Dictionary<string, long>
    {
        ["batch_size"] = 1, ["batch"] = 1, ["height"] = height, ["width"] = width,
        ["h"] = height, ["w"] = width, ["in_height"] = height, ["in_width"] = width
    };

    private static SessionOptions CreateOptions(IReadOnlyDictionary<string, long> dimensions)
    {
        var options = new SessionOptions
        {
            EnableMemoryPattern = false, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            EnableCpuMemArena = false, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        // Specialize the named dimensions required by this graph.
        foreach (var dimension in dimensions) options.AddFreeDimensionOverrideByName(dimension.Key, dimension.Value);
        return options;
    }

    /// <summary>Installs compatible certified vendor providers through Microsoft's catalog when explicitly requested.</summary>
    internal static Task InstallAsync(Action<string>? progress, CancellationToken ct)
    {
#if WINDOWS
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100))
            throw new PlatformNotSupportedException("Windows ML hardware providers require Windows 11 24H2 or later.");
        // GetDefault/FindAllProviders 会同步枚举 GPU/NPU 驱动，TryRegister 会同步加载厂商 EP 的原生
        // DLL，已安装组件的 EnsureReadyAsync 也常同步完成；而信号量空闲时 ConfigureAwait(false) 并
        // 不会离开调用线程，导致整段同步代码落在 UI 线程上。必须整体移入线程池执行，
        // 与上方 RegisterInstalledAsync 的 Task.Run 包装保持一致。
        return Task.Run(() => InstallCoreAsync(progress, ct), ct);
#else
        return Task.FromException(new PlatformNotSupportedException("Windows ML requires Windows"));
#endif
    }
#if WINDOWS
    private static async Task InstallCoreAsync(Action<string>? progress, CancellationToken ct)
    {
        await CatalogGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _ = OrtEnv.Instance();
            var catalog = ExecutionProviderCatalog.GetDefault() ?? throw new InvalidOperationException("Windows ML catalog unavailable");
            var providers = catalog.FindAllProviders() ?? throw new InvalidOperationException("Windows ML provider discovery failed");
            foreach (var provider in providers.Where(p => IsVendorProvider(p.Name)))
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
    }
#endif
}
