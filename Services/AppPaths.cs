namespace YoutubeToMp4.Services;

public static class AppPaths
{
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YoutubeToMp4");

    public static string ToolsDir => Path.Combine(Root, "tools");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string UsageFile => Path.Combine(Root, "usage.json");
}
