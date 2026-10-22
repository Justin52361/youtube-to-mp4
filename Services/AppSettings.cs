using System.Text.Json;

namespace YoutubeToMp4.Services;

public sealed class AppSettings
{
    public string OutputDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "YouTube");

    public string PaceKey { get; set; } = "2";
    public bool UseBrowserCookies { get; set; }
    public string CookieBrowser { get; set; } = "firefox";
    public bool RandomizeWaits { get; set; } = true;
    public bool PauseWhenBlocked { get; set; } = true;
    public int MaxHeight { get; set; } = 1080;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings();
        }
        catch { /* corrupt settings file: start from defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json));
        }
        catch { /* not worth interrupting the user over */ }
    }
}
