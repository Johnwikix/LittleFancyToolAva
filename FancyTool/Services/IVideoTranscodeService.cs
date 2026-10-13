using FancyToolAva.Models;

namespace FancyToolAva.Services;

/// <summary>Provides video probing, source frame previews, and clipped transcoding.</summary>
public interface IVideoTranscodeService
{
    /// <summary>Decodes a small source frame at the requested position.</summary>
    /// <param name="inputPath">The local source file.</param>
    /// <param name="positionSeconds">The position on the source timeline in seconds.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The decoded preview frame, or null when no frame is available.</returns>
    Task<VideoPreviewFrame?> GetPreviewAsync(string inputPath, double positionSeconds, CancellationToken ct = default);

    /// <summary>Reads the video and audio stream metadata from a local source.</summary>
    /// <param name="inputPath">The local source file.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The source metadata, or null when the source is unavailable.</returns>
    Task<VideoProbeInfo?> ProbeAsync(string inputPath, CancellationToken ct = default);

    /// <summary>Transcodes the selected source range and optionally reports its current decoded frame.</summary>
    /// <param name="inputPath">The local source file.</param>
    /// <param name="outputPath">The destination file.</param>
    /// <param name="options">The encoding settings and per-file clip range.</param>
    /// <param name="progress">The optional normalized conversion progress reporter.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <param name="preview">The optional reporter for small source frames at the current conversion position.</param>
    /// <returns>The task that completes after the output has been finalized.</returns>
    Task TranscodeAsync(
        string inputPath,
        string outputPath,
        VideoTranscodeOptions options,
        IProgress<double>? progress = null,
        CancellationToken ct = default,
        IProgress<VideoPreviewFrame>? preview = null);
}
