using System.IO;
using System.Text.Json;

namespace VolSync;

public class Settings
{
    /// <summary>"auto" (follow GG's live selection) or the endpoint ID of a fixed target device.</summary>
    public string Target { get; set; } = "auto";

    /// <summary>Start with Windows (HKCU Run key). Default off.</summary>
    public bool AutoStart { get; set; } = false;
}

public static class SettingsStore
{
    static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VolSync");
    static string FilePath => Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { }
        return new Settings();
    }

    public static void Save(Settings s)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s, Options));
        }
        catch { }
    }
}
