using FancyToolAva.AudioEnhancement;
using FFmpeg.AutoGen;

namespace FancyToolAva.Services;

public sealed unsafe partial class AudioConvertService
{
    /// <summary>Bridges normalized PCM to any registered block backend, then converts its native output for the encoder.</summary>
    private sealed class EnhancementFeeder : IDisposable
    {
        private readonly IAudioUpsamplingProcessor _processor;
        private readonly double[] _input, _output;
        private readonly int _channels;
        private readonly FrameFeeder _feeder;
        private readonly AVCodecContext* _encoder;
        private readonly SwrContext* _gainSwr;
        private readonly AVFrame* _gainFrame;
        private readonly bool _applyGain;
        private readonly double _gain;
        private readonly AVSampleFormat _postFormat;
        private readonly CancellationToken _cancellationToken;
        private AVFrame* _enhanced, _converted;
        private SwrContext* _postSwr;
        private int _filled, _skip;
        private long _inputFrames, _emitted;
        private bool _finished, _disposed;

        /// <summary>Takes ownership of one processor and its file-local signal state.</summary>
        public EnhancementFeeder(IAudioUpsamplingProcessor processor, AVCodecContext* encoder, FrameFeeder feeder,
            SwrContext* gainSwr, AVFrame* gainFrame, bool applyGain, double gain, CancellationToken cancellationToken)
        {
            _processor = processor;
            _encoder = encoder;
            _feeder = feeder;
            _gainSwr = gainSwr;
            _gainFrame = gainFrame;
            _applyGain = applyGain;
            _gain = gain;
            _cancellationToken = cancellationToken;
            _channels = processor.Channels;
            _postFormat = gainSwr != null ? AVSampleFormat.AV_SAMPLE_FMT_FLT : encoder->sample_fmt;
            try
            {
                if (processor.InputFramesPerBlock <= 0 || processor.OutputFramesPerBlock <= 0
                    || processor.InputSampleRate <= 0 || processor.OutputSampleRate <= 0
                    || _channels != encoder->ch_layout.nb_channels || processor.LatencyFrames < 0
                    || (long)processor.InputFramesPerBlock * processor.OutputSampleRate
                        != (long)processor.OutputFramesPerBlock * processor.InputSampleRate)
                    throw new InvalidOperationException("Invalid upsampling processor contract.");
                _input = new double[checked(processor.InputFramesPerBlock * _channels)];
                _output = new double[checked(processor.OutputFramesPerBlock * _channels)];
                _skip = processor.LatencyFrames;
                _enhanced = ffmpeg.av_frame_alloc();
                _converted = ffmpeg.av_frame_alloc();
                if (_enhanced == null || _converted == null) throw new OutOfMemoryException();
                PrepareFrame(_enhanced, AVSampleFormat.AV_SAMPLE_FMT_DBL, processor.OutputSampleRate, &encoder->ch_layout);
                _enhanced->nb_samples = processor.OutputFramesPerBlock;
                int result = ffmpeg.av_frame_get_buffer(_enhanced, 0);
                if (result < 0) throw FFmpegNative.CreateException(result, "升频缓冲分配失败");
                SwrContext* post = null;
                result = ffmpeg.swr_alloc_set_opts2(&post, &encoder->ch_layout, _postFormat, encoder->sample_rate,
                    &encoder->ch_layout, AVSampleFormat.AV_SAMPLE_FMT_DBL, processor.OutputSampleRate, 0, null);
                _postSwr = post;
                if (result < 0 || post == null || ffmpeg.swr_init(post) < 0)
                    throw new InvalidOperationException("升频输出转换器初始化失败");
            }
            catch { Dispose(); throw; }
        }

        public void Push(AVFrame* frame)
        {
            if (_finished) throw new InvalidOperationException("The upsampling stream has ended.");
            _inputFrames += frame->nb_samples;
            var source = new ReadOnlySpan<double>(frame->data[0], checked(frame->nb_samples * _channels));
            int offset = 0;
            while (offset < frame->nb_samples)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                int take = Math.Min(frame->nb_samples - offset, _processor.InputFramesPerBlock - _filled);
                source.Slice(offset * _channels, take * _channels).CopyTo(_input.AsSpan(_filled * _channels));
                offset += take;
                _filled += take;
                if (_filled == _processor.InputFramesPerBlock) ProcessBlock();
            }
        }

        private long ValidOutputFrames => checked((_inputFrames * _processor.OutputSampleRate
            + _processor.InputSampleRate / 2) / _processor.InputSampleRate);

        public void Finish()
        {
            if (_finished) return;
            _finished = true;
            if (_filled > 0)
            {
                _input.AsSpan(_filled * _channels).Clear();
                ProcessBlock();
            }
            while (_emitted < ValidOutputFrames)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                Array.Clear(_input);
                ProcessBlock();
            }
            while (ConvertOutput(null)) { }
            if (_gainSwr != null)
            {
                while (true)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    PrepareFrame(_gainFrame, _encoder->sample_fmt, _encoder->sample_rate, &_encoder->ch_layout);
                    int result = ffmpeg.swr_convert_frame(_gainSwr, _gainFrame, null);
                    if (result < 0) throw FFmpegNative.CreateException(result, "增益级冲洗失败");
                    if (_gainFrame->nb_samples == 0) break;
                    _feeder.Push(_gainFrame);
                }
            }
        }

        private void ProcessBlock()
        {
            _processor.ProcessBlock(_input, _output, _cancellationToken);
            _filled = 0;
            int offset = Math.Min(_skip, _processor.OutputFramesPerBlock);
            _skip -= offset;
            int frames = (int)Math.Min(_processor.OutputFramesPerBlock - offset, ValidOutputFrames - _emitted);
            if (frames == 0) return;
            _enhanced->nb_samples = _processor.OutputFramesPerBlock;
            int result = ffmpeg.av_frame_make_writable(_enhanced);
            if (result < 0) throw FFmpegNative.CreateException(result, "升频缓冲不可写");
            _output.AsSpan(offset * _channels, frames * _channels)
                .CopyTo(new Span<double>(_enhanced->data[0], frames * _channels));
            _enhanced->nb_samples = frames;
            _emitted += frames;
            ConvertOutput(_enhanced);
        }

        private bool ConvertOutput(AVFrame* input)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            PrepareFrame(_converted, _postFormat, _encoder->sample_rate, &_encoder->ch_layout);
            int result = ffmpeg.swr_convert_frame(_postSwr, _converted, input);
            if (result < 0) throw FFmpegNative.CreateException(result, "升频输出转换失败");
            if (_converted->nb_samples == 0) return false;
            if (_applyGain) ApplyGain(_converted, _gain);
            if (_gainSwr != null)
            {
                PrepareFrame(_gainFrame, _encoder->sample_fmt, _encoder->sample_rate, &_encoder->ch_layout);
                result = ffmpeg.swr_convert_frame(_gainSwr, _gainFrame, _converted);
                if (result < 0) throw FFmpegNative.CreateException(result, "升频增益转换失败");
                if (_gainFrame->nb_samples > 0) _feeder.Push(_gainFrame);
            }
            else _feeder.Push(_converted);
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_postSwr != null) { SwrContext* p = _postSwr; _postSwr = null; ffmpeg.swr_free(&p); }
            if (_enhanced != null) { AVFrame* p = _enhanced; _enhanced = null; ffmpeg.av_frame_free(&p); }
            if (_converted != null) { AVFrame* p = _converted; _converted = null; ffmpeg.av_frame_free(&p); }
            _processor.Dispose();
        }
    }
}
