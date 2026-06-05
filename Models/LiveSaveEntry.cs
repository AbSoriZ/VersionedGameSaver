namespace VersionedGameSaver.Models;

public enum LiveSaveEntryKind
{
    AllSaveData,
    DetectedLocation,
    DetectedChildFolder,
    Registry,
    CustomFolder,
    CustomFile
}

public enum LiveSaveItemKind
{
    File,
    Directory,
    RegistryKey
}

public sealed class LiveSaveEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public string? Alias { get; set; }
    public LiveSaveEntryKind Kind { get; set; } = LiveSaveEntryKind.AllSaveData;
    public string? Source { get; set; }
    public List<LiveSaveItem> Items { get; set; } = [];

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Alias)
            ? Alias!
            : !string.IsNullOrWhiteSpace(OriginalName)
                ? OriginalName
                : Label;

    public override string ToString() => DisplayName;
}

public sealed class LiveSaveItem
{
    public string SourcePath { get; set; } = "";
    public LiveSaveItemKind Kind { get; set; } = LiveSaveItemKind.Directory;
    public string? ManifestPath { get; set; }
    public List<string> Tags { get; set; } = [];
}
