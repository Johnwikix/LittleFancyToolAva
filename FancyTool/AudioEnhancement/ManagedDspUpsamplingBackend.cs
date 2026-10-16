namespace FancyToolAva.AudioEnhancement;

/// <summary>将托管 DSP 适配到统一的归一化 PCM 接口。</summary>
public sealed class ManagedDspUpsamplingBackend : IAudioUpsamplingBackend
{
    /// <inheritdoc/>
    public string Id => AudioUpsamplingBackendRegistry.DefaultBackendId;

    /// <inheritdoc/>
    public bool Supports(AudioUpsamplingSource source) => !source.IsDsd
        && source.SampleRate is 44100 or 48000 && source.Channels is 1 or 2;

    /// <inheritdoc/>
    public IAudioUpsamplingProcessor Create(AudioUpsamplingRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Supports(request.Source)) throw new NotSupportedException("Unsupported managed upsampling input.");
        return new Processor(request);
    }

    private sealed class Processor : IAudioUpsamplingProcessor
    {
        private readonly SpectralEnhancer _core;
        private readonly AllPassProcessor _allPass;
        private readonly int[] _input, _middle, _output = new int[1024];
        private bool _disposed;

        internal Processor(AudioUpsamplingRequest request)
        {
            InputSampleRate = request.Source.SampleRate;
            Channels = request.Source.Channels;
            int ratio = request.TargetSampleRate >= InputSampleRate * 4 ? 4
                : request.TargetSampleRate >= InputSampleRate * 2 ? 2 : 1;
            OutputSampleRate = InputSampleRate * ratio;
            int bitrate = Math.Clamp(request.Source.BitrateKbps, 4, 1020);
            _core = new(new() { InputSampleRate = InputSampleRate, OutputSampleRate = OutputSampleRate,
                SourceFormatSelector = request.Source.IsLossless ? 16 : 4, AverageBitRateKbps = bitrate });
            int bits = request.Source.BitDepth is 8 or 16 or 20 or 24 or 32 ? request.Source.BitDepth : 16;
            _allPass = new(InputSampleRate, request.Source.IsLossless ? 0 : 3, bitrate * 1000, bits);
            _input = new int[_core.InputFramesPerBlock * 2];
            _middle = new int[_input.Length];
            LatencyFrames = _core.LatencyFrames + (InputSampleRate == 44100 ? 5 : 6) * ratio;
        }

        public int InputSampleRate { get; }
        public int OutputSampleRate { get; }
        public int Channels { get; }
        public int InputFramesPerBlock => _core.InputFramesPerBlock;
        public int OutputFramesPerBlock => _core.OutputFramesPerBlock;
        public int LatencyFrames { get; }

        public void ProcessBlock(ReadOnlySpan<double> input, Span<double> output, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (input.Length != InputFramesPerBlock * Channels || output.Length < OutputFramesPerBlock * Channels)
                throw new ArgumentException("Incorrect upsampling block size.");
            // 归一化的双精度数可以精确表示 S32 样本。
            for (int frame = 0; frame < InputFramesPerBlock; frame++)
            {
                int left = ToInteger(input[frame * Channels]);
                _input[frame * 2] = left;
                _input[frame * 2 + 1] = Channels == 1 ? left : ToInteger(input[frame * 2 + 1]);
            }
            _allPass.ProcessBlock(_input, _middle);
            _core.ProcessBlock(_middle, _output);
            for (int frame = 0; frame < OutputFramesPerBlock; frame++)
                for (int channel = 0; channel < Channels; channel++)
                    output[frame * Channels + channel] = _output[frame * 2 + channel] / 2147483648.0;
        }

        private static int ToInteger(double sample) => !double.IsFinite(sample) ? 0
            : (int)Math.Clamp(Math.Round(sample * 2147483648.0), int.MinValue, int.MaxValue);

        public void Dispose() => _disposed = true;
    }
}
