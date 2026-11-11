using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace FancyToolAva.Services;

/// <summary>
/// VOSR-1.4B one-step generative restoration.
/// </summary>
/// <remarks>
/// Unlike the ESRGAN-family models this is a nine-graph chain: DINOv2 semantic features,
/// Qwen-Image VAE encode, DiT prepare, four contiguous DiT block stages, DiT final
/// (velocity) and the VAE decoder. Every graph is exported with static 512×512 shapes,
/// so one network call sees exactly one fixed tile and the host owns tiling, the one-step
/// latent update and blending.
///
/// The network is resolution preserving: it restores detail at the resolution it is given.
/// Callers therefore pre-scale the source (bicubic) to the requested output size and lay
/// 512×512 windows over that image. Only the interior 384×384 of each window is kept, which
/// removes the need for overlap blending and keeps tile boundaries seam free.
/// </remarks>
internal sealed class VosrImagePipeline : IDisposable
{
    internal const int TileSize = 512;
    // Overlap between neighbouring tiles; also the size of the cross-fade band.
    internal const int BlendSize = 128;

    private const int LatentChannels = 16;
    private const int LatentSize = 64;
    private const int LatentPlane = LatentSize * LatentSize;
    private const int TokenCount = 1024;
    private const int HiddenSize = 1536;
    private const int BlockConditioningSize = 9216;
    private const int DinoChannels = 1024;

    // DiT 块图文件名（按执行顺序，取自固定清单）：这些是显存占用的大头，按阶段流式加载。
    private static readonly string[] BlockFiles = ModelManifest.Vosr
        .Select(entry => entry.FileName)
        .Where(name => name.Contains("dit_blocks", StringComparison.Ordinal))
        .ToArray();

    // 阶段主序每个批次最多处理的图块数：约束每块约 6 MB 中间态的总内存（约 300 MB）。
    private const int MaxTilesPerStageBatch = 48;

    private readonly ILogger _logger;
    private readonly object _gate = new();
    private readonly object _runGate = new();
    private Graphs? _graphs;
    private string? _sessionDirectory;
    private VideoSrDevice? _device;
    private bool _disposed;
    // GPU 资源类失败（如 OpenVINO CL_OUT_OF_RESOURCES）之后，OpenVINO 会话处于损坏状态：
    // 释放会触发原生 abort（进程直接退出）。此时跳过释放并改用 CPU。
    private bool _forceCpu;
    private bool _graphsPoisoned;

    internal VosrImagePipeline(ILogger logger) => _logger = logger;

    /// <summary>Gets whether every graph of the chain is present in the managed model directory.</summary>
    internal static bool IsAvailable(out string? missing)
    {
        missing = null;
        string directory = AppPaths.ModelsDirectory;
        foreach (var graph in ModelManifest.Vosr)
        {
            if (!File.Exists(Path.Combine(directory, graph.FileName)))
            {
                missing = graph.FileName;
                return false;
            }
        }
        return true;
    }

    /// <summary>Restores fine structure at the pixel resolution of <paramref name="targetScale"/>.</summary>
    internal SKBitmap Restore(SKBitmap source, int targetScale, IProgress<double>? progress,
        Action<VideoSrDevice>? deviceChanged, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (targetScale is not (2 or 4))
            throw new ArgumentOutOfRangeException(nameof(targetScale), "Target scale must be 2x or 4x.");

        // 与 ReleaseSessions/Dispose 串行：释放原生会话期间不允许有在途推理，否则会在
        // OpenVINO/DML 会话内部触发原生崩溃（进程直接退出，托管 catch 无法接管）。
        lock (_runGate)
            return RestoreCore(source, targetScale, progress, deviceChanged, ct);
    }

