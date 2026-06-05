namespace VersionedGameSaver.Models;

public enum SaveVersionKind
{
    Manual,
    Safety
}

public sealed class SaveVersion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string GameId { get; set; } = "";
    public string LiveSaveEntryId { get; set; } = "";
    public SaveVersionKind Kind { get; set; } = SaveVersionKind.Manual;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public string? Name { get; set; }
    public string OriginalName { get; set; } = "";
    public string? Alias { get; set; }
    public int? SlotNumber { get; set; }
    public bool IsPlaceholder { get; set; }
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

            if (IsPlaceholder && Kind == SaveVersionKind.Manual && SlotNumber is not null)
            {
                return $"Empty Slot {SlotNumber}";
            }

            if (Kind == SaveVersionKind.Manual && SlotNumber is not null)
            {
                return $"Slot {SlotNumber}";
            }

            var localTime = CreatedAtUtc.ToLocalTime();
            return Kind == SaveVersionKind.Safety
                ? $"Auto before restore - {localTime:g}"
                : localTime.ToString("g");
        }
    }

    public string DisplayName => VersionName;

    public override string ToString() => DisplayName;
}
