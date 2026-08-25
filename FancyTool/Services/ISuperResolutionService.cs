using SkiaSharp;

namespace FancyToolAva.Services;

public enum SuperResolutionModel
{
    RealEsrganX4Plus = 0,
    RealEsrganX4PlusAnime = 1,
    RealEsrganGeneralX4V3 = 2
}

public interface ISuperResolutionService : IDisposable
{
    IReadOnlyList<string> AvailableModels { get; }

    bool IsModelAvailable(SuperResolutionModel model);

    Task<SKBitmap> UpscaleAsync(
        SKBitmap source,
        SuperResolutionModel model,
        int targetScale,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}