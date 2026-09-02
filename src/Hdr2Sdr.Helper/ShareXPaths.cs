namespace Hdr2Sdr.Helper;

public static class ShareXPaths
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShareX");
    public static string HotkeysConfig => Path.Combine(Folder, "HotkeysConfig.json");
    public static string ApplicationConfig => Path.Combine(Folder, "ApplicationConfig.json");
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "hdr2sdr", "settings.json");
}
