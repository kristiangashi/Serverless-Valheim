using System.IO.Compression;

namespace Helper;

/// <summary>
/// Packs/unpacks the files that make up one Valheim world. A world is
/// <c>&lt;name&gt;.db</c> + <c>&lt;name&gt;.fwl</c>, plus <c>.old</c> backups Valheim keeps.
/// </summary>
public static class WorldFiles
{
    // Extensions that belong to a world, in order of importance.
    private static readonly string[] Extensions = [".db", ".fwl", ".db.old", ".fwl.old"];

    public static IEnumerable<string> ExistingFiles(string worldsFolder, string worldName) =>
        Extensions
            .Select(ext => Path.Combine(worldsFolder, worldName + ext))
            .Where(File.Exists);

    /// <summary>True if at least the core .db and .fwl exist locally.</summary>
    public static bool WorldExistsLocally(string worldsFolder, string worldName) =>
        File.Exists(Path.Combine(worldsFolder, worldName + ".db")) &&
        File.Exists(Path.Combine(worldsFolder, worldName + ".fwl"));

    /// <summary>Zip the world's files into a temp archive and return its path. Throws if nothing to zip.</summary>
    public static string CreateZip(string worldsFolder, string worldName)
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
                var entry = archive.CreateEntry(Path.GetFileName(file), CompressionLevel.Optimal);
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
