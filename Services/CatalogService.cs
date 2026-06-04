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
        Save(libraryPath, catalog);
        return catalog;
    }

    public void Save(string libraryPath, BackupCatalog catalog) =>
        JsonFile.Save(GetCatalogPath(libraryPath), catalog);
}
