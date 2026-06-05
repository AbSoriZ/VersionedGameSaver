namespace VersionedGameSaver.Models;

public sealed class SnapshotListItem
{
    public required SnapshotRecord Snapshot { get; init; }
    public required SaveScope Scope { get; init; }

    public string SlotText => Snapshot.SlotNumber?.ToString() ?? "Auto";
    public int SlotSort => Snapshot.SlotNumber ?? int.MaxValue;
    public string AliasText => string.IsNullOrWhiteSpace(Snapshot.Alias) ? "-" : Snapshot.Alias!;
    public string EditableAlias
    {
        get => Snapshot.Alias ?? "";
        set => Snapshot.Alias = string.IsNullOrWhiteSpace(value) ? null : value;
    }
    public string AliasSort => Snapshot.Alias ?? "";
    public string SaveEntryName => Scope.DisplayName;
    public string SaveEntrySort => Scope.DisplayName;
    public string CreatedText => Snapshot.CreatedAtUtc.ToLocalTime().ToString("g");
    public DateTime CreatedSort => Snapshot.CreatedAtUtc;
    public string FileCountText => Snapshot.FileCount.ToString("N0");
    public int FileCountSort => Snapshot.FileCount;
    public string SizeText => Services.FileSizeFormatter.Format(Snapshot.SizeBytes);
    public long SizeSort => Snapshot.SizeBytes;
    public string DisplayName => $"{SaveEntryName} - {SlotText}";

    public override string ToString() => DisplayName;
}
