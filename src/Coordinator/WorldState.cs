namespace Coordinator;

/// <summary>Persisted coordinator state. One world per deployment (friends-only group).</summary>
public sealed class WorldState
{
    /// <summary>Monotonic version, bumped on every successful upload. 0 = no world uploaded yet.</summary>
    public int Version { get; set; }

    /// <summary>True once at least one world archive has been uploaded.</summary>
    public bool HasWorld { get; set; }

    /// <summary>When the current version was uploaded. Null until the first upload.</summary>
    public DateTimeOffset? LastUpdatedAt { get; set; }

    /// <summary>Admin-set number of recent versions to keep. Null = use the configured default.</summary>
    public int? KeepVersions { get; set; }

    /// <summary>Display name of the current lock holder, or null if free.</summary>
    public string? HostName { get; set; }

    /// <summary>Valheim join code the host typed in so others can connect. Null until set.</summary>
    public string? JoinCode { get; set; }

    /// <summary>Secret token proving lock ownership. Never exposed in the public state view.</summary>
    public string? LockToken { get; set; }

    /// <summary>When the current lease expires. A held lock past this time is treated as free.</summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }
}

/// <summary>Public, token-free projection of the state sent to the web UI.</summary>
public sealed record PublicState(
    int Version,
    bool HasWorld,
    bool Locked,
    string? HostName,
    string? JoinCode,
    DateTimeOffset? LeaseExpiresAt,
    int? SecondsUntilExpiry,
    DateTimeOffset? LastUpdatedAt,
    int KeepVersions);
