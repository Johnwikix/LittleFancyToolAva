using System.Security.Cryptography;

namespace FancyToolAva.Services;

/// <summary>Coordinates model installation, session creation, and safe directory migration.</summary>
internal static class ModelStorage
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    internal static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static async Task<IDisposable> AcquireAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        return new Lease();
    }

    internal static IDisposable Acquire(CancellationToken ct)
    {
        Gate.Wait(ct);
        return new Lease();
    }

    private sealed class Lease : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) Gate.Release(); }
    }

    /// <summary>Copies and verifies files first; the caller commits settings before removing originals.</summary>
    internal static List<(string Source, string Target, byte[] Hash)> PrepareMigration(string source, string target)
    {
        source = AppPaths.ResolveModelsDirectory(source);
        target = AppPaths.ResolveModelsDirectory(target);
        Directory.CreateDirectory(target);
        using (var probe = new FileStream(Path.Combine(target, ".fancytool-write-" + Guid.NewGuid().ToString("N")),
                   FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
        var copied = new List<(string, string, byte[])>();
        if (string.Equals(source, target, PathComparison)) return copied;
        if (source.StartsWith(target + Path.DirectorySeparatorChar, PathComparison)
            || target.StartsWith(source + Path.DirectorySeparatorChar, PathComparison))
            throw new IOException("Model folders must not contain one another.");
        if (!Directory.Exists(source)) return copied;

        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
        foreach (string file in Directory.EnumerateFiles(source, "*", options))
        {
            if (file.EndsWith(".partial", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(file).Contains(".fancytool-", StringComparison.Ordinal)) continue;
            string destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] hash = SHA256.HashData(input);
            if (File.Exists(destination))
            {
                using var existing = File.OpenRead(destination);
                if (!hash.AsSpan().SequenceEqual(SHA256.HashData(existing)))
                    throw new IOException($"A different file already exists: {destination}");
            }
            else
            {
                string temporary = destination + ".fancytool-" + Guid.NewGuid().ToString("N");
                try
                {
                    input.Position = 0;
                    using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                    {
                        input.CopyTo(output);
                        output.Position = 0;
                        if (!hash.AsSpan().SequenceEqual(SHA256.HashData(output)))
                            throw new IOException($"Model copy verification failed: {destination}");
                    }
                    File.Move(temporary, destination);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            copied.Add((file, destination, hash));
        }
        return copied;
    }

    /// <summary>Removes verified originals after commit, retaining files still held by active native sessions.</summary>
    internal static void RemoveOriginals(List<(string Source, string Target, byte[] Hash)> copied)
    {
        foreach (var file in copied)
        {
            try
            {
                using (var source = File.OpenRead(file.Source))
                using (var target = File.OpenRead(file.Target))
                    if (!file.Hash.AsSpan().SequenceEqual(SHA256.HashData(source))
                        || !file.Hash.AsSpan().SequenceEqual(SHA256.HashData(target))) continue;
                File.Delete(file.Source);
            }
            catch (IOException ex) { Serilog.Log.Warning(ex, "Retaining model in previous folder: {Path}", file.Source); }
            catch (UnauthorizedAccessException ex) { Serilog.Log.Warning(ex, "Retaining model in previous folder: {Path}", file.Source); }
        }
    }
}
