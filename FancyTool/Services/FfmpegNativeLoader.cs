using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;

namespace FancyToolAva.Services;

/// <summary>
/// Centralizes FFmpeg.AutoGen 9.0 native library loading for BYO directory.
/// Uses DynamicallyLoaded bindings via ffmpeg.RootPath.
/// Thread-safe: Initialize is idempotent per directory.
/// </summary>
public static unsafe class FfmpegNativeLoader
{
    private static readonly object _lock = new();
    private static string? _loadedDir;
    private static bool _initialized;
    private static string? _versionInfo;
    private static int _avCodecVersionMajor;

    public static bool IsInitialized
    {
        get { lock (_lock) return _initialized; }
    }

    public static string? LoadedDirectory
    {
        get { lock (_lock) return _loadedDir; }
    }

    public static string? VersionInfo
    {
        get { lock (_lock) return _versionInfo; }
    }

    public static bool TryInitialize(string dir, ILogger? logger, out string? error)
    {
        lock (_lock)
        {
            if (_initialized && string.Equals(_loadedDir, dir, StringComparison.OrdinalIgnoreCase))
            {
                error = null;
                return true;
            }
            if (_initialized && !string.Equals(_loadedDir, dir, StringComparison.OrdinalIgnoreCase))
            {
                logger?.LogWarning("FFmpeg native already initialized from {OldDir}, switching to {NewDir} requires app restart; attempting anyway", _loadedDir, dir);
            }
        }

        // Ensure OS loader can find dependencies (avutil-61.dll etc. are dependencies of avcodec-63.dll)
        try
        {
            string curPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!curPath.Split(Path.PathSeparator).Contains(dir, StringComparer.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + curPath);
            if (!OperatingSystem.IsWindows())
            {
                string ld = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH") ?? "";
                if (!ld.Split(Path.PathSeparator).Contains(dir))
                    Environment.SetEnvironmentVariable("LD_LIBRARY_PATH", dir + Path.PathSeparator + ld);
            }
        }
        catch { }

        try
        {
            // 9.0: prefer DynamicallyLoadedBindings.LibrariesPath (single-package ffmpeg.RootPath is deprecated and throws PlatformNotSupported on net10)
            try
            {
                FFmpeg.AutoGen.Bindings.DynamicallyLoaded.DynamicallyLoadedBindings.LibrariesPath = dir;
                try { FFmpeg.AutoGen.Bindings.DynamicallyLoaded.DynamicallyLoadedBindings.Initialize(); } catch { }
            }
            catch (Exception ex2)
            {
                logger?.LogWarning(ex2, "DynamicallyLoadedBindings.LibrariesPath set failed, fallback to ffmpeg.RootPath at {Dir}", dir);
            }

            try { ffmpeg.RootPath = dir; } catch (Exception ex3) { logger?.LogDebug(ex3, "ffmpeg.RootPath set failed at {Dir} (expected on 9.0 split package)", dir); }

            // Pre-load check: ensure required DLLs exist (wildcard already validated, but verify file can be opened)
            // This helps surface "Specified method is not supported" vs actual DllNotFound
            string? ver = GetVersionInfoSafe(out string? verError);
            if (ver == null)
            {
                error = verError ?? "FFmpeg av_version_info returned null";
                logger?.LogWarning("FFmpeg av_version_info failed at {Dir}: {Err} | full={Full}", dir, error, verError);
                return false;
            }

            uint avcodecVer = ffmpeg.avcodec_version();
            int major = (int)(avcodecVer >> 16);
            uint avutilVer = ffmpeg.avutil_version();
            int avutilMajor = (int)(avutilVer >> 16);

            if (major == 0)
            {
                error = "FFmpeg avcodec_version returned 0";
                return false;
            }

            lock (_lock)
            {
                _loadedDir = dir;
                _initialized = true;
                _versionInfo = ver;
                _avCodecVersionMajor = major;
            }

            logger?.LogInformation("FFmpeg native loaded from {Dir}: version={Ver} avcodec={Major} avutil={AvUtilMajor}", dir, ver, major, avutilMajor);
            error = null;
            return true;
        }
        catch (DllNotFoundException ex)
        {
            error = $"Native library not found: {ex.Message} | {ex.GetType().Name}: {ex.ToString()}";
            logger?.LogWarning(ex, "FFmpeg native DllNotFound at {Dir}", dir);
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            error = $"Entry point not found: {ex.Message} | {ex.ToString()}";
            logger?.LogWarning(ex, "FFmpeg native EntryPointNotFound at {Dir}", dir);
            return false;
        }
        catch (PlatformNotSupportedException ex)
        {
            error = $"Platform not supported: {ex.Message} | {ex.ToString()}";
            logger?.LogWarning(ex, "FFmpeg native PlatformNotSupported at {Dir} (likely RootPath API mismatch, tried DynamicallyLoadedBindings)", dir);
            return false;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message} | {ex.ToString()}";
            logger?.LogWarning(ex, "FFmpeg native init failed at {Dir}", dir);
            return false;
        }
    }

    private static string? GetVersionInfoSafe(out string? error)
    {
        try
        {
            string? s = ffmpeg.av_version_info();
            if (string.IsNullOrEmpty(s)) { error = "av_version_info returned null/empty"; return null; }
            error = null;
            return s;
        }
        catch (Exception ex)
        {
            error = $"{ex.GetType().Name}: {ex.Message} | {ex.ToString()}";
            return null;
        }
    }

    public static string GetErrorString(int errNum)
    {
        try
        {
            const int bufSize = 1024;
            byte* buffer = stackalloc byte[bufSize];
            ffmpeg.av_strerror(errNum, buffer, (ulong)bufSize);
            string? s = Marshal.PtrToStringAnsi((IntPtr)buffer);
            return string.IsNullOrWhiteSpace(s) ? $"AVERROR {errNum}" : s!;
        }
        catch
        {
            return $"AVERROR {errNum}";
        }
    }

    public static bool IsAvError(int ret) => ret < 0;

    public static (IReadOnlyList<string> Video, IReadOnlyList<string> Audio) EnumerateEncoders()
    {
        var video = new List<string>();
        var audio = new List<string>();
        void* opaque = null;
        AVCodec* codec;
        while ((codec = ffmpeg.av_codec_iterate(&opaque)) != null)
        {
            if (ffmpeg.av_codec_is_encoder(codec) == 0) continue;
            string? name = Marshal.PtrToStringAnsi((IntPtr)codec->name);
            if (string.IsNullOrEmpty(name)) continue;
            if (codec->type == AVMediaType.AVMEDIA_TYPE_VIDEO) video.Add(name!);
            else if (codec->type == AVMediaType.AVMEDIA_TYPE_AUDIO) audio.Add(name!);
        }
        return (video.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), audio.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }
}
