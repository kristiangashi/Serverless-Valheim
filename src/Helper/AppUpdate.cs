using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace Helper;

/// <summary>
/// Startup update check against the project's GitHub Releases.
///
/// <para>The installer is already everything an updater needs: it installs per-user under
/// <c>%LOCALAPPDATA%</c> with <c>PrivilegesRequired=lowest</c>, so it can run without a UAC prompt,
/// and it carries a stable <c>AppId</c>, so re-running a newer one upgrades the existing install in
/// place. All that was missing is asking whether a newer one exists and handing off to it.</para>
///
/// <para>Nothing here is allowed to be load-bearing: a friend with no network, a GitHub outage, or
/// a tripped anonymous rate limit must still get a working app. Every failure path ends in "carry
/// on without updating".</para>
/// </summary>
internal static class AppUpdate
{
    private const string Repo = "kristiangashi/Serverless-Valheim";
    private const string InstallerAsset = "ValheimWorldKeeper-Setup.exe";

    /// <summary>This build's version, from the assembly CI stamped with the release tag.</summary>
    public static Version Current { get; } =
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);

    /// <summary>
    /// Three-part version for display. Not <see cref="Version.ToString(int)"/>, which throws when
    /// a component is absent — a two-part tag like <c>v1.2</c> parses with Build = -1.
    /// </summary>
    public static string Format(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";

    public static string CurrentDisplay => Format(Current);

    /// <summary>
    /// Major.Minor.Build only, so the two sides compare on the parts a release tag actually carries.
    /// A tag parses with Revision = -1 while the assembly's is 0, which would otherwise make the
    /// tag for the very build you are running look *older* than it. That happens to produce the
    /// right answer for the equal case, but by accident, and it breaks the moment anything grows a
    /// fourth component.
    /// </summary>
    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>A published release newer than this build, and where to get its installer.</summary>
    public readonly record struct Release(Version Version, string DownloadUrl);

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        // GitHub rejects API requests that don't identify themselves.
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("ValheimWorldKeeper", CurrentDisplay));
        return http;
    }

    /// <summary>
    /// The newest release if it's ahead of this build, else null. Throws only on transport or
    /// parse failures, which the caller treats as "couldn't tell".
    /// </summary>
    public static async Task<Release?> CheckAsync(CancellationToken ct = default)
    {
        using var http = NewClient(TimeSpan.FromSeconds(15));
        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var json = await http.GetStringAsync(
            $"https://api.github.com/repos/{Repo}/releases/latest", ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("tag_name", out var tag)) return null;
        // Tags are "v1.2.3"; the workflow derives the assembly version from the same string.
        if (!Version.TryParse(tag.GetString()?.TrimStart('v', 'V'), out var latest)) return null;
        if (Normalize(latest) <= Normalize(Current)) return null;

        // Take the asset URL from the release we just inspected rather than the generic
        // .../releases/latest/download/... redirect: if a release is published between the two
        // requests, that redirect would hand back a different build than the one compared above.
        if (!root.TryGetProperty("assets", out var assets)) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var name)) continue;
            if (!string.Equals(name.GetString(), InstallerAsset, StringComparison.OrdinalIgnoreCase)) continue;
            if (asset.TryGetProperty("browser_download_url", out var url) &&
                url.GetString() is { Length: > 0 } direct)
            {
                return new Release(latest, direct);
            }
        }

        // A release with no installer attached (a CI failure part-way through publishing) is not
        // something to offer the user.
        return null;
    }

    /// <summary>Download the installer to a private temp folder and return its path.</summary>
    public static async Task<string> DownloadAsync(Release release, CancellationToken ct = default)
    {
        using var http = NewClient(TimeSpan.FromMinutes(10)); // ~100 MB over a home uplink

        // A fresh folder per attempt. Windows never clears %TEMP% on its own, so a fixed name would
        // eventually collide with a half-written file left by a download that died mid-transfer —
        // and running *that* would be worse than not updating at all.
        var folder = Path.Combine(
            Path.GetTempPath(), "ValheimWorldKeeper-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, InstallerAsset);

        await using (var source = await http.GetStreamAsync(release.DownloadUrl, ct))
        await using (var file = File.Create(path))
        {
            await source.CopyToAsync(file, ct);
        }

        return path;
    }

    /// <summary>
    /// Start the installer and return immediately, so the caller can close the app — a running exe
    /// can't be overwritten, so the app has to be gone before the file copy starts.
    /// </summary>
    public static void Launch(string installerPath)
    {
        // /SILENT shows a progress window but no wizard; /SP- drops the "this will install…" prompt;
        // /NORESTART keeps it from ever rebooting the machine; /CLOSEAPPLICATIONS lets Restart
        // Manager shut this process down if it hasn't finished exiting by the time files are copied.
        // The installer's own [Run] entry starts the app again once it's done.
        Process.Start(new ProcessStartInfo(installerPath, "/SILENT /SP- /NORESTART /CLOSEAPPLICATIONS")
        {
            UseShellExecute = true,
        });
    }
}
