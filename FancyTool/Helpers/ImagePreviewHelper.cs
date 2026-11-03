using Avalonia.Media.Imaging;

namespace FancyToolAva.Helpers;

internal static class ImagePreviewHelper
{
    public static Bitmap? ToBitmap(byte[]? pngBytes)
    {
        if (pngBytes is not { Length: > 0 }) return null;
        using var stream = new MemoryStream(pngBytes);
        return new Bitmap(stream);
    }
}
