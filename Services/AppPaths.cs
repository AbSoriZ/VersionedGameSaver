using System.IO;

namespace VersionedGameSaver.Services;

public static class AppPaths
{
    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VersionedGameSaver");

    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");
}
