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
    public string OriginalName { get; set; } = "";
    public string? Alias { get; set; }
    public int? SlotNumber { get; set; }
    public string? Notes { get; set; }
    public string ArchiveRelativePath { get; set; } = "";
    public long SizeBytes { get; set; }
    public int FileCount { get; set; }

    public string VersionName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Alias))
            {
                return Alias!;
            }

            if (!string.IsNullOrWhiteSpace(OriginalName))
            {
                return OriginalName;
            }

            if (Kind == SnapshotKind.Manual && SlotNumber is not null)
            {
                return $"Slot {SlotNumber}";
            }

            var localTime = CreatedAtUtc.ToLocalTime();
            return Kind == SnapshotKind.Safety
                ? $"Auto before restore - {localTime:g}"
                : localTime.ToString("g");
        }
    }

    public string DisplayName => VersionName;

    public override string ToString() => DisplayName;
}
