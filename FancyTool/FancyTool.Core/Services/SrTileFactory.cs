namespace FancyToolAva.Services;

/// <summary>Creates shared image/video tile runners through the Windows ML runtime.</summary>
internal static class SrTileFactory
{
    internal static IVideoSrTileModel Create(string path, VideoSrBackend backend, int window, CancellationToken ct, out VideoSrDevice device, int nativeScale = 4)
    {
        var ort = new OrtVideoSrTileModel(path, backend, window, ct, nativeScale);
        device = ort.Device;
        return ort;
    }
}
