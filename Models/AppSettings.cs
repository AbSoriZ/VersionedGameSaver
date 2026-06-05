namespace VersionedGameSaver.Models;

public sealed class AppSettings
{
    public string? LibraryPath { get; set; }
    public bool SuppressBackupRunningWarning { get; set; }
}
