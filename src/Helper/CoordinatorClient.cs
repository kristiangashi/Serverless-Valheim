using System.Net;
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
    [property: JsonPropertyName("secondsUntilExpiry")] int? SecondsUntilExpiry);

/// <summary>Raised when the coordinator returns a non-success status, carrying its error message.</summary>
public sealed class CoordinatorException(string message) : Exception(message);

/// <summary>Thin HTTP client over the coordinator API.</summary>
public sealed class CoordinatorClient(string baseUrl)
{
    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromMinutes(10), // large world uploads/downloads
    };

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

    /// <summary>Download the latest world archive to a temp file and return its path.</summary>
    public async Task<string> DownloadToTempAsync(string passphrase, CancellationToken ct = default)
    {
        using var res = await _http.GetAsync(
            $"api/download?passphrase={WebUtility.UrlEncode(passphrase)}",
            HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new CoordinatorException(await ErrorMessage(res));
        var tmp = Path.Combine(Path.GetTempPath(), $"vwk-download-{Guid.NewGuid():N}.zip");
        await using (var fs = File.Create(tmp))
        {
            await res.Content.CopyToAsync(fs, ct);
        }
        return tmp;
    }

    /// <summary>Upload a world archive. Returns the new version. finish=true also releases the lock.</summary>
    public async Task<int> UploadAsync(string token, string zipPath, bool finish, int baseVersion, CancellationToken ct = default)
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
