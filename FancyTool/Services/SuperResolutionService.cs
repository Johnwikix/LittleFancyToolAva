using System.Runtime.InteropServices;
using System.Buffers;
using FancyToolAva.Models;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace FancyToolAva.Services;

public sealed class SuperResolutionService : ISuperResolutionService
{
    public const int MaxOutputDimension = 16384;

    private readonly ILogger<SuperResolutionService> _logger;
    private readonly ModelDownloadService _downloader;
    private readonly SynchronizationContext _uiContext;
    private readonly ILicenseService _license;
    private readonly object _warmupGate = new();
    private bool _warmupStopped;
    private string _modelsDirectory => AppPaths.ModelsDirectory;
    private string? _sessionDirectory;
    private readonly Dictionary<SuperResolutionModel, InferenceSession> _sessions = new();
    private readonly Dictionary<SuperResolutionModel, string> _inputNames = new();
    private readonly Dictionary<SuperResolutionModel, string> _outputNames = new();
    private readonly Dictionary<SuperResolutionModel, TensorElementType> _inputElementTypes = new();
    private readonly Dictionary<SuperResolutionModel, TensorElementType> _outputElementTypes = new();
    private readonly Dictionary<SuperResolutionModel, (string Name, TensorElementType Type)[]> _extraInputs = new();
    private readonly Dictionary<SuperResolutionModel, bool> _sessionUsesDml = new();
    private readonly Dictionary<SuperResolutionModel, int> _sessionWindows = new();
    private readonly object _lock = new();
    private readonly object _inferenceGate = new();
    private readonly Dictionary<(SuperResolutionModel Model, int Window), IVideoSrTileModel> _npuSessions = new();
    private readonly Dictionary<(SuperResolutionModel Model, int Window), VideoSrDevice> _npuDevices = new();
    private readonly Dictionary<(SuperResolutionModel Model, int Window, VideoSrBackend Backend), VideoFrameUpscaler> _frameSessions = new();
    private readonly Dictionary<(SuperResolutionModel Model, int Window), string> _npuFailures = new();

    /// <inheritdoc />
    public VideoSrDevice? InferenceDevice { get; private set; }
    /// <inheritdoc />
    public event Action<VideoSrDevice>? DeviceChanged;
    /// <inheritdoc />
    public bool PreferNpu { get => _preferences.UseSuperResolutionNpu; set => _preferences.UseSuperResolutionNpu = value; }
    private bool _disposed;
    private bool _dmlDisabled;
    private bool _dmlAvailable;
    private string _dmlDeviceName = "GPU · DirectML";

    // Memory/speed tiers: padded window sizes (MinMemory, Memory, Balanced, Quality).
    private static readonly int[] WindowSizes = [192, 288, 384, 576];
    private const int TilePad = 32;

    private readonly AppPreferences _preferences;
    private Task? _warmupTask;
    private CancellationTokenSource? _warmupCts;
    private string? _lastDownloadError;
    private readonly Dictionary<SuperResolutionModel, double> _perModelProgress = new();
    private string? _activeFileName;

    public event Action<ModelDownloadProgress>? DownloadProgressChanged;
    public event EventHandler<bool>? IsDownloadingChanged;

