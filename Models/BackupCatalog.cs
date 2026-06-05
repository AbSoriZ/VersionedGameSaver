namespace VersionedGameSaver.Models;

public sealed class BackupCatalog
{
    public int SchemaVersion { get; set; } = 2;
    public List<GameEntry> Games { get; set; } = [];
    public List<SaveVersion> Versions { get; set; } = [];
}
