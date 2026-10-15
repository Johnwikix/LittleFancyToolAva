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
foreach(int ratio in new[]{2,4})
foreach(int frames in new[]{1,127,1001,8193})
{
    string name=$"{rate}-{channels}ch-x{ratio}-{frames}";
    Run(name,()=>
    {
        string input=Path.Combine(work,name+".in.wav"),output=Path.Combine(work,name+".out.wav");
        int[] samples=WriteInput(input,rate,channels,frames);
        service.ConvertAsync(input,output,new AudioConvertOptions{Format=AudioConvertFormat.Wav,BitDepth=AudioBitDepthMode.S32,SampleRateHz=rate*ratio,EnableSpectralEnhancement=true}).GetAwaiter().GetResult();
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
    foreach(int rate in new[]{44100,48000,96000})foreach(double gain in new[]{0.0,-6.0})
    {
        string output=Path.Combine(work,$"gain-{rate}-{gain}.wav");
        service.ConvertAsync(source,output,new AudioConvertOptions{Format=AudioConvertFormat.Wav,SampleRateHz=rate,GainDb=gain,BitDepth=AudioBitDepthMode.S24,EnableSpectralEnhancement=true}).GetAwaiter().GetResult();
        var w=ReadWave(output);Require(Math.Abs(w.Data.Length/6-(int)Math.Round(1001.0*rate/44100))<=1,"resampled duration");
    }
});
foreach(var format in Enum.GetValues<AudioConvertFormat>())Run("codec-"+format,()=>
{
    string source=Path.Combine(work,"codec.in.wav"),output=Path.Combine(work,"codec-"+format+AudioConvertService.GetExtension(format));WriteInput(source,48000,2,4800);
    service.ConvertAsync(source,output,new AudioConvertOptions{Format=format,EnableSpectralEnhancement=true,GainDb=-3}).GetAwaiter().GetResult();
    var p=service.ProbeAsync(output).GetAwaiter().GetResult();Require(p is not null&&p.SampleRate>0&&p.Channels==2,"enhanced codec output");
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
    var state=new AudioConvertViewState{EnableSpectralEnhancement=true};
    Require(JsonSerializer.Deserialize<AudioConvertViewState>(JsonSerializer.Serialize(state))!.EnableSpectralEnhancement,"state roundtrip");
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
