using FancyToolAva.AudioEnhancement;

/// <summary>Tests backend substitution with a 17-frame block, independent native rate and seven-frame latency.</summary>
internal sealed class FakeUpsamplingBackend : IAudioUpsamplingBackend
{
    public string Id => "test-alternate";
    internal int Created, Disposed;
    internal bool CancelProcessing;
    internal int? NativeInputRate;
    internal int NativeRatio=2;
    internal bool InvalidContract;
    internal CancellationTokenSource? Cancellation;
    public bool Supports(AudioUpsamplingSource source) => !source.IsDsd && source.Channels is 1 or 2;
    public IAudioUpsamplingProcessor Create(AudioUpsamplingRequest request,CancellationToken cancellationToken)
    {
        Created++;
        return new Processor(this,NativeInputRate??request.Source.SampleRate,request.Source.Channels);
    }
    private sealed class Processor(FakeUpsamplingBackend owner,int rate,int channels) : IAudioUpsamplingProcessor
    {
        private readonly double[] _delay=new double[7*channels];
        private int _position;
        public int InputSampleRate=>rate;
        public int OutputSampleRate=>rate*owner.NativeRatio;
        public int Channels=>channels;
        public int InputFramesPerBlock=>owner.InvalidContract?0:17;
        public int OutputFramesPerBlock=>17*owner.NativeRatio;
        public int LatencyFrames=>7;
        public void ProcessBlock(ReadOnlySpan<double> input,Span<double> output,CancellationToken ct)
        {
            if(owner.CancelProcessing)owner.Cancellation!.Cancel();
            ct.ThrowIfCancellationRequested();
            for(int frame=0;frame<OutputFramesPerBlock;frame++)
            {
                for(int channel=0;channel<channels;channel++)
                {
                    output[frame*channels+channel]=_delay[_position*channels+channel];
                    _delay[_position*channels+channel]=-input[(frame/owner.NativeRatio)*channels+channel];
                }
                _position=(_position+1)%7;
            }
        }
        public void Dispose()=>owner.Disposed++;
    }
}
