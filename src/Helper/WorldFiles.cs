using System.Buffers.Binary;
using System.IO.Compression;

namespace Helper;

/// <summary>
/// The files making up one world, plus where they live.
///
/// <para><see cref="Root"/> is the folder the archive's entries are relative to — the world folder
/// for a chunked save, the worlds folder itself for a legacy one. Archives are always flat: entry
/// names carry no folder prefix, so the world's folder name comes from each player's own settings
/// rather than travelling inside the zip where it could disagree with them.</para>
/// </summary>
public sealed record WorldSaveSet(
    WorldSaveFormat Format,
    string Root,
    IReadOnlyList<string> Files,
    string? Marker);

/// <summary>
/// Packs and unpacks the files that make up one Valheim world.
///
/// <para>Valheim used to keep a world as <c>&lt;name&gt;.db</c> plus <c>&lt;name&gt;.fwl</c>. It now
/// keeps it as a <c>&lt;name&gt;</c> folder holding the map split across <c>.chunk</c> files, a few
/// <c>_main.&lt;n&gt;.*</c> files describing that particular save, and some minimap caches. Both
/// layouts are handled here; <see cref="WorldFormat"/> decides which one is in play.</para>
/// </summary>
public static class WorldFiles
{
    private static readonly string[] LegacyExtensions = [".db", ".fwl"];

    /// <summary>
    /// Minimap caches are left out of uploads and left alone by downloads. They're derived —
    /// Valheim rebuilds them from the world, and they didn't change when the world was saved (their
    /// timestamps track loading, not saving). At ~4.5 MB they'd be a seventh of every upload, and
    /// re-sending one player's rendering cache to everybody buys nothing. Map *exploration* is
    /// character data, in the character file, not here.
    ///
    /// If it turns out something player-visible does live in these, deleting this line is the fix.
    /// </summary>
    private static bool IsDerivedCache(string fileName) =>
        fileName.StartsWith("cacheMinimap", StringComparison.OrdinalIgnoreCase);

    /// <summary>Files this app puts in the world folder, and so may remove from it again.</summary>
    private static bool IsManagedSaveFile(string fileName) =>
        fileName.EndsWith(".chunk", StringComparison.OrdinalIgnoreCase) ||
        fileName.StartsWith("_main.", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Find the world's current save, or null when there isn't a usable one.
    ///
    /// <para>For a chunked world this is the newest completed save — the one whose
    /// <c>_main.&lt;n&gt;.ok</c> marker exists, meaning Valheim finished writing every other file
    /// before stamping it.</para>
    /// </summary>
    public static WorldSaveSet? Locate(string worldsFolder, string worldName)
    {
        if (string.IsNullOrWhiteSpace(worldName)) return null;

        var folder = Path.Combine(worldsFolder, worldName);
        if (Directory.Exists(folder) && LocateChunked(folder) is { } chunked) return chunked;

        var legacy = LegacyExtensions
            .Select(ext => Path.Combine(worldsFolder, worldName + ext))
            .Where(File.Exists)
            .ToList();
        return legacy.Count > 0
            ? new WorldSaveSet(WorldSaveFormat.Legacy, worldsFolder, legacy, null)
            : null;
    }

    private static WorldSaveSet? LocateChunked(string folder)
    {
        if (WorldFormat.NewestCompletedSave(folder) is not { } marker) return null;

        // "_main.4.ok" -> "4". Everything describing this save shares that generation.
        var markerName = Path.GetFileName(marker);
        var generation = markerName["_main.".Length..^".ok".Length];
        if (generation.Length == 0) return null;

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, $"_main.{generation}.*").ToList();
        }
        catch { return null; }

