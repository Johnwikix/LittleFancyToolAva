using Avalonia.Threading;
using FancyToolAva.Models;

namespace FancyToolAva.ViewModels;

public partial class VideoTranscodeViewModel
{
    /// <summary>Posts statistics directly in producer order, including the final actual frame count.</summary>
    private sealed class EncodingStatisticsReporter(VideoFileItem item) : IProgress<VideoEncodingProgress>
    {
        public void Report(VideoEncodingProgress value) => Dispatcher.UIThread.Post(() => item.UpdateEncodingStatistics(value));
    }
}
