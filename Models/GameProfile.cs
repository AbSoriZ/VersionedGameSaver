namespace VersionedGameSaver.Models;

public sealed class GameProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string GameId { get; set; } = "";
    public string GameName { get; set; } = "";
    public string Label { get; set; } = "";
    public string SaveRootPath { get; set; } = "";
    public bool IsPreset { get; set; }
    public List<SaveScope> Scopes { get; set; } = [];

    public override string ToString() => string.IsNullOrWhiteSpace(Label) ? GameName : Label;
}
