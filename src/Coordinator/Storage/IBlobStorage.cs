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
    /// Highest world version present in storage (0 if none) and when it was last written. Used at
    /// startup to recover the version + save date after a restart (e.g. ephemeral disk wipes state).
    /// </summary>
    Task<(int Version, DateTimeOffset? UpdatedAt)> GetLatestAsync(CancellationToken ct = default);

    /// <summary>Delete every world archive. Used by the admin reset / start-a-new-world flow.</summary>
    Task DeleteAllAsync(CancellationToken ct = default);
}

/// <summary>
/// Implemented by stores that can hand out short-lived URLs letting a client read or write a blob
/// directly, without the bytes passing through this service.
///
/// World archives are the only large thing this app moves (tens of MB, re-sent on every in-game
/// save), and proxying them costs the coordinator's host outbound bandwidth on the storage-facing
/// hop of every transfer. Handing out a URL instead keeps that traffic entirely between the client
/// and the object store, leaving only small JSON on the coordinator.
///
/// Not every store can do this — <see cref="LocalDiskBlobStorage"/> has nowhere to point a client —
/// so callers must check for this interface and fall back to proxying.
/// </summary>
public interface IPresignedBlobStorage
{
    /// <summary>
    /// URL the client should PUT an archive to. The upload lands on a staging key identified by
    /// <paramref name="uploadId"/>, deliberately *not* a version key: a direct upload can be
    /// abandoned by a client that dies mid-transfer, and anything sitting at a version key is
    /// treated as canonical world data by the boot-time scan in <c>ReconcileWithStorage</c>.
    /// Staging keeps an uncommitted upload invisible until <see cref="PromoteAsync"/> accepts it.
    /// </summary>
    Task<string> PresignPutAsync(string uploadId, TimeSpan lifetime, CancellationToken ct = default);

    /// <summary>
    /// Move a staged upload onto <paramref name="version"/>'s key, making it the world. Implementations
    /// must do this inside the store (a server-side copy) — routing the bytes back through this
    /// service would undo the entire point of presigning. Throws if the staged upload is gone.
    /// </summary>
    Task PromoteAsync(string uploadId, int version, CancellationToken ct = default);

    /// <summary>URL the client should GET the archive for <paramref name="version"/> from.</summary>
    Task<string> PresignGetAsync(int version, TimeSpan lifetime, CancellationToken ct = default);

    /// <summary>
    /// Content-Type the PUT must send. It's part of the signature, so a client that sends anything
    /// else gets a 403 from the store — the value has to travel to the client alongside the URL.
    /// </summary>
    string PutContentType { get; }
}
