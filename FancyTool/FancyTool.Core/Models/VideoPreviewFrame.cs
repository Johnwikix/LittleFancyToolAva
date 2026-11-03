namespace FancyToolAva.Models;

/// <summary>Contains a small BGRA source frame and its position on the source timeline.</summary>
/// <param name="Pixels">The tightly packed BGRA pixel data.</param>
/// <param name="Width">The frame width in pixels.</param>
/// <param name="Height">The frame height in pixels.</param>
/// <param name="PositionSeconds">The source position in seconds.</param>
public sealed record VideoPreviewFrame(byte[] Pixels, int Width, int Height, double PositionSeconds);
