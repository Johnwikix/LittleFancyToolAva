using System.Runtime.InteropServices;
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
    private readonly string _modelsDirectory;
    private readonly Dictionary<SuperResolutionModel, InferenceSession> _sessions = new();
    private readonly Dictionary<SuperResolutionModel, string> _inputNames = new();
    private readonly Dictionary<SuperResolutionModel, string> _outputNames = new();
    private readonly Dictionary<SuperResolutionModel, TensorElementType> _inputElementTypes = new();
    private readonly Dictionary<SuperResolutionModel, TensorElementType> _outputElementTypes = new();
    private readonly Dictionary<SuperResolutionModel, (string Name, TensorElementType Type)[]> _extraInputs = new();
    private readonly Dictionary<SuperResolutionModel, bool> _sessionUsesDml = new();
    private readonly object _lock = new();
    private bool _disposed;
    private bool _dmlDisabled;
    private bool _dmlAvailable;

    private const int TileSize = 512;
    private const int TilePad = 32;
    private const int WindowSize = TileSize + TilePad * 2;
    private const int ModelScale = 4;

    private readonly AppPreferences _preferences;

    public SuperResolutionService(ILogger<SuperResolutionService> logger, AppPreferences preferences)
    {
        _logger = logger;
        _preferences = preferences;
        _modelsDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Models");
    }

    public IReadOnlyList<string> AvailableModels { get; } = new[]
    {
        "RealESRGAN_x4plus",
        "RealESRGAN_x4plus_anime",
        "realesr-general-x4v3_fp16"
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

        if (targetScale != 2 && targetScale != 4)
            throw new ArgumentOutOfRangeException(nameof(targetScale), "Target scale must be 2x or 4x.");

        if (!IsModelAvailable(model))
        {
            string name = GetFileName(model);
            throw new FileNotFoundException(
                $"Super-resolution model '{name}' not found in '{_modelsDirectory}'. Run tools\\download-models.ps1 to fetch it.",
                name);
        }

        var srcBitmap = source;
        int srcW = srcBitmap.Width;
        int srcH = srcBitmap.Height;

        int outW = srcW * targetScale;
        int outH = srcH * targetScale;

        progress?.Report(0.05);

        var outInfo = new SKImageInfo(outW, outH, SKColorType.Bgra8888, SKAlphaType.Premul);
        var destBitmap = new SKBitmap(outInfo);

        bool hasAlpha = srcBitmap.AlphaType != SKAlphaType.Opaque;

        int totalTiles = CeilDiv(srcW, TileSize) * CeilDiv(srcH, TileSize);
        int tileIndex = 0;

        try
        {
            for (int y = 0; y < srcH; y += TileSize)
            {
                ct.ThrowIfCancellationRequested();
                int tileH = Math.Min(TileSize, srcH - y);
                for (int x = 0; x < srcW; x += TileSize)
                {
                    int tileW = Math.Min(TileSize, srcW - x);

                    // Fixed 576x576 padded window: constant model input shape for every
                    // tile. pads are constant TilePad; areas outside the source stay zero.
                    // Constant shape avoids per-shape recompilation inside ORT/DML EP.
                    int windowX = x - TilePad;
                    int windowY = y - TilePad;
                    int interX = Math.Clamp(windowX, 0, srcW);
                    int interY = Math.Clamp(windowY, 0, srcH);
                    int interW = Math.Clamp(windowX + WindowSize, 0, srcW) - interX;
                    int interH = Math.Clamp(windowY + WindowSize, 0, srcH) - interY;

                    using var tileBitmap = new SKBitmap(WindowSize, WindowSize, srcBitmap.ColorType, srcBitmap.AlphaType);
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
                    using var srTile = RunModelTile(model, tileImage, ct);

                    if (hasAlpha)
                    {
                        var alphaInfo = new SKImageInfo(WindowSize * ModelScale, WindowSize * ModelScale, SKColorType.Bgra8888, SKAlphaType.Premul);
                        using var alphaTile = new SKBitmap(alphaInfo);
                        UpsampleAlphaChannel(srcBitmap, alphaTile, windowX, windowY, WindowSize, WindowSize);
                        MergeAlphaIntoRgb(srTile, alphaTile);
                    }

                    if (targetScale == 2)
                    {
                        using var canvas = new SKCanvas(destBitmap);
                        using var paint = new SKPaint
                        {
                            FilterQuality = SKFilterQuality.High,
                            IsAntialias = true,
                            BlendMode = SKBlendMode.Src
                        };
                        canvas.DrawBitmap(
                            srTile,
                            new SKRect(TilePad * ModelScale, TilePad * ModelScale,
                                (TilePad + tileW) * ModelScale, (TilePad + tileH) * ModelScale),
                            new SKRect(x * 2, y * 2, (x + tileW) * 2, (y + tileH) * 2),
                            paint);
                    }
                    else
                    {
                        PasteTileInterior(
                            destBitmap,
                            srTile,
                            x * ModelScale,
                            y * ModelScale,
                            TilePad * ModelScale,
                            TilePad * ModelScale,
                            tileW * ModelScale,
                            tileH * ModelScale);
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

    private SKBitmap RunModelTile(SuperResolutionModel model, SKImage tile, CancellationToken ct)
    {
        InferenceSession session = GetOrCreateSession(model);
        try
        {
            return RunModelTileCore(session, model, tile, ct);
        }
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
            return RunModelTileCore(session, model, tile, ct);
        }
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
            var (inputOrt, pinHandle) = CreateInputOrt(model, srcBitmap, w, h);
            inputOrtValues.Add(inputOrt);
            if (pinHandle.HasValue) inputPins.Add(pinHandle.Value);
            inputs[inputName] = inputOrt;

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
            using var outputs = session.Run(runOptions, inputs, new[] { outputName });
            var outputValue = outputs[0];

            var outputShape = outputValue.GetTensorTypeAndShape().Shape;
            int outChannels = (int)outputShape[1];
            int outH = (int)outputShape[2];
            int outW = (int)outputShape[3];

            var outputInfo = new SKImageInfo(outW, outH, SKColorType.Bgra8888, SKAlphaType.Premul);
            var outBitmap = new SKBitmap(outputInfo);

            if (_outputElementTypes.TryGetValue(model, out var outType) && outType == TensorElementType.Float16)
                ReadOutputToBitmapHalf(outputValue, outBitmap, outW, outH, outChannels);
            else
                ReadOutputToBitmap(outputValue, outBitmap, outW, outH, outChannels);
            return outBitmap;
        }
        finally
        {
            foreach (var p in inputPins)
            {
                try { p.Free(); } catch { }
            }
            foreach (var o in inputOrtValues)
            {
                try { o.Dispose(); } catch { }
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

    private (OrtValue Ort, System.Runtime.InteropServices.GCHandle? Pin) CreateInputOrt(SuperResolutionModel model, SKBitmap src, int w, int h)
    {
        TensorElementType inputType = _inputElementTypes.TryGetValue(model, out var t) ? t : TensorElementType.Float;
        if (inputType == TensorElementType.Float16)
        {
            var halfTensor = new DenseTensor<Half>(new[] { 1, 3, h, w });
            FillRgbTensorHalf(src, halfTensor, w, h);
            if (!System.Runtime.InteropServices.MemoryMarshal.TryGetArray<Half>(halfTensor.Buffer, out var seg) || seg.Array == null)
                throw new InvalidOperationException("Half tensor buffer unavailable.");
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(seg.Array, System.Runtime.InteropServices.GCHandleType.Pinned);
            return (
                OrtValue.CreateTensorValueWithData(
                    OrtMemoryInfo.DefaultInstance,
                    TensorElementType.Float16,
                    new long[] { 1, 3, h, w },
                    handle.AddrOfPinnedObject(),
                    seg.Count * 2L),
                handle);
        }
        else
        {
            var tensor = new DenseTensor<float>(new[] { 1, 3, h, w });
            FillRgbTensor(src, tensor, w, h);
            return (
                OrtValue.CreateTensorValueFromMemory(OrtMemoryInfo.DefaultInstance, tensor.Buffer, new long[] { 1, 3, h, w }),
                null);
        }
    }

    private static void FillRgbTensor(SKBitmap src, DenseTensor<float> tensor, int w, int h)
    {
        var span = tensor.Buffer.Span;
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
    {
        var span = outputValue.GetTensorDataAsSpan<float>();

        int channelSize = w * h;
        int rOff = 0;
        int gOff = channels >= 2 ? channelSize : 0;
        int bOff = channels >= 3 ? channelSize * 2 : 0;

        var pixels = dst.GetPixelSpan();
        int stride = w * 4;

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int p = row + x;
                float r = Math.Clamp(span[rOff + p], 0f, 1f);
                float g = Math.Clamp(span[gOff + p], 0f, 1f);
                float b = Math.Clamp(span[bOff + p], 0f, 1f);

                int dp = y * stride + x * 4;
                pixels[dp]     = (byte)(b * 255f);
                pixels[dp + 1] = (byte)(g * 255f);
                pixels[dp + 2] = (byte)(r * 255f);
                pixels[dp + 3] = 255;
            }
        }
    }

    private static void FillRgbTensorHalf(SKBitmap src, DenseTensor<Half> tensor, int w, int h)
    {
        var span = tensor.Buffer.Span;
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
    {
        var span = outputValue.GetTensorDataAsSpan<Float16>();

        int channelSize = w * h;
        int rOff = 0;
        int gOff = channels >= 2 ? channelSize : 0;
        int bOff = channels >= 3 ? channelSize * 2 : 0;

        var pixels = dst.GetPixelSpan();
        int stride = w * 4;

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                int p = row + x;
                float r = Math.Clamp(span[rOff + p].ToFloat(), 0f, 1f);
                float g = Math.Clamp(span[gOff + p].ToFloat(), 0f, 1f);
                float b = Math.Clamp(span[bOff + p].ToFloat(), 0f, 1f);

                int dp = y * stride + x * 4;
                pixels[dp]     = (byte)(b * 255f);
                pixels[dp + 1] = (byte)(g * 255f);
                pixels[dp + 2] = (byte)(r * 255f);
                pixels[dp + 3] = 255;
            }
        }
    }

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
    {
        var rgbPixels = rgb.GetPixelSpan();
        var alphaPixels = alpha.GetPixelSpan();
        for (int i = 0; i < rgbPixels.Length; i += 4)
        {
            byte a = alphaPixels[i + 3];
            rgbPixels[i]     = (byte)(rgbPixels[i]     * a / 255);
            rgbPixels[i + 1] = (byte)(rgbPixels[i + 1] * a / 255);
            rgbPixels[i + 2] = (byte)(rgbPixels[i + 2] * a / 255);
            rgbPixels[i + 3] = a;
        }
    }

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
            if (_sessions.TryGetValue(model, out var existing))
            {
                // Rebuild the session when the desired EP changed (user toggled the setting).
                if (_sessionUsesDml.TryGetValue(model, out var cur) && cur == wantDml)
                    return existing;
                _sessions.Remove(model);
                _sessionUsesDml.Remove(model);
                try { existing.Dispose(); } catch { }
            }

            string fileName = GetFileName(model);
            string path = Path.Combine(_modelsDirectory, fileName);
            var sessionOptions = BuildSessionOptions();

            var session = new InferenceSession(path, sessionOptions);
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
        return _preferences.UseSuperResolutionDml;
    }

    private SessionOptions BuildSessionOptions()
    {
        // Attempt DML per session creation (not only for the first model). DML is
        // skipped when disabled at runtime (failure fallback) or via user settings.
        var options = new SessionOptions();

        if (WantDml())
        {
            try
            {
                options.AppendExecutionProvider_DML(0);
                _dmlAvailable = true;
                _logger.LogInformation("Super-resolution: DirectML execution provider enabled.");
            }
            catch (Exception ex)
            {
                _dmlAvailable = false;
                _logger.LogWarning(ex, "DirectML unavailable, falling back to CPU EP.");
            }
        }
        else
        {
            _dmlAvailable = false;
            _logger.LogWarning("Super-resolution: GPU acceleration disabled (setting or SR_FORCE_CPU); using CPU EP.");
        }

        if (!_dmlAvailable)
        {
            options.AppendExecutionProvider_CPU(0);
        }

        // ORT's CPU memory arena retains its high-water mark for the session lifetime;
        // on this workload that is multiple GB kept after the first run. Disable it so
        // each run returns its buffers (per-tile memory stays flat).
        options.EnableCpuMemArena = false;

        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        return options;
    }

    private static string GetFileName(SuperResolutionModel model) => model switch
    {
        SuperResolutionModel.RealEsrganX4Plus => "RealESRGAN_x4plus.onnx",
        SuperResolutionModel.RealEsrganX4PlusAnime => "RealESRGAN_x4plus_anime.onnx",
        SuperResolutionModel.RealEsrganGeneralX4V3 => "realesr-general-x4v3_fp16.onnx",
        _ => throw new ArgumentOutOfRangeException(nameof(model))
    };

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SuperResolutionService));
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var s in _sessions.Values)
            {
                try { s.Dispose(); } catch { }
            }
            _sessions.Clear();
        }
    }
}
