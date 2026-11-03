using System.Text.Json;
using System.Text.Json.Serialization;
using FancyToolAva.AudioEnhancement;
using FancyToolAva.Services;
using FancyToolAva.Services.Lyrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace FancyToolAva.Cli;

/// <summary>单个 CLI 调用的执行上下文：参数、输出、取消与按需构建的服务容器。</summary>
public sealed class CliContext : IDisposable
{
    private ServiceProvider? _services;

    public required CliArgs Args { get; init; }
    public required bool Json { get; init; }
    public required bool Quiet { get; init; }
    public required bool Zh { get; init; }
    public required CancellationToken CancellationToken { get; init; }

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonSerializerOptions JsonLineOptions { get; } = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public T Get<T>() where T : notnull
    {
        _services ??= CliServices.Build();
        return _services.GetRequiredService<T>();
    }

    public void Info(string message)
    {
        if (!Quiet) Console.Error.WriteLine(message);
    }

    public void Progress(double fraction)
    {
        if (Quiet || Console.IsErrorRedirected) return;
        Console.Error.Write($"\r{fraction,7:P1}");
    }

    public void ProgressDone()
    {
        if (Quiet || Console.IsErrorRedirected) return;
        Console.Error.WriteLine();
    }

    public void WriteResult(string text)
    {
        Console.Out.Write(text);
        if (!text.EndsWith('\n')) Console.Out.WriteLine();
    }

    public void WriteJson(object value) => Console.Out.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    public void WriteJsonLine(object value) => Console.Out.WriteLine(JsonSerializer.Serialize(value, JsonLineOptions));

    public void Error(string message) => Console.Error.WriteLine((Zh ? "错误: " : "Error: ") + message);

    public async Task<string?> ReadTextAsync(bool required = true)
    {
        if (Args.Has("--text")) return Args.Get("--text");
        if (Args.Get("--input") is { } path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException(path);
            return await File.ReadAllTextAsync(path, CancellationToken).ConfigureAwait(false);
        }
        if (Args.Has("--stdin"))
            return await Console.In.ReadToEndAsync(CancellationToken).ConfigureAwait(false);
        if (required) throw new CliUsageException(Zh ? "需要 -t/--text、-i/--input 或 --stdin。" : "Provide -t/--text, -i/--input, or --stdin.");
        return null;
    }

    public async Task<byte[]?> ReadBytesAsync(bool required = true)
    {
        if (Args.Get("--input") is { } path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException(path);
            return await File.ReadAllBytesAsync(path, CancellationToken).ConfigureAwait(false);
        }
        if (Args.Has("--stdin"))
        {
            using var stream = Console.OpenStandardInput();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, CancellationToken).ConfigureAwait(false);
            return buffer.ToArray();
        }
        if (Args.Has("--text"))
            return System.Text.Encoding.UTF8.GetBytes(Args.Get("--text") ?? string.Empty);
        if (required) throw new CliUsageException(Zh ? "需要 -i/--input、-t/--text 或 --stdin。" : "Provide -i/--input, -t/--text, or --stdin.");
        return null;
    }

    public async Task WriteOutputAsync(string content, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            WriteResult(content);
            return;
        }
        await File.WriteAllTextAsync(path, content, new System.Text.UTF8Encoding(false), CancellationToken).ConfigureAwait(false);
        Info((Zh ? "已写入 " : "Wrote ") + path);
    }

    public async Task WriteOutputAsync(byte[] content, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) && !Json)
            throw new CliUsageException(Zh ? "二进制输出必须使用 -o 指定文件。" : "Binary output requires -o.");
        if (string.IsNullOrWhiteSpace(path))
        {
            WriteJson(new { base64 = Convert.ToBase64String(content) });
            return;
        }
        await File.WriteAllBytesAsync(path, content, CancellationToken).ConfigureAwait(false);
        Info((Zh ? "已写入 " : "Wrote ") + path);
    }

    public static bool ResolveLanguage(IReadOnlyList<string> args, out bool zh)
    {
        for (int i = 0; i < args.Count; i++)
        {
            string token = args[i];
            string? value = null;
            if (token is "-lang" or "--lang") value = i + 1 < args.Count ? args[i + 1] : null;
            else if (token.StartsWith("--lang=", StringComparison.OrdinalIgnoreCase)) value = token["--lang=".Length..];
            if (value is null) continue;
            zh = value.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            return true;
        }
        zh = System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    public void Dispose()
    {
        _services?.Dispose();
        _services = null;
    }
}

/// <summary>CLI 的最小 DI 容器：只注册 Core 服务，不构造任何 GUI 类型。</summary>
public static class CliServices
{
    public static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddSerilog(Log.Logger, dispose: false);
            builder.SetMinimumLevel(LogLevel.Warning);
        });

        services.AddSingleton<IUiDispatcher>(ImmediateUiDispatcher.Instance);
        services.AddSingleton<ISuperResolutionPreferences>(new DefaultSuperResolutionPreferences());
        services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromMinutes(20) });
        services.AddSingleton<ModelDownloadService>(sp => new ModelDownloadService(
            sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ILogger<ModelDownloadService>>()));

        services.AddSingleton<ISuperResolutionService>(sp => new SuperResolutionService(
            sp.GetRequiredService<ILogger<SuperResolutionService>>(),
            sp.GetRequiredService<ISuperResolutionPreferences>(),
            sp.GetRequiredService<ModelDownloadService>()));
        services.AddSingleton<IVideoSuperResolutionService, VideoSuperResolutionService>();
        services.AddSingleton<IVideoInterpolationService, VideoInterpolationService>();
        services.AddSingleton<IImageConversionService, ImageConversionService>();
        services.AddSingleton<IFfmpegService, FfmpegService>();
        services.AddSingleton<IVideoTranscodeService, VideoTranscodeService>();
        services.AddSingleton<IAudioUpsamplingBackend, ManagedDspUpsamplingBackend>();
        services.AddSingleton<AudioUpsamplingBackendRegistry>();
        services.AddSingleton<IAudioConvertService, AudioConvertService>();
        services.AddSingleton<IFileEncryptionService, FileEncryptionService>();
        services.AddSingleton<IFolderCompareService, FolderCompareService>();
        services.AddSingleton<ISerialPortService, SerialPortService>();
        services.AddSingleton<ITcpServerService, TcpServerService>();
        services.AddSingleton<IUdpService, UdpService>();
        services.AddSingleton<TcpLinkTestService>();
        services.AddSingleton<UdpLinkTestService>();
        services.AddSingleton<PcmAudioDecoder>();
        services.AddSingleton<LyricsModelService>();
        services.AddSingleton<LyricsExtractionService>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = false, ValidateScopes = false });
    }
}