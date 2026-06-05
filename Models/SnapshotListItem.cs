namespace VersionedGameSaver.Models;

public sealed class SnapshotListItem
{
    public required SaveVersion Version { get; init; }
    public required LiveSaveEntry LiveSave { get; init; }

    public string SlotText => Version.SlotNumber?.ToString() ?? "Auto";
    public int SlotSort => Version.SlotNumber ?? int.MaxValue;
    public string AliasText => string.IsNullOrWhiteSpace(Version.Alias) ? "-" : Version.Alias!;
    public string AliasSort => Version.Alias ?? "";
    public string SaveEntryName => LiveSave.DisplayName;
    public string SaveEntrySort => LiveSave.DisplayName;
    public string CreatedText => Version.CreatedAtUtc.ToLocalTime().ToString("g");
    public DateTime CreatedSort => Version.CreatedAtUtc;
    public string FileCountText => Version.IsPlaceholder ? "0" : Version.FileCount.ToString("N0");
    public int FileCountSort => Version.FileCount;
    public string SizeText => Version.IsPlaceholder ? "0 B" : Services.FileSizeFormatter.Format(Version.SizeBytes);
    public long SizeSort => Version.SizeBytes;
    public string DisplayName => $"{SaveEntryName} - {SlotText}";

    public override string ToString() => DisplayName;
}
