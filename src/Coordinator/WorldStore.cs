using System.Security.Cryptography;
using System.Text.Json;
using Coordinator.Storage;

namespace Coordinator;

/// <summary>Outcome of a state-changing operation, so endpoints can map to HTTP codes.</summary>
public enum OpResult { Ok, Conflict, Forbidden, BadVersion, NotFound }

/// <summary>
/// Thread-safe owner of the world lock + version protocol. All mutations go through a single
/// lock so claim/upload/release can never race. State is persisted to JSON after every change
/// so a coordinator restart keeps the lock and version intact.
/// </summary>
public sealed class WorldStore
{
    private readonly object _gate = new();
    private readonly string _statePath;
    private readonly IBlobStorage _blobs;
    private readonly TimeSpan _leaseDuration;
    private readonly int _keepVersions;
    private WorldState _state;

    public WorldStore(IBlobStorage blobs, string dataDir, TimeSpan leaseDuration, int keepVersions)
    {
        _blobs = blobs;
        _statePath = Path.Combine(dataDir, "state.json");
        _leaseDuration = leaseDuration;
        _keepVersions = keepVersions;
        Directory.CreateDirectory(dataDir);
        _state = Load();
        ReconcileWithStorage();
    }

    // If the blob store holds a newer version than our local state (e.g. local state was wiped by
    // an ephemeral-disk redeploy while R2 kept the world), adopt it. This guarantees we never lose
    // the world or accept an upload that would overwrite newer data. The lock itself resets to free
    // on restart, which is safe — any prior lease would have expired across a redeploy anyway.
    private void ReconcileWithStorage()
    {
        try
        {
            var (latest, updatedAt) = _blobs.GetLatestAsync().GetAwaiter().GetResult();
            if (latest > 0)
            {
                _state.HasWorld = true;
                if (latest >= _state.Version)
                {
                    _state.Version = latest;
                    if (updatedAt is not null) _state.LastUpdatedAt = updatedAt; // recover save date after a redeploy
                }
                Persist();
            }
        }
        catch { /* storage may be unreachable at boot; fall back to local state */ }
    }

    private WorldState Load()
    {
        if (!File.Exists(_statePath)) return new WorldState();
        try { return JsonSerializer.Deserialize<WorldState>(File.ReadAllText(_statePath)) ?? new WorldState(); }
        catch { return new WorldState(); }
    }

