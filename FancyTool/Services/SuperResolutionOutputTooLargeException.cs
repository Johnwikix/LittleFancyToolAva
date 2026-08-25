namespace FancyToolAva.Services;

public sealed class SuperResolutionOutputTooLargeException : Exception
{
    public int OutputWidth { get; }
    public int OutputHeight { get; }
    public int MaxDimension { get; }

    public SuperResolutionOutputTooLargeException(int outputWidth, int outputHeight, int maxDimension)
        : base($"Super-resolution output {outputWidth}x{outputHeight} exceeds the {maxDimension}px limit.")
    {
        OutputWidth = outputWidth;
        OutputHeight = outputHeight;
        MaxDimension = maxDimension;
    }
}