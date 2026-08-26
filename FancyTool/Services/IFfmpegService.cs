namespace FancyToolAva.Services;

public interface IFfmpegService : IDisposable
{
    bool IsAvailable { get; }

    string? ResolvedDirectory { get; }

    string? LastError { get; }

    IReadOnlyList<string> AvailableVideoEncoders { get; }

    IReadOnlyList<string> AvailableAudioEncoders { get; }

    Task<bool> ValidateAsync(CancellationToken ct = default);

    bool ValidateEncoder(string encoderName);

    event EventHandler<bool>? AvailabilityChanged;
}