    private SKBitmap RestoreCore(SKBitmap source, int targetScale, IProgress<double>? progress,
        Action<VideoSrDevice>? deviceChanged, CancellationToken ct)
    {
        int targetWidth = source.Width * targetScale;
        int targetHeight = source.Height * targetScale;
        // Windows must stay inside the working image; tiny targets are padded and cropped back.
        int workWidth = Math.Max(targetWidth, TileSize);
        int workHeight = Math.Max(targetHeight, TileSize);

        using var storage = ModelStorage.Acquire(ct);
        var graphs = GetOrCreateGraphs(ct);
        ReportDevice(graphs.Device, deviceChanged);

        using var work = ResizeInto(source, workWidth, workHeight, targetWidth, targetHeight);
        bool hasAlpha = source.AlphaType != SKAlphaType.Opaque;
        var dest = new SKBitmap(new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Opaque));

        // Tiles overlap by BlendSize and are cross-faded, which keeps generative tile
        // boundaries invisible; the last origin is pulled in so nothing is left uncovered.
        int step = TileSize - BlendSize;
        int columns = Math.Max(1, (workWidth - TileSize + step - 1) / step + 1);
        int rows = Math.Max(1, (workHeight - TileSize + step - 1) / step + 1);
        var originsX = Distribute(workWidth, columns);
        var originsY = Distribute(workHeight, rows);
        int total = columns * rows;
        var ramp = BuildRamp();
        // 每个批次最多约 48 块：4 个 DiT 阶段各只加载一次，同时把中间态内存约束在约 300 MB。
        int columnsPerBatch = Math.Max(1, Math.Min(columns, MaxTilesPerStageBatch / rows));
        int prepared = 0;
        int blended = 0;
        Strip? previous = null;

