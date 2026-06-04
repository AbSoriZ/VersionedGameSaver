namespace VersionedGameSaver.Models;

public enum SnapshotKind
{
    Manual,
    Safety
}

public sealed class SnapshotRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProfileId { get; set; } = "";
    public string ScopeId { get; set; } = "";
    public SnapshotKind Kind { get; set; } = SnapshotKind.Manual;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? Name { get; set; }
    public string? Notes { get; set; }
    public string ArchiveRelativePath { get; set; } = "";
    public long SizeBytes { get; set; }
    public int FileCount { get; set; }

    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name))
            {
                return Name!;
            }

            var localTime = CreatedAtUtc.ToLocalTime();
            return Kind == SnapshotKind.Safety
                ? $"Auto before restore - {localTime:g}"
                : localTime.ToString("g");
        }
    }

    public override string ToString() => DisplayName;
}
