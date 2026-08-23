using System.IO;
using System.Text.Json;

namespace SteamRecordingBrowser.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string SettingsDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SteamRecordingBrowser");

    public static string SettingsPath =>
        Path.Combine(SettingsDirectory, "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                   ?? new AppSettings();
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Settings load failed", ex);
            return new AppSettings();
        }
    }

    public void SaveRecordingRoot(string root)
    {
        var settings = Load();
        settings.RecordingRoot = root.Trim();
        Save(settings);

        AppLogger.Write($"Saved recording root: {settings.RecordingRoot}");
    }

    public void MarkDesktopShortcutPromptShown()
    {
        var settings = Load();
        settings.DesktopShortcutPromptShown = true;
        Save(settings);
    }

    public void SaveUseTileLayout(bool useTileLayout)
    {
        var settings = Load();
        settings.UseTileLayout = useTileLayout;
        Save(settings);

        AppLogger.Write($"Saved clip layout: {(useTileLayout ? "Tiles" : "List")}");
    }

    private static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);

            var tempPath = SettingsPath + ".tmp";
            File.WriteAllText(
                tempPath,
                JsonSerializer.Serialize(settings, JsonOptions));

            File.Move(tempPath, SettingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLogger.WriteException("Settings save failed", ex);
        }
    }
}

public sealed class AppSettings
{
    public string RecordingRoot { get; set; } = "";
    public bool DesktopShortcutPromptShown { get; set; }
    public bool UseTileLayout { get; set; }
}
