using FFmpeg.AutoGen;

namespace FancyToolAva.Services;

/// <summary>Records a probed adapter, supported integer multipliers, and the reason for unavailability.</summary>
internal sealed record HardwareInterpolationSupport(VideoInterpolationMode Mode, VideoGpuAdapter? Adapter,
    IReadOnlyList<int> Multipliers, string ReasonKey, string Detail, bool UseLinearImages = false)
{
    internal bool IsSupported => Adapter != null && Multipliers.Count > 0;
}

/// <summary>Checks the bundled filters and executes GPU frame probes before exposing vendor interpolation.</summary>
internal static unsafe class HardwareVideoInterpolation
{
    private static readonly Lazy<Task<IReadOnlyList<HardwareInterpolationSupport>>> Capabilities = new(() => Task.Run(Probe));

    internal static bool IsHardware(VideoInterpolationMode mode) => mode is VideoInterpolationMode.AmdOpticalFlow or VideoInterpolationMode.NvidiaOpticalFlow;

    internal static Task<IReadOnlyList<HardwareInterpolationSupport>> GetCapabilitiesAsync() => Capabilities.Value;

    internal static HardwareInterpolationSupport RequireSupport(VideoInterpolationMode mode, int multiplier)
    {
        var support = GetCapabilitiesAsync().GetAwaiter().GetResult().Single(value => value.Mode == mode);
        if (!support.IsSupported || !support.Multipliers.Contains(multiplier))
            throw new NotSupportedException(LocalizationRegistry.Get(support.IsSupported
                ? mode == VideoInterpolationMode.AmdOpticalFlow && multiplier == 6 ? "VideoTranscode.Interp_AmdRates" : "VideoTranscode.Interp_UnsupportedMultiplier"
                : support.ReasonKey) + " " + support.Detail);
        return support;
    }

    internal static AVBufferRef* CreateDevice(VideoInterpolationMode mode, VideoGpuAdapter adapter, bool useLinearImages = false)
    {
        string typeName = mode == VideoInterpolationMode.NvidiaOpticalFlow ? "vulkan" : "d3d11va";
        var type = ffmpeg.av_hwdevice_find_type_by_name(typeName);
        AVBufferRef* device = null;
        // Vulkan identifies a physical device by name; its enumeration order is independent of DXGI.
        string selector = mode == VideoInterpolationMode.NvidiaOpticalFlow ? adapter.Name : adapter.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        AVDictionary* options = null;
        try
        {
            if (mode == VideoInterpolationMode.NvidiaOpticalFlow && useLinearImages)
            {
                int configured = ffmpeg.av_dict_set(&options, "linear_images", "1", 0);
                if (configured < 0) throw FFmpegNative.CreateException(configured, LocalizationRegistry.Get("VideoTranscode.Interp_ProbeFailed"));
            }
            int result = ffmpeg.av_hwdevice_ctx_create(&device, type, selector, options, 0);
            if (result < 0 || device == null)
            {
                ffmpeg.av_buffer_unref(&device);
                throw FFmpegNative.CreateException(result, LocalizationRegistry.Get("VideoTranscode.Interp_ProbeFailed"));
            }
            return device;
        }
        finally { ffmpeg.av_dict_free(&options); }
    }

    internal static string Filter(VideoInterpolationMode mode, int multiplier, string outputRate)
    {
        if (mode == VideoInterpolationMode.NvidiaOpticalFlow && multiplier is 2 or 4 or 6)
            return $"settb=expr=1/1000000,hwupload,fruc_vulkan=fps={outputRate}:perf=medium,hwdownload,format=bgra";
        if (mode == VideoInterpolationMode.AmdOpticalFlow && multiplier is 2 or 4)
        {
            const string stage = "frc_amf=engine_type=dx11:profile=high:use_future_frame=1:fallback_mode=duplicate";
            // AMF preserves the input time base; AVI's 1/fps clock cannot represent intermediate timestamps.
            return "settb=expr=1/1000000,hwupload," + stage + (multiplier == 4 ? "," + stage : string.Empty) + ",hwdownload,format=bgra";
        }
        throw new ArgumentOutOfRangeException(nameof(multiplier));
    }

    private static IReadOnlyList<HardwareInterpolationSupport> Probe()
    {
        var modes = new[] { VideoInterpolationMode.AmdOpticalFlow, VideoInterpolationMode.NvidiaOpticalFlow };
        if (!OperatingSystem.IsWindows()) return modes.Select(mode => Unavailable(mode, "VideoTranscode.Interp_UnsupportedOs")).ToArray();
        try
        {
            var adapters = VideoSrGpuSelector.EnumerateAdapters();
            return modes.Select(mode => ProbeMode(mode, adapters)).ToArray();
        }
        catch (Exception ex) { return modes.Select(mode => Unavailable(mode, "VideoTranscode.Interp_ProbeFailed", ex.Message)).ToArray(); }
    }

    private static HardwareInterpolationSupport ProbeMode(VideoInterpolationMode mode, IReadOnlyList<VideoGpuAdapter> adapters)
    {
        uint vendor = mode == VideoInterpolationMode.AmdOpticalFlow ? 0x1002u : 0x10deu;
        var candidates = adapters.Where(adapter => adapter.VendorId == vendor).OrderByDescending(adapter => adapter.VideoMemory).ToArray();
        if (candidates.Length == 0) return Unavailable(mode, "VideoTranscode.Interp_NoAdapter");
        string filter = mode == VideoInterpolationMode.AmdOpticalFlow ? "frc_amf" : "fruc_vulkan";
        if (!FFmpegNative.TryLoad(out var error)) return Unavailable(mode, "VideoTranscode.Interp_ProbeFailed", error?.Message ?? string.Empty);
        if (ffmpeg.avfilter_get_by_name(filter) == null) return Unavailable(mode, "VideoTranscode.Interp_FilterMissing", filter);
        var failures = new List<string>();
        foreach (var adapter in candidates)
        {
            HardwareInterpolationSupport? best = null;
            // 线性图像降低 Windows 回读开销；探测失败或倍率不全时再尝试最优平铺布局。
            foreach (bool useLinearImages in mode == VideoInterpolationMode.NvidiaOpticalFlow ? new[] { true, false } : new[] { false })
            {
                var multipliers = new List<int>();
                foreach (int multiplier in mode == VideoInterpolationMode.AmdOpticalFlow ? new[] { 2, 4 } : new[] { 2, 4, 6 })
                {
                    try
                    {
                        VideoTranscodeService.ProbeHardwareInterpolation(mode, adapter, multiplier, useLinearImages);
                        multipliers.Add(multiplier);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{adapter.Name} ×{multiplier} (linear={useLinearImages}): {ex.Message}");
                    }
                }
                if (multipliers.Count == 0) continue;
                var support = new HardwareInterpolationSupport(mode, adapter, multipliers, "VideoTranscode.Interp_Ready",
                    adapter.Name + " · " + string.Join(" / ", multipliers.Select(value => $"{value}×")), useLinearImages);
                if (best == null || multipliers.Count > best.Multipliers.Count) best = support;
                if (mode != VideoInterpolationMode.NvidiaOpticalFlow || multipliers.Count == 3) return support;
            }
            if (best != null) return best;
        }
        return Unavailable(mode, "VideoTranscode.Interp_ProbeFailed", string.Join("; ", failures));
    }

    private static HardwareInterpolationSupport Unavailable(VideoInterpolationMode mode, string reason, string detail = "")
        => new(mode, null, [], reason, detail);
}
