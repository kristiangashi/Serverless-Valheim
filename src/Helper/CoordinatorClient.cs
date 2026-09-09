using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Helper;

/// <summary>Public, token-free state from the coordinator (mirrors the server's PublicState).</summary>
public sealed record CoordinatorState(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("hasWorld")] bool HasWorld,
    [property: JsonPropertyName("locked")] bool Locked,
    [property: JsonPropertyName("hostName")] string? HostName,
    [property: JsonPropertyName("joinCode")] string? JoinCode,
    [property: JsonPropertyName("secondsUntilExpiry")] int? SecondsUntilExpiry,
    [property: JsonPropertyName("lastUpdatedAt")] DateTimeOffset? LastUpdatedAt);

/// <summary>Raised when the coordinator returns a non-success status, carrying its error message.</summary>
public sealed class CoordinatorException(string message) : Exception(message);

/// <summary>Thin HTTP client over the coordinator API.</summary>
public sealed class CoordinatorClient(string baseUrl)
{
    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
        // Covers a whole request/response, but only up to the point response headers arrive — under
        // HttpCompletionOption.ResponseHeadersRead it stops applying to the body, which is why the
        // download streams under its own stall deadline instead (see StallTimeout).
        Timeout = TimeSpan.FromMinutes(10), // large world uploads
    };

    /// <summary>Bytes transferred so far, and the total when the server declared one.</summary>
    public readonly record struct TransferProgress(long Received, long? Total);

    // A world archive is tens of MB over a home connection, so there's no honest ceiling on how
    // long a healthy download takes — but there is one on how long it may sit completely still.
    // HttpClient.Timeout can't provide it: once response headers are in, a body read that never
    // returns (dropped Wi-Fi, a NAT entry reaped mid-transfer, a storage hiccup) blocks forever and
    // nothing faults it. So watch for progress rather than for elapsed time.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private static async Task<string> ErrorMessage(HttpResponseMessage res)
    {
        try
        {
            var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("error", out var e)) return e.GetString() ?? res.ReasonPhrase ?? "Request failed";
        }
        catch { }
        return $"{(int)res.StatusCode} {res.ReasonPhrase}";
    }

    public async Task<CoordinatorState> GetStateAsync(CancellationToken ct = default)
    {
        var s = await _http.GetFromJsonAsync<CoordinatorState>("api/state", ct);
        return s ?? throw new CoordinatorException("Empty state response.");
    }

    public async Task<(string Token, int Version)> ClaimAsync(string displayName, string passphrase, CancellationToken ct = default)
    {
        var res = await _http.PostAsJsonAsync("api/claim", new { displayName, passphrase }, ct);
        if (!res.IsSuccessStatusCode) throw new CoordinatorException(await ErrorMessage(res));
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement;
        return (json.GetProperty("token").GetString()!, json.GetProperty("version").GetInt32());
    }

    public async Task HeartbeatAsync(string token, CancellationToken ct = default)
    {
        var res = await _http.PostAsJsonAsync("api/heartbeat", new { token }, ct);
        if (!res.IsSuccessStatusCode) throw new CoordinatorException(await ErrorMessage(res));
    }

    public async Task SetJoinCodeAsync(string token, string joinCode, CancellationToken ct = default)
    {
        var res = await _http.PostAsJsonAsync("api/joincode", new { token, joinCode }, ct);
        if (!res.IsSuccessStatusCode) throw new CoordinatorException(await ErrorMessage(res));
    }

    public async Task ReleaseAsync(string token, CancellationToken ct = default)
    {
        var res = await _http.PostAsJsonAsync("api/release", new { token }, ct);
        if (!res.IsSuccessStatusCode) throw new CoordinatorException(await ErrorMessage(res));
    }

    // A coordinator that can hand out storage URLs lets the world archive skip it entirely, which
    // is the difference between its host metering every save and metering none of them. Two cases
    // mean "it can't, proxy instead" rather than "something went wrong": 404 (coordinator predates
    // these endpoints) and 501 (its storage is local disk, which has nowhere to point us). Any
    // other failure is a real one — a lost lock or a stale version — and must not be swallowed.
    private static bool MeansProxyInstead(HttpStatusCode s) =>
        s is HttpStatusCode.NotFound or HttpStatusCode.NotImplemented;

    /// <summary>Download the latest world archive to a temp file and return its path.</summary>
    public async Task<string> DownloadToTempAsync(
        string passphrase, IProgress<TransferProgress>? progress = null, CancellationToken ct = default)
    {
        var direct = await TryGetDownloadUrlAsync(passphrase, ct);
        // An absolute URL bypasses BaseAddress, so this goes straight to the object store.
        using var res = await _http.GetAsync(
            direct ?? $"api/download?passphrase={WebUtility.UrlEncode(passphrase)}",
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new CoordinatorException(await ErrorMessage(res));

        var total = res.Content.Headers.ContentLength;
        var tmp = Path.Combine(Path.GetTempPath(), $"vwk-download-{Guid.NewGuid():N}.zip");
        try
        {
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var fs = File.Create(tmp);
            await CopyWatchingForStallAsync(src, fs, total, progress, ct);
        }
        catch
        {
            // A partial archive is worse than none: it would be handed to the extractor and fail
            // there, blaming the zip instead of the connection that actually broke.
            try { File.Delete(tmp); } catch { }
            throw;
        }
        return tmp;
    }

    /// <summary>
    /// Stream <paramref name="src"/> into <paramref name="dst"/>, giving up if no bytes at all
    /// arrive for <see cref="StallTimeout"/>. The deadline is per-read, so a slow-but-moving
    /// transfer runs as long as it needs while a dead one fails promptly and says so.
    /// </summary>
    private static async Task CopyWatchingForStallAsync(
        Stream src, Stream dst, long? total, IProgress<TransferProgress>? progress, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long received = 0;
        while (true)
        {
            int read;
            using (var stall = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                stall.CancelAfter(StallTimeout);
                try
                {
                    read = await src.ReadAsync(buffer, stall.Token);
                }
                // Only ours fired: a caller-requested cancel is theirs to see as a cancellation.
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new CoordinatorException(
                        $"The download stopped responding after {Describe(received, total)}. " +
                        "Check your connection, then try hosting again.");
                }
                // A reset connection faults the read rather than ending it. Either way the world is
                // incomplete, and what helps is how far it got — not which socket error surfaced.
                catch (IOException)
                {
                    throw new CoordinatorException(
                        $"The download broke off after {Describe(received, total)}. Please try hosting again.");
                }
            }
            if (read == 0) break;
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            received += read;
            progress?.Report(new TransferProgress(received, total));
        }

        // A connection cut cleanly mid-transfer ends the stream instead of faulting it, so short
        // content is the only evidence left that we didn't get the whole world.
        if (total is { } expected && received < expected)
            throw new CoordinatorException(
                $"The download ended early ({Describe(received, total)}). Please try hosting again.");
    }

    private static string Describe(long received, long? total) =>
        total is { } t and > 0
            ? $"{received / 1024d / 1024d:F1} of {t / 1024d / 1024d:F1} MB"
            : $"{received / 1024d / 1024d:F1} MB";

    private async Task<string?> TryGetDownloadUrlAsync(string passphrase, CancellationToken ct)
    {
        var res = await _http.GetAsync($"api/download-url?passphrase={WebUtility.UrlEncode(passphrase)}", ct);
        if (MeansProxyInstead(res.StatusCode)) return null;
        if (!res.IsSuccessStatusCode) throw new CoordinatorException(await ErrorMessage(res));
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement;
        return json.GetProperty("url").GetString();
    }

    /// <summary>Upload a world archive. Returns the new version. finish=true also releases the lock.</summary>
    public async Task<int> UploadAsync(string token, string zipPath, bool finish, int baseVersion, CancellationToken ct = default)
    {
        var direct = await TryGetUploadUrlAsync(token, baseVersion, ct);
        if (direct is not { } slot) return await UploadViaCoordinatorAsync(token, zipPath, finish, baseVersion, ct);

        await using (var fs = File.OpenRead(zipPath))
        {
            var body = new StreamContent(fs);
            // The store signed this content type in; sending anything else is a 403.
            body.Headers.ContentType = new MediaTypeHeaderValue(slot.ContentType);
            using var put = await _http.PutAsync(slot.Url, body, ct);
            if (!put.IsSuccessStatusCode)
                throw new CoordinatorException($"Storage rejected the upload ({(int)put.StatusCode} {put.ReasonPhrase}).");
        }

        // The upload is staged, not live: this is what promotes it to the world.
        var res = await _http.PostAsJsonAsync(
            "api/upload-complete",
            new { token, uploadId = slot.UploadId, version = slot.Version, finish }, ct);
        if (!res.IsSuccessStatusCode) throw new CoordinatorException(await ErrorMessage(res));
        return slot.Version;
    }

    private async Task<(int Version, string UploadId, string Url, string ContentType)?> TryGetUploadUrlAsync(
        string token, int baseVersion, CancellationToken ct)
    {
        var res = await _http.PostAsJsonAsync("api/upload-url", new { token, baseVersion }, ct);
        if (MeansProxyInstead(res.StatusCode)) return null;
        if (!res.IsSuccessStatusCode) throw new CoordinatorException(await ErrorMessage(res));
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement;
        return (json.GetProperty("version").GetInt32(),
                json.GetProperty("uploadId").GetString()!,
                json.GetProperty("url").GetString()!,
                json.GetProperty("contentType").GetString()!);
    }

    private async Task<int> UploadViaCoordinatorAsync(
        string token, string zipPath, bool finish, int baseVersion, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "token" },
            { new StringContent(finish ? "true" : "false"), "finish" },
            { new StringContent(baseVersion.ToString()), "baseVersion" },
        };
        await using var fs = File.OpenRead(zipPath);
        var fileContent = new StreamContent(fs);
        form.Add(fileContent, "file", "world.zip");

        var res = await _http.PostAsync("api/upload", form, ct);
        if (!res.IsSuccessStatusCode) throw new CoordinatorException(await ErrorMessage(res));
        var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement;
        return json.GetProperty("version").GetInt32();
    }
}
