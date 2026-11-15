using System.Text.Json;
using Microsoft.Win32;

namespace BrokenArrowSquadTracker;

public sealed class TrackerSettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BrokenArrowSquadTracker", "settings.json");

    public bool ShowEnemyPlayers { get; private set; } = true;
    public string? SteamPath { get; private set; }
    public bool SteamPathFound => SteamPath is not null;

    public TrackerSettingsStore()
    {
        try
        {
            SavedSettings? saved = null;
            if (File.Exists(FilePath))
                saved = JsonSerializer.Deserialize<SavedSettings>(File.ReadAllText(FilePath));
            ShowEnemyPlayers = true;
            SteamPath = ValidateSteamPath(saved?.SteamPath) ?? FindSteamPath();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            Save();
        }
        catch { }
    }

    public bool SetSteamPath(string path)
    {
        var validated = ValidateSteamPath(path);
        if (validated is null) return false;
        SteamPath = validated;
        try { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); Save(); } catch { }
        DiagnosticLog.Write("SETTINGS", $"Steam path={SteamPath}");
        return true;
    }

    private void Save() => File.WriteAllText(FilePath,
        JsonSerializer.Serialize(new SavedSettings(true, SteamPath)));

    private static string? FindSteamPath()
    {
        var defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        var found = ValidateSteamPath(defaultPath);
        if (found is not null) return found;
        foreach (var keyName in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam" })
        {
            var registryPath = Registry.GetValue(keyName, "SteamPath", null) as string ??
                               Registry.GetValue(keyName, "InstallPath", null) as string;
            found = ValidateSteamPath(registryPath);
            if (found is not null) return found;
        }
        return null;
    }

    private static string? ValidateSteamPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"'));
            return File.Exists(Path.Combine(full, "steam.exe")) && Directory.Exists(Path.Combine(full, "config"))
                ? full : null;
        }
        catch { return null; }
    }

    private sealed record SavedSettings(bool ShowEnemyPlayers, string? SteamPath = null);
}
