namespace VersionedGameSaver.Models;

public sealed class SnapshotArchiveManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string SnapshotId { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<SnapshotArchiveItem> Items { get; set; } = [];
}

public sealed class SnapshotArchiveItem
{
    public string OriginalPath { get; set; } = "";
    public string ArchivePath { get; set; } = "";
    public bool IsDirectory { get; set; }
}
