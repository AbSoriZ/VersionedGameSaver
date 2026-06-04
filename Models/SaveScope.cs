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
    public SaveScopeKind Kind { get; set; } = SaveScopeKind.WholeGame;
    public List<ScopeItem> Items { get; set; } = [];

    public override string ToString() => Label;
}

public sealed class ScopeItem
{
    public string SourcePath { get; set; } = "";
}
