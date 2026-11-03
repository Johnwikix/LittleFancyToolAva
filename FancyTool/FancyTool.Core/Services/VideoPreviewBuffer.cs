using System.Buffers;
using FancyToolAva.Models;

namespace FancyToolAva.Services;

/// <summary>Accepts preview pixels synchronously without retaining the producer's reusable buffer.</summary>
internal interface IVideoPreviewSink : IProgress<VideoPreviewFrame>
{
    /// <summary>Gets whether this producer currently owns the visible preview.</summary>
    bool WantsFrame { get; }
    /// <summary>Copies one frame before the producer reuses its pixels.</summary>
    void Publish(ReadOnlySpan<byte> pixels, int width, int height, double positionSeconds);
}

/// <summary>Owns one pooled preview until the UI finishes copying it to its bitmap.</summary>
internal sealed class BufferedVideoPreview : IDisposable
{
    private readonly ArrayPool<byte> _pool;
    private byte[]? _pixels;
    internal int Width { get; }
    internal int Height { get; }
    internal double PositionSeconds { get; }
    internal ReadOnlySpan<byte> Pixels => (_pixels ?? throw new ObjectDisposedException(nameof(BufferedVideoPreview))).AsSpan(0, checked(Width * Height * 4));

    internal BufferedVideoPreview(ReadOnlySpan<byte> pixels, int width, int height, double positionSeconds, ArrayPool<byte> pool)
    {
        Width = width;
        Height = height;
        PositionSeconds = positionSeconds;
        _pool = pool;
        _pixels = pool.Rent(checked(width * height * 4));
        pixels[..(width * height * 4)].CopyTo(_pixels);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var pixels = Interlocked.Exchange(ref _pixels, null);
        if (pixels != null) _pool.Return(pixels);
    }
}

/// <summary>Keeps only the latest preview, returning superseded pixels instead of queueing UI work.</summary>
internal sealed class VideoPreviewBuffer(ArrayPool<byte>? pool = null) : IDisposable
{
    private readonly object _gate = new();
    private readonly ArrayPool<byte> _pool = pool ?? ArrayPool<byte>.Shared;
    private BufferedVideoPreview? _latest;
    private bool _disposed;

    internal void Publish(ReadOnlySpan<byte> pixels, int width, int height, double positionSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (pixels.Length < checked(width * height * 4)) throw new ArgumentException("Incomplete preview pixels", nameof(pixels));
        lock (_gate)
        {
            if (_disposed) return;
            var frame = new BufferedVideoPreview(pixels, width, height, positionSeconds, _pool);
            var previous = _latest;
            _latest = frame;
            previous?.Dispose();
        }
    }

    internal BufferedVideoPreview? TakeLatest()
    {
        lock (_gate)
        {
            var frame = _latest;
            _latest = null;
            return frame;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _latest?.Dispose();
            _latest = null;
        }
    }
}