    // Caller must hold _gate.
    private void Persist()
    {
        var tmp = _statePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_state));
        File.Move(tmp, _statePath, overwrite: true);
    }

    // Caller must hold _gate. Clears an expired lease so the world reads as free.
    private void ExpireIfStale()
    {
        if (_state.LockToken is not null && _state.LeaseExpiresAt is { } exp && exp <= DateTimeOffset.UtcNow)
        {
            _state.LockToken = null;
            _state.HostName = null;
            _state.JoinCode = null;
            _state.LeaseExpiresAt = null;
            Persist();
        }
    }

    private static bool IsLocked(WorldState s) =>
        s.LockToken is not null && s.LeaseExpiresAt is { } exp && exp > DateTimeOffset.UtcNow;

    public PublicState GetPublicState()
    {
        lock (_gate)
        {
            ExpireIfStale();
            var locked = IsLocked(_state);
            int? secs = locked && _state.LeaseExpiresAt is { } e
                ? (int)Math.Max(0, (e - DateTimeOffset.UtcNow).TotalSeconds)
                : null;
            return new PublicState(_state.Version, _state.HasWorld, locked,
                _state.HostName, _state.JoinCode, _state.LeaseExpiresAt, secs, _state.LastUpdatedAt);
        }
    }

    public int CurrentVersion { get { lock (_gate) return _state.Version; } }

    /// <summary>Atomically acquire the lock if the world is free. Returns a token on success.</summary>
    public (OpResult Result, string? Token, int Version) Claim(string displayName)
    {
        lock (_gate)
        {
            ExpireIfStale();
            if (IsLocked(_state)) return (OpResult.Conflict, null, _state.Version);

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            _state.LockToken = token;
            _state.HostName = displayName;
            _state.JoinCode = null;
            _state.LeaseExpiresAt = DateTimeOffset.UtcNow + _leaseDuration;
            Persist();
            return (OpResult.Ok, token, _state.Version);
        }
    }

    /// <summary>Renew the lease for the current holder.</summary>
    public OpResult Heartbeat(string token)
    {
        lock (_gate)
        {
            ExpireIfStale();
            if (!IsLocked(_state)) return OpResult.NotFound;
            if (_state.LockToken != token) return OpResult.Forbidden;
            _state.LeaseExpiresAt = DateTimeOffset.UtcNow + _leaseDuration;
            Persist();
            return OpResult.Ok;
        }
    }

    /// <summary>Set the Valheim join code so others can connect. Also renews the lease.</summary>
    public OpResult SetJoinCode(string token, string joinCode)
    {
        lock (_gate)
        {
            ExpireIfStale();
            if (!IsLocked(_state)) return OpResult.NotFound;
            if (_state.LockToken != token) return OpResult.Forbidden;
            _state.JoinCode = joinCode;
            _state.LeaseExpiresAt = DateTimeOffset.UtcNow + _leaseDuration;
            Persist();
            return OpResult.Ok;
        }
    }

    /// <summary>
    /// Validate the holder + version before accepting an upload. The actual blob write happens
    /// outside the gate (it can be slow / large); call <see cref="CommitUpload"/> afterwards.
    /// </summary>
    public (OpResult Result, int NextVersion) BeginUpload(string token, int? baseVersion)
    {
        lock (_gate)
        {
            ExpireIfStale();
            if (!IsLocked(_state)) return (OpResult.NotFound, _state.Version);
            if (_state.LockToken != token) return (OpResult.Forbidden, _state.Version);
            // Defensive: the single-writer lock should already prevent this, but reject a client
            // that downloaded an older version than what the server now holds.
            if (baseVersion is { } b && b != _state.Version) return (OpResult.BadVersion, _state.Version);
            return (OpResult.Ok, _state.Version + 1);
        }
    }

    /// <summary>Bump the version after a blob was written, optionally releasing the lock.</summary>
    public OpResult CommitUpload(string token, int newVersion, bool release)
    {
        lock (_gate)
        {
            ExpireIfStale();
            if (!IsLocked(_state)) return OpResult.NotFound;
            if (_state.LockToken != token) return OpResult.Forbidden;
            if (newVersion != _state.Version + 1) return OpResult.BadVersion;

            _state.Version = newVersion;
            _state.HasWorld = true;
            _state.LastUpdatedAt = DateTimeOffset.UtcNow;
            if (release)
            {
                _state.LockToken = null;
                _state.HostName = null;
                _state.JoinCode = null;
                _state.LeaseExpiresAt = null;
            }
            else
            {
                _state.LeaseExpiresAt = DateTimeOffset.UtcNow + _leaseDuration;
            }
            Persist();
            return OpResult.Ok;
        }
    }

    /// <summary>Release the lock held by <paramref name="token"/>.</summary>
    public OpResult Release(string token)
    {
        lock (_gate)
        {
            ExpireIfStale();
            if (!IsLocked(_state)) return OpResult.NotFound;
            if (_state.LockToken != token) return OpResult.Forbidden;
            _state.LockToken = null;
            _state.HostName = null;
            _state.JoinCode = null;
            _state.LeaseExpiresAt = null;
            Persist();
            return OpResult.Ok;
        }
    }

    /// <summary>Admin break-glass: clear the lock regardless of who holds it.</summary>
    public void ForceRelease()
    {
        lock (_gate)
        {
            _state.LockToken = null;
            _state.HostName = null;
            _state.JoinCode = null;
            _state.LeaseExpiresAt = null;
            Persist();
        }
    }

    /// <summary>
    /// Admin "start a brand-new world": purge every stored archive and reset to version 0.
    /// Deletes blobs first so a crash mid-reset can't leave state pointing at gone data; the
    /// reverse order could orphan blobs but never lose a referenced one.
    /// </summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        await _blobs.DeleteAllAsync(ct);
        lock (_gate)
        {
            _state = new WorldState();
            Persist();
        }
    }

    public Task SaveBlobAsync(int version, Stream content, CancellationToken ct) =>
        _blobs.SaveAsync(version, content, ct);

    public Task PruneBlobsAsync(CancellationToken ct) =>
        _blobs.PruneAsync(CurrentVersion, _keepVersions, ct);

    public Task<Stream?> OpenLatestBlobAsync(CancellationToken ct) =>
        _blobs.OpenAsync(CurrentVersion, ct);
}
