using System.Text.Json;

namespace Helper;

/// <summary>User settings, persisted to %APPDATA%\ServerlessValheim\config.json.</summary>
public sealed class AppConfig
{
    public string CoordinatorUrl { get; set; } = "https://serverless-valheim.onrender.com";
    public string DisplayName { get; set; } = "";
    public string GroupPassphrase { get; set; } = "";
    public string WorldName { get; set; } = "";
    public string WorldsFolder { get; set; } = DefaultWorldsFolder();
    public bool AutoSaveWhileHosting { get; set; } = true;
    public bool AutoLaunchWhenReady { get; set; } = false;

    /// <summary>
    /// Backstop interval for auto-saves. Normally uploads are triggered by Valheim writing the
    /// world, so this only matters if the folder watcher can't be set up or misses an event.
    /// </summary>
    public int AutoSaveMinutes { get; set; } = 10;

    public const int MinAutoSaveMinutes = 2;
    public const int MaxAutoSaveMinutes = 60;

    public int ClampedAutoSaveMinutes =>
        Math.Clamp(AutoSaveMinutes, MinAutoSaveMinutes, MaxAutoSaveMinutes);

    private static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ServerlessValheim", "config.json");

    /// <summary>Valheim's local worlds live in %USERPROFILE%\AppData\LocalLow\IronGate\Valheim\worlds_local.</summary>
    public static string DefaultWorldsFolder()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(profile, "AppData", "LocalLow", "IronGate", "Valheim", "worlds_local");
    }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new AppConfig();
        }
        catch { /* corrupt config — fall back to defaults */ }
        return new AppConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
