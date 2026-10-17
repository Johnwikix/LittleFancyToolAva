using SkiaSharp;

namespace FancyToolAva.Services;

/// <summary>可选的目标缓冲路径；调用期间由调用方持有像素内存，返回前完成全部写入。</summary>
internal interface IVideoFrameDestinationUpscaler
{
    bool TryUpscaleInto(SKBitmap source, int scale, SKBitmap destination, CancellationToken ct);
}

/// <summary>用于限制保留的原生会话数量，并记录诊断时的实际并发数。</summary>
internal interface IVideoInferenceSessions
{
    int Count { get; }
    int Reserved { get; }
}

/// <summary>已知无状态的视频模型租约；相同 BGRA 输入允许复用像素，时钟仍逐帧更新。</summary>
internal interface IVideoFrameReuseUpscaler { }
