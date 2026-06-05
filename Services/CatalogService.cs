using System.IO;
using VersionedGameSaver.Models;

namespace VersionedGameSaver.Services;

public sealed class CatalogService
{
    public const string MetadataFolderName = ".vgs";

    public string GetCatalogPath(string libraryPath) =>
        Path.Combine(libraryPath, MetadataFolderName, "catalog.json");

    public string GetSnapshotRoot(string libraryPath) =>
        Path.Combine(libraryPath, "snapshots");

    public BackupCatalog LoadOrCreate(string libraryPath)
    {
        Directory.CreateDirectory(Path.Combine(libraryPath, MetadataFolderName));
        Directory.CreateDirectory(GetSnapshotRoot(libraryPath));

        var catalogPath = GetCatalogPath(libraryPath);
        var catalog = JsonFile.Load<BackupCatalog>(catalogPath) ?? new BackupCatalog();
        Normalize(catalog);
        Save(libraryPath, catalog);
        return catalog;
    }

    public void Save(string libraryPath, BackupCatalog catalog) =>
        JsonFile.Save(GetCatalogPath(libraryPath), catalog);

    private static void Normalize(BackupCatalog catalog)
    {
        catalog.SchemaVersion = Math.Max(catalog.SchemaVersion, 2);

        foreach (var game in catalog.Games)
        {
            if (string.IsNullOrWhiteSpace(game.OriginalName))
            {
                game.OriginalName = string.IsNullOrWhiteSpace(game.Name) ? "Game" : game.Name;
            }

            if (string.IsNullOrWhiteSpace(game.Name))
            {
                game.Name = game.DisplayName;
            }

            if (string.IsNullOrWhiteSpace(game.GameKey))
            {
                game.GameKey = Slug(game.OriginalName);
            }

            foreach (var liveSave in game.LiveSaves)
            {
                if (string.IsNullOrWhiteSpace(liveSave.OriginalName))
                {
                    liveSave.OriginalName = InferOriginalName(liveSave);
                }

                liveSave.Label = liveSave.DisplayName;
            }

            foreach (var versionGroup in catalog.Versions
                         .Where(version => version.GameId == game.Id)
                         .GroupBy(version => version.LiveSaveEntryId))
            {
                var nextSlot = 0;
                foreach (var version in versionGroup.OrderBy(version => version.CreatedAtUtc))
                {
                    if (!string.IsNullOrWhiteSpace(version.Name) && string.IsNullOrWhiteSpace(version.Alias))
                    {
                        version.Alias = version.Name;
                    }

                    if (version.Kind == SaveVersionKind.Manual && version.SlotNumber is null)
                    {
                        version.SlotNumber = nextSlot;
                    }

                    if (version.Kind == SaveVersionKind.Manual && version.SlotNumber is not null)
                    {
                        nextSlot = Math.Max(nextSlot, version.SlotNumber.Value + 1);
                    }

                    if (string.IsNullOrWhiteSpace(version.OriginalName))
                    {
                        version.OriginalName = version.IsPlaceholder && version.Kind == SaveVersionKind.Manual && version.SlotNumber is not null
                            ? $"Empty Slot {version.SlotNumber}"
                            : version.Kind == SaveVersionKind.Manual && version.SlotNumber is not null
                            ? $"Slot {version.SlotNumber}"
                            : $"Auto before restore - {version.CreatedAtUtc.ToLocalTime():g}";
                    }
                }
            }
        }
    }

    private static string InferOriginalName(LiveSaveEntry liveSave)
    {
        if (liveSave.Kind == LiveSaveEntryKind.AllSaveData)
        {
            return "All save data";
        }

        var firstPath = liveSave.Items.FirstOrDefault()?.SourcePath;
        if (!string.IsNullOrWhiteSpace(firstPath))
        {
            var trimmed = firstPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return string.IsNullOrWhiteSpace(liveSave.Label) ? "Live save data" : liveSave.Label;
    }

    private static string Slug(string value)
    {
        var chars = value.ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();
        return new string(chars).Trim('-');
    }
}
