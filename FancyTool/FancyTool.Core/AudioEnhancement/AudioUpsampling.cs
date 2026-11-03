namespace FancyToolAva.AudioEnhancement;

/// <summary>描述后端选择处理格式之前的已解码 PCM 信息。</summary>
public sealed record AudioUpsamplingSource(int SampleRate, int Channels, int BitDepth, int BitrateKbps, bool IsLossless, bool IsDsd);

/// <summary>区分请求的编码采样率与后端内部处理采样率。</summary>
public sealed record AudioUpsamplingRequest(AudioUpsamplingSource Source, int TargetSampleRate);

/// <summary>为每个文件创建独立处理器，使转码器无需了解后端实现。</summary>
public interface IAudioUpsamplingBackend
{
    /// <summary>获取可持久化保存的稳定标识符。</summary>
    string Id { get; }
    /// <summary>在重采样或混音之前检查原始音源。</summary>
    bool Supports(AudioUpsamplingSource source);
    /// <summary>创建由单次转换持有并负责释放的有状态处理器。</summary>
    IAudioUpsamplingProcessor Create(AudioUpsamplingRequest request, CancellationToken cancellationToken);
}

/// <summary>以可复用的数据块处理归一化的交织双精度 PCM，独立于编解码器。</summary>
/// <remarks>处理采样率和块大小可与编码器要求不同；桥接层会裁剪声明的输出延迟，
/// 对最后一个输入块补零、使用静音排空处理器，并执行最终输出重采样。
/// 实现负责信号历史和取消处理；所有调用在同一工作线程串行执行。</remarks>
public interface IAudioUpsamplingProcessor : IDisposable
{
    /// <summary>获取处理器要求的输入采样率。</summary>
    int InputSampleRate { get; }
    /// <summary>获取最终编码重采样之前的内部输出采样率。</summary>
    int OutputSampleRate { get; }
    /// <summary>获取输入输出交织数据的声道数。</summary>
    int Channels { get; }
    /// <summary>获取每次调用必须消耗的输入帧数。</summary>
    int InputFramesPerBlock { get; }
    /// <summary>获取每次调用必须写入的输出帧数。</summary>
    int OutputFramesPerBlock { get; }
    /// <summary>获取流开始时一次性丢弃的输出帧数。</summary>
    int LatencyFrames { get; }
    /// <summary>读取一个归一化 PCM 输入块，并写入一个输出块。</summary>
    void ProcessBlock(ReadOnlySpan<double> input, Span<double> output, CancellationToken cancellationToken);
}

/// <summary>查找已注册的实现；默认注册表仅包含托管 DSP 后端。</summary>
public sealed class AudioUpsamplingBackendRegistry
{
    /// <summary>获取内置后端的稳定标识符。</summary>
    public const string DefaultBackendId = "managed-dsp";
    private readonly Dictionary<string, IAudioUpsamplingBackend> _backends = new(StringComparer.Ordinal);

    /// <summary>使用内置后端或显式传入的后端初始化不可变注册表。</summary>
    public AudioUpsamplingBackendRegistry(IEnumerable<IAudioUpsamplingBackend>? backends = null)
    {
        foreach (var backend in backends ?? [new ManagedDspUpsamplingBackend()])
        {
            if (string.IsNullOrWhiteSpace(backend.Id) || !_backends.TryAdd(backend.Id, backend))
                throw new ArgumentException("Upsampling backend identifiers must be nonempty and unique.", nameof(backends));
        }
    }

    /// <summary>查找指定后端，不创建处理器。</summary>
    public IAudioUpsamplingBackend Get(string id) => _backends.TryGetValue(id, out var backend)
        ? backend : throw new NotSupportedException($"Unknown upsampling backend: {id}");
}

/// <summary>解析用户选择的目标采样率，独立于后端内部采样率。</summary>
public static class AudioUpsamplingRate
{
    /// <summary>应用一倍、二倍或四倍倍率；结果超过 192 kHz 时选择可整除的降采样因子。</summary>
    public static int Resolve(int sourceRate, int multiplier)
    {
        if (sourceRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRate));
        if (multiplier is not (1 or 2 or 4)) throw new ArgumentOutOfRangeException(nameof(multiplier));
        int target = checked(sourceRate * multiplier);
        int divisor = Math.Max(1, (target + 191999) / 192000);
        while (target % divisor != 0) divisor++;
        return target / divisor;
    }
}