    public Task WarmupTask
    {
        get
        {
            TryStartWarmup();
            lock (_warmupGate) return _warmupTask ?? Task.CompletedTask;
        }
    }

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (_isDownloading == value) return;
            _isDownloading = value;
            _uiContext.Post(_ => IsDownloadingChanged?.Invoke(this, value), null);
        }
    }

    public string? LastDownloadError => _lastDownloadError;

    public SuperResolutionService(
        ILogger<SuperResolutionService> logger,
        AppPreferences preferences,
        ModelDownloadService downloader,
        ILicenseService? licenseService = null)
    {
        _logger = logger;
        _preferences = preferences;
        _downloader = downloader;
        _license = licenseService ?? LicenseService.Unrestricted;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _license.StateChanged += OnLicenseChanged;
        TryStartWarmup();
    }

    private void OnLicenseChanged() => TryStartWarmup();

    private void TryStartWarmup()
    {
        lock (_warmupGate)
        {
            // Store 初查和受限期间不下载；恢复许可后沿用同一个服务启动首次预热。
            if (_warmupStopped || _warmupTask != null || !_license.CanUseFeature(LicenseFeature.ImageSuperResolution)) return;
            _warmupCts = new CancellationTokenSource();
            CancellationToken ct = _warmupCts.Token;
            _warmupTask = Task.Run(() => StartWarmupAsync(ct));
        }
    }

    private async Task StartWarmupAsync(CancellationToken ct)
    {
        // 调用方在线程池执行，缓存文件的同步 SHA-256 校验也不阻塞购买恢复后的 UI。
        try
        {
            ct.ThrowIfCancellationRequested();
            IsDownloading = true;
            OnDownloadProgress(new ModelDownloadProgress(
                string.Empty, ModelDownloadStage.Connecting, 0, null, "正在准备…"));
            var progress = new Progress<ModelDownloadProgress>(OnDownloadProgress);
            await _downloader
                .UseModelsAsync(ModelManifest.RealEsrgan, () => AppPaths.ModelsDirectory, _ => true, progress, ct)
                .ConfigureAwait(false);
            _lastDownloadError = null;
            _logger.LogInformation("All super-resolution models are ready in {Dir}", _modelsDirectory);
        }
        catch (OperationCanceledException)
        {
            // App shutting down. Nothing to do.
        }
        catch (Exception ex)
        {
            _lastDownloadError = ex.Message;
            _logger.LogError(ex, "Model warmup failed");
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private void OnDownloadProgress(ModelDownloadProgress p)
    {
        _activeFileName ??= p.FileName;
        _uiContext.Post(_ => DownloadProgressChanged?.Invoke(p), null);
    }

    public IReadOnlyList<string> AvailableModels { get; } = new[]
    {
        "RealESRGAN_x4plus",
        "RealESRGAN_x4plus_anime",
        "realesr-general-x4v3_fp16",
        "4x-ClearRealityV1_fp16"
    };

    public bool IsModelAvailable(SuperResolutionModel model)
    {
        string fileName = GetFileName(model);
        string path = Path.Combine(_modelsDirectory, fileName);
        return File.Exists(path);
    }

    public async Task<SKBitmap> UpscaleAsync(
        SKBitmap source,
        SuperResolutionModel model,
        int targetScale,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _license.EnsureFeatureAsync(LicenseFeature.ImageSuperResolution, ct).ConfigureAwait(false);
        ThrowIfDisposed();

        if (targetScale != 2 && targetScale != 4)
            throw new ArgumentOutOfRangeException(nameof(targetScale), "Target scale must be 2x or 4x.");
        int modelScale = ModelManifest.GetImageModel(model).NativeScale;
        if (targetScale > modelScale) throw new ArgumentOutOfRangeException(nameof(targetScale), "This model supports 2× output only");

        if (!IsModelAvailable(model))
        {
            string name = GetFileName(model);
            string detail = _isDownloading
                ? $"正在下载模型 '{name}'，请稍候。"
                : !string.IsNullOrEmpty(_lastDownloadError)
                    ? $"模型 '{name}' 下载失败：{_lastDownloadError}。可重启程序重试。"
                    : $"模型 '{name}' 不存在，请重启程序自动下载。";
            throw new FileNotFoundException(detail, name);
        }

        var srcBitmap = source;
        int srcW = srcBitmap.Width;
        int srcH = srcBitmap.Height;

        int outW = srcW * targetScale;
        int outH = srcH * targetScale;

        // Padded window size comes from the user's memory/speed tier. Constant within
        // a run: every tile uses the same shape, so ORT/DML reuse their compiled graph.
        int windowSize = WindowSizes[Math.Clamp(_preferences.SuperResolutionTileSizeIndex, 0, WindowSizes.Length - 1)];
        int tileSize = windowSize - TilePad * 2;

        if (source.ColorType == SKColorType.Bgra8888 && source.AlphaType == SKAlphaType.Opaque
            && outW <= VideoFrameUpscaler.MaxDimension && outH <= VideoFrameUpscaler.MaxDimension && (long)outW * outH <= VideoFrameUpscaler.MaxPixels)
        {
            lock (_inferenceGate)
            {
                using var storage = ModelStorage.Acquire(ct);
                _license.ThrowIfRestricted(LicenseFeature.ImageSuperResolution);
                RefreshSessionDirectory();
                ThrowIfDisposed();
                var backends = Environment.GetEnvironmentVariable("SR_FORCE_CPU") == "1"
                    ? new[] { VideoSrBackend.Cpu }
                    : PreferNpu ? WantDml() ? new[] { VideoSrBackend.PreferNpu, VideoSrBackend.Gpu, VideoSrBackend.Cpu } : new[] { VideoSrBackend.PreferNpu, VideoSrBackend.Cpu }
                    : WantDml() ? new[] { VideoSrBackend.Gpu, VideoSrBackend.Cpu } : new[] { VideoSrBackend.Cpu };
                string? failure = null;
                foreach (var backend in backends)
                {
                    try
                    {
                        var key = (model, windowSize, backend);
                        if (!_frameSessions.TryGetValue(key, out var session))
                        {
                            var tile = SrTileFactory.Create(Path.Combine(_modelsDirectory, GetFileName(model)), backend, windowSize, ct, out var device, modelScale);
                            session = new VideoFrameUpscaler(tile, device, windowSize, modelScale);
                            _frameSessions.Add(key, session);
                        }
                        var result = session.Upscale(source, targetScale, ct, progress);
                        ReportDevice(session.Device with { FallbackReason = failure });
                        return result;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        failure = ex.Message;
                        if (_frameSessions.Remove((model, windowSize, backend), out var failed)) failed.Dispose();
                        if (backend == VideoSrBackend.Cpu) throw;
                        _logger.LogWarning(ex, "Image SR backend {Backend} failed", backend);
                    }
                }
            }
        }

        progress?.Report(0.05);

        var outInfo = new SKImageInfo(outW, outH, SKColorType.Bgra8888, SKAlphaType.Premul);
        var destBitmap = new SKBitmap(outInfo);

        bool hasAlpha = srcBitmap.AlphaType != SKAlphaType.Opaque;

        int totalTiles = CeilDiv(srcW, tileSize) * CeilDiv(srcH, tileSize);
        int tileIndex = 0;

        try
        {
            for (int y = 0; y < srcH; y += tileSize)
            {
                ct.ThrowIfCancellationRequested();
                int tileH = Math.Min(tileSize, srcH - y);
                for (int x = 0; x < srcW; x += tileSize)
                {
                    int tileW = Math.Min(tileSize, srcW - x);

                    // Constant padded window: identical model input shape for every
                    // tile; pads are constant TilePad; areas outside the source stay zero.
                    int windowX = x - TilePad;
                    int windowY = y - TilePad;
                    int interX = Math.Clamp(windowX, 0, srcW);
                    int interY = Math.Clamp(windowY, 0, srcH);
                    int interW = Math.Clamp(windowX + windowSize, 0, srcW) - interX;
                    int interH = Math.Clamp(windowY + windowSize, 0, srcH) - interY;

                    using var tileBitmap = new SKBitmap(windowSize, windowSize, srcBitmap.ColorType, srcBitmap.AlphaType);
                    using (var canvas = new SKCanvas(tileBitmap))
                    {
                        if (interW > 0 && interH > 0)
                        {
                            canvas.DrawBitmap(
                                srcBitmap,
                                new SKRect(interX, interY, interX + interW, interY + interH),
                                new SKRect(interX - windowX, interY - windowY,
                                    interX - windowX + interW, interY - windowY + interH));
                        }
                    }

                    using var tileImage = SKImage.FromBitmap(tileBitmap);
                    using var srTile = RunModelTile(model, tileImage, ct, checkLicense: tileIndex == 0);

                    if (hasAlpha)
                    {
                        long alphaStamp = VideoPipelineMetrics.Start();
                        var alphaInfo = new SKImageInfo(windowSize * modelScale, windowSize * modelScale, SKColorType.Bgra8888, SKAlphaType.Premul);
                        using var alphaTile = new SKBitmap(alphaInfo);
                        UpsampleAlphaChannel(srcBitmap, alphaTile, windowX, windowY, windowSize, windowSize);
                        MergeAlphaIntoRgb(srTile, alphaTile);
                        VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.ImageAlpha, alphaStamp);
                    }

                    if (targetScale < modelScale)
                    {
                        long resizeStamp = VideoPipelineMetrics.Start();
                        using var canvas = new SKCanvas(destBitmap);
                        using var paint = new SKPaint
                        {
                            FilterQuality = SKFilterQuality.High,
                            IsAntialias = true,
                            BlendMode = SKBlendMode.Src
                        };
                        canvas.DrawBitmap(
                            srTile,
                            new SKRect(TilePad * modelScale, TilePad * modelScale,
                                (TilePad + tileW) * modelScale, (TilePad + tileH) * modelScale),
                            new SKRect(x * 2, y * 2, (x + tileW) * 2, (y + tileH) * 2),
                            paint);
                        VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.ImageResize, resizeStamp);
                    }
                    else
                    {
                        PasteTileInterior(
                            destBitmap,
                            srTile,
                            x * modelScale,
                            y * modelScale,
                            TilePad * modelScale,
                            TilePad * modelScale,
                            tileW * modelScale,
                            tileH * modelScale);
                    }

                    tileIndex++;
                    progress?.Report(0.05 + 0.8 * (double)tileIndex / totalTiles);
                }
            }

            return await Task.FromResult(destBitmap);
        }
        catch
        {
            destBitmap.Dispose();
            throw;
        }
    }

    private static int CeilDiv(int a, int b) => (a + b - 1) / b;

    private SKBitmap RunModelTile(SuperResolutionModel model, SKImage tile, CancellationToken ct, bool checkLicense)
    {
        lock (_inferenceGate) return RunModelTileSerial(model, tile, ct, checkLicense);
    }

    private SKBitmap RunModelTileSerial(SuperResolutionModel model, SKImage tile, CancellationToken ct, bool checkLicense)
    {
        using var storage = ModelStorage.Acquire(ct);
        // 只在图像首块实际获得推理/模型存储锁时复查；不突断已开始的原生作业。
        if (checkLicense) _license.ThrowIfRestricted(LicenseFeature.ImageSuperResolution);
        RefreshSessionDirectory();
        ct.ThrowIfCancellationRequested();
        string? npuFailure = null;
        if (PreferNpu && Environment.GetEnvironmentVariable("SR_FORCE_CPU") != "1")
        {
            var key = (model, tile.Width);
            if (!_npuFailures.TryGetValue(key, out npuFailure))
            {
                try
                {
                    if (!_npuSessions.TryGetValue(key, out var npu))
                    {
                        npu = SrTileFactory.Create(Path.Combine(_modelsDirectory, GetFileName(model)), VideoSrBackend.PreferNpu, tile.Width, ct, out var npuDevice, ModelManifest.GetImageModel(model).NativeScale);
                        _npuSessions.Add(key, npu);
                        _npuDevices[key] = npuDevice;
                    }
                    var result = RunNpuTile(npu, tile, ct, ModelManifest.GetImageModel(model).NativeScale);
                    ReportDevice(_npuDevices[key]);
                    return result;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    npuFailure = ex.Message;
                    _npuFailures[key] = npuFailure;
                    if (_npuSessions.Remove(key, out var failed)) failed.Dispose();
                    _logger.LogWarning(ex, "Image SR NPU unavailable for {Model}; trying GPU", model);
                }
            }
        }
        InferenceSession session;
        try { session = GetOrCreateSession(model); }
        catch (OnnxRuntimeException) when (WantDml())
        {
            _dmlDisabled = true;
            _dmlAvailable = false;
            session = GetOrCreateSession(model);
            npuFailure ??= "DirectML initialization failed";
        }
        try
        {
            var result = RunModelTileCore(session, model, tile, ct);
            ReportDevice(new VideoSrDevice(_sessionUsesDml.GetValueOrDefault(model) ? _dmlDeviceName : "CPU",
                _sessionUsesDml.GetValueOrDefault(model), npuFailure));
            return result;
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (OnnxRuntimeException) when (_dmlAvailable)
        {
            _logger.LogWarning("DirectML execution failed on model {Model}; dropping all DML sessions and falling back to CPU EP.", model);
            lock (_lock)
            {
                foreach (var kv in _sessions.ToList())
                {
                    _sessions.Remove(kv.Key);
                    try { kv.Value.Dispose(); } catch { }
                }
                _dmlAvailable = false;
                _dmlDisabled = true;
            }
            session = GetOrCreateSession(model);
            var result = RunModelTileCore(session, model, tile, ct);
            ReportDevice(new VideoSrDevice("CPU", false, "DirectML inference failed"));
            return result;
        }
    }

    private void ReportDevice(VideoSrDevice device)
    {
        if (device == InferenceDevice) return;
        InferenceDevice = device;
        _uiContext.Post(_ => DeviceChanged?.Invoke(device), null);
    }

    private static SKBitmap RunNpuTile(IVideoSrTileModel npu, SKImage tile, CancellationToken ct, int scale)
    {
        using var inputBitmap = SKBitmap.FromImage(tile);
        int window = tile.Width;
        var tensor = new DenseTensor<float>(new[] { 1, 3, window, window });
        FillRgbTensor(inputBitmap, tensor.Buffer.Span, window, window);
        var input = tensor.Buffer.ToArray();
        var output = new float[input.Length * scale * scale];
        npu.Infer(input, output, ct);
        var result = new SKBitmap(new SKImageInfo(window * scale, window * scale, SKColorType.Bgra8888, SKAlphaType.Premul));
        var pixels = result.GetPixelSpan();
        int channel = window * window * scale * scale;
        for (int i = 0; i < channel; i++)
        {
            pixels[i * 4] = (byte)Math.Clamp((int)(output[channel * 2 + i] * 255f), 0, 255);
            pixels[i * 4 + 1] = (byte)Math.Clamp((int)(output[channel + i] * 255f), 0, 255);
            pixels[i * 4 + 2] = (byte)Math.Clamp((int)(output[i] * 255f), 0, 255);
            pixels[i * 4 + 3] = 255;
        }
        return result;
    }

    private SKBitmap RunModelTileCore(InferenceSession session, SuperResolutionModel model, SKImage tile, CancellationToken ct)
    {
        using var srcBitmap = SKBitmap.FromImage(tile);
        int w = srcBitmap.Width;
        int h = srcBitmap.Height;

        var inputName = _inputNames[model];
        var outputName = _outputNames[model];

        var inputs = new Dictionary<string, OrtValue>();
        var inputOrtValues = new List<OrtValue>();
        var inputPins = new List<System.Runtime.InteropServices.GCHandle>();
        try
        {
            long stamp = VideoPipelineMetrics.Start();
            using var input = CreateInputOrt(model, srcBitmap, w, h);
            inputs[inputName] = input.Value;

            if (_extraInputs.TryGetValue(model, out var extras))
            {
                foreach (var (name, type) in extras)
                {
                    var (extraOrt, extraPin) = CreateScalarOrt(name, type);
                    inputOrtValues.Add(extraOrt);
                    if (extraPin.HasValue) inputPins.Add(extraPin.Value);
                    inputs[name] = extraOrt;
                }
            }

            using var runOptions = new RunOptions();
            using var registration = ct.Register(() => runOptions.Terminate = true);
            VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.SrTensorPrepare, stamp);
            stamp = VideoPipelineMetrics.Start();
            using var outputs = session.Run(runOptions, inputs, new[] { outputName });
            VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.SrNativeRun, stamp);
            ct.ThrowIfCancellationRequested();
            var outputValue = outputs[0];

            var outputShape = outputValue.GetTensorTypeAndShape().Shape;
            int outChannels = (int)outputShape[1];
            int outH = (int)outputShape[2];
            int outW = (int)outputShape[3];

            var outputInfo = new SKImageInfo(outW, outH, SKColorType.Bgra8888, SKAlphaType.Premul);
            var outBitmap = new SKBitmap(outputInfo);

            try
            {
                stamp = VideoPipelineMetrics.Start();
                if (_outputElementTypes.TryGetValue(model, out var outType) && outType == TensorElementType.Float16)
                    ReadOutputToBitmapHalf(outputValue, outBitmap, outW, outH, outChannels);
                else
                    ReadOutputToBitmap(outputValue, outBitmap, outW, outH, outChannels);
                VideoPipelineMetrics.Record(VideoPipelineMetrics.Phase.SrTensorRead, stamp);
                return outBitmap;
            }
            catch { outBitmap.Dispose(); throw; }
        }
        finally
        {
            foreach (var o in inputOrtValues)
            {
                try { o.Dispose(); } catch { }
            }
            foreach (var p in inputPins)
            {
                try { p.Free(); } catch { }
            }
        }
    }

    private (OrtValue Ort, System.Runtime.InteropServices.GCHandle? Pin) CreateScalarOrt(string name, TensorElementType type)
    {
        // Only known extra input across the shipped models: denoise_strength (default 0.5).
        float value = 0.5f;
        if (type == TensorElementType.Float16)
        {
            var array = new Half[] { (Half)value };
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(array, System.Runtime.InteropServices.GCHandleType.Pinned);
            return (
                OrtValue.CreateTensorValueWithData(
                    OrtMemoryInfo.DefaultInstance,
                    TensorElementType.Float16,
                    new long[] { 1 },
                    handle.AddrOfPinnedObject(),
                    array.Length * 2L),
                handle);
        }
        else
        {
            var array = new[] { value };
            return (
                OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, array.AsMemory(), new long[] { 1 }),
                null);
        }
    }

    private struct ImageInputLease(OrtValue value, GCHandle pin, Half[]? half, float[]? single) : IDisposable
    {
        private OrtValue? _value = value;
        internal readonly OrtValue Value => _value ?? throw new ObjectDisposedException(nameof(ImageInputLease));

        public void Dispose()
        {
            var owned = _value;
            if (owned == null) return;
            _value = null;
            // 原生执行同步返回且 OrtValue 释放后才解除固定并归还；下一次使用会覆写有效区。
            try { owned.Dispose(); }
            finally
            {
                try { if (pin.IsAllocated) pin.Free(); }
                finally
                {
                    if (half != null) ArrayPool<Half>.Shared.Return(half);
                    if (single != null) ArrayPool<float>.Shared.Return(single);
                }
            }
        }
    }

    private ImageInputLease CreateInputOrt(SuperResolutionModel model, SKBitmap src, int w, int h)
    {
        TensorElementType inputType = _inputElementTypes.TryGetValue(model, out var t) ? t : TensorElementType.Float;
        int length = checked(3 * w * h);
        if (inputType == TensorElementType.Float16)
        {
            var buffer = ArrayPool<Half>.Shared.Rent(length);
            GCHandle handle = default;
            try
            {
                FillRgbTensorHalf(src, buffer.AsSpan(0, length), w, h);
                handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                return new ImageInputLease(OrtValue.CreateTensorValueWithData(OrtMemoryInfo.DefaultInstance,
                    TensorElementType.Float16, [1, 3, h, w], handle.AddrOfPinnedObject(), length * 2L), handle, buffer, null);
            }
            catch
            {
                try { if (handle.IsAllocated) handle.Free(); }
                finally { ArrayPool<Half>.Shared.Return(buffer); }
                throw;
            }
        }
        else
        {
            var buffer = ArrayPool<float>.Shared.Rent(length);
            try
            {
                FillRgbTensor(src, buffer.AsSpan(0, length), w, h);
                return new ImageInputLease(OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance,
                    buffer.AsMemory(0, length), [1, 3, h, w]), default, null, buffer);
            }
            catch { ArrayPool<float>.Shared.Return(buffer); throw; }
        }
    }

    private static void FillRgbTensor(SKBitmap src, Span<float> span, int w, int h)
    {
        int stride = w * 4;
        var pixels = src.GetPixelSpan();

        int idx = 0;
        int channelSize = w * h;
        int rOff = 0;
        int gOff = channelSize;
        int bOff = channelSize * 2;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int p = y * stride + x * 4;
                byte b = pixels[p];
                byte g = pixels[p + 1];
                byte r = pixels[p + 2];
                span[rOff + idx] = r / 255.0f;
                span[gOff + idx] = g / 255.0f;
                span[bOff + idx] = b / 255.0f;
                idx++;
            }
        }
    }

    private static void ReadOutputToBitmap(OrtValue outputValue, SKBitmap dst, int w, int h, int channels)
        => ImageTensorPixels.ReadFloat(outputValue.GetTensorDataAsSpan<float>(), dst, w, h, channels);

    private static void FillRgbTensorHalf(SKBitmap src, Span<Half> span, int w, int h)
    {
        int stride = w * 4;
        var pixels = src.GetPixelSpan();

        int idx = 0;
        int channelSize = w * h;
        int rOff = 0;
        int gOff = channelSize;
        int bOff = channelSize * 2;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int p = y * stride + x * 4;
                byte b = pixels[p];
                byte g = pixels[p + 1];
                byte r = pixels[p + 2];
                span[rOff + idx] = (Half)(r / 255.0f);
                span[gOff + idx] = (Half)(g / 255.0f);
                span[bOff + idx] = (Half)(b / 255.0f);
                idx++;
            }
        }
    }

    private static void ReadOutputToBitmapHalf(OrtValue outputValue, SKBitmap dst, int w, int h, int channels)
        => ImageTensorPixels.ReadHalf(MemoryMarshal.Cast<Float16, Half>(outputValue.GetTensorDataAsSpan<Float16>()), dst, w, h, channels);

    private static void PasteTileInterior(SKBitmap dst, SKBitmap srcTile, int dstX, int dstY, int srcX, int srcY, int width, int height)
    {
        var dstPixels = dst.GetPixelSpan();
        var srcPixels = srcTile.GetPixelSpan();
        int dstStride = dst.Width * 4;
        int srcStride = srcTile.Width * 4;

        for (int y = 0; y < height; y++)
        {
            int s = (srcY + y) * srcStride + srcX * 4;
            int d = (dstY + y) * dstStride + dstX * 4;
            srcPixels.Slice(s, width * 4).CopyTo(dstPixels.Slice(d, width * 4));
        }
    }

    private static void MergeAlphaIntoRgb(SKBitmap rgb, SKBitmap alpha)
        => ImageTensorPixels.MergeAlpha(rgb, alpha);

    private static void UpsampleAlphaChannel(SKBitmap src, SKBitmap dst, int srcX, int srcY, int srcW, int srcH)
    {
        using (var canvas = new SKCanvas(dst))
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
        {
            var srcRect = new SKRect(srcX, srcY, srcX + srcW, srcY + srcH);
            var dstRect = new SKRect(0, 0, dst.Width, dst.Height);
            canvas.DrawBitmap(src, srcRect, dstRect, paint);
        }
    }

    private InferenceSession GetOrCreateSession(SuperResolutionModel model)
    {
        lock (_lock)
        {
            ThrowIfDisposed();

            bool wantDml = WantDml();
            int window = WindowSizes[Math.Clamp(_preferences.SuperResolutionTileSizeIndex, 0, WindowSizes.Length - 1)];
            if (_sessions.TryGetValue(model, out var existing))
            {
                // Rebuild the session when the desired EP changed (user toggled the setting).
                if (_sessionUsesDml.TryGetValue(model, out var cur) && cur == wantDml && _sessionWindows.GetValueOrDefault(model) == window)
                    return existing;
                _sessions.Remove(model);
                _sessionUsesDml.Remove(model);
                try { existing.Dispose(); } catch { }
            }

            string fileName = GetFileName(model);
            string path = Path.Combine(_modelsDirectory, fileName);
            var session = WindowsMlAccelerator.CreateSession(path, wantDml ? VideoSrBackend.Gpu : VideoSrBackend.Cpu, window, default, out var selectedDevice);
            _dmlAvailable = selectedDevice.IsHardware;
            _dmlDeviceName = selectedDevice.Name;
            var inputMeta = session.InputMetadata.First();
            var outputMeta = session.OutputMetadata.First();
            _inputNames[model] = inputMeta.Key;
            _outputNames[model] = outputMeta.Key;
            _inputElementTypes[model] = inputMeta.Value.ElementDataType;
            _outputElementTypes[model] = outputMeta.Value.ElementDataType;
            _extraInputs[model] = session.InputMetadata
                .Where(kv => kv.Key != inputMeta.Key)
                .Select(kv => (kv.Key, kv.Value.ElementDataType))
                .ToArray();
            _sessions[model] = session;
            _sessionWindows[model] = window;
            _sessionUsesDml[model] = wantDml && _dmlAvailable;
            _logger.LogInformation("Super-resolution session ready: {Model} ({Input} {InType} -> {Output} {OutType})",
                fileName, _inputNames[model], _inputElementTypes[model], _outputNames[model], _outputElementTypes[model]);
            return session;
        }
    }

    private bool WantDml()
    {
        if (_dmlDisabled) return false;
        if (Environment.GetEnvironmentVariable("SR_FORCE_CPU") == "1") return false;
        return OperatingSystem.IsWindows();
    }

    private static string GetFileName(SuperResolutionModel model) => ModelManifest.GetImageModel(model).FileName;

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SuperResolutionService));
    }

    public void ReleaseSessions()
    {
        lock (_inferenceGate)
        {
            ReleaseSessionsCore();
        }
    }

    /// <inheritdoc />
    public async Task InstallAccelerationAsync(CancellationToken ct = default)
    {
        await _license.EnsureFeatureAsync(LicenseFeature.ImageSuperResolution, ct).ConfigureAwait(false);
        await WindowsMlAccelerator.InstallAsync(
            name => _uiContext.Post(_ => DownloadProgressChanged?.Invoke(new ModelDownloadProgress(name, ModelDownloadStage.Connecting, 0, null,
                LocalizationRegistry.Get("VideoTranscode.Sr_InstallingHardware") + " · " + name)), null), ct).ConfigureAwait(false);
    }

    private void RefreshSessionDirectory()
    {
        string directory = AppPaths.ModelsDirectory;
        if (string.Equals(directory, _sessionDirectory, ModelStorage.PathComparison)) return;
        ReleaseSessionsCore();
        _sessionDirectory = directory;
    }

    private void ReleaseSessionsCore()
    {
        lock (_lock)
        {
            if (_disposed) return;
            foreach (var s in _sessions.Values)
            {
                try { s.Dispose(); } catch { }
            }
            _sessions.Clear();
            _inputNames.Clear();
            _outputNames.Clear();
            _inputElementTypes.Clear();
            _outputElementTypes.Clear();
            _extraInputs.Clear();
            _sessionUsesDml.Clear();
            _sessionWindows.Clear();
            foreach (var session in _frameSessions.Values) session.Dispose();
            _frameSessions.Clear();
            foreach (var session in _npuSessions.Values) session.Dispose();
            _npuSessions.Clear();
            _npuDevices.Clear();
            _npuFailures.Clear();
            _logger.LogInformation("Super-resolution sessions released.");
        }
    }

    public void Dispose()
    {
        _license.StateChanged -= OnLicenseChanged;
        lock (_warmupGate)
        {
            _warmupStopped = true;
            try { _warmupCts?.Cancel(); } catch { }
            _warmupCts?.Dispose();
        }
        lock (_inferenceGate)
        {
            ReleaseSessionsCore();
            _disposed = true;
        }
    }
}
