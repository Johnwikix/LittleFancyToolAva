namespace FancyToolAva.Services;

/// <summary>
/// 进程内 FFmpeg（shared DLL，随应用分发）可用性探针。
/// </summary>
public interface IFfmpegService : IDisposable
{
    bool IsAvailable { get; }

    string? LastError { get; }

    IReadOnlyList<string> AvailableVideoEncoders { get; }

    IReadOnlyList<string> AvailableAudioEncoders { get; }

    string? VersionInfo { get; }

    Task<bool> ValidateAsync(CancellationToken ct = default);

    bool ValidateEncoder(string encoderName);

    event EventHandler<bool>? AvailabilityChanged;
}
