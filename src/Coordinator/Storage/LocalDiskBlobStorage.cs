using System.Text.RegularExpressions;

namespace Coordinator.Storage;

/// <summary>
/// Stores world archives on the server's local disk. Fine for local dev and proving
/// the protocol, but Railway's disk is ephemeral — production should use R2.
/// </summary>
public sealed partial class LocalDiskBlobStorage : IBlobStorage
{
    private readonly string _dir;

    public LocalDiskBlobStorage(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(_dir);
    }

    private string PathFor(int version) => Path.Combine(_dir, $"world-v{version}.zip");

    public async Task SaveAsync(int version, Stream content, CancellationToken ct = default)
    {
        var tmp = PathFor(version) + ".tmp";
        await using (var fs = File.Create(tmp))
        {
            await content.CopyToAsync(fs, ct);
        }
        // Atomic-ish swap so a crash mid-write never leaves a corrupt archive at the real path.
        File.Move(tmp, PathFor(version), overwrite: true);
    }

    public Task<Stream?> OpenAsync(int version, CancellationToken ct = default)
    {
        var path = PathFor(version);
        Stream? s = File.Exists(path) ? File.OpenRead(path) : null;
        return Task.FromResult(s);
    }

    public Task PruneAsync(int currentVersion, int keep, CancellationToken ct = default)
    {
        foreach (var file in Directory.EnumerateFiles(_dir, "world-v*.zip"))
        {
            var m = VersionRegex().Match(Path.GetFileName(file));
            if (m.Success && int.TryParse(m.Groups[1].Value, out var v) && v <= currentVersion - keep)
            {
                try { File.Delete(file); } catch { /* best effort */ }
            }
        }
        return Task.CompletedTask;
    }

    public Task<int> GetLatestVersionAsync(CancellationToken ct = default)
    {
        var latest = 0;
        foreach (var file in Directory.EnumerateFiles(_dir, "world-v*.zip"))
        {
            var m = VersionRegex().Match(Path.GetFileName(file));
            if (m.Success && int.TryParse(m.Groups[1].Value, out var v) && v > latest) latest = v;
        }
        return Task.FromResult(latest);
    }

    public Task DeleteAllAsync(CancellationToken ct = default)
    {
        foreach (var file in Directory.EnumerateFiles(_dir, "world-v*.zip"))
        {
            try { File.Delete(file); } catch { /* best effort */ }
        }
        return Task.CompletedTask;
    }

    [GeneratedRegex(@"^world-v(\d+)\.zip$")]
    private static partial Regex VersionRegex();
}
