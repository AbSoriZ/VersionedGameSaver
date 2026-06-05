namespace VersionedGameSaver.Models;

public enum SaveScopeKind
{
    WholeGame,
    DetectedWorld,
    CustomFolder,
    CustomFile
}

public sealed class SaveScope
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Label { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public string? Alias { get; set; }
    public SaveScopeKind Kind { get; set; } = SaveScopeKind.WholeGame;
    public List<ScopeItem> Items { get; set; } = [];

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(Alias)
            ? Alias!
            : !string.IsNullOrWhiteSpace(OriginalName)
                ? OriginalName
                : Label;

    public override string ToString() => DisplayName;
}

public sealed class ScopeItem
{
    public string SourcePath { get; set; } = "";
}
