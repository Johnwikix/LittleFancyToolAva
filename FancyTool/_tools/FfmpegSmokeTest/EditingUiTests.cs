using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FancyToolAva;
using FancyToolAva.Models;
using FancyToolAva.Models.ViewStates;
using FancyToolAva.Services;
using FancyToolAva.ViewModels;
using FancyToolAva.Views;
using FluentAvalonia.UI.Controls;
using Lang.Avalonia;
using System.Globalization;

namespace FfmpegSmokeTest;

internal static partial class Program
{
    private static int RunEditingUi(VideoTranscodeService service, string source, string workDir, bool srOptions = false, bool interpolationOptions = false)
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
                if (vm.VideoSrScaleLabels.Length != 2 || vm.VideoSrScaleIndex != 0) throw new InvalidOperationException("Native 2x video scale choice incorrect");
                vm.VideoSrModelIndex = 0;
                vm.VideoSrBackendIndex = 0;
            }
            var view = new VideoTranscodeView { DataContext = vm, Background = Brushes.WhiteSmoke };
            var window = new Window { Width = 1100, Height = 760, Content = view, Title = "Video editing smoke test" };
            window.Show();
            var tabs = view.FindControl<TabControl>("ParamTabs")!;
            tabs.SelectedIndex = 2;
            vm.IsVideoSrEnabled = true;
            PumpUntil(() => view.GetVisualDescendants().OfType<Control>().Any(c => AutomationProperties.GetAutomationId(c) == "VideoSuperResolutionOptions"));
            var srOptionsPanel = view.GetVisualDescendants().OfType<Control>().Single(c => AutomationProperties.GetAutomationId(c) == "VideoSuperResolutionOptions");
            var hdrSelector = view.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetAutomationId(c) == "VideoHdrOutput");
            var srModelSelector = view.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetAutomationId(c) == "VideoSrModel");
            var srScaleSelector = view.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetAutomationId(c) == "VideoSrScale");
            var srBackendSelector = view.GetVisualDescendants().OfType<TextBlock>().Single(c => AutomationProperties.GetAutomationId(c) == "VideoSrBackend");
            vm.VideoSrModelIndex = 1;
            if (srScaleSelector.SelectedIndex != 0 || srScaleSelector.ItemCount != 2) throw new InvalidOperationException("Native 2x scale selection cleared");
            vm.VideoSrModelIndex = 0;
            if (srScaleSelector.SelectedIndex != 0) throw new InvalidOperationException("Model change cleared scale selection");
            var fp16 = view.GetVisualDescendants().OfType<CheckBox>().Single(c => AutomationProperties.GetAutomationId(c) == "VideoSrFp16");
            if (srModelSelector.ItemCount != 6 || vm.VideoSrBackendLabels.Count != 1) throw new InvalidOperationException("Unexpected GPU model/backend choices");
            foreach (int index in new[] { 4, 5 })
            {
                vm.VideoSrModelIndex = index;
                vm.VideoSrUseFp16 = true;
                if (vm.VideoSrUseFp16 || vm.SupportsVideoSrFp16 || fp16.IsEnabled || fp16.IsChecked == true)
                    throw new InvalidOperationException("RAVU FP16 was not blocked");
            }
            vm.VideoSrModelIndex = 3;
            vm.VideoSrBackendIndex = 1;
            vm.VideoSrUseFp16 = true;
            if (vm.VideoSrBackendIndex != 0) throw new InvalidOperationException("An unqualified backend was exposed");
            vm.VideoSrBackendIndex = 0;
            vm.VideoSrUseFp16 = true;
            vm.VideoSrScaleIndex = 1;
            var srSaved = ((IViewState)vm).CaptureState();
            ((IViewState)vm).RestoreState(new VideoTranscodeViewState { VideoSrModelIndex = 5, VideoSrBackendIndex = 3 });
            if (vm.VideoSrModelIndex != 0 || vm.VideoSrBackendIndex != 0) throw new InvalidOperationException("Legacy model/backend selection was reinterpreted");
            ((IViewState)vm).RestoreState(srSaved);
            if (vm.VideoSrModelIndex != 3 || vm.VideoSrBackendIndex != 0 || vm.VideoSrScaleIndex != 1 || !vm.VideoSrUseFp16 || fp16.IsChecked != true)
                throw new InvalidOperationException("GPU model/precision/scale selection did not persist");
            vm.HdrOutputIndex = 1;
            var saved = ((IViewState)vm).CaptureState();
            vm.HdrOutputIndex = 0;
            ((IViewState)vm).RestoreState(saved);
            if (vm.HdrOutputIndex != 1 || hdrSelector.SelectedIndex != 1) throw new InvalidOperationException("HDR selection was not persisted");
            foreach (var culture in new[] { "en-US", "zh-CN" })
            {
                I18nManager.Instance.Culture = new CultureInfo(culture);
                string expected = culture == "en-US" ? "Force HDR" : "强制 HDR";
                PumpUntil(() => vm.HdrOutputOptions[1].Label == expected);
                if (vm.HdrOutputIndex != 1 || hdrSelector.SelectedIndex != 1)
                    throw new InvalidOperationException("Changing language cleared HDR selection");
                if (srModelSelector.SelectedIndex != vm.VideoSrModelIndex || vm.VideoSrBackendIndex != 0 || srBackendSelector.Text != LocalizationRegistry.Get("VideoTranscode.Sr_AutoGpu"))
                    throw new InvalidOperationException("Changing language cleared super-resolution selection");
            }
            Render(window, view, Path.Combine(workDir, "quality-options.png"));
            srOptionsPanel.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            Render(window, view, Path.Combine(workDir, "quality-super-resolution.png"));
            tabs.SelectedIndex = 0;
            PumpUntil(() => TopLevel.GetTopLevel(srOptionsPanel) == null || !srOptionsPanel.IsEffectivelyVisible);
            if (!srOptions) vm.IsVideoSrEnabled = false;
            vm.HdrOutputIndex = 0;
            if (interpolationOptions)
            {
                tabs.SelectedIndex = 3;
                vm.InterpolationModeIndex = 1;
                vm.InterpolationModelIndex = 1;
                vm.InterpolationRateIndex = 2;
                vm.InterpolationBackendIndex = (int)VideoSrBackend.Gpu;
                vm.IsVideoSrEnabled = true;
                vm.InterpolationOrderIndex = 2;
                vm.FpsValue = 60000d / 1001;
                PumpUntil(() => view.GetVisualDescendants().OfType<ComboBox>().Any(c => AutomationProperties.GetAutomationId(c) == "VideoInterpolationMode"));
                var mode = view.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetAutomationId(c) == "VideoInterpolationMode");
                var model = view.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetAutomationId(c) == "VideoInterpolationModel");
                var rate = view.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetAutomationId(c) == "VideoInterpolationRate");
                var order = view.GetVisualDescendants().OfType<ComboBox>().Single(c => AutomationProperties.GetAutomationId(c) == "VideoInterpolationOrder");
                PumpUntil(() => !vm.IsCheckingInterpolationHardware);
                foreach (var capability in HardwareVideoInterpolation.GetCapabilitiesAsync().GetAwaiter().GetResult())
                {
                    if (vm.InterpolationModes[(int)capability.Mode].IsEnabled != capability.IsSupported)
                        throw new InvalidOperationException("Hardware interpolation availability did not reach the UI");
                    if (!capability.IsSupported) continue;
                    vm.InterpolationModeIndex = (int)capability.Mode;
                    PumpUntil(() => mode.SelectedIndex == (int)capability.Mode);
                    foreach (int multiplier in capability.Multipliers)
                    {
                        vm.InterpolationRateIndex = multiplier / 2 - 1;
                        if (!vm.IsHardwareInterpolation || rate.SelectedIndex != vm.InterpolationRateIndex
                            || !vm.InterpolationRates[vm.InterpolationRateIndex].IsEnabled)
                            throw new InvalidOperationException("Probed hardware mode or multiplier could not be selected in the UI");
                    }
                    mode.BringIntoView();
                    Render(window, view, Path.Combine(workDir, "interpolation-" + capability.Mode + ".png"));
                }
                vm.InterpolationModeIndex = 1;
                vm.InterpolationRateIndex = 2;
                var state = ((IViewState)vm).CaptureState();
                vm.InterpolationModeIndex = 0; vm.InterpolationModelIndex = 0; vm.InterpolationRateIndex = 0;
                ((IViewState)vm).RestoreState(state);
                foreach (string culture in new[] { "en-US", "zh-CN" })
                {
                    I18nManager.Instance.Culture = new CultureInfo(culture);
                    PumpUntil(() => vm.InterpolationModes[0].Label == (culture == "en-US" ? "Off" : "关闭插帧"));
                    if (mode.SelectedIndex != 1 || model.SelectedIndex != 1 || rate.SelectedIndex != 2
                        || order.SelectedIndex != 2 || !order.IsVisible || vm.InterpolationRates.Count != 3
                        || vm.InterpolationRates[2].Label != (culture == "en-US" ? "Source frame rate × 6" : "源帧率 × 6")
                        || view.GetVisualDescendants().OfType<NumericUpDown>().Any(c => AutomationProperties.GetAutomationId(c) == "VideoInterpolationFps"))
                        throw new InvalidOperationException("Interpolation state, multiplier choices, or localization is incorrect");
                }
                mode.BringIntoView();
                PumpUntil(() => true);
                Render(window, view, Path.Combine(workDir, "interpolation-wide.png"));
                vm.InterpolationRateIndex = 0;
                if (!srOptions) vm.IsVideoSrEnabled = false;
            }
            var info = service.ProbeAsync(source).GetAwaiter().GetResult()!;
            var first = new VideoFileItem(source) { ProbeInfo = info };
            var second = new VideoFileItem(source) { ProbeInfo = info with { DurationMs = 1000 } };
            first.Status = FileStatus.Converting;
            first.UpdateEncodingStatistics(new VideoEncodingProgress(12, 40, 24.5, true, 1, 2));
            if (!first.ProgressDisplay.Contains("12") || !first.ProgressDisplay.Contains("40") || !first.ProgressDisplay.Contains("fps"))
                throw new InvalidOperationException("Conversion UI omitted the processed/total frame counters or FPS");
            first.UpdateEncodingStatistics(new VideoEncodingProgress(40, 40, 24.5, false, 2, 2));
            first.Status = FileStatus.Completed;
            if (!first.ProgressDisplay.Contains("40") || first.ProgressDisplay.Contains("fps"))
                throw new InvalidOperationException("Completed conversion did not retain its actual frame count");
            first.Status = FileStatus.Pending;
            first.UpdateEncodingStatistics(null);
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

            CheckLivePreviewUi(vm, first, second);

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
            if (srOptions && !vm.VideoSrDeviceText.StartsWith("GPU")) throw new InvalidOperationException("Video UI failed to report actual GPU use");
            if (interpolationOptions && !vm.InterpolationDeviceText.StartsWith("GPU")) throw new InvalidOperationException("Interpolation UI failed to report actual GPU use");
            if (interpolationOptions && Directory.GetFiles(vm.OutputFolder, "*.mp4").Any(p => Math.Abs(service.ProbeAsync(p).GetAwaiter().GetResult()!.Fps - 20) > .001))
                throw new InvalidOperationException("UI batch did not interpolate to the selected rate");
            vm.SelectedFileItem = first;
            vm.FileItems.Remove(first);
            if (!ReferenceEquals(vm.SelectedFileItem, second)) throw new InvalidOperationException("Removing selected file did not move selection");
            vm.FileItems.Clear();
            if (vm.SelectedFileItem != null || vm.HasPreview || vm.CanEditTrim) throw new InvalidOperationException("Clearing files retained the editor");
            vm.Dispose();
            if (srOptions)
            {
                var imageVm = ((App)Application.Current!).TryGetService<ImageConvertViewModel>()!;
                if (imageVm.AvailableSuperResolutionModels.Count != 6)
                    throw new InvalidOperationException("Temporal model leaked into image-only model choices");
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
