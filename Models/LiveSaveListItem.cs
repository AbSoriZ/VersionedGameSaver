using System.IO;

namespace VersionedGameSaver.Models;

public sealed class LiveSaveListItem
{
    private DateTime? _modifiedSort;
    private bool _modifiedSortLoaded;

    public required LiveSaveEntry Entry { get; init; }
    public bool IsLatest { get; set; }

    public string Name => Entry.DisplayName;
    public string LatestText => IsLatest ? "Latest" : "";
    public string KindText => Entry.Kind switch
    {
        LiveSaveEntryKind.AllSaveData => "All save data",
        LiveSaveEntryKind.DetectedLocation => "Detected path",
        LiveSaveEntryKind.DetectedChildFolder => "Detected folder",
        LiveSaveEntryKind.Registry => "Registry",
        LiveSaveEntryKind.CustomFolder => "Custom folder",
        LiveSaveEntryKind.CustomFile => "Custom file",
        _ => "Live save"
    };

    public DateTime? ModifiedSort
    {
        get
        {
            if (!_modifiedSortLoaded)
            {
                _modifiedSort = GetLastModifiedUtc(Entry);
                _modifiedSortLoaded = true;
            }

            return _modifiedSort;
        }
    }

    public string ModifiedText => ModifiedSort is null
        ? "No modified time"
        : ModifiedSort.Value.ToLocalTime().ToString("g");

    public int ItemCount => Entry.Items.Count;
    public string ItemCountText => ItemCount.ToString("N0");

    public override string ToString() => Name;

    private static DateTime? GetLastModifiedUtc(LiveSaveEntry entry)
    {
        DateTime? latest = null;
        foreach (var item in entry.Items)
        {
            var modified = GetLastModifiedUtc(item);
            if (modified is not null && (latest is null || modified > latest))
            {
                latest = modified;
            }
        }

        return latest;
    }

    private static DateTime? GetLastModifiedUtc(LiveSaveItem item)
    {
        try
        {
            if (item.Kind == LiveSaveItemKind.File && File.Exists(item.SourcePath))
            {
                return File.GetLastWriteTimeUtc(item.SourcePath);
            }

            if (item.Kind == LiveSaveItemKind.Directory && Directory.Exists(item.SourcePath))
            {
                return Directory.GetLastWriteTimeUtc(item.SourcePath);
            }
        }
        catch
        {
            return null;
        }

        return null;
    }
}
