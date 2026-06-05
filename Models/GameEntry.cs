namespace VersionedGameSaver.Models;

public sealed class GameEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string GameKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public string? Alias { get; set; }
    public bool IsDetected { get; set; }
    public List<LiveSaveEntry> LiveSaves { get; set; } = [];

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Alias)
            ? Alias!
            : !string.IsNullOrWhiteSpace(OriginalName)
                ? OriginalName
                : Name;

    public override string ToString() => DisplayName;
}
