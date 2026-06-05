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

    private static bool Normalize(BackupCatalog catalog)
    {
        var changed = false;

        foreach (var scope in catalog.Profiles.SelectMany(profile => profile.Scopes))
        {
            var inferredOriginalName = InferOriginalName(scope);
            if (string.IsNullOrWhiteSpace(scope.OriginalName))
            {
                scope.OriginalName = inferredOriginalName;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(scope.Alias)
                && !string.IsNullOrWhiteSpace(scope.Label)
                && !string.Equals(scope.Label, scope.OriginalName, StringComparison.Ordinal))
            {
                scope.Alias = scope.Label;
                changed = true;
            }

            var desiredLabel = scope.DisplayName;
            if (!string.Equals(scope.Label, desiredLabel, StringComparison.Ordinal))
            {
                scope.Label = desiredLabel;
                changed = true;
            }
        }

        foreach (var profile in catalog.Profiles)
        {
            foreach (var snapshotGroup in catalog.Snapshots.Where(snapshot => snapshot.ProfileId == profile.Id).GroupBy(snapshot => snapshot.ScopeId))
            {
                var nextSlot = 0;
                foreach (var snapshot in snapshotGroup.OrderBy(snapshot => snapshot.CreatedAtUtc))
                {
                    if (!string.IsNullOrWhiteSpace(snapshot.Name) && string.IsNullOrWhiteSpace(snapshot.Alias))
                    {
                        snapshot.Alias = snapshot.Name;
                        changed = true;
                    }

                    if (snapshot.Kind == SnapshotKind.Manual && snapshot.SlotNumber is null)
                    {
                        snapshot.SlotNumber = nextSlot;
                        changed = true;
                    }

                    if (snapshot.Kind == SnapshotKind.Manual && snapshot.SlotNumber is not null)
                    {
                        nextSlot = Math.Max(nextSlot, snapshot.SlotNumber.Value + 1);
                    }

                    if (string.IsNullOrWhiteSpace(snapshot.OriginalName))
                    {
                        snapshot.OriginalName = snapshot.Kind == SnapshotKind.Manual && snapshot.SlotNumber is not null
                            ? $"Slot {snapshot.SlotNumber}"
                            : $"Auto before restore - {snapshot.CreatedAtUtc.ToLocalTime():g}";
                        changed = true;
                    }
                }
            }
        }

        return changed;
    }

    private static string InferOriginalName(SaveScope scope)
    {
        var firstPath = scope.Items.FirstOrDefault()?.SourcePath;
        if (!string.IsNullOrWhiteSpace(firstPath))
        {
            var trimmed = firstPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        if (!string.IsNullOrWhiteSpace(scope.Label))
        {
            return scope.Label;
        }

        return "Save Entry";
    }
}
