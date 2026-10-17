using System.Runtime.InteropServices;
using System.Text.Json;
using FancyToolAva.Models;
using FancyToolAva.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static unsafe int RunCudaVideoTests(string workDir)
    {
        using var http = new HttpClient();
        var downloads = new ModelDownloadService(http, NullLogger<ModelDownloadService>.Instance);
        using var sr = new VideoSuperResolutionService(downloads, NullLogger<VideoSuperResolutionService>.Instance,
            new AppPreferences { SuperResolutionTileSizeIndex = 0 });
        using var cuda = new CudaVideoContext(0);
        var records = new List<object>();
        int failed = 0;
        void Record(string name, SKBitmap before, SKBitmap after)
        {
            var diff = PixelDifference(before, after);
            bool passed = diff.max == 0;
            if (!passed) failed++;
            var row = new { name, passed, diff.max, diff.mean, diff.different };
            Console.WriteLine(JsonSerializer.Serialize(row)); records.Add(row);
        }
        foreach (var model in VideoSrModelCatalog.Models.Where(m => !m.RequiresDirectMl))
        foreach (bool half in model.SupportsFp16 ? new[] { false, true } : new[] { false })
        foreach (var size in new[] { (31, 17), (386, 258) })
        {
            var frames = CreateCandidateFrames(size.Item1, size.Item2, 1);
            try
            {
                using var lease = sr.CreateSessionAsync(new VideoTranscodeOptions
                { HardwareBackend = HardwareBackend.Nvidia, SuperResolutionEnabled = true, SuperResolutionModel = model.Model, SuperResolutionUseFp16 = half }).GetAwaiter().GetResult();
                var gpu = (IVideoCudaUpscaler)lease;
                using var input = cuda.Allocate(size.Item1, size.Item2);
                Upload(cuda, input.Frame, frames[0]);
                foreach (int scale in new[] { 2, 4 })
                {
                    using var output = cuda.Allocate(size.Item1 * scale, size.Item2 * scale);
                    using var expected = lease.Upscale(frames[0], scale, default);
                    gpu.UpscaleCuda(input.Frame, output.Frame, scale, default);
                    using var actual = Download(cuda, output.Frame);
                    Record($"{model.Model}/{half}/{size}/{scale}", expected, actual);
                }
            }
            finally { foreach (var frame in frames) frame.Dispose(); }
        }
        foreach (var model in Enum.GetValues<VideoInterpolationModel>())
        foreach (var size in new[] { (17, 9), (170, 98), (640, 360) })
        {
            var frames = CreateCandidateFrames(size.Item1, size.Item2, 2);
            try
            {
                using var runner = new OrtBgraVideoFrameInterpolator(Path.Combine(AppPaths.ModelsDirectory, "Interpolation", VideoInterpolationService.GetModel(model).FileName),
                    size.Item1, size.Item2, default, null, false);
                var cudaRunner = (IVideoCudaInterpolator)(object)runner;
                using var first = cuda.Allocate(size.Item1, size.Item2);
                using var second = cuda.Allocate(size.Item1, size.Item2);
                using var output = cuda.Allocate(size.Item1, size.Item2);
                using var before = new SKBitmap(size.Item1, size.Item2, SKColorType.Bgra8888, SKAlphaType.Opaque);
                for (int repeat = 0; repeat < 3; repeat++)
                {
                    frames[1].GetPixelSpan()[Math.Min(20, frames[1].ByteCount - 1)] ^= 63;
                    Upload(cuda, first.Frame, frames[0]); Upload(cuda, second.Frame, frames[1]);
                    foreach (double amount in new[] { 1d / 6, .5, 5d / 6 })
                    {
                        runner.SetFrames(frames[0], frames[1], default);
                        runner.Interpolate(amount, before, default);
                        cudaRunner.SetCudaFrames(first.Frame, second.Frame, default);
                        cudaRunner.InterpolateCuda(amount, output.Frame, default);
                        using var after = Download(cuda, output.Frame);
                        Record($"{model}/{size}/{repeat}/{amount}", before, after);
                    }
                }
                if (!cuda.HoldPair(first.Frame, first.Frame, true) || cuda.HoldPair(first.Frame, second.Frame, false)) failed++;
            }
            finally { foreach (var frame in frames) frame.Dispose(); }
        }
        TestCudaCaptureIsolation(sr, cuda);
        using (var black = new SKBitmap(64, 64, SKColorType.Bgra8888, SKAlphaType.Opaque))
        using (var white = new SKBitmap(64, 64, SKColorType.Bgra8888, SKAlphaType.Opaque))
        using (var first = cuda.Allocate(64, 64))
        using (var second = cuda.Allocate(64, 64))
        {
            black.Erase(SKColors.Black);
            white.Erase(SKColors.White);
            Upload(cuda, first.Frame, black);
            Upload(cuda, second.Frame, white);
            if (!cuda.HoldPair(first.Frame, second.Frame, true) || cuda.HoldPair(first.Frame, second.Frame, false))
                throw new InvalidOperationException("CUDA scene detection changed");
        }
        File.WriteAllText(Path.Combine(workDir, "cuda-adapters.json"), JsonSerializer.Serialize(new { failed, records }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }

    private static void TestCudaCaptureIsolation(VideoSuperResolutionService service, CudaVideoContext cuda)
    {
        var frames = CreateCandidateFrames(386, 258, 2);
        try
        {
            using var sr = service.CreateSessionAsync(new VideoTranscodeOptions
            { HardwareBackend = HardwareBackend.Nvidia, SuperResolutionEnabled = true, SuperResolutionModel = VideoSrModel.FsrcnnxM, SuperResolutionUseFp16 = true }).GetAwaiter().GetResult();
            using var rife = new OrtBgraVideoFrameInterpolator(Path.Combine(AppPaths.ModelsDirectory, "Interpolation", VideoInterpolationService.GetModel(VideoInterpolationModel.Rife425Lite).FileName),
                386, 258, default, null, true);
            using var source = cuda.Allocate(386, 258);
            using var result = cuda.Allocate(772, 516);
            using var expectedSr = sr.Upscale(frames[0], 2, default);
            using var expectedRife = new SKBitmap(386, 258, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var actualRife = new SKBitmap(386, 258, SKColorType.Bgra8888, SKAlphaType.Opaque);
            Upload(cuda, source.Frame, frames[0]);
            rife.SetFrames(frames[0], frames[1], default);
            rife.Interpolate(.5, expectedRife, default);
            Parallel.Invoke(() =>
            {
                for (int i = 0; i < 20; i++)
                {
                    ((IVideoCudaUpscaler)sr).UpscaleCuda(source.Frame, result.Frame, 2, default);
                    using var actual = Download(cuda, result.Frame);
                    if (PixelDifference(expectedSr, actual).max != 0) throw new InvalidOperationException("CUDA/Graph SR isolation failed");
                }
            }, () =>
            {
                for (int i = 0; i < 20; i++)
                {
                    rife.Interpolate(.5, actualRife, default);
                    if (PixelDifference(expectedRife, actualRife).max != 0) throw new InvalidOperationException("CUDA/Graph RIFE isolation failed");
                }
            });
            Console.WriteLine("PASS CUDA pixels and captured RIFE: 2 x 20 concurrent calls, exact pixels");
        }
        finally { foreach (var frame in frames) frame.Dispose(); }
    }

    private static unsafe void Upload(CudaVideoContext cuda, CudaVideoFrame destination, SKBitmap source)
    {
        using var scope = cuda.Enter();
        if (source.RowBytes != source.Width * 4 || destination.Pitch != source.RowBytes) throw new InvalidOperationException();
        CudaVideoContext.Check(CudaUpload((ulong)destination.Data, (void*)source.GetPixels(), (nuint)source.ByteCount));
    }
    private static unsafe SKBitmap Download(CudaVideoContext cuda, CudaVideoFrame source)
    {
        using var scope = cuda.Enter();
        var bitmap = new SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        try { CudaVideoContext.Check(CudaDownload((void*)bitmap.GetPixels(), (ulong)source.Data, (nuint)bitmap.ByteCount)); return bitmap; }
        catch { bitmap.Dispose(); throw; }
    }
    [DllImport("nvcuda.dll", EntryPoint = "cuMemcpyHtoD_v2")] private static extern unsafe int CudaUpload(ulong dst, void* src, nuint bytes);
    [DllImport("nvcuda.dll", EntryPoint = "cuMemcpyDtoH_v2")] private static extern unsafe int CudaDownload(void* dst, ulong src, nuint bytes);
}
