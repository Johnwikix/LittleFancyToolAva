using FFmpeg.AutoGen;

namespace FancyToolAva.Services;

/// <summary>
/// 进程内 FFmpeg（shared DLL）加载引导：库随应用分发（Libraries/FFmpeg，输出到应用根目录），
/// 经 FFmpeg.AutoGen 直接调用 libavformat/libavcodec/libavfilter/libswscale/libswresample。
/// 首次触碰 ffmpeg 门面触发绑定初始化；失败（缺库/ABI 不匹配）时缓存异常，避免重复抛出。
/// </summary>
public static unsafe class FFmpegNative
{
    private static readonly object Gate = new();
    private static bool _loadAttempted;
    private static bool _loaded;
    private static Exception? _loadError;

    /// <summary>尝试加载并缓存结果。DLL 已在应用根目录，成功后返回 true。</summary>
    public static bool TryLoad(out Exception? error)
    {
        lock (Gate)
        {
            if (_loadAttempted)
            {
                error = _loadError;
                return _loaded;
            }
            _loadAttempted = true;
            try
            {
                // 任意一次调用都会触发 ffmpeg 静态构造器完成全部 DLL 的解析
                _ = ffmpeg.av_version_info();
                _loaded = true;
                error = null;
            }
            catch (Exception ex)
            {
                _loadError = ex is TypeInitializationException tie && tie.InnerException != null
                    ? tie.InnerException
                    : ex;
                _loaded = false;
                error = _loadError;
            }
            return _loaded;
        }
    }

    /// <summary>返回类似 "FFmpeg n9.0.1-84-g946fcce07b" 的版本串；未加载时触发加载。</summary>
    public static string GetVersionInfo()
    {
        if (!TryLoad(out _)) return "FFmpeg (not loaded)";
        try
        {
            string v = ffmpeg.av_version_info() ?? "";
            return string.IsNullOrWhiteSpace(v) ? "FFmpeg" : $"FFmpeg {v}";
        }
        catch
        {
            return "FFmpeg";
        }
    }

    /// <summary>枚举内置 FFmpeg 的编码器（video/audio 分开）。未加载时返回空列表。</summary>
    public static (IReadOnlyList<string> Video, IReadOnlyList<string> Audio) EnumerateEncoders()
    {
        var video = new List<string>();
        var audio = new List<string>();
        if (!TryLoad(out _)) return (video, audio);
        try
        {
            void* opaque = null;
            AVCodec* c;
            while ((c = ffmpeg.av_codec_iterate(&opaque)) != null)
            {
                if (ffmpeg.av_codec_is_encoder(c) == 0) continue;
                string name = PtrToString(c->name);
                if (name.Length == 0) continue;
                switch (c->type)
                {
                    case AVMediaType.AVMEDIA_TYPE_VIDEO: video.Add(name); break;
                    case AVMediaType.AVMEDIA_TYPE_AUDIO: audio.Add(name); break;
                }
            }
        }
        catch
        {
            // 枚举失败不致命：调用方回退到默认编码器名单
        }
        return (video, audio);
    }

    /// <summary>av_strerror 包装，失败时仅给出错误码。</summary>
    public static string GetErrorString(int errorCode)
    {
        const int BufSize = 512;
        byte* buf = stackalloc byte[BufSize];
        int ret = ffmpeg.av_strerror(errorCode, buf, (ulong)BufSize);
        if (ret < 0) return $"FFmpeg error {errorCode}";
        int len = 0;
        while (len < BufSize && buf[len] != 0) len++;
        return System.Text.Encoding.ASCII.GetString(buf, len);
    }

    /// <summary>带 FFmpeg 错误详情的异常（样式与 music_player 的转换器一致）。</summary>
    public static InvalidOperationException CreateException(int errorCode, string message)
        => new($"{message} (FFmpeg 错误 {errorCode}: {GetErrorString(errorCode)})");

    private static unsafe string PtrToString(byte* p)
    {
        if (p == null) return "";
        int len = 0;
        while (p[len] != 0) len++;
        return len == 0 ? "" : System.Text.Encoding.ASCII.GetString(p, len);
    }
}
