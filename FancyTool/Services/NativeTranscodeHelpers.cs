using FFmpeg.AutoGen;

namespace FancyToolAva.Services;

internal static unsafe class NativeTranscodeHelpers
{
    public static string AvErrorToString(int err)
    {
        return FfmpegNativeLoader.GetErrorString(err);
    }

    public static void Check(int ret, string context = "")
    {
        if (ret < 0) throw new InvalidOperationException($"{context}: {AvErrorToString(ret)} (code {ret})");
    }

    public static AVRational ToAVRational(double fps)
    {
        if (fps <= 0) return new AVRational { num = 0, den = 1 };
        int num = (int)Math.Round(fps * 1000);
        return new AVRational { num = num, den = 1000 };
    }

    public static double Q2D(AVRational q) => q.den == 0 ? 0 : (double)q.num / q.den;
}
