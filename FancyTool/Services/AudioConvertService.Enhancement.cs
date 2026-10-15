using FancyToolAva.AudioEnhancement;
using FFmpeg.AutoGen;

namespace FancyToolAva.Services;

public sealed unsafe partial class AudioConvertService
{
    /// <summary>Bridges variable decoded frames to the stateful spectral filter and back to the encoder.</summary>
    private sealed class EnhancementFeeder : IDisposable
    {
        private readonly SpectralEnhancer _processor;
        private readonly AllPassProcessor _allPass;
        private readonly int[] _input;
        private readonly int[] _middle;
        private readonly int[] _output = new int[1024];
        private readonly int _channels;
        private readonly int _ratio;
        private readonly int _rate;
        private readonly FrameFeeder _feeder;
        private readonly AVCodecContext* _encoder;
        private readonly SwrContext* _gainSwr;
        private readonly AVFrame* _gainFrame;
        private readonly bool _applyGain;
        private readonly double _gain;
        private readonly AVSampleFormat _postFormat;
        private AVFrame* _enhanced;
        private AVFrame* _converted;
        private SwrContext* _postSwr;
        private int _filled;
        private int _skip;
        private long _inputFrames;
        private long _emitted;
        private bool _finished;

        /// <summary>Initializes the block filter and output resampler for one file.</summary>
        public EnhancementFeeder(int inputRate, int ratio, int selector, int bitrateKbps, int sourceDepth,
            AVCodecContext* encoder, FrameFeeder feeder, SwrContext* gainSwr, AVFrame* gainFrame,
            bool applyGain, double gain)
        {
            _processor = new SpectralEnhancer(new SpectralEnhancementOptions
            {
                InputSampleRate = inputRate,
                OutputSampleRate = inputRate * ratio,
                SourceFormatSelector = selector,
                AverageBitRateKbps = bitrateKbps
            });
            int bits = sourceDepth is 8 or 16 or 20 or 24 or 32 ? sourceDepth : 16;
            _allPass = new AllPassProcessor(inputRate, selector == 16 ? 0 : 3, bitrateKbps * 1000, bits);
            _input = new int[_processor.InputFramesPerBlock * 2];
            _middle = new int[_input.Length];
            _channels = encoder->ch_layout.nb_channels;
            _ratio = ratio;
            _rate = inputRate * ratio;
            _skip = _processor.LatencyFrames + (inputRate == 44100 ? 5 : 6) * ratio;
            _encoder = encoder;
            _feeder = feeder;
            _gainSwr = gainSwr;
            _gainFrame = gainFrame;
            _applyGain = applyGain;
            _gain = gain;
            _postFormat = gainSwr != null ? AVSampleFormat.AV_SAMPLE_FMT_FLT : encoder->sample_fmt;
            try
            {
                _enhanced = ffmpeg.av_frame_alloc();
                _converted = ffmpeg.av_frame_alloc();
                if (_enhanced == null || _converted == null) throw new OutOfMemoryException();
                PrepareFrame(_enhanced, AVSampleFormat.AV_SAMPLE_FMT_S32, _rate, &encoder->ch_layout);
                _enhanced->nb_samples = 512;
                int result = ffmpeg.av_frame_get_buffer(_enhanced, 0);
                if (result < 0) throw FFmpegNative.CreateException(result, "频谱增强缓冲分配失败");
                SwrContext* post = null;
                result = ffmpeg.swr_alloc_set_opts2(&post, &encoder->ch_layout, _postFormat, encoder->sample_rate,
                    &encoder->ch_layout, AVSampleFormat.AV_SAMPLE_FMT_S32, _rate, 0, null);
                _postSwr = post;
                if (result < 0 || _postSwr == null) throw new InvalidOperationException("频谱增强输出转换器创建失败");
                result = ffmpeg.swr_init(_postSwr);
                if (result < 0) throw FFmpegNative.CreateException(result, "频谱增强输出转换器初始化失败");
            }
            catch { Dispose(); throw; }
        }

        /// <summary>Accepts packed integer PCM, preserving histories across arbitrary frame boundaries.</summary>
        public void Push(AVFrame* frame)
        {
            if (_finished) throw new InvalidOperationException("The enhancement stream has ended.");
            _inputFrames += frame->nb_samples;
            int* source = (int*)frame->data[0];
            for (int i = 0; i < frame->nb_samples; i++)
            {
                _input[_filled * 2] = source[i * _channels];
                _input[_filled * 2 + 1] = source[i * _channels + (_channels == 1 ? 0 : 1)];
                if (++_filled == _processor.InputFramesPerBlock) ProcessBlock();
            }
        }

        /// <summary>Drains filter latency and resampling while discarding block padding.</summary>
        public void Finish()
        {
            if (_finished) return;
            _finished = true;
            if (_filled > 0)
            {
                _input.AsSpan(_filled * 2).Clear();
                ProcessBlock();
            }
            while (_emitted < checked(_inputFrames * _ratio))
            {
                Array.Clear(_input);
                ProcessBlock();
            }
            while (ConvertOutput(null)) { }
            if (_gainSwr != null)
            {
                while (true)
                {
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
            _allPass.ProcessBlock(_input, _middle);
            _processor.ProcessBlock(_middle, _output);
            _filled = 0;
            int offset = Math.Min(_skip, 512);
            _skip -= offset;
            int frames = (int)Math.Min(512 - offset, checked(_inputFrames * _ratio) - _emitted);
            if (frames == 0) return;
            // The native encoder can retain frame references; make the reusable input writable.
            _enhanced->nb_samples = 512;
            int result = ffmpeg.av_frame_make_writable(_enhanced);
            if (result < 0) throw FFmpegNative.CreateException(result, "频谱增强缓冲不可写");
            int* destination = (int*)_enhanced->data[0];
            for (int i = 0; i < frames; i++)
                for (int channel = 0; channel < _channels; channel++)
                    destination[i * _channels + channel] = _output[(offset + i) * 2 + channel];
            _enhanced->nb_samples = frames;
            _emitted += frames;
            ConvertOutput(_enhanced);
        }

        private bool ConvertOutput(AVFrame* input)
        {
            PrepareFrame(_converted, _postFormat, _encoder->sample_rate, &_encoder->ch_layout);
            int result = ffmpeg.swr_convert_frame(_postSwr, _converted, input);
            if (result < 0) throw FFmpegNative.CreateException(result, "频谱增强输出转换失败");
            if (_converted->nb_samples == 0) return false;
            if (_applyGain) ApplyGain(_converted, _gain);
            if (_gainSwr != null)
            {
                PrepareFrame(_gainFrame, _encoder->sample_fmt, _encoder->sample_rate, &_encoder->ch_layout);
                result = ffmpeg.swr_convert_frame(_gainSwr, _gainFrame, _converted);
                if (result < 0) throw FFmpegNative.CreateException(result, "频谱增强增益转换失败");
                if (_gainFrame->nb_samples > 0) _feeder.Push(_gainFrame);
            }
            else _feeder.Push(_converted);
            return true;
        }

        /// <summary>Releases only the native frames and resampler owned by this bridge.</summary>
        public void Dispose()
        {
            if (_postSwr != null) { SwrContext* p = _postSwr; ffmpeg.swr_free(&p); _postSwr = null; }
            if (_enhanced != null) { AVFrame* p = _enhanced; ffmpeg.av_frame_free(&p); _enhanced = null; }
            if (_converted != null) { AVFrame* p = _converted; ffmpeg.av_frame_free(&p); _converted = null; }
        }
    }
}
