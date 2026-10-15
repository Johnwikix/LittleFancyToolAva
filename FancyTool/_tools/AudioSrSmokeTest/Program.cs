using System.Diagnostics;
using FancyToolAva.AudioEnhancement;
using FancyToolAva.Models;
using FancyToolAva.Services;
using Microsoft.Extensions.Logging.Abstractions;

string root=Path.GetFullPath(args[0]);
Environment.SetEnvironmentVariable("FANCYTOOL_AUDIOSR_MODELS",Path.Combine(root,"models-fp32"));
string work=Path.Combine(root,"smoke");Directory.CreateDirectory(work);
var failures=new List<string>();
double[] basis=args.Contains("--real")?[]:AudioSrDsp.LoadNpy(Path.Combine(root,"models-fp32","mel_basis.npy"),256*1025);
double[] alphas=args.Contains("--real")?[]:AudioSrDsp.LoadNpy(Path.Combine(root,"models-fp32","alphas_cumprod.npy"),1000);
double[] Vector(string name,int count)=>AudioSrDsp.LoadNpy(Path.Combine(root,"vectors",name+".npy"),count);
if(!args.Contains("--real"))
{
    Check("normalization",()=>Near(AudioSrDsp.Normalize(Vector("input",245760),out _),Vector("normalized",245760),1e-7));
    Check("STFT frontend",()=>Near(AudioSrDsp.Magnitude(Vector("normalized",245760),default),Vector("magnitude",512*1025),1e-7));
    Check("log mel frontend",()=>Near(AudioSrDsp.LogMel(Vector("magnitude",512*1025),basis,default).Select(v=>(double)v).ToArray(),Vector("mel",512*256),2e-5));
    Check("Butterworth polyphase lowpass",()=>Near(AudioSrDsp.Lowpass(Vector("normalized",245760),8437.5,default),Vector("lowpass",245760),2e-5));
    Check("low band STFT anchoring",()=>Near(AudioSrDsp.AnchorLowBand(Vector("restored",245760),Vector("normalized",245760),default),Vector("anchor",245760),1e-7));
    Check("DDIM v parameterization",()=>
    {
        float[] x=Vector("x",1001).Select(v=>(float)v).ToArray();
        AudioSrPipeline.DdimStep(x,Vector("v",1001).Select(v=>(float)v).ToArray(),Vector("noise",1001).Select(v=>(float)v).ToArray(),.2,.21);
        Near(x.Select(v=>(double)v).ToArray(),Vector("step",1001),1e-6);
    });
    Check("DDIM timestep schedules",()=>
    {
        foreach(int steps in new[]{1,20,50,100,200})
        {var schedule=AudioSrPipeline.Timesteps(steps);Require(schedule.Length==steps&&schedule[0]==1&&schedule[^1]<1000,"invalid schedule");}
        try{AudioSrPipeline.Timesteps(30);throw new Exception("invalid steps accepted");}catch(ArgumentOutOfRangeException){}
    });
    Check("legacy video dimension wrapper",()=>
    {
        using var session=WindowsMlAccelerator.CreateSession(Path.Combine(root,"vectors","video_identity.onnx"),VideoSrBackend.Cpu,96,64,default,out var device);
        Require(!device.IsHardware&&session.InputMetadata["image"].Dimensions.SequenceEqual(new[]{1,3,64,96}),"legacy video width/height overrides changed");
    });
    Check("pinned external-data pack",()=>Require(ModelManifest.AudioSr.Count==8&&ModelManifest.AudioSr.All(v=>v.Sha256.Length==64)&&ModelManifest.AudioSr.Any(v=>v.FileName=="ddpm.onnx.data"),"incomplete manifest"));
    Check("silence overlap and lifecycle",()=>
    {
        var graphs=new FakeGraphs();int releases=0;
        var p=new AudioSrUpsamplingBackend.Processor(2,graphs,basis,alphas,new(Steps:1),()=>releases++,null);
        double[] input=new double[p.InputFramesPerBlock*2],output=new double[input.Length];
        p.ProcessBlock(input,output,default);
        Require(output.All(v=>v==0)&&graphs.Calls==0,"silence must not hallucinate or invoke graphs");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        try{p.ProcessBlock(input,output,cancelled.Token);throw new Exception("cancel ignored");}catch(OperationCanceledException){}
        p.Dispose();p.Dispose();Require(graphs.Disposals==1&&releases==1,"processor disposal not idempotent");
    });
    Check("stereo coherence and graph contract",()=>
    {
        var graphs=new FakeGraphs();
        using var p=new AudioSrUpsamplingBackend.Processor(2,graphs,basis,alphas,new(Steps:1),()=>{},null);
        double[] input=new double[p.InputFramesPerBlock*2],output=new double[input.Length];
        for(int i=0;i<input.Length/2;i++)input[2*i]=input[2*i+1]=.1*Math.Sin(i*.07);
        p.ProcessBlock(input,output,default);
        Require(graphs.Calls==10,"expected all four graph stages and CFG per channel");
        for(int i=0;i<output.Length/2;i++)Require(output[2*i]==output[2*i+1]&&double.IsFinite(output[2*i]),"identical stereo channels diverged");
    });
}
else
{
    using var http=new HttpClient();
    var models=new AudioSrModelService(new ModelDownloadService(http,NullLogger<ModelDownloadService>.Instance),NullLogger<AudioSrModelService>.Instance);
    models.DownloadProgressChanged+=p=>{if(p.Stage!=ModelDownloadStage.Downloading)Console.WriteLine($"[model] {p.FileName} {p.Stage}");};
    models.DeviceChanged+=d=>Console.WriteLine($"[device] {d.Name} fallback={d.FallbackReason}");
    models.InferenceProgressChanged+=(done,total)=>Console.WriteLine($"[infer] {done}/{total}");
    var backend=new AudioSrUpsamplingBackend(models);
    var service=new AudioConvertService(NullLogger<AudioConvertService>.Instance,new AudioUpsamplingBackendRegistry([new ManagedDspUpsamplingBackend(),backend]));
    string source=Path.Combine(work,"input.wav"),output=Path.Combine(work,"audiosr.wav");
    string? inputArg=args.FirstOrDefault(a=>a.StartsWith("--input="));
    if(inputArg!=null)
    {
        string decoded=Path.Combine(work,"decoded.wav");
        await service.ConvertAsync(inputArg[8..],decoded,new(){Format=AudioConvertFormat.Wav,BitDepth=AudioBitDepthMode.S16,SampleRateHz=48000,CopyMetadata=false});
        var w=ReadWave(decoded);int offset=Math.Min(w.Data.Length/2,48000*w.Channels*2*20);offset-=offset%(w.Channels*2);
        WriteWave(source,48000,w.Channels,w.Data.AsSpan(offset,Math.Min(48000*w.Channels*2,w.Data.Length-offset)).ToArray());
    }
    else WriteWave(source,44100,1,Enumerable.Range(0,4410).SelectMany(i=>BitConverter.GetBytes((short)(10000*Math.Sin(i*.07)))).ToArray());
    int steps=int.Parse(args.FirstOrDefault(a=>a.StartsWith("--steps="))?[8..]??"1");
    var device=Enum.Parse<VideoSrBackend>(args.FirstOrDefault(a=>a.StartsWith("--backend="))?[10..]??"Gpu");
    int multiplier=int.Parse(args.FirstOrDefault(a=>a.StartsWith("--multiplier="))?[13..]??"1");
    using var cancellation=new CancellationTokenSource();
    bool expectCancellation=args.Contains("--cancel");
    if(expectCancellation)models.InferenceProgressChanged+=(_,_)=>cancellation.Cancel();
    if(args.Contains("--install-only"))
    {
        Environment.SetEnvironmentVariable("FANCYTOOL_AUDIOSR_MODELS",Path.Combine(root,"models-download-smoke"));
        await models.EnsureModelAsync();Console.WriteLine("PASS shared downloader installed and SHA-verified AudioSR pack");return 0;
    }
    var timer=Stopwatch.StartNew();
    output=Path.Combine(work,expectCancellation?"cancel-preserved.wav":$"audiosr-{steps}steps-x{multiplier}.wav");
    if(expectCancellation)File.WriteAllText(output,"preserve existing target");
    try
    {
    await service.ConvertAsync(source,output,new(){Format=AudioConvertFormat.Wav,BitDepth=AudioBitDepthMode.S32,
        EnableSpectralEnhancement=true,UpsamplingBackendId=backend.Id,UpsamplingMultiplier=multiplier,
        UpsamplingInference=new(device,AllowCpuFallback:true,Steps:steps),CopyMetadata=false},ct:cancellation.Token);
    }
    catch(OperationCanceledException) when(expectCancellation)
    {
        Require(!File.Exists(output+".tmp")&&File.ReadAllText(output)=="preserve existing target","native cancellation leaked temp or replaced target");
        Console.WriteLine("PASS cancellation during actual native diffusion and file cleanup");return 0;
    }
    Require(!expectCancellation,"expected native inference cancellation");
    var actual=ReadWave(output);var original=ReadWave(source);
    Require(actual.Rate==AudioUpsamplingRate.Resolve(original.Rate,multiplier)&&actual.Channels==original.Channels&&actual.Data.Length==original.Data.Length*2*multiplier,"wrong native pipeline rate/channel/duration");
    Require(actual.Data.Any(v=>v!=0),"empty real model output");
    Console.WriteLine($"PASS actual AudioSR ONNX + encoder, steps={steps}, elapsed={timer.Elapsed.TotalSeconds:F2}s, bytes={actual.Data.Length}, rate={actual.Rate}");
}
Console.WriteLine($"AudioSR regression failures: {failures.Count}");
return failures.Count==0?0:1;

