using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using FancyToolAva.AudioEnhancement;
using FancyToolAva.Models;
using FancyToolAva.Models.ViewStates;
using FancyToolAva.Services;
using Microsoft.Extensions.Logging.Abstractions;

string work=Path.GetFullPath(args.Length>0?args[0]:Path.Combine(Path.GetTempPath(),"spectral-enhancement-smoke"));
Directory.CreateDirectory(work);
var service=new AudioConvertService(NullLogger<AudioConvertService>.Instance);
var results=new List<object>();
int failures=0;
foreach(int rate in new[]{44100,48000})
foreach(int channels in new[]{1,2})
foreach(int ratio in new[]{1,2,4})
foreach(int frames in new[]{1,127,1001,8193})
{
    string name=$"{rate}-{channels}ch-x{ratio}-{frames}";
    Run(name,()=>
    {
        string input=Path.Combine(work,name+".in.wav"),output=Path.Combine(work,name+".out.wav");
        int[] samples=WriteInput(input,rate,channels,frames);
        service.ConvertAsync(input,output,new AudioConvertOptions{Format=AudioConvertFormat.Wav,BitDepth=AudioBitDepthMode.S32,SampleRateHz=192000,UpsamplingMultiplier=ratio,EnableSpectralEnhancement=true}).GetAwaiter().GetResult();
        var wav=ReadWave(output);
        Require(wav.Rate==rate*ratio&&wav.Channels==channels&&wav.Bits==32,"output format");
        Require(wav.Data.Length==frames*ratio*channels*4,"exact duration, no padded tail");
        // Compare complete conversion (decode / block buffering / latency trim / EOF drain) with the managed core.
        var processor=new SpectralEnhancer(new SpectralEnhancementOptions{InputSampleRate=rate,OutputSampleRate=rate*ratio,SourceFormatSelector=16,AverageBitRateKbps=192});
        var allPass=new AllPassProcessor(rate,0,192000,16);
        int skip=processor.LatencyFrames+(rate==44100?5:6)*ratio;
        int[] block=new int[processor.InputFramesPerBlock*2],mid=new int[block.Length],outBlock=new int[1024];
        var expected=new List<int>();
        for(int position=0;expected.Count/2<skip+frames*ratio;position+=processor.InputFramesPerBlock)
        {
            Array.Clear(block);
            for(int i=0;i<processor.InputFramesPerBlock&&position+i<frames;i++)
            {
                block[2*i]=samples[(position+i)*channels];
                block[2*i+1]=samples[(position+i)*channels+(channels==1?0:1)];
            }
            allPass.ProcessBlock(block,mid);processor.ProcessBlock(mid,outBlock);
            expected.AddRange(outBlock);
        }
        for(int i=0;i<frames*ratio;i++)for(int c=0;c<channels;c++)
            Require(BinaryPrimitives.ReadInt32LittleEndian(wav.Data.AsSpan((i*channels+c)*4,4))==expected[(skip+i)*2+c],"sample equality");
    });
}
Run("auto-rate-depth",()=>
{
    string source=Path.Combine(work,"auto.in.wav"),output=Path.Combine(work,"auto.out.flac");WriteInput(source,44100,2,4410);
    service.ConvertAsync(source,output,new AudioConvertOptions{Format=AudioConvertFormat.Flac,EnableSpectralEnhancement=true}).GetAwaiter().GetResult();
    var p=service.ProbeAsync(output).GetAwaiter().GetResult()!;Require(p.SampleRate==88200&&p.BitDepth==24&&p.Channels==2,"automatic enhanced output");
});
Run("gain-resample",()=>
{
    string source=Path.Combine(work,"gain.in.wav");WriteInput(source,44100,2,1001);
    foreach(int ratio in new[]{1,2,4})foreach(double gain in new[]{0.0,-6.0})
    {
        int rate=44100*ratio;
        string output=Path.Combine(work,$"gain-{rate}-{gain}.wav");
        service.ConvertAsync(source,output,new AudioConvertOptions{Format=AudioConvertFormat.Wav,UpsamplingMultiplier=ratio,GainDb=gain,BitDepth=AudioBitDepthMode.S24,EnableSpectralEnhancement=true}).GetAwaiter().GetResult();
        var w=ReadWave(output);Require(Math.Abs(w.Data.Length/6-(int)Math.Round(1001.0*rate/44100))<=1,"resampled duration");
    }
});
foreach(var format in Enum.GetValues<AudioConvertFormat>())Run("codec-"+format,()=>
{
    string source=Path.Combine(work,"codec.in.wav"),output=Path.Combine(work,"codec-"+format+AudioConvertService.GetExtension(format));WriteInput(source,48000,2,4800);
    service.ConvertAsync(source,output,new AudioConvertOptions{Format=format,EnableSpectralEnhancement=true,GainDb=-3}).GetAwaiter().GetResult();
    var p=service.ProbeAsync(output).GetAwaiter().GetResult();Require(p is not null&&p.SampleRate==(AudioConvertService.IsLosslessOutput(format)?96000:48000)&&p.Channels==2,"format-gated upsampling output");
});
foreach(var format in Enum.GetValues<AudioConvertFormat>().Where(f=>!AudioConvertService.IsLosslessOutput(f)))
Run("lossy-output-skips-backend-"+format,()=>
{
    var backend=new FakeUpsamplingBackend();
    var alternate=new AudioConvertService(NullLogger<AudioConvertService>.Instance,new AudioUpsamplingBackendRegistry([backend]));
    string source=Path.Combine(work,"lossy-out.in.wav");WriteInput(source,48000,2,4800);
    string enabled=Path.Combine(work,"lossy-on-"+format+AudioConvertService.GetExtension(format));
    string disabled=Path.Combine(work,"lossy-off-"+format+AudioConvertService.GetExtension(format));
    alternate.ConvertAsync(source,enabled,new(){Format=format,SampleRateHz=48000,EnableSpectralEnhancement=true,
        UpsamplingMultiplier=4,UpsamplingBackendId=backend.Id,GainDb=-3}).GetAwaiter().GetResult();
    alternate.ConvertAsync(source,disabled,new(){Format=format,SampleRateHz=48000,GainDb=-3}).GetAwaiter().GetResult();
    Require(backend.Created==0&&backend.Disposed==0,"lossy output must not create or run any backend");
    Require(alternate.ProbeAsync(enabled).GetAwaiter().GetResult()!.SampleRate==48000,"lossy output ignores multiplier");
    string onPcm=enabled+".decoded.wav",offPcm=disabled+".decoded.wav";
    alternate.ConvertAsync(enabled,onPcm,new(){Format=AudioConvertFormat.Wav,BitDepth=AudioBitDepthMode.S32,CopyMetadata=false}).GetAwaiter().GetResult();
    alternate.ConvertAsync(disabled,offPcm,new(){Format=AudioConvertFormat.Wav,BitDepth=AudioBitDepthMode.S32,CopyMetadata=false}).GetAwaiter().GetResult();
    Require(ReadWave(onPcm).Data.AsSpan().SequenceEqual(ReadWave(offPcm).Data),"lossy output must equal ordinary conversion");
});
Run("lossy-output-does-not-resolve-unknown-backend",()=>
{
    string source=Path.Combine(work,"unknown-lossy.in.wav"),output=source+".mp3";
    WriteInput(source,44100,2,4410);
    service.ConvertAsync(source,output,new(){Format=AudioConvertFormat.Mp3,EnableSpectralEnhancement=true,
        UpsamplingMultiplier=0,UpsamplingBackendId="not-installed"}).GetAwaiter().GetResult();
    Require(service.ProbeAsync(output).GetAwaiter().GetResult()!.SampleRate==44100,"lossy output bypasses backend resolution and rate policy");
});
foreach((int rate,int channels) in new[]{(96000,2),(44100,6)})Run($"unsupported-passthrough-{rate}-{channels}",()=>
{
    string source=Path.Combine(work,$"bypass-{rate}-{channels}.in.wav");WriteInput(source,rate,channels,1001);
    string a=source+".off.wav",b=source+".on.wav";
    service.ConvertAsync(source,a,new AudioConvertOptions{Format=AudioConvertFormat.Wav}).GetAwaiter().GetResult();
    service.ConvertAsync(source,b,new AudioConvertOptions{Format=AudioConvertFormat.Wav,EnableSpectralEnhancement=true}).GetAwaiter().GetResult();
    Require(ReadWave(a).Data.AsSpan().SequenceEqual(ReadWave(b).Data),"unsupported input remains unchanged");
});
Run("persisted-state-default",()=>
{
    Require(!new AudioConvertOptions().EnableSpectralEnhancement,"default off");
    var state=new AudioConvertViewState{EnableSpectralEnhancement=true,UpsamplingMultiplier=4};
    Require(JsonSerializer.Deserialize<AudioConvertViewState>("{}")!.UpsamplingMultiplier==2,"legacy multiplier default");
    Require(JsonSerializer.Deserialize<AudioConvertViewState>(JsonSerializer.Serialize(state))!.UpsamplingMultiplier==4,"multiplier state roundtrip");
    Require(JsonSerializer.Deserialize<AudioConvertViewState>(JsonSerializer.Serialize(state))!.EnableSpectralEnhancement,"state roundtrip");
});
Run("target-cap-divisors",()=>
{
    Require(AudioUpsamplingRate.Resolve(96000,4)==192000,"384k cap");
    Require(AudioUpsamplingRate.Resolve(88200,4)==176400,"352.8k cap");
    Require(AudioUpsamplingRate.Resolve(44100,1)==44100,"1x is a valid target");
});
Run("x1-lossy-band-repair",()=>
{
    var core=new SpectralEnhancer(new(){InputSampleRate=44100,OutputSampleRate=44100,SourceFormatSelector=4,AverageBitRateKbps=128});
    Require(core.BandRepairActive&&!core.RateExtensionActive,"1x must retain repair while disabling rate extension");
});
foreach(int sourceRate in new[]{48000,96000})Run($"alternate-backend-{sourceRate}",()=>
{
    var backend=new FakeUpsamplingBackend();
    var alternate=new AudioConvertService(NullLogger<AudioConvertService>.Instance,new AudioUpsamplingBackendRegistry([backend]));
    string input=Path.Combine(work,$"alternate-{sourceRate}.in.wav"),output=input+".out.wav";
    int[] samples=WriteInput(input,sourceRate,2,1001);
    alternate.ConvertAsync(input,output,new(){Format=AudioConvertFormat.Wav,BitDepth=AudioBitDepthMode.S32,
        EnableSpectralEnhancement=true,UpsamplingMultiplier=4,UpsamplingBackendId=backend.Id}).GetAwaiter().GetResult();
    var wave=ReadWave(output);
    int rate=AudioUpsamplingRate.Resolve(sourceRate,4);
    Require(wave.Rate==rate&&wave.Data.Length==1001*(rate/sourceRate)*2*4,"backend-independent duration and cap");
    if(sourceRate==96000)
        for(int i=0;i<2002;i++)for(int ch=0;ch<2;ch++)
            Require(BinaryPrimitives.ReadInt32LittleEndian(wave.Data.AsSpan((i*2+ch)*4,4))==-samples[(i/2)*2+ch],"alternate signal and latency");
    Require(backend.Created==1&&backend.Disposed==1,"processor lifecycle");
});
Run("alternate-backend-cancel-cleanup",()=>
{
    using var cancellation=new CancellationTokenSource();
    var backend=new FakeUpsamplingBackend{CancelProcessing=true,Cancellation=cancellation};
    var alternate=new AudioConvertService(NullLogger<AudioConvertService>.Instance,new AudioUpsamplingBackendRegistry([backend]));
    string input=Path.Combine(work,"cancel.in.wav"),output=input+".out.wav";
    WriteInput(input,48000,2,1001);File.WriteAllText(output,"keep existing output");
    try
    {
        alternate.ConvertAsync(input,output,new(){Format=AudioConvertFormat.Wav,EnableSpectralEnhancement=true,
            UpsamplingBackendId=backend.Id},ct:cancellation.Token).GetAwaiter().GetResult();
        throw new Exception("Expected cancellation");
    }
    catch(OperationCanceledException){ }
    Require(backend.Disposed==1&&!File.Exists(output+".tmp"),"cancellation releases processor and temp file");
    Require(File.ReadAllText(output)=="keep existing output","cancellation replaced existing output");
});
Run("alternate-fixed-48k-and-requested-x1",()=>
{
    var backend=new FakeUpsamplingBackend{NativeInputRate=48000,NativeRatio=1};
    var alternate=new AudioConvertService(NullLogger<AudioConvertService>.Instance,new AudioUpsamplingBackendRegistry([backend]));
    string input=Path.Combine(work,"fixed48.in.wav"),output=input+".out.wav";
    WriteInput(input,44100,2,8193);
    alternate.ConvertAsync(input,output,new(){Format=AudioConvertFormat.Wav,BitDepth=AudioBitDepthMode.S32,
        EnableSpectralEnhancement=true,UpsamplingMultiplier=1,UpsamplingBackendId=backend.Id}).GetAwaiter().GetResult();
    var wave=ReadWave(output);
    Require(wave.Rate==44100&&Math.Abs(wave.Data.Length/8-8193)<=1,"fixed native rate can serve x1 via pre/post SRC");
    Require(backend.Created==1&&backend.Disposed==1,"fixed rate backend disposed");
});
Run("backend-invalid-contract-cleanup",()=>
{
    var backend=new FakeUpsamplingBackend{InvalidContract=true};
    var alternate=new AudioConvertService(NullLogger<AudioConvertService>.Instance,new AudioUpsamplingBackendRegistry([backend]));
    string input=Path.Combine(work,"invalid-backend.in.wav"),output=input+".out.wav";
    WriteInput(input,48000,2,1001);
    try
    {
        alternate.ConvertAsync(input,output,new(){Format=AudioConvertFormat.Wav,EnableSpectralEnhancement=true,
            UpsamplingBackendId=backend.Id}).GetAwaiter().GetResult();
        throw new Exception("Expected invalid processor contract");
    }
    catch(InvalidOperationException e) when(e.Message.Contains("contract")){ }
    Require(backend.Disposed==1&&!File.Exists(output+".tmp"),"failed feeder construction releases processor");
});
Run("disabled-does-not-resolve-backend",()=>
{
    var backend=new FakeUpsamplingBackend();
    var alternate=new AudioConvertService(NullLogger<AudioConvertService>.Instance,new AudioUpsamplingBackendRegistry([backend]));
    string input=Path.Combine(work,"disabled.in.wav"),output=input+".out.wav";
    WriteInput(input,48000,2,1001);
    alternate.ConvertAsync(input,output,new(){Format=AudioConvertFormat.Wav,UpsamplingBackendId="not-installed",EnableSpectralEnhancement=false}).GetAwaiter().GetResult();
    Require(backend.Created==0&&ReadWave(output).Rate==48000,"disabled feature must not create/load a backend");
});
Run("registry-unknown-and-duplicates",()=>
{
    try{new AudioUpsamplingBackendRegistry([new FakeUpsamplingBackend(),new FakeUpsamplingBackend()]);throw new Exception("duplicate accepted");}
    catch(ArgumentException){ }
    try{new AudioUpsamplingBackendRegistry().Get("unknown");throw new Exception("unknown accepted");}
    catch(NotSupportedException){ }
});
File.WriteAllText(Path.Combine(work,"results.json"),JsonSerializer.Serialize(new{failures,cases=results},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine($"{results.Count} cases, {failures} failures");
return failures==0?0:1;

void Run(string name,Action action)
{
    try{action();results.Add(new{name,passed=true});Console.WriteLine("PASS "+name);}
    catch(Exception e){failures++;results.Add(new{name,passed=false,error=e.ToString()});Console.WriteLine("FAIL "+name+": "+e);}
}
static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
static int[] WriteInput(string path,int rate,int channels,int frames)
{
    int[] pcm=new int[frames*channels];using var w=new BinaryWriter(File.Create(path));
    w.Write("RIFF"u8);w.Write(36+frames*channels*2);w.Write("WAVEfmt "u8);w.Write(16);w.Write((short)1);w.Write((short)channels);
    w.Write(rate);w.Write(rate*channels*2);w.Write((short)(channels*2));w.Write((short)16);w.Write("data"u8);w.Write(frames*channels*2);
    for(int i=0;i<frames;i++)for(int c=0;c<channels;c++){short value=(short)(i==frames-1?9000:8000*Math.Sin((i+c*11)*.31));w.Write(value);pcm[i*channels+c]=value<<16;}
    return pcm;
}
static (int Rate,int Channels,int Bits,byte[] Data) ReadWave(string path)
{
    using var r=new BinaryReader(File.OpenRead(path));r.ReadBytes(12);int rate=0,channels=0,bits=0;byte[] data=[];
    while(r.BaseStream.Position+8<=r.BaseStream.Length)
    {
        string name=Encoding.ASCII.GetString(r.ReadBytes(4));int size=r.ReadInt32();long next=r.BaseStream.Position+size+(size&1);
        if(name=="fmt "){r.ReadUInt16();channels=r.ReadUInt16();rate=r.ReadInt32();r.ReadBytes(6);bits=r.ReadUInt16();}
        if(name=="data")data=r.ReadBytes(size);r.BaseStream.Position=next;
    }
    return(rate,channels,bits,data);
}