        try
        {
            progress?.Report(0.02);
            for (int batchStart = 0; batchStart < columns; batchStart += columnsPerBatch)
            {
                int batchEnd = Math.Min(columns, batchStart + columnsPerBatch);
                var states = new TileState[(batchEnd - batchStart) * rows];

                // 1) 逐块求语义条件、VAE 编码与 DiT prepare，中间态留在内存。
                for (int column = batchStart; column < batchEnd; column++)
                {
                    int x = originsX[column];
                    for (int row = 0; row < rows; row++)
                    {
                        ct.ThrowIfCancellationRequested();
                        using var window = ExtractWindow(work, x, originsY[row]);
                        states[(column - batchStart) * rows + row] = PrepareTile(graphs, window, column, row, ct);
                        progress?.Report(0.02 + 0.40 * (++prepared) / total);
                    }
                }

                // 2) 四个 DiT 阶段按阶段主序执行：同一时刻只常驻一个阶段会话，用完立即释放，
                //    显存峰值从"四阶段同时常驻（约 2.7 GB）"降到单个最大阶段（约 0.9 GB）。
                foreach (string blockFile in BlockFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    RunBlockStage(graphs.Backend, blockFile, states, ct);
                }

                // 3) DiT final 求 velocity，单步 Euler 更新 latent。
                foreach (var state in states) FinishTile(graphs, state, ct);

                // 4) 逐列解码并做重叠带交叉淡化后写出。
                for (int column = batchStart; column < batchEnd; column++)
                {
                    int x = originsX[column];
                    int nextX = column + 1 < columns ? originsX[column + 1] : x + TileSize;
                    var sums = new float[TileSize * workHeight * 3];
                    var weights = new float[TileSize * workHeight];
                    for (int row = 0; row < rows; row++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var state = states[(column - batchStart) * rows + row];
                        using var decoded = Run(graphs.VaeDecoder,
                            [new Input("normalized_latent", state.Updated!, [1, LatentChannels, LatentSize, LatentSize])], ["sr_image"], ct);
                        using var tile = ToBitmap(decoded[0]);
                        Accumulate(sums, weights, tile, ramp, originsY[row], workHeight);
                        progress?.Report(0.42 + 0.50 * (++blended) / total);
                    }
                    previous = WriteStrip(dest, sums, weights, x, nextX, workHeight, targetWidth, targetHeight, previous);
                }
            }

            previous?.Dispose();
            if (hasAlpha) MergeUpscaledAlpha(source, dest);
            progress?.Report(1.0);
            return dest;
        }
        catch
        {
            previous?.Dispose();
            dest.Dispose();
            throw;
        }
    }

    /// <summary>Evenly spaced tile origins so the last window ends exactly on the far edge.</summary>
    private static int[] Distribute(int extent, int count)
    {
        var origins = new int[count];
        if (count == 1) return origins;
        double spacing = (extent - TileSize) / (double)(count - 1);
        for (int i = 0; i < count; i++) origins[i] = (int)Math.Round(i * spacing);
        origins[count - 1] = extent - TileSize;
        return origins;
    }

    /// <summary>Right-hand overlap of one processed column, used to cross-fade with the next one.</summary>
    private sealed class Strip(int width, int height, float[] rgb) : IDisposable
    {
        internal int Width { get; } = width;
        internal int Height { get; } = height;
        internal float[] Rgb { get; } = rgb;

        public void Dispose() => ArrayPool<float>.Shared.Return(Rgb);
    }

    /// <summary>Per-tile state carried across the stage-major DiT execution.</summary>
    private sealed class TileState(Half[] latent, Half[] hidden, Half[] conditioning, Half[] blockConditioning, Half[] projectedDino)
    {
        internal Half[] Latent { get; } = latent;
        internal Half[] Hidden { get; set; } = hidden;
        internal Half[] Conditioning { get; } = conditioning;
        internal Half[] BlockConditioning { get; } = blockConditioning;
        internal Half[] ProjectedDino { get; } = projectedDino;
        internal Half[]? Updated { get; set; }
    }

    /// <summary>Steps 1–4 of the chain for one tile: DINO features, VAE encode and DiT prepare.</summary>
    private TileState PrepareTile(Graphs graphs, SKBitmap window, int tileX, int tileY, CancellationToken ct)
    {
        var image = new Half[3 * TileSize * TileSize];
        FillImageTensor(window, image);
        var imageInput = new Input("lq_image", image, [1, 3, TileSize, TileSize]);

        // 1) Semantic condition: DINOv2 layer-17 features of the same tile.
        Half[] dino;
        using (var outputs = Run(graphs.Dino, [imageInput], ["dino_features"], ct))
            dino = ReadHalf(outputs[0]);

        // 2) VAE encode. Zero posterior noise keeps the latent deterministic, matching the
        //    reference implementation's default posterior_mode.
        var posterior = new Half[LatentChannels * LatentPlane];
        var posteriorInput = new Input("posterior_noise", posterior, [1, LatentChannels, LatentSize, LatentSize]);
        Half[] lowQualityLatent;
        using (var outputs = Run(graphs.VaeEncoder, [imageInput, posteriorInput], ["lq_latent"], ct))
            lowQualityLatent = ReadHalf(outputs[0]);

        // 3) Initial latent seeded per tile position so repeated runs are byte-identical.
        var latent = new Half[LatentChannels * LatentPlane];
        var random = new Random(SeedBase + tileY * 7919 + tileX);
        for (int i = 0; i < latent.Length; i++) latent[i] = (Half)NextGaussian(random);

        var pair = new Half[2 * LatentChannels * LatentPlane];
        Array.Copy(lowQualityLatent, 0, pair, 0, LatentChannels * LatentPlane);
        Array.Copy(latent, 0, pair, LatentChannels * LatentPlane, LatentChannels * LatentPlane);

        // 4) DiT prepare at the one-step boundary (t = 1, next = 0).
        Half[] hidden, conditioning, blockConditioning, projectedDino;
        using (var outputs = Run(graphs.Prepare,
        [
            new Input("latent_pair", pair, [1, 32, LatentSize, LatentSize]),
            new Input("timestep", new float[] { 1f }, [1]),
            new Input("next_timestep", new float[] { 0f }, [1]),
            new Input("dino_features", dino, [1, TokenCount, DinoChannels])
        ], ["hidden", "conditioning", "block_conditioning", "projected_dino"], ct))
        {
            hidden = ReadHalf(outputs[0]);
            conditioning = ReadHalf(outputs[1]);
            blockConditioning = ReadHalf(outputs[2]);
            projectedDino = ReadHalf(outputs[3]);
        }

        return new TileState(latent, hidden, conditioning, blockConditioning, projectedDino);
    }

    /// <summary>
    /// Loads one DiT stage session, runs every tile of the batch through it and releases the
    /// session. Only one stage is resident at a time, which is what cuts the GPU weight peak.
    /// </summary>
    private void RunBlockStage(VideoSrBackend backend, string fileName, TileState[] states, CancellationToken ct)
    {
        InferenceSession? stage = null;
        try
        {
            stage = WindowsMlAccelerator.CreateTensorSession(
                Path.Combine(AppPaths.ModelsDirectory, fileName), backend, new Dictionary<string, long>(), ct, out _);
            foreach (var state in states)
            {
                ct.ThrowIfCancellationRequested();
                using var outputs = Run(stage,
                [
                    new Input("hidden", state.Hidden, [1, TokenCount, HiddenSize]),
                    new Input("block_conditioning", state.BlockConditioning, [1, BlockConditioningSize]),
                    new Input("projected_dino", state.ProjectedDino, [1, TokenCount, HiddenSize])
                ], ["hidden_out"], ct);
                state.Hidden = ReadHalf(outputs[0]);
            }
            stage.Dispose();
        }
        catch (OperationCanceledException)
        {
            try { stage?.Dispose(); } catch { }
            throw;
        }
        catch (Exception ex)
        {
            // 资源耗尽后 OpenVINO 会话已损坏：Dispose 会原生崩溃，只放弃它、交给进程退出回收。
            if (stage is not null && IsGpuResourceFailure(ex)) GC.SuppressFinalize(stage);
            else { try { stage?.Dispose(); } catch { } }
            throw;
        }
    }

    /// <summary>Step 6 of the chain: DiT final velocity and the one-step Euler update z = z − v.</summary>
    private static void FinishTile(Graphs graphs, TileState state, CancellationToken ct)
    {
        Half[] velocity;
        using (var outputs = Run(graphs.Final,
        [
            new Input("hidden", state.Hidden, [1, TokenCount, HiddenSize]),
            new Input("conditioning", state.Conditioning, [1, HiddenSize])
        ], ["velocity"], ct))
            velocity = ReadHalf(outputs[0]);

        var updated = new Half[state.Latent.Length];
        for (int i = 0; i < updated.Length; i++) updated[i] = (Half)((float)state.Latent[i] - (float)velocity[i]);
        state.Updated = updated;
    }

    #region graph lifecycle

    private Graphs GetOrCreateGraphs(CancellationToken ct)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            string directory = AppPaths.ModelsDirectory;
            // 强制 CPU 后不得复用已缓存的硬件会话。
            if (_graphs is { } existing && string.Equals(directory, _sessionDirectory, ModelStorage.PathComparison)
                && !(_forceCpu && existing.Device.IsHardware))
                return existing;

            ReleaseGraphs();
            _sessionDirectory = directory;

            var backends = SelectBackends();
            List<string> failures = [];
            foreach (var backend in backends)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    _graphs = Load(directory, backend, ct);
                    _logger.LogInformation("VOSR chain ready on {Device}", _graphs.Device.Name);
                    return _graphs;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    failures.Add($"{backend}: {ex.Message}");
                    _logger.LogWarning(ex, "VOSR chain failed on backend {Backend}", backend);
                }
            }

            throw new InvalidOperationException(
                "VOSR model could not be loaded. " + string.Join("; ", failures));
        }
    }

    private VideoSrBackend[] SelectBackends()
    {
        if (_forceCpu || Environment.GetEnvironmentVariable("SR_FORCE_CPU") == "1" || !OperatingSystem.IsWindows())
            return [VideoSrBackend.Cpu];
        // 厂商 GPU 提供程序（OpenVINO / TensorRT-RTX）优先，DirectML 作为兼容回退。
        return [VideoSrBackend.Gpu, VideoSrBackend.Cpu];
    }

    /// <summary>
    /// 记录 GPU 资源类推理失败并毒化当前会话组。返回 true 表示这是硬件资源耗尽，
    /// 调用方应把异常换成可操作提示；会话将在释放时被跳过（放弃而非 Dispose）。
    /// </summary>
    internal bool MarkGpuResourceFailure(Exception exception)
    {
        lock (_gate)
        {
            if (_graphs is not { Device.IsHardware: true } || !IsGpuResourceFailure(exception)) return false;
            _graphsPoisoned = true;
            _forceCpu = true;
            _logger.LogError(exception,
                "VOSR GPU inference exhausted graphics resources; contaminated sessions will be abandoned and later runs use CPU");
            return true;
        }
    }

    private static bool IsGpuResourceFailure(Exception exception)
    {
        string message = exception.Message;
        return message.Contains("CL_OUT_OF_RESOURCES", StringComparison.OrdinalIgnoreCase)
            || message.Contains("CL_OUT_OF_HOST_MEMORY", StringComparison.OrdinalIgnoreCase)
            || message.Contains("out of resources", StringComparison.OrdinalIgnoreCase)
            || message.Contains("DeviceLost", StringComparison.OrdinalIgnoreCase)
            || message.Contains("device lost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Disposes or abandons the current graph set depending on whether it is contaminated.</summary>
    private void ReleaseGraphs()
    {
        if (_graphsPoisoned) _graphs?.Abandon();
        else _graphs?.Dispose();
        _graphs = null;
        _graphsPoisoned = false;
        _device = null;
    }

    private static Graphs Load(string directory, VideoSrBackend backend, CancellationToken ct)
    {
        // Every VOSR graph is exported with static shapes, so no free dimension needs pinning.
        var dimensions = new Dictionary<string, long>();
        var sessions = new List<InferenceSession>();
        VideoSrDevice? device = null;
        try
        {
            InferenceSession Open(string fileName)
            {
                string path = Path.Combine(directory, fileName);
                if (!File.Exists(path)) throw new FileNotFoundException("VOSR graph missing", path);
                var session = WindowsMlAccelerator.CreateTensorSession(path, backend, dimensions, ct, out var selected);
                sessions.Add(session);
                device ??= selected;
                return session;
            }

            var dino = Open("vosr_dinov2l_layer17.onnx");
            var encoder = Open("vosr_qwen_vae_encoder.onnx");
            var prepare = Open("vosr_dit_prepare.onnx");
            // 四个 DiT 块不常驻：按阶段在主循环里加载、跑完整批再释放。
            var final = Open("vosr_dit_final.onnx");
            var decoder = Open("vosr_qwen_vae_decoder.onnx");
            return new Graphs(dino, encoder, prepare, final, decoder,
                device ?? new VideoSrDevice("CPU", false), backend);
        }
        catch
        {
            foreach (var session in sessions)
            {
                try { session.Dispose(); } catch { }
            }
            throw;
        }
    }

    private sealed record Graphs(
        InferenceSession Dino,
        InferenceSession VaeEncoder,
        InferenceSession Prepare,
        InferenceSession Final,
        InferenceSession VaeDecoder,
        VideoSrDevice Device,
        VideoSrBackend Backend) : IDisposable
    {
        public void Dispose()
        {
            try { Dino.Dispose(); } catch { }
            try { VaeEncoder.Dispose(); } catch { }
            try { Prepare.Dispose(); } catch { }
            try { Final.Dispose(); } catch { }
            try { VaeDecoder.Dispose(); } catch { }
        }

        /// <summary>
        /// Drops the sessions without releasing their native handles. Only used after the
        /// OpenVINO CL context is known to be corrupted (CL_OUT_OF_RESOURCES): calling
        /// Dispose there aborts the process natively, which no managed catch can intercept.
        /// Suppressing each finalizer keeps the broken release off the finalizer thread too;
        /// the handles then leak until process exit, where the OS reclaims them.
        /// </summary>
        internal void Abandon()
        {
            GC.SuppressFinalize(Dino);
            GC.SuppressFinalize(VaeEncoder);
            GC.SuppressFinalize(Prepare);
            GC.SuppressFinalize(Final);
            GC.SuppressFinalize(VaeDecoder);
        }
    }

    #endregion

    #region tensor plumbing

    private readonly record struct Input(string Name, Array Data, long[] Shape);

    private static IDisposableReadOnlyCollection<OrtValue> Run(
        InferenceSession session, Input[] inputs, string[] outputs, CancellationToken ct)
    {
        var leases = new List<Lease>(inputs.Length);
        try
        {
            foreach (var input in inputs)
            {
                var lease = new Lease();
                if (input.Data is Half[] half)
                {
                    var pin = GCHandle.Alloc(half, GCHandleType.Pinned);
                    lease.Pin = pin;
                    lease.Value = OrtValue.CreateTensorValueWithData(OrtMemoryInfo.DefaultInstance,
                        TensorElementType.Float16, input.Shape, pin.AddrOfPinnedObject(), half.LongLength * 2L);
                }
                else if (input.Data is float[] single)
                {
                    lease.Value = OrtValue.CreateTensorValueFromMemory(single, input.Shape);
                }
                else
                {
                    throw new NotSupportedException($"Unsupported tensor payload: {input.Data.GetType()}");
                }
                leases.Add(lease);
            }

            using var options = new RunOptions();
            using var registration = ct.Register(() => options.Terminate = true);
            var result = session.Run(options, inputs.Select(i => i.Name).ToArray(),
                leases.Select(l => l.Value).ToArray(), outputs);
            return new OutputLease(result, leases);
        }
        catch
        {
            foreach (var lease in leases) lease.Dispose();
            throw;
        }
    }

    private sealed class Lease : IDisposable
    {
        internal OrtValue Value = null!;
        internal GCHandle? Pin;

        public void Dispose()
        {
            try { Value?.Dispose(); } catch { }
            try { if (Pin is { IsAllocated: true } pin) pin.Free(); } catch { }
        }
    }

    private sealed class OutputLease(IDisposableReadOnlyCollection<OrtValue> outputs, List<Lease> leases)
        : IDisposableReadOnlyCollection<OrtValue>
    {
        public int Count => outputs.Count;
        public OrtValue this[int index] => outputs[index];
        public IEnumerator<OrtValue> GetEnumerator() => outputs.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        public void Dispose()
        {
            outputs.Dispose();
            foreach (var lease in leases) lease.Dispose();
        }
    }

    private static Half[] ReadHalf(OrtValue value)
        => MemoryMarshal.Cast<Float16, Half>(value.GetTensorDataAsSpan<Float16>()).ToArray();

    private static void FillImageTensor(SKBitmap source, Half[] destination)
    {
        var pixels = source.GetPixelSpan();
        const int plane = TileSize * TileSize;
        for (int i = 0; i < plane; i++)
        {
            int p = i * 4;
            destination[i] = (Half)(pixels[p + 2] / 127.5f - 1f);
            destination[plane + i] = (Half)(pixels[p + 1] / 127.5f - 1f);
            destination[plane * 2 + i] = (Half)(pixels[p] / 127.5f - 1f);
        }
    }

    private static SKBitmap ToBitmap(OrtValue value)
    {
        var data = MemoryMarshal.Cast<Float16, Half>(value.GetTensorDataAsSpan<Float16>());
        var bitmap = new SKBitmap(new SKImageInfo(TileSize, TileSize, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var pixels = bitmap.GetPixelSpan();
        const int plane = TileSize * TileSize;
        for (int i = 0; i < plane; i++)
        {
            float r = Math.Clamp((float)data[i], -1f, 1f);
            float g = Math.Clamp((float)data[plane + i], -1f, 1f);
            float b = Math.Clamp((float)data[plane * 2 + i], -1f, 1f);
            int p = i * 4;
            pixels[p] = (byte)((b + 1f) * 127.5f);
            pixels[p + 1] = (byte)((g + 1f) * 127.5f);
            pixels[p + 2] = (byte)((r + 1f) * 127.5f);
            pixels[p + 3] = 255;
        }
        return bitmap;
    }

    private static double NextGaussian(Random random)
    {
        double u1 = 1.0 - random.NextDouble();
        double u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }

    private const int SeedBase = 20260101;

    #endregion

    #region pixels

    private static SKBitmap ResizeInto(SKBitmap source, int workWidth, int workHeight, int width, int height)
    {
        var result = new SKBitmap(new SKImageInfo(workWidth, workHeight, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(result);
        using var paint = new SKPaint { IsAntialias = true };
        using var image = SKImage.FromBitmap(source);
        canvas.Clear(SKColors.Black);
        canvas.DrawImage(image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        return result;
    }

    /// <summary>Cosine window: 0 → 1 over the first overlap band, 1 in the middle, 1 → 0 at the end.</summary>
    private static float[] BuildRamp()
    {
        var ramp = new float[TileSize];
        for (int i = 0; i < BlendSize; i++)
        {
            float weight = 0.5f - 0.5f * MathF.Cos(MathF.PI * (i + 0.5f) / BlendSize);
            ramp[i] = weight;
            ramp[TileSize - 1 - i] = weight;
        }
        for (int i = BlendSize; i < TileSize - BlendSize; i++) ramp[i] = 1f;
        return ramp;
    }

    private static SKBitmap ExtractWindow(SKBitmap work, int x, int y)
    {
        var window = new SKBitmap(new SKImageInfo(TileSize, TileSize, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(window);
        using var image = SKImage.FromBitmap(work);
        // The origin is clamped inside the working image, so this is a 1:1 copy without resampling.
        canvas.DrawImage(image, new SKRect(x, y, x + TileSize, y + TileSize), new SKRect(0, 0, TileSize, TileSize),
            new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        return window;
    }

    /// <summary>Adds one restored tile into the column accumulator with the 2D cosine weight.</summary>
    private static void Accumulate(float[] sums, float[] weights, SKBitmap tile, float[] ramp, int originY, int workHeight)
    {
        var pixels = tile.GetPixelSpan();
        int stride = tile.Width * 4;
        int plane = TileSize * workHeight;
        for (int y = 0; y < TileSize; y++)
        {
            float weightY = ramp[y];
            int row = (originY + y) * TileSize;
            for (int x = 0; x < TileSize; x++)
            {
                float weight = weightY * ramp[x];
                int p = y * stride + x * 4;
                sums[row + x] += weight * pixels[p + 2];
                sums[plane + row + x] += weight * pixels[p + 1];
                sums[plane * 2 + row + x] += weight * pixels[p];
                weights[row + x] += weight;
            }
        }
    }

    /// <summary>
    /// Normalizes the column accumulator, cross-fades its leading overlap with the previous
    /// column, writes the settled columns and returns the tail the next column fades against.
    /// </summary>
    private static Strip? WriteStrip(SKBitmap destination, float[] sums, float[] weights, int x, int nextX,
        int workHeight, int targetWidth, int targetHeight, Strip? previous)
    {
        int plane = TileSize * workHeight;
        var rgb = ArrayPool<float>.Shared.Rent(plane * 3);
        try
        {
            for (int i = 0; i < plane; i++)
            {
                float weight = weights[i];
                if (weight < 1e-4f)
                {
                    rgb[i] = 0f;
                    rgb[plane + i] = 0f;
                    rgb[plane * 2 + i] = 0f;
                    continue;
                }
                float inverse = 1f / weight;
                rgb[i] = sums[i] * inverse;
                rgb[plane + i] = sums[plane + i] * inverse;
                rgb[plane * 2 + i] = sums[plane * 2 + i] * inverse;
            }

            var pixels = destination.GetPixelSpan();
            int stride = destination.Width * 4;
            int visibleWidth = Math.Max(0, Math.Min(TileSize, targetWidth - x));
            int visibleHeight = Math.Min(workHeight, targetHeight);
            int fadeWidth = previous is null ? 0 : Math.Min(previous.Width, visibleWidth);
            // Columns past the next origin belong to the next column's cross-fade.
            int writeEnd = Math.Min(visibleWidth, nextX - x);

            for (int y = 0; y < visibleHeight; y++)
            {
                int row = y * TileSize;
                int targetRow = y * stride + x * 4;
                for (int column = 0; column < writeEnd; column++)
                {
                    float red = rgb[row + column];
                    float green = rgb[plane + row + column];
                    float blue = rgb[plane * 2 + row + column];
                    if (column < fadeWidth)
                    {
                        // The previous column owns the complementary weight over the same band.
                        float t = 0.5f - 0.5f * MathF.Cos(MathF.PI * (column + 0.5f) / previous!.Width);
                        int previousRow = y * previous.Width + column;
                        red = previous.Rgb[previousRow] * (1f - t) + red * t;
                        green = previous.Rgb[previous.Width * previous.Height + previousRow] * (1f - t) + green * t;
                        blue = previous.Rgb[previous.Width * previous.Height * 2 + previousRow] * (1f - t) + blue * t;
                    }
                    int p = targetRow + column * 4;
                    pixels[p] = Clamp(blue);
                    pixels[p + 1] = Clamp(green);
                    pixels[p + 2] = Clamp(red);
                    pixels[p + 3] = 255;
                }
            }

            int tailStart = Math.Min(TileSize, Math.Max(0, nextX - x));
            int tailWidth = TileSize - tailStart;
            if (tailWidth <= 0)
            {
                ArrayPool<float>.Shared.Return(rgb);
                return null;
            }

            int tailPlane = tailWidth * visibleHeight;
            var tail = ArrayPool<float>.Shared.Rent(tailPlane * 3);
            for (int y = 0; y < visibleHeight; y++)
            {
                int sourceRow = y * TileSize;
                int targetRow = y * tailWidth;
                for (int column = 0; column < tailWidth; column++)
                {
                    int s = sourceRow + tailStart + column;
                    int t = targetRow + column;
                    tail[t] = rgb[s];
                    tail[tailPlane + t] = rgb[plane + s];
                    tail[tailPlane * 2 + t] = rgb[plane * 2 + s];
                }
            }
            ArrayPool<float>.Shared.Return(rgb);
            previous?.Dispose();
            return new Strip(tailWidth, visibleHeight, tail);
        }
        catch
        {
            ArrayPool<float>.Shared.Return(rgb);
            throw;
        }
    }

    private static byte Clamp(float value) => (byte)Math.Clamp(value, 0f, 255f);

    private static void MergeUpscaledAlpha(SKBitmap source, SKBitmap destination)
    {
        using var alpha = new SKBitmap(new SKImageInfo(destination.Width, destination.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(alpha))
        using (var paint = new SKPaint { IsAntialias = true })
        using (var image = SKImage.FromBitmap(source))
            canvas.DrawImage(image, new SKRect(0, 0, destination.Width, destination.Height),
                new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        ImageTensorPixels.MergeAlpha(destination, alpha);
    }

    #endregion

    private void ReportDevice(VideoSrDevice device, Action<VideoSrDevice>? deviceChanged)
    {
        if (device == _device) return;
        _device = device;
        deviceChanged?.Invoke(device);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>Drops every session, releasing roughly 3.3 GB of weights.</summary>
    internal void ReleaseSessions()
    {
        lock (_runGate)
        {
            lock (_gate)
            {
                ReleaseGraphs();
                _sessionDirectory = null;
            }
        }
    }

    public void Dispose()
    {
        // 与 Restore 同一把 _runGate：释放不得与在途推理重叠。
        lock (_runGate)
        {
            lock (_gate)
            {
                _disposed = true;
                ReleaseGraphs();
                _sessionDirectory = null;
            }
        }
    }
}