void Check(string name,Action body){try{body();Console.WriteLine("PASS "+name);}catch(Exception e){failures.Add(name);Console.WriteLine("FAIL "+name+": "+e);}}
static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
static void Near(double[] actual,double[] expected,double tolerance)
{
    Require(actual.Length==expected.Length,"shape mismatch");
    double error=actual.Zip(expected,(a,b)=>Math.Abs(a-b)).Max();Console.WriteLine($"max abs error {error:G6}");
    Require(error<=tolerance,"numeric error exceeds "+tolerance);
}
static void WriteWave(string path,int rate,int channels,byte[] data)
{
    using var w=new BinaryWriter(File.Create(path));w.Write("RIFF"u8);w.Write(data.Length+36);w.Write("WAVEfmt "u8);w.Write(16);w.Write((short)1);w.Write((short)channels);w.Write(rate);w.Write(rate*channels*2);w.Write((short)(channels*2));w.Write((short)16);w.Write("data"u8);w.Write(data.Length);w.Write(data);
}
static (int Rate,int Channels,byte[] Data) ReadWave(string path)
{
    using var r=new BinaryReader(File.OpenRead(path));r.ReadBytes(12);int rate=0,channels=0;byte[] data=[];
    while(r.BaseStream.Position+8<=r.BaseStream.Length)
    {
        string name=System.Text.Encoding.ASCII.GetString(r.ReadBytes(4));int length=r.ReadInt32();long end=r.BaseStream.Position+length+(length&1);
        if(name=="fmt "){r.ReadInt16();channels=r.ReadInt16();rate=r.ReadInt32();}
        if(name=="data")data=r.ReadBytes(length);r.BaseStream.Position=end;
    }
    return(rate,channels,data);
}
sealed class FakeGraphs : IAudioSrGraphs
{
    internal int Calls,Disposals;
    public float[] Run(string graph,CancellationToken ct,params AudioSrTensor[] inputs)
    {
        ct.ThrowIfCancellationRequested();Calls++;
        return graph switch
        {
            "vae_feature_extract"=>new float[AudioSrPipeline.LatentSize],
            "ddpm"=>new float[AudioSrPipeline.LatentSize],
            "vae_decoder"=>new float[512*256],
            "vocoder"=>new float[AudioSrPipeline.WindowFrames+16],
            _=>throw new Exception("unexpected graph")
        };
    }
    public void Dispose()=>Disposals++;
}
