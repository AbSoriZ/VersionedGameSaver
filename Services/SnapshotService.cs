using System.IO;
using System.IO.Compression;
using System.Text.Json;
using VersionedGameSaver.Models;

namespace VersionedGameSaver.Services;

public sealed class SnapshotService
{
    private const long LargeScopeWarningBytes = 1024L * 1024L * 1024L;
    private const int LargeScopeWarningFiles = 20000;

    private readonly CatalogService _catalogService;

    public SnapshotService(CatalogService catalogService)
    {
        _catalogService = catalogService;
    }

    public ScopeEstimate Estimate(SaveScope scope)
    {
        long bytes = 0;
        var files = 0;

        foreach (var item in scope.Items)
        {
            if (Directory.Exists(item.SourcePath))
            {
                foreach (var file in Directory.EnumerateFiles(item.SourcePath, "*", SearchOption.AllDirectories))
                {
                    files++;
                    bytes += new FileInfo(file).Length;
                }
            }
            else if (File.Exists(item.SourcePath))
            {
                files++;
                bytes += new FileInfo(item.SourcePath).Length;
            }
        }

        return new ScopeEstimate(bytes, files, bytes >= LargeScopeWarningBytes || files >= LargeScopeWarningFiles);
    }

    public SnapshotRecord CreateSnapshot(
        string libraryPath,
        BackupCatalog catalog,
        GameProfile profile,
        SaveScope scope,
        SnapshotKind kind,
        SnapshotRecord? overwrite = null)
    {
        var snapshot = overwrite ?? new SnapshotRecord
        {
            ProfileId = profile.Id,
            ScopeId = scope.Id,
            Kind = kind,
            CreatedAtUtc = DateTime.UtcNow,
            SlotNumber = kind == SnapshotKind.Manual ? NextSlotNumber(catalog, profile.Id, scope.Id) : null
        };

        if (overwrite is not null)
        {
            DeleteArchiveIfPresent(libraryPath, overwrite);
            snapshot.CreatedAtUtc = DateTime.UtcNow;
            snapshot.Kind = kind;
        }

        if (string.IsNullOrWhiteSpace(snapshot.OriginalName))
        {
            snapshot.OriginalName = snapshot.Kind == SnapshotKind.Manual && snapshot.SlotNumber is not null
                ? $"Slot {snapshot.SlotNumber}"
                : $"Auto before restore - {snapshot.CreatedAtUtc.ToLocalTime():g}";
        }

        var snapshotDirectory = Path.Combine(
            _catalogService.GetSnapshotRoot(libraryPath),
            SafePathPart(profile.GameId),
            profile.Id,
            scope.Id);
        Directory.CreateDirectory(snapshotDirectory);

        var archivePath = Path.Combine(snapshotDirectory, $"{snapshot.Id}.zip");
        var manifest = new SnapshotArchiveManifest
        {
            SnapshotId = snapshot.Id,
            CreatedAtUtc = snapshot.CreatedAtUtc
        };

        var fileCount = 0;
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            for (var index = 0; index < scope.Items.Count; index++)
            {
                var item = scope.Items[index];
                if (Directory.Exists(item.SourcePath))
                {
                    var rootName = $"items/{index}-{SafePathPart(Path.GetFileName(item.SourcePath))}";
                    AddDirectory(archive, item.SourcePath, rootName, ref fileCount);
                    manifest.Items.Add(new SnapshotArchiveItem
                    {
                        OriginalPath = item.SourcePath,
                        ArchivePath = rootName,
                        IsDirectory = true
                    });
                }
                else if (File.Exists(item.SourcePath))
                {
                    var rootName = $"items/{index}-{SafePathPart(Path.GetFileName(item.SourcePath))}";
                    archive.CreateEntryFromFile(item.SourcePath, rootName, CompressionLevel.SmallestSize);
                    fileCount++;
                    manifest.Items.Add(new SnapshotArchiveItem
                    {
                        OriginalPath = item.SourcePath,
                        ArchivePath = rootName,
                        IsDirectory = false
                    });
                }
                else
                {
                    throw new FileNotFoundException("The selected save item no longer exists.", item.SourcePath);
                }
            }

            var manifestEntry = archive.CreateEntry("snapshot.json", CompressionLevel.SmallestSize);
            using var stream = manifestEntry.Open();
            JsonSerializer.Serialize(stream, manifest, new JsonSerializerOptions { WriteIndented = true });
        }

        ValidateArchive(archivePath);

        snapshot.ArchiveRelativePath = Path.GetRelativePath(libraryPath, archivePath);
        snapshot.SizeBytes = new FileInfo(archivePath).Length;
        snapshot.FileCount = fileCount;

        if (overwrite is null)
        {
            catalog.Snapshots.Add(snapshot);
        }

