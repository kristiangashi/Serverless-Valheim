using Coordinator;
using Coordinator.Storage;
using Microsoft.AspNetCore.Http.Features;

var builder = WebApplication.CreateBuilder(args);

// World archives (.db can be hundreds of MB) need a generous upload ceiling — default is ~30 MB.
const long MaxUploadBytes = 2L * 1024 * 1024 * 1024; // 2 GB
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = MaxUploadBytes);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = MaxUploadBytes);

// --- Configuration (env vars override appsettings; sane local-dev defaults) ---
string Cfg(string key, string fallback) =>
    builder.Configuration[key] is { Length: > 0 } v ? v : fallback;

var dataDir = Path.GetFullPath(Cfg("DATA_DIR", "data"));
var groupPassphrase = Cfg("GROUP_PASSPHRASE", "valheim");
var adminPassphrase = Cfg("ADMIN_PASSPHRASE", "changeme-admin");
var leaseMinutes = int.TryParse(Cfg("LEASE_MINUTES", "5"), out var lm) ? lm : 5;
var keepVersions = int.TryParse(Cfg("KEEP_VERSIONS", "3"), out var kv) ? kv : 3;

// Railway (and most PaaS) inject the port to bind via $PORT.
if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } port)
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// Use Cloudflare R2 when fully configured (durable, survives redeploys); otherwise local disk.
var r2Account = Cfg("R2_ACCOUNT_ID", "");
var r2Key = Cfg("R2_ACCESS_KEY_ID", "");
var r2Secret = Cfg("R2_SECRET_ACCESS_KEY", "");
var r2Bucket = Cfg("R2_BUCKET", "");
var r2Configured = new[] { r2Account, r2Key, r2Secret, r2Bucket }.All(v => v.Length > 0);

IBlobStorage blobs = r2Configured
    ? new R2BlobStorage(r2Account, r2Key, r2Secret, r2Bucket)
    : new LocalDiskBlobStorage(Path.Combine(dataDir, "worlds"));

var store = new WorldStore(blobs, dataDir, TimeSpan.FromMinutes(leaseMinutes), keepVersions);
builder.Services.AddSingleton(store);

var bootLogger = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("Startup");
bootLogger.LogInformation("World storage: {Storage}", r2Configured ? $"Cloudflare R2 (bucket '{r2Bucket}')" : "local disk");

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// --- Helpers ---
static IResult Map(OpResult r, string verb) => r switch
{
    OpResult.Ok => Results.Ok(),
    OpResult.Conflict => Results.Conflict(new { error = "World is already locked by someone else." }),
    OpResult.Forbidden => Results.Json(new { error = "Your lock token isn't valid." }, statusCode: 403),
    OpResult.BadVersion => Results.Conflict(new { error = "Version mismatch — re-download the latest world before " + verb + "." }),
    OpResult.NotFound => Results.Conflict(new { error = "You don't currently hold the lock (it may have expired)." }),
    _ => Results.StatusCode(500),
};

bool GroupOk(string? p) => string.Equals(p, groupPassphrase, StringComparison.Ordinal);

// --- API ---
app.MapGet("/api/state", (WorldStore s) => Results.Ok(s.GetPublicState()));

app.MapPost("/api/claim", (ClaimRequest req, WorldStore s) =>
{
    if (!GroupOk(req.Passphrase)) return Results.Json(new { error = "Wrong group passphrase." }, statusCode: 401);
    if (string.IsNullOrWhiteSpace(req.DisplayName)) return Results.BadRequest(new { error = "Display name required." });
    var (result, token, version) = s.Claim(req.DisplayName.Trim());
    return result == OpResult.Ok
        ? Results.Ok(new { token, version })
        : Map(result, "claiming");
});

app.MapPost("/api/heartbeat", (TokenRequest req, WorldStore s) => Map(s.Heartbeat(req.Token), "heartbeat"));

app.MapPost("/api/joincode", (JoinCodeRequest req, WorldStore s) =>
{
    if (string.IsNullOrWhiteSpace(req.JoinCode)) return Results.BadRequest(new { error = "Join code required." });
    return Map(s.SetJoinCode(req.Token, req.JoinCode.Trim()), "setting the join code");
});

app.MapPost("/api/release", (TokenRequest req, WorldStore s) => Map(s.Release(req.Token), "releasing"));

app.MapPost("/api/admin/force-release", (AdminRequest req, WorldStore s) =>
{
    if (!string.Equals(req.AdminPassphrase, adminPassphrase, StringComparison.Ordinal))
        return Results.Json(new { error = "Wrong admin passphrase." }, statusCode: 401);
    s.ForceRelease();
    return Results.Ok();
});

// Start a brand-new world: wipe all stored archives and reset to version 0.
app.MapPost("/api/admin/reset", async (AdminRequest req, WorldStore s, CancellationToken ct) =>
{
    if (!string.Equals(req.AdminPassphrase, adminPassphrase, StringComparison.Ordinal))
        return Results.Json(new { error = "Wrong admin passphrase." }, statusCode: 401);
    await s.ResetAsync(ct);
    return Results.Ok(new { reset = true });
});

app.MapPost("/api/upload", async (HttpRequest http, WorldStore s, CancellationToken ct) =>
{
    if (!http.HasFormContentType) return Results.BadRequest(new { error = "Expected multipart form upload." });
    var form = await http.ReadFormAsync(ct);
    var token = form["token"].ToString();
    var file = form.Files["file"];
    if (string.IsNullOrEmpty(token) || file is null || file.Length == 0)
        return Results.BadRequest(new { error = "token and a non-empty file are required." });

    var finish = form["finish"].ToString() is "true" or "1";
    int? baseVersion = int.TryParse(form["baseVersion"], out var bv) ? bv : null;

    var (begin, nextVersion) = s.BeginUpload(token, baseVersion);
    if (begin != OpResult.Ok) return Map(begin, "uploading");

    await using (var stream = file.OpenReadStream())
    {
        await s.SaveBlobAsync(nextVersion, stream, ct);
    }

    var commit = s.CommitUpload(token, nextVersion, finish);
    if (commit != OpResult.Ok) return Map(commit, "uploading");

    await s.PruneBlobsAsync(ct);
    return Results.Ok(new { version = nextVersion, released = finish });
});

app.MapGet("/api/download", async (string? passphrase, WorldStore s, CancellationToken ct) =>
{
    if (!GroupOk(passphrase)) return Results.Json(new { error = "Wrong group passphrase." }, statusCode: 401);
    var state = s.GetPublicState();
    if (!state.HasWorld) return Results.NotFound(new { error = "No world has been uploaded yet." });
    var blob = await s.OpenLatestBlobAsync(ct);
    if (blob is null) return Results.NotFound(new { error = "World archive is missing on the server." });
    return Results.File(blob, "application/zip", $"valheim-world-v{state.Version}.zip");
});

app.Run();

// --- Request DTOs ---
record ClaimRequest(string DisplayName, string Passphrase);
record TokenRequest(string Token);
record JoinCodeRequest(string Token, string JoinCode);
record AdminRequest(string AdminPassphrase);
