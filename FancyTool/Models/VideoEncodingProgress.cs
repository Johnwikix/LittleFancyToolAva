namespace FancyToolAva.Models;

/// <summary>Reports output frame counts and measured throughput for the current encoding pass.</summary>
/// <param name="ProcessedFrames">The number of output frames submitted to the encoder.</param>
/// <param name="TotalFrames">The exact or estimated output total during conversion, or the actual total at completion; null when unknown.</param>
/// <param name="FramesPerSecond">The measured output frames processed per wall-clock second.</param>
/// <param name="IsTotalEstimated">Whether the total was estimated from duration and output frame rate.</param>
/// <param name="Pass">The current encoding pass, starting at one.</param>
/// <param name="PassCount">The total number of encoding passes.</param>
public sealed record VideoEncodingProgress(long ProcessedFrames, long? TotalFrames, double FramesPerSecond,
    bool IsTotalEstimated, int Pass, int PassCount);