        _catalogService.Save(libraryPath, catalog);
        return snapshot;
    }

    public SnapshotRecord RestoreSnapshot(
        string libraryPath,
        BackupCatalog catalog,
        GameProfile profile,
        SaveScope scope,
        SnapshotRecord snapshot)
    {
        var safetySnapshot = CreateSnapshot(libraryPath, catalog, profile, scope, SnapshotKind.Safety);
        var archivePath = Path.Combine(libraryPath, snapshot.ArchiveRelativePath);
        var tempRoot = Path.Combine(libraryPath, CatalogService.MetadataFolderName, "temp", Guid.NewGuid().ToString("N"));
        var extractRoot = Path.Combine(tempRoot, "extract");
        var rollbackRoot = Path.Combine(tempRoot, "rollback");
        var movedItems = new List<(SnapshotArchiveItem Item, string RollbackPath)>();

        Directory.CreateDirectory(extractRoot);
        Directory.CreateDirectory(rollbackRoot);

        try
        {
            ZipFile.ExtractToDirectory(archivePath, extractRoot);
            var manifestPath = Path.Combine(extractRoot, "snapshot.json");
            var manifest = JsonFile.Load<SnapshotArchiveManifest>(manifestPath)
                ?? throw new InvalidOperationException("Snapshot archive is missing metadata.");

            for (var index = 0; index < manifest.Items.Count; index++)
            {
                var item = manifest.Items[index];
                var sourcePath = Path.Combine(extractRoot, item.ArchivePath);
                var destinationPath = item.OriginalPath;
                var rollbackPath = Path.Combine(rollbackRoot, index.ToString());

                if (Directory.Exists(destinationPath))
                {
                    Directory.Move(destinationPath, rollbackPath);
                    movedItems.Add((item, rollbackPath));
                }
                else if (File.Exists(destinationPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(rollbackPath)!);
                    File.Move(destinationPath, rollbackPath);
                    movedItems.Add((item, rollbackPath));
                }

                if (item.IsDirectory)
                {
                    CopyDirectory(sourcePath, destinationPath);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    File.Copy(sourcePath, destinationPath, overwrite: true);
                }
            }

            Directory.Delete(tempRoot, recursive: true);
            return safetySnapshot;
        }
        catch
        {
            TryRollback(movedItems);
            throw;
        }
    }

    public void DeleteSnapshot(string libraryPath, BackupCatalog catalog, SnapshotRecord snapshot)
    {
        DeleteArchiveIfPresent(libraryPath, snapshot);
        catalog.Snapshots.Remove(snapshot);
        _catalogService.Save(libraryPath, catalog);
    }

    public void DeleteScope(string libraryPath, BackupCatalog catalog, GameProfile profile, SaveScope scope)
    {
        foreach (var snapshot in catalog.Snapshots.Where(s => s.ProfileId == profile.Id && s.ScopeId == scope.Id).ToList())
        {
            DeleteArchiveIfPresent(libraryPath, snapshot);
            catalog.Snapshots.Remove(snapshot);
        }

        profile.Scopes.Remove(scope);
        _catalogService.Save(libraryPath, catalog);
    }

    public void DeleteProfile(string libraryPath, BackupCatalog catalog, GameProfile profile)
    {
        foreach (var snapshot in catalog.Snapshots.Where(s => s.ProfileId == profile.Id).ToList())
        {
            DeleteArchiveIfPresent(libraryPath, snapshot);
            catalog.Snapshots.Remove(snapshot);
        }

        catalog.Profiles.Remove(profile);
        _catalogService.Save(libraryPath, catalog);
    }

    private static void AddDirectory(ZipArchive archive, string sourceDirectory, string archiveRoot, ref int fileCount)
    {
        var files = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories).ToList();
        if (files.Count == 0)
        {
            archive.CreateEntry($"{archiveRoot}/");
            return;
        }

        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');
            archive.CreateEntryFromFile(file, $"{archiveRoot}/{relativePath}", CompressionLevel.SmallestSize);
            fileCount++;
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(file, destinationPath, overwrite: true);
        }
    }

    private static void TryRollback(List<(SnapshotArchiveItem Item, string RollbackPath)> movedItems)
    {
        foreach (var (item, rollbackPath) in movedItems.AsEnumerable().Reverse())
        {
            try
            {
                if (item.IsDirectory && Directory.Exists(item.OriginalPath))
                {
                    Directory.Delete(item.OriginalPath, recursive: true);
                }
                else if (!item.IsDirectory && File.Exists(item.OriginalPath))
                {
                    File.Delete(item.OriginalPath);
                }

                if (Directory.Exists(rollbackPath))
                {
                    Directory.Move(rollbackPath, item.OriginalPath);
                }
                else if (File.Exists(rollbackPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(item.OriginalPath)!);
                    File.Move(rollbackPath, item.OriginalPath);
                }
            }
            catch
            {
                // Best effort: the safety snapshot remains available if rollback cannot fully complete.
            }
        }
    }

    private static void ValidateArchive(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.GetEntry("snapshot.json") is null)
        {
            throw new InvalidOperationException("Snapshot archive failed validation.");
        }
    }

    private static void DeleteArchiveIfPresent(string libraryPath, SnapshotRecord snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.ArchiveRelativePath))
        {
            return;
        }

        var path = Path.Combine(libraryPath, snapshot.ArchiveRelativePath);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string SafePathPart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "item" : cleaned;
    }

    private static int NextSlotNumber(BackupCatalog catalog, string profileId, string scopeId)
    {
        var usedSlots = catalog.Snapshots
            .Where(snapshot => snapshot.ProfileId == profileId
                && snapshot.ScopeId == scopeId
                && snapshot.Kind == SnapshotKind.Manual
                && snapshot.SlotNumber is not null)
            .Select(snapshot => snapshot.SlotNumber!.Value);

        return usedSlots.DefaultIfEmpty(-1).Max() + 1;
    }
}

public sealed record ScopeEstimate(long Bytes, int Files, bool IsLarge);
