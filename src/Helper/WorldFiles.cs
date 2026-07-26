using System.IO.Compression;

namespace Helper;

/// <summary>
/// Packs/unpacks the files that make up one Valheim world. A world is
/// <c>&lt;name&gt;.db</c> + <c>&lt;name&gt;.fwl</c>, plus <c>.old</c> backups Valheim keeps.
/// </summary>
public static class WorldFiles
{
    // The world itself. Valheim's own .old rollbacks are deliberately NOT uploaded: the coordinator
    // keeps its own version history (see the keep-versions admin setting), so shipping them doubled
    // every upload's CPU, disk reads and upstream bandwidth without adding any safety.
    private static readonly string[] Extensions = [".db", ".fwl"];

    public static IEnumerable<string> ExistingFiles(string worldsFolder, string worldName) =>
        Extensions
            .Select(ext => Path.Combine(worldsFolder, worldName + ext))
            .Where(File.Exists);

    /// <summary>
    /// Cheap fingerprint of the world on disk (name, length and write time of each file). Lets a
    /// caller skip an upload when Valheim hasn't written a new save since the last one.
    /// </summary>
    public static string Fingerprint(string worldsFolder, string worldName) =>
        string.Join('|', ExistingFiles(worldsFolder, worldName)
            .Select(path => new FileInfo(path))
            .Select(info => $"{info.Name}:{info.Length}:{info.LastWriteTimeUtc.Ticks}"));

    /// <summary>True if at least the core .db and .fwl exist locally.</summary>
    public static bool WorldExistsLocally(string worldsFolder, string worldName) =>
        File.Exists(Path.Combine(worldsFolder, worldName + ".db")) &&
        File.Exists(Path.Combine(worldsFolder, worldName + ".fwl"));

    /// <summary>
    /// Zip the world's files into a temp archive and return its path. Throws if nothing to zip.
    /// Auto-saves pass <see cref="CompressionLevel.Fastest"/> — the deflate cost is paid while the
    /// user is mid-game, and the extra ratio from Optimal isn't worth the CPU. The final upload on
    /// stop-hosting can afford Optimal.
    /// </summary>
    public static string CreateZip(
        string worldsFolder, string worldName, CompressionLevel compression = CompressionLevel.Optimal)
    {
        var files = ExistingFiles(worldsFolder, worldName).ToList();
        if (files.Count == 0)
            throw new InvalidOperationException(
                $"No files found for world \"{worldName}\" in {worldsFolder}.");

        var tmp = Path.Combine(Path.GetTempPath(), $"vwk-upload-{Guid.NewGuid():N}.zip");
        using (var archive = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            foreach (var file in files)
            {
                var entry = archive.CreateEntry(Path.GetFileName(file), compression);
                // Open shared so we can read even if Valheim still has the file handle open.
                using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var dst = entry.Open();
                src.CopyTo(dst);
            }
        }
        return tmp;
    }

    /// <summary>Extract a downloaded world archive into the worlds folder, overwriting existing files.</summary>
    public static void ExtractInto(string zipPath, string worldsFolder)
    {
        Directory.CreateDirectory(worldsFolder);
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // skip directory entries
            var dest = Path.Combine(worldsFolder, entry.Name);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }
}
