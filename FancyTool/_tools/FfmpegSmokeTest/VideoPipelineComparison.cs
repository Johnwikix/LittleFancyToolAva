using System.Text.Json;
using System.Runtime.InteropServices;
using FancyToolAva.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static int CompareVideoPipeline(string workDir, string[] args)
    {
        string Argument(string key) => args[Array.IndexOf(args, key) + 1];
        string Option(string key, string fallback) => args.Contains(key) ? Argument(key) : fallback;
        var records = new List<object>();
        bool all = true;
        foreach (string mode in Option("--modes", "sr,rife,both").Split(','))
        {
            var before = new List<string>(); var after = new List<string>();
            string beforePath = Path.Combine(Argument("--before"), mode + "-" + Option("--before-iteration", "3"), "output.mp4");
            string afterPath = Path.Combine(Argument("--after"), mode + "-" + Option("--after-iteration", "3"), "output.mp4");
            var beforeTimes = ReadVideoTimes(beforePath, before);
            var afterTimes = ReadVideoTimes(afterPath, after);
            bool equal = before.SequenceEqual(after) && beforeTimes.SequenceEqual(afterTimes);
            all &= equal;
            records.Add(new { mode, equal, beforeTimes, afterTimes, before, after });
            Console.WriteLine($"{mode}: decoded frames={before.Count}/{after.Count}, all pixels and timestamps identical={equal}");
            if (!equal)
            {
                var service = new VideoTranscodeService(NullLogger<VideoTranscodeService>.Instance);
                var differences = new List<object>();
                foreach (double time in new[] { 0, .25, .5, 1, 2, 2.75 })
                {
                    var a = service.GetPreviewAsync(beforePath, time).GetAwaiter().GetResult()!;
                    var b = service.GetPreviewAsync(afterPath, time).GetAwaiter().GetResult()!;
                    long sum = 0; int max = 0;
                    for (int i = 0; i < a.Pixels.Length; i++) { int delta = Math.Abs(a.Pixels[i] - b.Pixels[i]); sum += delta; max = Math.Max(max, delta); }
                    differences.Add(new { time, mean = sum / (double)a.Pixels.Length, max });
                    Save(a, $"{mode}-{time}-before.png"); Save(b, $"{mode}-{time}-after.png");
                }
                Console.WriteLine(JsonSerializer.Serialize(differences));
                File.WriteAllText(Path.Combine(workDir, mode + "-differences.json"), JsonSerializer.Serialize(differences));
                void Save(FancyToolAva.Models.VideoPreviewFrame frame, string name)
                {
                    using var bitmap = new SKBitmap(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                    Marshal.Copy(frame.Pixels, 0, bitmap.GetPixels(), frame.Pixels.Length);
                    using var image = SKImage.FromBitmap(bitmap);
                    using var png = image.Encode(SKEncodedImageFormat.Png, 100);
                    File.WriteAllBytes(Path.Combine(workDir, name), png.ToArray());
                }
            }
        }
        File.WriteAllText(Path.Combine(workDir, "decoded-comparison.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        return all ? 0 : 1;
    }
}
