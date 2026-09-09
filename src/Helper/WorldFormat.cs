using System.Buffers.Binary;
using System.IO.Compression;

namespace Helper;

/// <summary>How a Valheim world is laid out.</summary>
public enum WorldSaveFormat
{
    /// <summary>Nothing found under that world name.</summary>
    None,

    /// <summary>Pre-2026 Valheim: a single <c>&lt;name&gt;.db</c> beside a <c>&lt;name&gt;.fwl</c>.</summary>
    Legacy,

    /// <summary>Current Valheim: a <c>&lt;name&gt;</c> folder holding the map split across .chunk files.</summary>
    Chunked,

    /// <summary>Both layouts present under one name — the state this whole class exists to prevent.</summary>
    Mixed,
}

/// <summary>
/// What was found for one world, plus the save-format version Valheim stamped it with (41 at the
/// time of writing). The version is null when it couldn't be read — an incomplete save, or a legacy
/// world, which carries no such stamp.
/// </summary>
public readonly record struct WorldFormatInfo(WorldSaveFormat Format, int? SaveVersion)
{
    public static readonly WorldFormatInfo None = new(WorldSaveFormat.None, null);
}

/// <summary>
/// Identifies which save layout a world uses, on disk or inside a downloaded archive, and refuses
/// the combinations that would corrupt it.
///
/// This matters because the two layouts occupy different paths — <c>ArdaCoop.db</c> versus
/// <c>ArdaCoop/</c> — so unpacking one over the other doesn't overwrite anything. It leaves both
/// sitting there, and Valheim is then looking at two different worlds under one name.
/// </summary>
public static class WorldFormat
{
    /// <summary>
    /// Valheim writes <c>_main.&lt;n&gt;.ok</c> once every other file in the save is safely on disk,
    /// so its presence is what separates a finished save from a half-written one. The file is four
    /// bytes: the save format version.
    /// </summary>
    private static bool IsSaveMarker(string fileName) =>
        fileName.StartsWith("_main.", StringComparison.OrdinalIgnoreCase) &&
        fileName.EndsWith(".ok", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The newest completed save in a world folder, or null if none is complete.
    ///
    /// Normally there is exactly one — the game deletes the previous generation as it writes the
    /// next. If a crash left more than one behind, prefer the most recently written rather than the
    /// highest number: nothing documents how that number advances (we have watched it jump 2 to 4
    /// in one session, and a neighbouring field in the same format count *down*), so ordering by it
    /// would be a guess.
    /// </summary>
    private static string? NewestCompletedSave(string worldFolder)
    {
        try
        {
            return Directory.EnumerateFiles(worldFolder, "_main.*.ok")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private static int? ReadSaveVersion(Stream marker)
    {
        Span<byte> buffer = stackalloc byte[4];
        return marker.ReadAtLeast(buffer, 4, throwOnEndOfStream: false) == 4
            ? BinaryPrimitives.ReadInt32LittleEndian(buffer)
            : null;
    }

    /// <summary>What the named world looks like in the player's Valheim folder.</summary>
    public static WorldFormatInfo DetectLocal(string worldsFolder, string worldName)
    {
        var folder = Path.Combine(worldsFolder, worldName);
        var hasFolder = Directory.Exists(folder);
        var hasLegacy = File.Exists(Path.Combine(worldsFolder, worldName + ".db"));

        if (hasFolder && hasLegacy) return new(WorldSaveFormat.Mixed, null);
        if (hasLegacy) return new(WorldSaveFormat.Legacy, null);
        if (!hasFolder) return WorldFormatInfo.None;

        // A folder with no completed save in it is still unmistakably the new layout — we just
        // can't say which version wrote it.
        if (NewestCompletedSave(folder) is not { } marker)
            return new(WorldSaveFormat.Chunked, null);

        try
        {
            using var stream = File.OpenRead(marker);
            return new(WorldSaveFormat.Chunked, ReadSaveVersion(stream));
        }
        catch { return new(WorldSaveFormat.Chunked, null); }
    }

    /// <summary>What a downloaded world archive contains.</summary>
    public static WorldFormatInfo DetectArchive(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        ZipArchiveEntry? marker = null;
        var hasLegacy = false;

        foreach (var entry in archive.Entries)
        {
            // Entries may or may not carry a folder prefix depending on which build packed them,
            // so match on the file name alone.
            var name = Path.GetFileName(entry.FullName);
            if (name.Length == 0) continue; // directory entry
            if (IsSaveMarker(name))
            {
                if (marker is null || entry.LastWriteTime > marker.LastWriteTime) marker = entry;
            }
            else if (name.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            {
                hasLegacy = true;
            }
        }

        if (marker is not null && hasLegacy) return new(WorldSaveFormat.Mixed, null);
        if (hasLegacy) return new(WorldSaveFormat.Legacy, null);
        if (marker is null) return WorldFormatInfo.None;

        try
        {
            using var stream = marker.Open();
            return new(WorldSaveFormat.Chunked, ReadSaveVersion(stream));
        }
        catch { return new(WorldSaveFormat.Chunked, null); }
    }

    /// <summary>
    /// Whether <paramref name="incoming"/> can be unpacked over <paramref name="local"/>. Null when
    /// it can; otherwise a message to show the player.
    /// </summary>
    public static string? DescribeIncompatibility(
        WorldFormatInfo local, WorldFormatInfo incoming, string worldName)
    {
        if (incoming.Format is WorldSaveFormat.None)
            return "The world downloaded from the server doesn't contain any Valheim save files. " +
                   "Nothing was written to your Valheim folder.";

        if (incoming.Format is WorldSaveFormat.Mixed)
            return "The world on the server contains both an old-style and a new-style save. " +
                   "Nothing was written to your Valheim folder — tell whoever uploaded it.";

        if (local.Format is WorldSaveFormat.Mixed)
            return $"Your Valheim folder has both a \"{worldName}\" folder and an old-style " +
                   $"{worldName}.db file. Valheim can't tell which is the real world, and nor can " +
                   "this app. Move the one you don't want somewhere else, then try again.";

        // A player with nothing under that name has nothing to conflict with.
        if (local.Format is WorldSaveFormat.None) return null;

        if (local.Format != incoming.Format)
        {
            return $"The world on the server is {Describe(incoming.Format)}, but your " +
                   $"\"{worldName}\" is {Describe(local.Format)}. Unpacking one over the other " +
                   "would leave you with two different worlds under one name. Nothing was written " +
                   "to your Valheim folder. " +
                   (incoming.Format is WorldSaveFormat.Legacy
                       ? "The server's copy predates Valheim's new save format and needs to be " +
                         "replaced by someone running the current game."
                       : "Update Valheim, load the world once so it converts, then try again.");
        }

        // Same layout, but written by different Valheim versions. Letting this through is how one
        // player who hasn't updated corrupts the world for everybody.
        if (local.SaveVersion is { } mine && incoming.SaveVersion is { } theirs && mine != theirs)
        {
            return "The world on the server was saved by a different version of Valheim " +
                   $"(save format v{theirs}; yours is v{mine}). Nothing was written to your " +
                   "Valheim folder. Whoever is furthest behind should update the game.";
        }

        return null;
    }

    private static string Describe(WorldSaveFormat format) => format switch
    {
        WorldSaveFormat.Legacy => "an old-style single-file save",
        WorldSaveFormat.Chunked => "a new-style folder save",
        WorldSaveFormat.Mixed => "both formats at once",
        _ => "empty",
    };
}
