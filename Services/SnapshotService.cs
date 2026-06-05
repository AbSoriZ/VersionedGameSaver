using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Win32;
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

    public ScopeEstimate Estimate(
        LiveSaveEntry liveSave,
        IProgress<OperationStatus>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new OperationStatus($"Estimating {liveSave.DisplayName}..."));
        EnsureSupportedLiveSave(liveSave);
        long bytes = 0;
        var files = 0;

        foreach (var item in liveSave.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Kind == LiveSaveItemKind.Directory && Directory.Exists(item.SourcePath))
            {
                foreach (var file in Directory.EnumerateFiles(item.SourcePath, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    files++;
                    bytes += new FileInfo(file).Length;
                    if (files % 500 == 0)
                    {
                        progress?.Report(new OperationStatus($"Estimated {files:N0} file(s)..."));
                    }
                }
            }
            else if (item.Kind == LiveSaveItemKind.File && File.Exists(item.SourcePath))
            {
                files++;
                bytes += new FileInfo(item.SourcePath).Length;
            }
            else if (item.Kind == LiveSaveItemKind.RegistryKey)
            {
                throw UnsupportedRegistryEntry();
            }
        }

        progress?.Report(new OperationStatus($"Estimate complete: {files:N0} item(s), {FileSizeFormatter.Format(bytes)}."));
        return new ScopeEstimate(bytes, files, bytes >= LargeScopeWarningBytes || files >= LargeScopeWarningFiles);
    }

    public SaveVersion CreateSnapshot(
        string libraryPath,
        BackupCatalog catalog,
        GameEntry game,
        LiveSaveEntry liveSave,
        SaveVersionKind kind,
        SaveVersion? overwrite = null,
        IProgress<OperationStatus>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new OperationStatus($"Preparing snapshot for {liveSave.DisplayName}..."));
        EnsureSupportedLiveSave(liveSave);
        var version = overwrite ?? new SaveVersion
        {
            GameId = game.Id,
            LiveSaveEntryId = liveSave.Id,
            Kind = kind,
            CreatedAtUtc = DateTime.UtcNow,
            SlotNumber = kind == SaveVersionKind.Manual ? NextSlotNumber(catalog, game.Id, liveSave.Id) : null
        };

        if (overwrite is not null)
        {
            DeleteArchiveIfPresent(libraryPath, overwrite);
            version.CreatedAtUtc = DateTime.UtcNow;
            version.Kind = kind;
            version.IsPlaceholder = false;
            version.OriginalName = kind == SaveVersionKind.Manual && version.SlotNumber is not null
                ? $"Slot {version.SlotNumber}"
                : $"Auto before restore - {version.CreatedAtUtc.ToLocalTime():g}";
        }

        if (string.IsNullOrWhiteSpace(version.OriginalName))
        {
            version.OriginalName = version.Kind == SaveVersionKind.Manual && version.SlotNumber is not null
                ? $"Slot {version.SlotNumber}"
                : $"Auto before restore - {version.CreatedAtUtc.ToLocalTime():g}";
        }

        var snapshotDirectory = Path.Combine(
            _catalogService.GetSnapshotRoot(libraryPath),
            SafePathPart(string.IsNullOrWhiteSpace(game.GameKey) ? game.DisplayName : game.GameKey),
            game.Id,
            liveSave.Id);
        Directory.CreateDirectory(snapshotDirectory);

        var archivePath = Path.Combine(snapshotDirectory, $"{version.Id}.zip");
        var manifest = new SnapshotArchiveManifest
        {
            SnapshotId = version.Id,
            CreatedAtUtc = version.CreatedAtUtc
        };

        var fileCount = 0;
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            for (var index = 0; index < liveSave.Items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = liveSave.Items[index];
                if (item.Kind == LiveSaveItemKind.Directory && Directory.Exists(item.SourcePath))
                {
                    progress?.Report(new OperationStatus($"Archiving folder: {item.SourcePath}"));
                    var rootName = $"items/{index}-{SafePathPart(Path.GetFileName(item.SourcePath))}";
                    AddDirectory(archive, item.SourcePath, rootName, ref fileCount, progress, cancellationToken);
                    manifest.Items.Add(new SnapshotArchiveItem
                    {
                        OriginalPath = item.SourcePath,
                        ArchivePath = rootName,
                        IsDirectory = true,
                        Kind = LiveSaveItemKind.Directory
                    });
                }
                else if (item.Kind == LiveSaveItemKind.File && File.Exists(item.SourcePath))
                {
                    progress?.Report(new OperationStatus($"Archiving file: {item.SourcePath}"));
                    var rootName = $"items/{index}-{SafePathPart(Path.GetFileName(item.SourcePath))}";
                    archive.CreateEntryFromFile(item.SourcePath, rootName, CompressionLevel.SmallestSize);
                    fileCount++;
                    manifest.Items.Add(new SnapshotArchiveItem
                    {
                        OriginalPath = item.SourcePath,
                        ArchivePath = rootName,
                        IsDirectory = false,
                        Kind = LiveSaveItemKind.File
                    });
                }
                else if (item.Kind == LiveSaveItemKind.RegistryKey)
                {
                    throw UnsupportedRegistryEntry();
                }
                else
                {
                    throw new FileNotFoundException("The selected live save item no longer exists.", item.SourcePath);
                }
            }

            var manifestEntry = archive.CreateEntry("snapshot.json", CompressionLevel.SmallestSize);
            using var stream = manifestEntry.Open();
            JsonSerializer.Serialize(stream, manifest, new JsonSerializerOptions { WriteIndented = true });
        }

        progress?.Report(new OperationStatus("Validating snapshot archive..."));
        ValidateArchive(archivePath);

        version.ArchiveRelativePath = Path.GetRelativePath(libraryPath, archivePath);
        version.SizeBytes = new FileInfo(archivePath).Length;
        version.FileCount = fileCount;
        version.IsPlaceholder = false;

        if (overwrite is null)
        {
            catalog.Versions.Add(version);
        }

        _catalogService.Save(libraryPath, catalog);
        progress?.Report(new OperationStatus($"Snapshot complete: {version.VersionName}."));
        return version;
    }

    public SaveVersion RestoreSnapshot(
        string libraryPath,
        BackupCatalog catalog,
        GameEntry game,
        LiveSaveEntry liveSave,
        SaveVersion version,
        IProgress<OperationStatus>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (version.IsPlaceholder)
        {
            throw new InvalidOperationException("Empty slots cannot be restored until a save has been imported or backed up into them.");
        }

        progress?.Report(new OperationStatus("Creating safety snapshot before restore..."));
        EnsureSupportedLiveSave(liveSave);
        var safetySnapshot = CreateSnapshot(libraryPath, catalog, game, liveSave, SaveVersionKind.Safety, progress: progress, cancellationToken: cancellationToken);
        var archivePath = Path.Combine(libraryPath, version.ArchiveRelativePath);
        var tempRoot = Path.Combine(libraryPath, CatalogService.MetadataFolderName, "temp", Guid.NewGuid().ToString("N"));
        var extractRoot = Path.Combine(tempRoot, "extract");
        var rollbackRoot = Path.Combine(tempRoot, "rollback");
        var movedItems = new List<(SnapshotArchiveItem Item, string RollbackPath)>();

        Directory.CreateDirectory(extractRoot);
        Directory.CreateDirectory(rollbackRoot);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new OperationStatus($"Extracting version: {version.VersionName}"));
            ZipFile.ExtractToDirectory(archivePath, extractRoot);
            var manifestPath = Path.Combine(extractRoot, "snapshot.json");
            var manifest = JsonFile.Load<SnapshotArchiveManifest>(manifestPath)
                ?? throw new InvalidOperationException("Snapshot archive is missing metadata.");

            for (var index = 0; index < manifest.Items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = manifest.Items[index];
                var sourcePath = Path.Combine(extractRoot, item.ArchivePath);
                var destinationPath = item.OriginalPath;
                var rollbackPath = Path.Combine(rollbackRoot, index.ToString());
                progress?.Report(new OperationStatus($"Restoring item {index + 1:N0}/{manifest.Items.Count:N0}: {destinationPath}"));

                if (item.Kind == LiveSaveItemKind.RegistryKey)
                {
                    throw UnsupportedRegistryEntry();
                }

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

                if (item.Kind == LiveSaveItemKind.Directory || item.IsDirectory)
                {
                    CopyDirectory(sourcePath, destinationPath, progress, cancellationToken);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    File.Copy(sourcePath, destinationPath, overwrite: true);
                }
            }

            Directory.Delete(tempRoot, recursive: true);
            progress?.Report(new OperationStatus($"Restore complete: {version.VersionName}."));
            return safetySnapshot;
        }
        catch
        {
            TryRollback(movedItems);
            throw;
        }
    }

    public void DeleteSnapshot(string libraryPath, BackupCatalog catalog, SaveVersion version)
    {
        DeleteArchiveIfPresent(libraryPath, version);
        catalog.Versions.Remove(version);
        _catalogService.Save(libraryPath, catalog);
    }

    public SaveVersion ImportSnapshot(
        string libraryPath,
        BackupCatalog catalog,
        GameEntry game,
        LiveSaveEntry liveSave,
        SaveVersion slot,
        string sourcePath,
        SnapshotImportKind importKind,
        IProgress<OperationStatus>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new OperationStatus($"Importing prior save into {slot.VersionName}..."));
        EnsureSingleFileOrFolderSave(liveSave);
        var targetItem = liveSave.Items[0];
        ValidateImportSourceMatchesTarget(sourcePath, importKind, targetItem);

        var snapshotDirectory = Path.Combine(
            _catalogService.GetSnapshotRoot(libraryPath),
            SafePathPart(string.IsNullOrWhiteSpace(game.GameKey) ? game.DisplayName : game.GameKey),
            game.Id,
            liveSave.Id);
        Directory.CreateDirectory(snapshotDirectory);

        DeleteArchiveIfPresent(libraryPath, slot);

        slot.GameId = game.Id;
        slot.LiveSaveEntryId = liveSave.Id;
        slot.Kind = SaveVersionKind.Manual;
        slot.CreatedAtUtc = DateTime.UtcNow;
        slot.IsPlaceholder = false;
        slot.OriginalName = slot.SlotNumber is not null ? $"Slot {slot.SlotNumber}" : "";

        var archivePath = Path.Combine(snapshotDirectory, $"{slot.Id}.zip");
        var manifest = new SnapshotArchiveManifest
        {
            SnapshotId = slot.Id,
            CreatedAtUtc = slot.CreatedAtUtc
        };

        var fileCount = 0;
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var rootName = $"items/0-{SafePathPart(Path.GetFileName(targetItem.SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))}";
            if (importKind == SnapshotImportKind.Zip)
            {
                ImportZip(archive, sourcePath, targetItem, rootName, ref fileCount, progress, cancellationToken);
            }
            else if (targetItem.Kind == LiveSaveItemKind.Directory)
            {
                if (!Directory.Exists(sourcePath))
                {
                    throw new DirectoryNotFoundException($"The import folder does not exist: {sourcePath}");
                }

                AddDirectory(archive, sourcePath, rootName, ref fileCount, progress, cancellationToken);
            }
            else if (targetItem.Kind == LiveSaveItemKind.File)
            {
                if (!File.Exists(sourcePath))
                {
                    throw new FileNotFoundException("The import file does not exist.", sourcePath);
                }

                archive.CreateEntryFromFile(sourcePath, rootName, CompressionLevel.SmallestSize);
                fileCount++;
            }

            manifest.Items.Add(new SnapshotArchiveItem
            {
                OriginalPath = targetItem.SourcePath,
                ArchivePath = rootName,
                IsDirectory = targetItem.Kind == LiveSaveItemKind.Directory,
                Kind = targetItem.Kind
            });

            var manifestEntry = archive.CreateEntry("snapshot.json", CompressionLevel.SmallestSize);
            using var stream = manifestEntry.Open();
            JsonSerializer.Serialize(stream, manifest, new JsonSerializerOptions { WriteIndented = true });
        }

        ValidateArchive(archivePath);
        slot.ArchiveRelativePath = Path.GetRelativePath(libraryPath, archivePath);
        slot.SizeBytes = new FileInfo(archivePath).Length;
        slot.FileCount = fileCount;
        _catalogService.Save(libraryPath, catalog);
        progress?.Report(new OperationStatus($"Import complete: {slot.VersionName}."));
        return slot;
    }

    public void DeleteLiveSave(string libraryPath, BackupCatalog catalog, GameEntry game, LiveSaveEntry liveSave)
    {
        foreach (var version in catalog.Versions.Where(s => s.GameId == game.Id && s.LiveSaveEntryId == liveSave.Id).ToList())
        {
            DeleteArchiveIfPresent(libraryPath, version);
            catalog.Versions.Remove(version);
        }

        game.LiveSaves.Remove(liveSave);
        _catalogService.Save(libraryPath, catalog);
    }

    public void DeleteGame(string libraryPath, BackupCatalog catalog, GameEntry game)
    {
        foreach (var version in catalog.Versions.Where(s => s.GameId == game.Id).ToList())
        {
            DeleteArchiveIfPresent(libraryPath, version);
            catalog.Versions.Remove(version);
        }

        catalog.Games.Remove(game);
        _catalogService.Save(libraryPath, catalog);
    }

    private static void AddDirectory(
        ZipArchive archive,
        string sourceDirectory,
        string archiveRoot,
        ref int fileCount,
        IProgress<OperationStatus>? progress,
        CancellationToken cancellationToken)
    {
        var hasFiles = false;
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            hasFiles = true;
            var relativePath = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');
            archive.CreateEntryFromFile(file, $"{archiveRoot}/{relativePath}", CompressionLevel.SmallestSize);
            fileCount++;
            if (fileCount % 500 == 0)
            {
                progress?.Report(new OperationStatus($"Archived {fileCount:N0} item(s)..."));
            }
        }

        if (!hasFiles)
        {
            archive.CreateEntry($"{archiveRoot}/");
        }
    }

    private static void CopyDirectory(
        string sourceDirectory,
        string destinationDirectory,
        IProgress<OperationStatus>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        var copied = 0;

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(file, destinationPath, overwrite: true);
            copied++;
            if (copied % 500 == 0)
            {
                progress?.Report(new OperationStatus($"Restored {copied:N0} file(s)..."));
            }
        }
    }

    private static void ValidateImportSourceMatchesTarget(
        string sourcePath,
        SnapshotImportKind importKind,
        LiveSaveItem targetItem)
    {
        var expectedName = ExpectedImportName(targetItem);
        if (string.IsNullOrWhiteSpace(expectedName))
        {
            return;
        }

        var matched = importKind == SnapshotImportKind.Zip
            ? ZipImportMatchesTarget(sourcePath, expectedName)
            : FileOrFolderImportMatchesTarget(sourcePath, targetItem, expectedName);

        if (!matched)
        {
            var expectedType = targetItem.Kind == LiveSaveItemKind.File ? "file" : "folder or ZIP";
            throw new InvalidOperationException(
                $"The selected import does not match this save entry. Expected a {expectedType} named \"{expectedName}\". Choose the matching save source, or rename/package the import so its name or ZIP root matches \"{expectedName}\".");
        }
    }

    private static string ExpectedImportName(LiveSaveItem targetItem) =>
        Path.GetFileName(targetItem.SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static bool FileOrFolderImportMatchesTarget(string sourcePath, LiveSaveItem targetItem, string expectedName)
    {
        var sourceName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.Equals(sourceName, expectedName, StringComparison.OrdinalIgnoreCase)
            && (targetItem.Kind != LiveSaveItemKind.File || File.Exists(sourcePath))
            && (targetItem.Kind != LiveSaveItemKind.Directory || Directory.Exists(sourcePath));
    }

    private static bool ZipImportMatchesTarget(string sourceZipPath, string expectedName)
    {
        if (!File.Exists(sourceZipPath))
        {
            throw new FileNotFoundException("The import ZIP does not exist.", sourceZipPath);
        }

        var zipFileName = Path.GetFileNameWithoutExtension(sourceZipPath);
        if (string.Equals(zipFileName, expectedName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        using var sourceArchive = ZipFile.OpenRead(sourceZipPath);
        if (SnapshotManifestMatchesTarget(sourceArchive, expectedName))
        {
            return true;
        }

        var topLevelNames = sourceArchive.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Where(entry => !string.Equals(entry.FullName, "snapshot.json", StringComparison.OrdinalIgnoreCase))
            .Select(TopLevelArchiveName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return topLevelNames.Count == 1
            && string.Equals(topLevelNames[0], expectedName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool SnapshotManifestMatchesTarget(ZipArchive sourceArchive, string expectedName)
    {
        var manifestEntry = sourceArchive.GetEntry("snapshot.json");
        if (manifestEntry is null)
        {
            return false;
        }

        try
        {
            using var stream = manifestEntry.Open();
            var manifest = JsonSerializer.Deserialize<SnapshotArchiveManifest>(stream);
            return manifest?.Items.Any(item =>
            {
                var originalName = Path.GetFileName(item.OriginalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return string.Equals(originalName, expectedName, StringComparison.OrdinalIgnoreCase);
            }) == true;
        }
        catch
        {
            return false;
        }
    }

    private static string TopLevelArchiveName(ZipArchiveEntry entry)
    {
        var normalized = entry.FullName.Replace('\\', '/').Trim('/');
        var separatorIndex = normalized.IndexOf('/');
        return separatorIndex < 0 ? normalized : normalized[..separatorIndex];
    }

    private static void ImportZip(
        ZipArchive destinationArchive,
        string sourceZipPath,
        LiveSaveItem targetItem,
        string archiveRoot,
        ref int fileCount,
        IProgress<OperationStatus>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourceZipPath))
        {
            throw new FileNotFoundException("The import ZIP does not exist.", sourceZipPath);
        }

        using var sourceArchive = ZipFile.OpenRead(sourceZipPath);
        var entries = sourceArchive.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Where(entry => !string.Equals(entry.FullName, "snapshot.json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (targetItem.Kind == LiveSaveItemKind.File && entries.Count != 1)
        {
            throw new InvalidOperationException("File save imports from ZIP must contain exactly one file.");
        }

        if (entries.Count == 0)
        {
            throw new InvalidOperationException("The import ZIP does not contain any files.");
        }

        foreach (var sourceEntry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destinationName = targetItem.Kind == LiveSaveItemKind.File
                ? archiveRoot
                : $"{archiveRoot}/{sourceEntry.FullName.Replace('\\', '/')}";
            var destinationEntry = destinationArchive.CreateEntry(destinationName, CompressionLevel.SmallestSize);
            using var input = sourceEntry.Open();
            using var output = destinationEntry.Open();
            input.CopyTo(output);
            fileCount++;
            if (fileCount % 500 == 0)
            {
                progress?.Report(new OperationStatus($"Imported {fileCount:N0} file(s)..."));
            }
        }
    }

    private static void EnsureSingleFileOrFolderSave(LiveSaveEntry liveSave)
    {
        EnsureSupportedLiveSave(liveSave);
        if (liveSave.Kind == LiveSaveEntryKind.AllSaveData || liveSave.Items.Count != 1)
        {
            throw new InvalidOperationException("Import prior save is only available for save entries with one folder or one file.");
        }

        if (liveSave.Items[0].Kind is not (LiveSaveItemKind.Directory or LiveSaveItemKind.File))
        {
            throw new InvalidOperationException("Import prior save is only available for folder or file save entries.");
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

    private static void DeleteArchiveIfPresent(string libraryPath, SaveVersion version)
    {
        if (string.IsNullOrWhiteSpace(version.ArchiveRelativePath))
        {
            return;
        }

        var path = Path.Combine(libraryPath, version.ArchiveRelativePath);
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

    private static int NextSlotNumber(BackupCatalog catalog, string gameId, string liveSaveEntryId)
    {
        var usedSlots = catalog.Versions
            .Where(version => version.GameId == gameId
                && version.LiveSaveEntryId == liveSaveEntryId
                && version.Kind == SaveVersionKind.Manual
                && version.SlotNumber is not null)
            .Select(version => version.SlotNumber!.Value);

        return usedSlots.DefaultIfEmpty(0).Max() + 1;
    }

    private static void EnsureSupportedLiveSave(LiveSaveEntry liveSave)
    {
        if (liveSave.Items.Any(item => item.Kind == LiveSaveItemKind.RegistryKey))
        {
            throw UnsupportedRegistryEntry();
        }
    }

    private static NotSupportedException UnsupportedRegistryEntry() =>
        new("Registry save entries are disabled in this version. Use folder or file save entries instead.");

    private static int CountRegistryValues(string keyPath, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var key = OpenRegistryKey(keyPath, writable: false);
            return key is null ? 0 : CountRegistryValues(key, cancellationToken);
        }
        catch
        {
            return 0;
        }
    }

    private static int CountRegistryValues(RegistryKey key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var count = key.GetValueNames().Length;
        foreach (var subKeyName in key.GetSubKeyNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var subKey = key.OpenSubKey(subKeyName);
            if (subKey is not null)
            {
                count += CountRegistryValues(subKey, cancellationToken);
            }
        }

        return count;
    }

    private static RegistrySnapshot ReadRegistrySnapshot(
        RegistryKey key,
        string keyPath,
        ref int valueCount,
        IProgress<OperationStatus>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = new RegistrySnapshot { KeyPath = keyPath };
        foreach (var valueName in key.GetValueNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            snapshot.Values.Add(ReadRegistryValue(key, valueName));
            valueCount++;
            if (valueCount % 100 == 0)
            {
                progress?.Report(new OperationStatus($"Archived {valueCount:N0} registry value(s)..."));
            }
        }

        foreach (var subKeyName in key.GetSubKeyNames())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var subKey = key.OpenSubKey(subKeyName);
            if (subKey is not null)
            {
                snapshot.SubKeys.Add(ReadRegistrySnapshot(subKey, $"{keyPath}/{subKeyName}", ref valueCount, progress, cancellationToken));
            }
        }

        return snapshot;
    }

    private static RegistrySnapshotValue ReadRegistryValue(RegistryKey key, string valueName)
    {
        var kind = key.GetValueKind(valueName);
        var value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        var snapshotValue = new RegistrySnapshotValue
        {
            Name = valueName,
            Kind = kind.ToString()
        };

        switch (kind)
        {
            case RegistryValueKind.String:
            case RegistryValueKind.ExpandString:
                snapshotValue.StringValue = value?.ToString();
                break;
            case RegistryValueKind.MultiString:
                snapshotValue.StringValues = value is string[] values ? values.ToList() : [];
                break;
            case RegistryValueKind.Binary:
                snapshotValue.BinaryBase64 = value is byte[] bytes ? Convert.ToBase64String(bytes) : "";
                break;
            case RegistryValueKind.DWord:
                snapshotValue.IntValue = Convert.ToInt32(value);
                break;
            case RegistryValueKind.QWord:
                snapshotValue.LongValue = Convert.ToInt64(value);
                break;
            default:
                snapshotValue.StringValue = value?.ToString();
                break;
        }

        return snapshotValue;
    }

    private static void RestoreRegistrySnapshot(string keyPath, RegistrySnapshot snapshot)
    {
        var parsed = ParseRegistryPath(keyPath);
        if (string.IsNullOrWhiteSpace(parsed.SubKeyPath))
        {
            throw new InvalidOperationException("Refusing to restore a registry hive root.");
        }

        using var root = RegistryKey.OpenBaseKey(parsed.Hive, RegistryView.Default);
        root.DeleteSubKeyTree(parsed.SubKeyPath, throwOnMissingSubKey: false);
        using var key = root.CreateSubKey(parsed.SubKeyPath, writable: true)
            ?? throw new InvalidOperationException($"Could not create registry key: {keyPath}");
        WriteRegistrySnapshot(key, snapshot);
    }

    private static void WriteRegistrySnapshot(RegistryKey key, RegistrySnapshot snapshot)
    {
        foreach (var value in snapshot.Values)
        {
            WriteRegistryValue(key, value);
        }

        foreach (var subKeySnapshot in snapshot.SubKeys)
        {
            using var subKey = key.CreateSubKey(RegistryLeafName(subKeySnapshot.KeyPath), writable: true);
            if (subKey is not null)
            {
                WriteRegistrySnapshot(subKey, subKeySnapshot);
            }
        }
    }

    private static void WriteRegistryValue(RegistryKey key, RegistrySnapshotValue value)
    {
        var kind = Enum.TryParse<RegistryValueKind>(value.Kind, out var parsedKind)
            ? parsedKind
            : RegistryValueKind.String;

        object data = kind switch
        {
            RegistryValueKind.MultiString => value.StringValues?.ToArray() ?? [],
            RegistryValueKind.Binary => string.IsNullOrWhiteSpace(value.BinaryBase64) ? Array.Empty<byte>() : Convert.FromBase64String(value.BinaryBase64),
            RegistryValueKind.DWord => value.IntValue ?? 0,
            RegistryValueKind.QWord => value.LongValue ?? 0L,
            _ => value.StringValue ?? ""
        };

        key.SetValue(value.Name, data, kind);
    }

    public static bool RegistryKeyExists(string keyPath)
    {
        using var key = OpenRegistryKey(keyPath, writable: false);
        return key is not null;
    }

    private static RegistryKey? OpenRegistryKey(string keyPath, bool writable)
    {
        var parsed = ParseRegistryPath(keyPath);
        using var root = RegistryKey.OpenBaseKey(parsed.Hive, RegistryView.Default);
        return string.IsNullOrWhiteSpace(parsed.SubKeyPath)
            ? root
            : root.OpenSubKey(parsed.SubKeyPath, writable);
    }

    private static (RegistryHive Hive, string SubKeyPath) ParseRegistryPath(string keyPath)
    {
        var normalized = keyPath.Replace('\\', '/');
        var parts = normalized.Split('/', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            throw new InvalidOperationException("Registry path is empty.");
        }

        var hive = parts[0].ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHive.CurrentUser,
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHive.LocalMachine,
            "HKCR" or "HKEY_CLASSES_ROOT" => RegistryHive.ClassesRoot,
            "HKU" or "HKEY_USERS" => RegistryHive.Users,
            "HKCC" or "HKEY_CURRENT_CONFIG" => RegistryHive.CurrentConfig,
            _ => throw new InvalidOperationException($"Unsupported registry hive: {parts[0]}")
        };

        return (hive, parts.Length > 1 ? parts[1].Replace('/', '\\') : "");
    }

    private static string RegistryLeafName(string keyPath)
    {
        var normalized = keyPath.TrimEnd('/', '\\').Replace('\\', '/');
        var index = normalized.LastIndexOf('/');
        return index < 0 ? normalized : normalized[(index + 1)..];
    }
}

public sealed record ScopeEstimate(long Bytes, int Files, bool IsLarge);

public enum SnapshotImportKind
{
    FileOrFolder,
    Zip
}
