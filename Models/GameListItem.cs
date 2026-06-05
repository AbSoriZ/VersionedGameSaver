namespace VersionedGameSaver.Models;

public sealed class GameListItem
{
    public required GameEntry Entry { get; init; }
    public int VersionCount { get; init; }

    public string Name => Entry.DisplayName;
    public bool IsDetected => Entry.IsDetected;
    public bool HasBackups => VersionCount > 0;
    public string SourceText => IsDetected ? "Detected" : "Custom";
    public string BackupText => HasBackups ? "Backed up" : "No backups";
    public string VersionCountText => VersionCount == 1 ? "1 version" : $"{VersionCount:N0} versions";

    public override string ToString() => Name;
}
