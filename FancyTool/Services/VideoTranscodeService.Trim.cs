using FancyToolAva.Models;
using FFmpeg.AutoGen;

namespace FancyToolAva.Services;

public sealed unsafe partial class VideoTranscodeService
{
    private static void ValidateTrim(VideoTranscodeOptions options, long durationMs)
    {
        double start = options.TrimStartSeconds;
        double end = options.TrimEndSeconds ?? (durationMs > 0 ? durationMs / 1000.0 : double.PositiveInfinity);
        if (!double.IsFinite(start) || start < 0 || (options.TrimEndSeconds.HasValue && !double.IsFinite(end))
            || end <= start || (durationMs > 0 && start >= durationMs / 1000.0))
            throw new ArgumentException(LocalizationRegistry.Get("VideoTranscode.Msg_InvalidTrim"), nameof(options));
    }

    /// <summary>Clips decoded frames on one shared source timeline before resetting output timestamps.</summary>
    private sealed class TrimTimeline
    {
        public readonly double Origin;
        public readonly double Start;
        public readonly double End;
        public bool VideoEnded;
        public bool AudioEnded;
        public long NextAudioPts;
        private bool _audioStarted;

        public TrimTimeline(AVFormatContext* input, AVStream* video, VideoTranscodeOptions options, long durationMs, bool hasAudio)
        {
            Origin = GetTimelineOrigin(input, video);
            Start = options.TrimStartSeconds;
            double duration = durationMs > 0 ? durationMs / 1000.0 : double.PositiveInfinity;
            End = Math.Min(options.TrimEndSeconds ?? duration, duration);
            AudioEnded = !hasAudio;
        }

        public bool KeepVideo(AVFrame* frame, AVRational timeBase, out double position)
        {
            position = frame->pts * ffmpeg.av_q2d(timeBase) - Origin;
            if (position + 0.000001 < Start) return false;
            if (position >= End - 0.000001) { VideoEnded = true; return false; }
            frame->pts -= (long)Math.Round((Origin + Start) / ffmpeg.av_q2d(timeBase));
            return true;
        }

        public bool KeepAudio(AVFrame* frame, AVRational timeBase, EncoderFrameFeeder feeder)
        {
            long pts = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? frame->best_effort_timestamp : NextAudioPts;
            double position = pts * ffmpeg.av_q2d(timeBase) - Origin;
            int samples = frame->nb_samples;
            int rate = frame->sample_rate;
            NextAudioPts = pts + ffmpeg.av_rescale_q(samples, new AVRational { num = 1, den = rate }, timeBase);
            if (position >= End - 0.000001) { AudioEnded = true; return false; }
            int first = (int)Math.Clamp(Math.Ceiling((Start - position) * rate - 0.000001), 0, samples);
            int last = double.IsFinite(End)
                ? (int)Math.Clamp(Math.Ceiling((End - position) * rate - 0.000001), 0, samples) : samples;
            if (last <= first) return false;
            if (!_audioStarted)
            {
                feeder.SetStartSeconds(Math.Max(0, position + first / (double)rate - Start));
                _audioStarted = true;
            }
            if (first > 0)
            {
                bool planar = ffmpeg.av_sample_fmt_is_planar((AVSampleFormat)frame->format) != 0;
                int channels = frame->ch_layout.nb_channels;
                int bytes = ffmpeg.av_get_bytes_per_sample((AVSampleFormat)frame->format);
                int offset = first * bytes * (planar ? 1 : channels);
                for (uint i = 0; i < (planar ? channels : 1); i++)
                {
                    frame->extended_data[i] += offset;
                    if (i < 8) frame->data[i] = frame->extended_data[i];
                }
            }
            frame->nb_samples = last - first;
            return true;
        }
    }
}
