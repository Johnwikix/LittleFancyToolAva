using FancyToolAva.Models;

namespace FancyToolAva.Services;

public interface IVideoTranscodeService
{
    Task<VideoProbeInfo?> ProbeAsync(string inputPath, CancellationToken ct = default);

    Task TranscodeAsync(
        string inputPath,
        string outputPath,
        VideoTranscodeOptions options,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}
