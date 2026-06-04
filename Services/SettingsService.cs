using VersionedGameSaver.Models;

namespace VersionedGameSaver.Services;

public sealed class SettingsService
{
    public AppSettings Load() => JsonFile.Load<AppSettings>(AppPaths.SettingsPath) ?? new AppSettings();

    public void Save(AppSettings settings) => JsonFile.Save(AppPaths.SettingsPath, settings);
}
