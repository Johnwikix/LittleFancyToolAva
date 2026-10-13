using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FancyToolAva;
using FancyToolAva.Models;
using FancyToolAva.Services;
using FancyToolAva.ViewModels;
using FancyToolAva.Views;
using FluentAvalonia.UI.Controls;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static int RunEditingUi(VideoTranscodeService service, string source, string workDir, bool srOptions = false)
    {
        try
        {
            AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().SetupWithoutStarting();
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            var vm = ((App)Application.Current!).TryGetService<VideoTranscodeViewModel>()!;
            if (srOptions)
            {
                vm.IsVideoSrEnabled = true;
                vm.VideoSrModelIndex = 1;
                if (vm.VideoSrScaleLabels.Length != 1 || vm.VideoSrScaleIndex != 0) throw new InvalidOperationException("Native 2x video scale choice incorrect");
                vm.VideoSrModelIndex = 0;
                vm.VideoSrBackendIndex = (int)VideoSrBackend.PreferNpu;
                vm.VideoSrAllowCpuFallback = false;
            }
            var view = new VideoTranscodeView { DataContext = vm, Background = Brushes.WhiteSmoke };
            var window = new Window { Width = 1100, Height = 760, Content = view, Title = "Video editing smoke test" };
            window.Show();
            var info = service.ProbeAsync(source).GetAwaiter().GetResult()!;
            var first = new VideoFileItem(source) { ProbeInfo = info };
            var second = new VideoFileItem(source) { ProbeInfo = info with { DurationMs = 1000 } };
            vm.FileItems.Add(first);
            vm.TrimStartSeconds = 0.4;
            vm.TrimEndSeconds = 1.4;
            PumpUntil(() => vm.HasPreview && !vm.IsPreviewLoading);
            AssertRange(vm, 0.4, 1.4);
            var slider = view.GetVisualDescendants().OfType<FARangeSlider>().Single();
            if (Math.Abs(slider.RangeStart - 0.4) > 0.001 || Math.Abs(slider.RangeEnd - 1.4) > 0.001)
                throw new InvalidOperationException("Range slider bindings did not update");
            Render(window, view, Path.Combine(workDir, srOptions ? "sr-wide.png" : "editing-wide.png"));

            vm.FileItems.Add(second);
            vm.SelectedFileItem = second;
            PumpUntil(() => vm.HasPreview && !vm.IsPreviewLoading);
            AssertRange(vm, 0, 1);
            vm.SelectedFileItem = first;
            PumpUntil(() => vm.HasPreview && !vm.IsPreviewLoading);
            AssertRange(vm, 0.4, 1.4);

            // Simulate both thumbs, including attempts to cross the range boundaries.
            slider.RangeStart = 0.6;
            slider.RangeEnd = 1.2;
            PumpUntil(() => vm.HasPreview && !vm.IsPreviewLoading);
            AssertRange(vm, 0.6, 1.2);
            vm.TrimStartSeconds = 9;
            if (vm.TrimEndSeconds - vm.TrimStartSeconds < vm.MinimumTrimRange - 0.000001)
                throw new InvalidOperationException("Crossed trim boundaries");
            vm.ResetTrimCommand.Execute(null);
            AssertRange(vm, 0, 2);
            vm.TrimStartSeconds = 0.4;
            vm.TrimEndSeconds = 1.4;
            window.Width = 650;
            PumpUntil(() => view.FindControl<Grid>("BodyGrid")!.ColumnDefinitions.Count == 1);
            PumpUntil(() => vm.HasPreview && !vm.IsPreviewLoading);
            Render(window, view, Path.Combine(workDir, srOptions ? "sr-narrow.png" : "editing-narrow.png"));
            vm.IsBusy = true;
            double start = vm.TrimStartSeconds;
            vm.TrimStartSeconds = 0.7;
            if (slider.IsEnabled || vm.TrimStartSeconds != start) throw new InvalidOperationException("Trim was editable during conversion");
            vm.IsBusy = false;

            // Exercise the real batch command with independent ranges and shared encoding settings.
            vm.SelectedFileItem = second;
            vm.TrimStartSeconds = 0.2;
            vm.TrimEndSeconds = 0.8;
            vm.SelectedFileItem = first;
            vm.OutputFolder = Path.Combine(workDir, "ui-transcodes-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(vm.OutputFolder);
            var batch = vm.StartTranscodeCommand.ExecuteAsync(null);
            PumpUntil(() => batch.IsCompleted);
            batch.GetAwaiter().GetResult();
            if (vm.FileItems.Any(item => item.Status != FileStatus.Completed)) throw new InvalidOperationException("UI batch conversion failed");
            var durations = Directory.GetFiles(vm.OutputFolder, "*.mp4")
                .Select(path => service.ProbeAsync(path).GetAwaiter().GetResult()!.DurationMs).Order().ToArray();
            if (durations.Length != 2 || Math.Abs(durations[0] - 600) > 120 || Math.Abs(durations[1] - 1000) > 120)
                throw new InvalidOperationException("Batch command did not preserve per-file trim ranges");
            if (srOptions && !vm.VideoSrDeviceText.StartsWith("NPU")) throw new InvalidOperationException("Video UI failed to report actual NPU use");
            vm.SelectedFileItem = first;
            vm.FileItems.Remove(first);
            if (!ReferenceEquals(vm.SelectedFileItem, second)) throw new InvalidOperationException("Removing selected file did not move selection");
            vm.FileItems.Clear();
            if (vm.SelectedFileItem != null || vm.HasPreview || vm.CanEditTrim) throw new InvalidOperationException("Clearing files retained the editor");
            vm.Dispose();
            if (srOptions)
            {
                var imageVm = ((App)Application.Current!).TryGetService<ImageConvertViewModel>()!;
                imageVm.IsSuperResolutionEnabled = true;
                imageVm.SelectedSrModelIndex = (int)SuperResolutionModel.LiveActionSpanX2;
                if (imageVm.AvailableSrScaleLabels.Count != 1 || imageVm.SelectedSrScaleIndex != 0) throw new InvalidOperationException("Native 2x image scale choice incorrect");
                imageVm.SelectedSrModelIndex = (int)SuperResolutionModel.SpanClearRealityV1;
                imageVm.PreferSrNpu = true;
                var imageView = new ImageConvertView { DataContext = imageVm, Background = Brushes.WhiteSmoke };
                window.Width = 1100;
                window.Content = imageView;
                PumpUntil(() => imageView.Bounds.Width > 1000 && !imageVm.IsSuperResolutionDownloading);
                window.UpdateLayout();
                using var image = new RenderTargetBitmap(new PixelSize((int)imageView.Bounds.Width, (int)imageView.Bounds.Height), new Vector(96, 96));
                image.Render(imageView);
                image.Save(Path.Combine(workDir, "image-npu-ui.png"));
            }
            window.Close();
            Console.WriteLine("Editing UI: range bindings, per-file batch transcode, boundary constraints, reset, busy state, removal, wide/narrow renders OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Editing UI FAIL: {ex}");
            return 1;
        }
    }

    private static void AssertRange(VideoTranscodeViewModel vm, double start, double end)
    {
        if (Math.Abs(vm.TrimStartSeconds - start) > 0.001 || Math.Abs(vm.TrimEndSeconds - end) > 0.001)
            throw new InvalidOperationException($"Range: expected {start}-{end}, got {vm.TrimStartSeconds}-{vm.TrimEndSeconds}");
    }

    private static void PumpUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var finished = new CancellationTokenSource();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) => { if (condition() || timeout.IsCancellationRequested) finished.Cancel(); };
        timer.Start();
        Dispatcher.UIThread.MainLoop(finished.Token);
        timer.Stop();
        if (!condition()) throw new TimeoutException("UI condition did not settle");
    }

    private static void Render(Window window, VideoTranscodeView view, string path)
    {
        window.UpdateLayout();
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)view.Bounds.Width, (int)view.Bounds.Height), new Vector(96, 96));
        bitmap.Render(view);
        bitmap.Save(path);
    }
}
