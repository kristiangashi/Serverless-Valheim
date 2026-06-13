namespace Coordinator.Storage;

/// <summary>
/// Abstraction over where the canonical world archive lives.
/// Phase 0 uses <see cref="LocalDiskBlobStorage"/>; Phase 1+ swaps in a
/// Cloudflare R2 implementation without touching the rest of the app.
/// </summary>
public interface IBlobStorage
{
    /// <summary>Store the world archive for a given version. Overwrites if it already exists.</summary>
    Task SaveAsync(int version, Stream content, CancellationToken ct = default);

    /// <summary>Open the world archive for a version, or null if it doesn't exist.</summary>
    Task<Stream?> OpenAsync(int version, CancellationToken ct = default);

    /// <summary>Delete archives older than the most recent <paramref name="keep"/> versions.</summary>
    Task PruneAsync(int currentVersion, int keep, CancellationToken ct = default);

    /// <summary>
    /// Highest world version present in storage, or 0 if none. Used at startup to recover the
    /// version after a coordinator restart (e.g. Railway's ephemeral disk wipes local state).
    /// </summary>
    Task<int> GetLatestVersionAsync(CancellationToken ct = default);

    /// <summary>Delete every world archive. Used by the admin reset / start-a-new-world flow.</summary>
    Task DeleteAllAsync(CancellationToken ct = default);
}
