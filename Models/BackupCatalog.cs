namespace VersionedGameSaver.Models;

public sealed class BackupCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public List<GameProfile> Profiles { get; set; } = [];
    public List<SnapshotRecord> Snapshots { get; set; } = [];
}