        var index = Path.Combine(folder, $"_main.{generation}.chunks");
        files.AddRange(ChunkFiles(folder, index));
        return new WorldSaveSet(WorldSaveFormat.Chunked, folder, files, marker);
    }

    /// <summary>
    /// The map squares belonging to this save.
    ///
    /// <para>The index names them exactly, which matters because a square's file name changes when
    /// its contents do (<c>18_20__2_2.chunk</c> becomes <c>18_20__2_3.chunk</c>). A crash between
    /// writing the new one and deleting the old leaves both on disk, and only the index says which
    /// is current.</para>
    ///
    /// <para>If the index can't be read as expected — a future Valheim reshaping it, most likely —
    /// fall back to every <c>.chunk</c> in the folder. That risks carrying a superseded square
    /// along, which costs bandwidth but not correctness: Valheim loads what its own index names, so
    /// an extra file is inert.</para>
    /// </summary>
    private static IEnumerable<string> ChunkFiles(string folder, string indexPath)
    {
        if (ChunkNamesFromIndex(indexPath) is { } named)
        {
            var paths = named.Select(n => Path.Combine(folder, n)).ToList();
            if (paths.All(File.Exists)) return paths;
        }
        try { return Directory.EnumerateFiles(folder, "*.chunk").ToList(); }
        catch { return []; }
    }

    /// <summary>
    /// Parse <c>_main.&lt;n&gt;.chunks</c>. Ten-byte header ending in the square count, then one
    /// eleven-byte record per square: y, x, and the two numbers that make up the name's suffix.
    ///
    /// <para>Returns null if the file's length doesn't match that shape exactly — the layout is
    /// worked out from observation, not documentation, so a size that doesn't add up means the
    /// format moved and guessing would be worse than falling back.</para>
    /// </summary>
    private static List<string>? ChunkNamesFromIndex(string indexPath)
    {
        const int headerBytes = 10, recordBytes = 11;
        try
        {
            var bytes = File.ReadAllBytes(indexPath);
            if (bytes.Length < headerBytes) return null;

            var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(6, 4));
            if (count < 0 || headerBytes + (long)count * recordBytes != bytes.Length) return null;

            var names = new List<string>(count);
            for (var i = 0; i < count; i++)
            {
                var record = bytes.AsSpan(headerBytes + i * recordBytes, recordBytes);
                names.Add($"{record[1]:x2}_{record[0]:x2}__{record[2]}_{record[3]}.chunk");
            }
            return names;
        }
        catch { return null; }
    }

    /// <summary>True if there's a world on disk under this name that we could upload.</summary>
    public static bool WorldExistsLocally(string worldsFolder, string worldName) =>
        Locate(worldsFolder, worldName) is not null;

    /// <summary>
    /// Cheap fingerprint of the world on disk, so an upload can be skipped when Valheim hasn't
    /// written a new save since the last one. Includes the file count: a save that only removes a
    /// superseded map square would otherwise look unchanged.
    /// </summary>
    public static string Fingerprint(string worldsFolder, string worldName)
    {
        if (Locate(worldsFolder, worldName) is not { } set) return "";
        var parts = set.Files
            .Select(path => new FileInfo(path))
            .Where(info => info.Exists)
            .OrderBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
            .Select(info => $"{info.Name}:{info.Length}:{info.LastWriteTimeUtc.Ticks}");
        return $"{set.Files.Count}|{string.Join('|', parts)}";
    }

    /// <summary>
    /// Zip the world's files into a temp archive and return its path. Throws if there's nothing to
    /// zip, or if Valheim saved again while we were reading — a save that rolls underneath us would
    /// otherwise produce an archive holding half of one save and half of the next.
    ///
    /// <para>Auto-saves pass <see cref="CompressionLevel.Fastest"/>: the deflate cost is paid while
    /// the user is mid-game. The final upload on stop-hosting can afford Optimal.</para>
    /// </summary>
    public static string CreateZip(
        string worldsFolder, string worldName, CompressionLevel compression = CompressionLevel.Optimal)
    {
        if (Locate(worldsFolder, worldName) is not { } set || set.Files.Count == 0)
            throw new InvalidOperationException(
                $"No files found for world \"{worldName}\" in {worldsFolder}.");

        var before = SaveStamp(set);
        var tmp = Path.Combine(Path.GetTempPath(), $"vwk-upload-{Guid.NewGuid():N}.zip");
        try
        {
            using (var archive = ZipFile.Open(tmp, ZipArchiveMode.Create))
            {
                foreach (var file in set.Files)
                {
                    var name = Path.GetFileName(file);
                    if (IsDerivedCache(name)) continue;
                    var entry = archive.CreateEntry(name, compression);
                    // Open shared: Valheim may still hold a handle on these.
                    using var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var dst = entry.Open();
                    src.CopyTo(dst);
                }
            }
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }

        if (SaveStamp(set) != before)
        {
            try { File.Delete(tmp); } catch { }
            throw new InvalidOperationException(
                "Valheim saved the world again while it was being packed. Nothing was uploaded; " +
                "the next save will be picked up.");
        }
        return tmp;
    }

    /// <summary>
    /// Identifies which save we're looking at. The completion marker is rewritten under a new name
    /// on every save, so if this changes mid-pack the world moved underneath us.
    /// </summary>
    private static string SaveStamp(WorldSaveSet set)
    {
        if (set.Marker is null) return "";
        try { return $"{set.Marker}:{File.GetLastWriteTimeUtc(set.Marker).Ticks}"; }
        catch { return "gone"; }
    }

    /// <summary>
    /// Unpack a downloaded world archive, replacing whatever is there.
    ///
    /// <para>For a chunked world this mirrors rather than merges: files the archive doesn't contain
    /// are deleted. Overwriting alone isn't enough, because a map square arrives under a new name
    /// when its contents change — merging would leave the previous one behind, and two files
    /// claiming one square is exactly the state we can't reason about. Only <c>.chunk</c> and
    /// <c>_main.*</c> files are removed; a player's minimap caches are theirs and stay.</para>
    /// </summary>
    public static void ExtractInto(string zipPath, string worldsFolder, string worldName)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var chunked = archive.Entries.Any(e =>
            Path.GetFileName(e.FullName).StartsWith("_main.", StringComparison.OrdinalIgnoreCase));

        var target = chunked ? Path.Combine(worldsFolder, worldName) : worldsFolder;
        Directory.CreateDirectory(target);

        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            // Entry names are reduced to their file name, so an archive can't write outside the
            // folder we chose no matter what paths it claims.
            var name = Path.GetFileName(entry.FullName);
            if (name.Length == 0) continue; // directory entry
            entry.ExtractToFile(Path.Combine(target, name), overwrite: true);
            written.Add(name);
        }

        if (chunked) RemoveSupersededFiles(target, written);
    }

    private static void RemoveSupersededFiles(string folder, HashSet<string> keep)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(path);
                if (keep.Contains(name) || !IsManagedSaveFile(name)) continue;
                try { File.Delete(path); } catch { /* best effort; a leftover is inert */ }
            }
        }
        catch { /* folder vanished under us — the extract already succeeded */ }
    }
}
