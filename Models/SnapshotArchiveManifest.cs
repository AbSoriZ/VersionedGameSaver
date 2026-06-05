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
    public LiveSaveItemKind Kind { get; set; } = LiveSaveItemKind.Directory;
}

public sealed class RegistrySnapshot
{
    public string KeyPath { get; set; } = "";
    public List<RegistrySnapshotValue> Values { get; set; } = [];
    public List<RegistrySnapshot> SubKeys { get; set; } = [];
}

public sealed class RegistrySnapshotValue
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string? StringValue { get; set; }
    public List<string>? StringValues { get; set; }
    public string? BinaryBase64 { get; set; }
    public int? IntValue { get; set; }
    public long? LongValue { get; set; }
}
