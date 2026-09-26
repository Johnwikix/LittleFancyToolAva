using FancyToolAva.Models;

namespace FancyToolAva.Services;

public interface IAudioConvertService
{
    Task<AudioProbeInfo?> ProbeAsync(string inputPath, CancellationToken ct = default);

    Task ConvertAsync(
        string inputPath,
        string outputPath,
        AudioConvertOptions options,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}
