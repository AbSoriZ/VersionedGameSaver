using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using VersionedGameSaver.Dialogs;
using VersionedGameSaver.Models;
using VersionedGameSaver.Services;

namespace VersionedGameSaver;

public partial class MainWindow : Window
{
    private readonly SettingsService _settingsService = new();
    private readonly CatalogService _catalogService = new();
    private readonly GameSaveManifestScanner _manifestScanner = new();
    private readonly SnapshotService _snapshotService;

    private AppSettings _settings = new();
    private BackupCatalog _catalog = new();
    private bool _isUpdatingAliasText;
    private bool _isUpdatingNotesText;
    private bool _isBusy;
    private string _manualSortColumn = "Date";
    private bool _manualSortAscending;
    private string _safetySortColumn = "Date";
    private bool _safetySortAscending;
    private CancellationTokenSource? _scanCancellation;
    private readonly List<string> _operationLogLines = [];
    private const int MaxOperationLogLines = 200;

    public MainWindow()
    {
        InitializeComponent();
        _snapshotService = new SnapshotService(_catalogService);
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = _settingsService.Load();
        if (!string.IsNullOrWhiteSpace(_settings.LibraryPath) && Directory.Exists(_settings.LibraryPath))
        {
            OpenLibrary(_settings.LibraryPath);
        }
        else
        {
            RefreshAll();
            MessageBox.Show(
                "Choose a backup library folder to get started. This can be a normal folder or a cloud-synced folder.",
                "Versioned Game Saver",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            ChooseLibrary();
        }
    }

    private void ChooseLibrary_Click(object sender, RoutedEventArgs e) => ChooseLibrary();

    private void ChooseLibrary()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder for your game-save snapshot library"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _settings.LibraryPath = dialog.FolderName;
        _settingsService.Save(_settings);
        OpenLibrary(dialog.FolderName);
    }

    private void OpenLibrary(string libraryPath)
    {
        _catalog = _catalogService.LoadOrCreate(libraryPath);
        RefreshAll();
    }

    private async void ScanGames_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (!EnsureLibrary())
        {
            return;
        }

        ClearOperationLog();
        _scanCancellation = new CancellationTokenSource();
        var token = _scanCancellation.Token;
        var progress = new Progress<OperationStatus>(status => ReportOperationStatus(status.Message));

        try
        {
            SetBusy("Starting game scan...", canCancelScan: true);
            var detected = await Task.Run(() => _manifestScanner.Scan(progress, token), token);
            token.ThrowIfCancellationRequested();

            ReportOperationStatus("Merging scan results...");
            var (addedGames, addedLiveSaves) = MergeScanResults(detected);
            _catalogService.Save(_settings.LibraryPath!, _catalog);
            RefreshAll();

            var summary = $"Scan complete. Added games: {addedGames:N0}; live save entries: {addedLiveSaves:N0}.";
            ReportOperationStatus(summary);
            MessageBox.Show(
                $"Scan complete.\n\nAdded games: {addedGames}\nAdded live save entries: {addedLiveSaves}",
                "Scan Games",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            ReportOperationStatus("Scan canceled. No scan results were merged.");
            StatusText.Text = "Scan canceled";
        }
        catch (Exception exception)
        {
            ShowError("Scan failed", exception);
        }
        finally
        {
            _scanCancellation?.Dispose();
            _scanCancellation = null;
            ClearBusy();
        }
    }

    private void CancelScan_Click(object sender, RoutedEventArgs e)
    {
        if (_scanCancellation is null)
        {
            return;
        }

        CancelScanButton.IsEnabled = false;
        ReportOperationStatus("Cancel requested...");
        _scanCancellation.Cancel();
    }

    private (int AddedGames, int AddedLiveSaves) MergeScanResults(ManifestScanResult detected)
    {
        var addedGames = 0;
        var addedLiveSaves = 0;

        foreach (var game in detected.Games)
        {
            var existing = _catalog.Games.FirstOrDefault(existingGame =>
                string.Equals(existingGame.GameKey, game.GameKey, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                _catalog.Games.Add(game);
                addedGames++;
                addedLiveSaves += game.LiveSaves.Count;
                continue;
            }

            foreach (var liveSave in game.LiveSaves)
            {
                var sourcePaths = liveSave.Items.Select(i => i.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var alreadyExists = existing.LiveSaves.Any(existingLiveSave =>
                    existingLiveSave.Items.Select(i => i.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(sourcePaths));

                if (!alreadyExists)
                {
                    existing.LiveSaves.Add(liveSave);
                    addedLiveSaves++;
                }
            }
        }

        return (addedGames, addedLiveSaves);
    }

    private void AddCustomGame_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLibrary())
        {
            return;
        }

        var gameName = TextInputDialog.Ask(this, "Game Name", "Enter the game name:", "Custom Game");
        if (string.IsNullOrWhiteSpace(gameName))
        {
            return;
        }

        var label = TextInputDialog.Ask(this, "Game Label", "Enter the label to show in the game list:", gameName);
        if (string.IsNullOrWhiteSpace(label))
        {
            label = gameName;
        }

        var game = new GameEntry
        {
            GameKey = Slug(gameName),
            Name = gameName,
            OriginalName = gameName,
            Alias = string.Equals(label, gameName, StringComparison.Ordinal) ? null : label,
            IsDetected = false
        };

        _catalog.Games.Add(game);
        _catalogService.Save(_settings.LibraryPath!, _catalog);
        RefreshAll(game.Id);
        ReportOperationStatus($"Custom game added: {game.DisplayName}. Add a folder or file under Live Save Data.");
    }

    private void AddFolderScope_Click(object sender, RoutedEventArgs e)
    {
        var game = SelectedGame();
        if (game is null)
        {
            MessageBox.Show("Select a game first.", "Add Folder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var folderDialog = new OpenFolderDialog
        {
            Title = "Choose a save folder to track"
        };

        if (folderDialog.ShowDialog(this) != true)
        {
            return;
        }

        AddCustomLiveSave(game, folderDialog.FolderName, LiveSaveEntryKind.CustomFolder);
    }

    private void AddFileScope_Click(object sender, RoutedEventArgs e)
    {
        var game = SelectedGame();
        if (game is null)
        {
            MessageBox.Show("Select a game first.", "Add File", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Choose a save file to track",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        AddCustomLiveSave(game, dialog.FileName, LiveSaveEntryKind.CustomFile);
    }

    private void AddCustomLiveSave(GameEntry game, string sourcePath, LiveSaveEntryKind kind)
    {
        var originalName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var liveSave = new LiveSaveEntry
        {
            Label = originalName,
            OriginalName = originalName,
            Kind = kind,
            Source = "Manual",
            Items =
            [
                new LiveSaveItem
                {
                    SourcePath = sourcePath,
                    Kind = kind == LiveSaveEntryKind.CustomFile ? LiveSaveItemKind.File : LiveSaveItemKind.Directory
                }
            ]
        };

        game.LiveSaves.Add(liveSave);
        _catalogService.Save(_settings.LibraryPath!, _catalog);
        RefreshAll(game.Id, liveSave.Id);
    }

    private void ShowScopeDetails_Click(object sender, RoutedEventArgs e)
    {
        var liveSave = SelectedLiveSave();
        if (liveSave is null)
        {
            MessageBox.Show("Select a save entry first.", "Save Entry Details", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var paths = liveSave.Items.Count == 0
            ? "No tracked paths"
            : string.Join(Environment.NewLine, liveSave.Items.Select(item => item.SourcePath));

        MessageBox.Show(
            $"Display name: {liveSave.DisplayName}\nOriginal name: {liveSave.OriginalName}\nEntry type: {FormatLiveSaveKind(liveSave.Kind)}\n\nTracked paths:\n{paths}",
            "Save Entry Details",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void GoToLiveSaveFolder_Click(object sender, RoutedEventArgs e)
    {
        var liveSave = SelectedLiveSave();
        if (liveSave is null)
        {
            MessageBox.Show("Select a save entry first.", "Go to Folder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (liveSave.Items.Any(item => item.Kind == LiveSaveItemKind.RegistryKey))
        {
            const string message = "Registry save entries are disabled in this version. Use folder or file save entries instead.";
            ReportOperationStatus(message);
            MessageBox.Show(message, "Go to Folder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var target = ResolveExplorerTarget(liveSave);
        if (target is null)
        {
            var message = $"The save location for \"{liveSave.DisplayName}\" no longer exists.";
            ReportOperationStatus(message);
            MessageBox.Show(message, "Go to Folder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = target.Arguments,
                UseShellExecute = true
            });
            ReportOperationStatus($"Opened folder: {target.DisplayPath}");
        }
        catch (Exception exception)
        {
            ShowError("Could not open folder", exception);
        }
    }

    private void GoToVersionFolder_Click(object sender, RoutedEventArgs e)
    {
        var version = SelectedVersion();
        if (version is null)
        {
            MessageBox.Show("Select a version first.", "Go to Folder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (version.IsPlaceholder)
        {
            var message = $"{version.VersionName} is an empty slot and does not have an archive yet.";
            ReportOperationStatus(message);
            MessageBox.Show(message, "Go to Folder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.LibraryPath) || string.IsNullOrWhiteSpace(version.ArchiveRelativePath))
        {
            var message = $"The archive location for {version.VersionName} is not available.";
            ReportOperationStatus(message);
            MessageBox.Show(message, "Go to Folder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var archivePath = Path.Combine(_settings.LibraryPath, version.ArchiveRelativePath);
        if (!File.Exists(archivePath))
        {
            var message = $"The archive for {version.VersionName} was not found: {archivePath}";
            ReportOperationStatus(message);
            MessageBox.Show(message, "Go to Folder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var target = ExplorerTarget.SelectFolder(archivePath);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = target.Arguments,
                UseShellExecute = true
            });
            ReportOperationStatus($"Opened archive location: {archivePath}");
        }
        catch (Exception exception)
        {
            ShowError("Could not open folder", exception);
        }
    }

    private void EditScopeAlias_Click(object sender, RoutedEventArgs e)
    {
        var selected = RequireGameAndLiveSave();
        if (selected is null)
        {
            return;
        }

        var (game, liveSave) = selected.Value;
        var alias = TextInputDialog.Ask(this, "Edit Alias", "Enter an alias for this save entry:", liveSave.DisplayName);
        if (string.IsNullOrWhiteSpace(alias))
        {
            return;
        }

        liveSave.Alias = string.Equals(alias, liveSave.OriginalName, StringComparison.Ordinal) ? null : alias;
        liveSave.Label = liveSave.DisplayName;
        _catalogService.Save(_settings.LibraryPath!, _catalog);
        RefreshAll(game.Id, liveSave.Id);
    }

    private void ClearScopeAlias_Click(object sender, RoutedEventArgs e)
    {
        var selected = RequireGameAndLiveSave();
        if (selected is null)
        {
            return;
        }

        var (game, liveSave) = selected.Value;
        if (string.IsNullOrWhiteSpace(liveSave.Alias))
        {
            return;
        }

        liveSave.Alias = null;
        liveSave.Label = liveSave.DisplayName;
        _catalogService.Save(_settings.LibraryPath!, _catalog);
        RefreshAll(game.Id, liveSave.Id);
    }

    private async void BackupSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var selected = RequireGameAndLiveSave();
        if (selected is null)
        {
            return;
        }

        var (game, liveSave) = selected.Value;
        if (!EnsureSupportedLiveSave(liveSave, "Back Up Save"))
        {
            return;
        }

        if (!_settings.SuppressBackupRunningWarning)
        {
            if (!BackupWarningDialog.Confirm(this, out var suppressWarning))
            {
                return;
            }

            if (suppressWarning)
            {
                _settings.SuppressBackupRunningWarning = true;
                _settingsService.Save(_settings);
            }
        }

        try
        {
            var progress = new Progress<OperationStatus>(status => ReportOperationStatus(status.Message));
            SetBusy($"Checking {liveSave.DisplayName}...");
            var estimate = await Task.Run(() => _snapshotService.Estimate(liveSave, progress));
            if (estimate.IsLarge)
            {
                ClearBusy();
                var result = MessageBox.Show(
                    $"This snapshot is estimated at {FileSizeFormatter.Format(estimate.Bytes)} across {estimate.Files:N0} files.\n\nCreate it anyway?",
                    "Large Snapshot",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                {
                    return;
                }

                SetBusy($"Backing up {liveSave.DisplayName}...");
            }
            else
            {
                SetBusy($"Backing up {liveSave.DisplayName}...");
            }

            await Task.Run(() => _snapshotService.CreateSnapshot(_settings.LibraryPath!, _catalog, game, liveSave, SaveVersionKind.Manual, progress: progress));
            RefreshVersions(liveSave.Id);
            ReportOperationStatus($"Backup complete: {liveSave.DisplayName}");
            MessageBox.Show("Backup complete.", "Back Up Save", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            ShowError("Backup failed", exception);
        }
        finally
        {
            ClearBusy();
        }
    }

    private async void BackupAllGames_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (!EnsureLibrary())
        {
            return;
        }

        var mode = BackupAllModeDialog.Choose(this);
        if (mode is null)
        {
            return;
        }

        if (!_settings.SuppressBackupRunningWarning)
        {
            if (!BackupWarningDialog.Confirm(this, out var suppressWarning))
            {
                return;
            }

            if (suppressWarning)
            {
                _settings.SuppressBackupRunningWarning = true;
                _settingsService.Save(_settings);
            }
        }

        var progress = new Progress<OperationStatus>(status => ReportOperationStatus(status.Message));
        var summary = new BackupAllSummary();
        var skipRemaining = false;

        try
        {
            SetBusy("Backing up all games...");
            var candidates = _catalog.Games
                .OrderBy(game => game.DisplayName)
                .Select(game => new BackupAllCandidate(game, AllSaveDataEntry(game)))
                .ToList();

            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                var game = candidate.Game;
                var liveSave = candidate.LiveSave;

                if (liveSave is null)
                {
                    summary.Skipped++;
                    ReportOperationStatus($"Skipped {game.DisplayName}: no All save data entry.");
                    continue;
                }

                if (skipRemaining)
                {
                    summary.Skipped++;
                    ReportOperationStatus($"Skipped {game.DisplayName}: skip remaining selected.");
                    continue;
                }

                if (liveSave.Items.Any(item => item.Kind == LiveSaveItemKind.RegistryKey))
                {
                    summary.Skipped++;
                    ReportOperationStatus($"Skipped {game.DisplayName}: registry save entries are disabled in this version.");
                    continue;
                }

                var overwriteTarget = LatestManualVersion(game.Id, liveSave.Id);
                var action = mode.Value switch
                {
                    BackupAllMode.NewSlotForAll => BackupAllGameAction.NewSlot,
                    BackupAllMode.OverwriteLastForEach => BackupAllGameAction.OverwriteLast,
                    _ => BackupAllGameActionDialog.Choose(this, game.DisplayName, liveSave.DisplayName, overwriteTarget is not null)
                };

                if (action == BackupAllGameAction.CancelAll)
                {
                    summary.Canceled = true;
                    ReportOperationStatus("Backup all games canceled.");
                    break;
                }

                if (action == BackupAllGameAction.SkipRemaining)
                {
                    skipRemaining = true;
                    summary.Skipped++;
                    ReportOperationStatus($"Skipped {game.DisplayName}: skip remaining selected.");
                    continue;
                }

                if (action == BackupAllGameAction.Skip)
                {
                    summary.Skipped++;
                    ReportOperationStatus($"Skipped {game.DisplayName}.");
                    continue;
                }

                try
                {
                    ReportOperationStatus($"Checking {index + 1:N0}/{candidates.Count:N0}: {game.DisplayName}");
                    var estimate = await Task.Run(() => _snapshotService.Estimate(liveSave, progress));
                    if (estimate.IsLarge)
                    {
                        var result = MessageBox.Show(
                            $"{game.DisplayName} is estimated at {FileSizeFormatter.Format(estimate.Bytes)} across {estimate.Files:N0} files.\n\nCreate this snapshot anyway?",
                            "Large Snapshot",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning);

                        if (result != MessageBoxResult.Yes)
                        {
                            summary.Skipped++;
                            ReportOperationStatus($"Skipped {game.DisplayName}: large snapshot not confirmed.");
                            continue;
                        }
                    }

                    var overwrite = action == BackupAllGameAction.OverwriteLast ? overwriteTarget : null;
                    ReportOperationStatus(overwrite is null
                        ? $"Creating new slot for {game.DisplayName}..."
                        : $"Overwriting {overwrite.VersionName} for {game.DisplayName}...");

                    await Task.Run(() => _snapshotService.CreateSnapshot(_settings.LibraryPath!, _catalog, game, liveSave, SaveVersionKind.Manual, overwrite, progress));

                    if (overwrite is null)
                    {
                        summary.Created++;
                    }
                    else
                    {
                        summary.Overwritten++;
                    }

                    ReportOperationStatus($"Backed up {game.DisplayName}.");
                }
                catch (Exception exception)
                {
                    summary.Failed++;
                    ReportOperationStatus($"Failed {game.DisplayName}: {exception.Message}");
                }
            }

            RefreshAll(SelectedGame()?.Id, SelectedLiveSave()?.Id);
            var message = $"Backup all complete.\n\nCreated: {summary.Created}\nOverwritten: {summary.Overwritten}\nSkipped: {summary.Skipped}\nFailed: {summary.Failed}\nCanceled: {(summary.Canceled ? "Yes" : "No")}";
            ReportOperationStatus($"Backup all complete. Created: {summary.Created:N0}; overwritten: {summary.Overwritten:N0}; skipped: {summary.Skipped:N0}; failed: {summary.Failed:N0}; canceled: {(summary.Canceled ? "yes" : "no")}.");
            MessageBox.Show(message, "Back Up All Games", MessageBoxButton.OK, summary.Failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        finally
        {
            ClearBusy();
        }
    }

    private async void RestoreSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var selected = RequireGameLiveSaveAndVersion();
        if (selected is null)
        {
            return;
        }

        var (game, liveSave, version) = selected.Value;
        if (version.IsPlaceholder)
        {
            MessageBox.Show("Empty slots cannot be restored until a save has been imported or backed up into them.", "Restore Save", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!EnsureSupportedLiveSave(liveSave, "Restore Save"))
        {
            return;
        }

        var warning = MessageBox.Show(
            "Close the game before restoring if it might be writing save files.\n\nRestore will replace the live save with the selected version after creating a safety snapshot.",
            "Restore Save",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (warning != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var progress = new Progress<OperationStatus>(status => ReportOperationStatus(status.Message));
            SetBusy($"Restoring {version.VersionName}...");
            await Task.Run(() => _snapshotService.RestoreSnapshot(_settings.LibraryPath!, _catalog, game, liveSave, version, progress));
            RefreshVersions(SelectedLiveSave()?.Id);
            ReportOperationStatus($"Restore complete: {version.VersionName}");
            MessageBox.Show("Restore complete. A safety snapshot was created first.", "Restore Save", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            ShowError("Restore failed", exception);
        }
        finally
        {
            ClearBusy();
        }
    }

    private async void OverwriteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var selected = RequireGameLiveSaveAndVersion();
        if (selected is null)
        {
            return;
        }

        var (game, liveSave, version) = selected.Value;
        if (version.Kind == SaveVersionKind.Safety)
        {
            MessageBox.Show("Safety snapshots cannot be overwritten.", "Overwrite Version", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!EnsureSupportedLiveSave(liveSave, "Overwrite Version"))
        {
            return;
        }

        var result = MessageBox.Show(
            $"Overwrite \"{version.VersionName}\" with the current live save?\n\nThe slot number will stay the same.",
            "Overwrite Version",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var progress = new Progress<OperationStatus>(status => ReportOperationStatus(status.Message));
            SetBusy($"Overwriting {version.VersionName}...");
            await Task.Run(() => _snapshotService.CreateSnapshot(_settings.LibraryPath!, _catalog, game, liveSave, SaveVersionKind.Manual, version, progress));
            RefreshVersions(SelectedLiveSave()?.Id);
            ReportOperationStatus($"Overwrite complete: {liveSave.DisplayName}");
            MessageBox.Show("Overwrite complete.", "Overwrite Version", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            ShowError("Overwrite failed", exception);
        }
        finally
        {
            ClearBusy();
        }
    }

    private void AddEmptySlot_Click(object sender, RoutedEventArgs e)
    {
        var selected = RequireGameAndLiveSave();
        if (selected is null)
        {
            return;
        }

        var (game, liveSave) = selected.Value;
        if (!CanUseSlotTools(liveSave, "Add Empty Slot"))
        {
            return;
        }

        var slot = NextAvailableSlot(game.Id, liveSave.Id);

        var version = new SaveVersion
        {
            GameId = game.Id,
            LiveSaveEntryId = liveSave.Id,
            Kind = SaveVersionKind.Manual,
            CreatedAtUtc = DateTime.UtcNow,
            SlotNumber = slot,
            IsPlaceholder = true,
            OriginalName = $"Empty Slot {slot}"
        };

        _catalog.Versions.Add(version);
        _catalogService.Save(_settings.LibraryPath!, _catalog);
        RefreshVersions(liveSave.Id);
        SelectSnapshotById(version.Id);
        ReportOperationStatus($"Added empty slot {slot} for {liveSave.DisplayName}.");
    }

    private async void ImportPriorSave_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var selected = RequireGameLiveSaveAndVersion();
        if (selected is null)
        {
            return;
        }

        var (game, liveSave, version) = selected.Value;
        if (version.Kind == SaveVersionKind.Safety)
        {
            MessageBox.Show("Prior saves can only be imported into manual slots.", "Import Prior Save", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!CanUseSlotTools(liveSave, "Import Prior Save"))
        {
            return;
        }

        if (!version.IsPlaceholder)
        {
            var result = MessageBox.Show(
                $"Overwrite {version.VersionName} with the imported save?\n\nThe original import source will not be modified.",
                "Import Prior Save",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        var importSource = ChooseImportSource(liveSave);
        if (importSource is null)
        {
            return;
        }

        try
        {
            var progress = new Progress<OperationStatus>(status => ReportOperationStatus(status.Message));
            SetBusy($"Importing prior save into {version.VersionName}...");
            await Task.Run(() => _snapshotService.ImportSnapshot(_settings.LibraryPath!, _catalog, game, liveSave, version, importSource.Path, importSource.Kind, progress));
            RefreshVersions(liveSave.Id);
            SelectSnapshotById(version.Id);
            MessageBox.Show("Import complete.", "Import Prior Save", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            ShowError("Import failed", exception);
        }
        finally
        {
            ClearBusy();
        }
    }

    private void ChangeSlot_Click(object sender, RoutedEventArgs e)
    {
        var selected = RequireGameLiveSaveAndVersion();
        if (selected is null)
        {
            return;
        }

        var (game, liveSave, version) = selected.Value;
        if (version.Kind == SaveVersionKind.Safety)
        {
            MessageBox.Show("Safety snapshot slots cannot be changed.", "Change Slot Number", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var targetSlot = AskSlotNumber("Change Slot Number", "Enter the new slot number:", version.SlotNumber?.ToString());
        if (targetSlot is null || targetSlot == version.SlotNumber)
        {
            return;
        }

        var occupied = FindManualVersion(game.Id, liveSave.Id, targetSlot.Value);
        if (occupied is not null && occupied.Id != version.Id)
        {
            var swap = MessageBox.Show(
                $"Slot {targetSlot.Value} is occupied by \"{occupied.VersionName}\".\n\nSwap slot numbers?",
                "Change Slot Number",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (swap != MessageBoxResult.Yes)
            {
                return;
            }

            occupied.SlotNumber = version.SlotNumber;
            occupied.OriginalName = SlotName(occupied);
        }

        version.SlotNumber = targetSlot.Value;
        version.OriginalName = SlotName(version);
        _catalogService.Save(_settings.LibraryPath!, _catalog);
        RefreshVersions(liveSave.Id);
        SelectSnapshotById(version.Id);
        ReportOperationStatus($"Moved {version.VersionName} to slot {targetSlot.Value}.");
    }

    private void ShowSnapshotDetails_Click(object sender, RoutedEventArgs e)
    {
        var version = SelectedVersion();
        var liveSave = SelectedVersionLiveSave();
        if (version is null)
        {
            MessageBox.Show("Select a version first.", "Version Details", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var archivePath = string.IsNullOrWhiteSpace(_settings.LibraryPath)
            ? (string.IsNullOrWhiteSpace(version.ArchiveRelativePath) ? "No archive" : version.ArchiveRelativePath)
            : Path.Combine(_settings.LibraryPath, version.ArchiveRelativePath);
        if (string.IsNullOrWhiteSpace(version.ArchiveRelativePath))
        {
            archivePath = "No archive";
        }

        var notes = string.IsNullOrWhiteSpace(version.Notes) ? "None" : version.Notes;

        MessageBox.Show(
            $"Version: {version.VersionName}\nSlot: {(version.SlotNumber?.ToString() ?? "Auto")}\nSave entry: {liveSave?.DisplayName ?? "Unknown"}\nType: {version.Kind}\nCreated: {version.CreatedAtUtc.ToLocalTime():F}\nFiles: {version.FileCount:N0}\nArchive size: {FileSizeFormatter.Format(version.SizeBytes)}\nArchive path: {archivePath}\n\nNotes:\n{notes}",
            "Version Details",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void EditSnapshotAlias_Click(object sender, RoutedEventArgs e)
    {
        var version = SelectedVersion();
        if (version is null)
        {
            MessageBox.Show("Select a version first.", "Edit Alias", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var alias = TextInputDialog.Ask(this, "Edit Alias", "Enter an alias for this version:", version.VersionName);
        if (string.IsNullOrWhiteSpace(alias))
        {
            return;
        }

        version.Alias = string.Equals(alias, version.OriginalName, StringComparison.Ordinal) ? null : alias;
        version.Name = version.Alias;
        _catalogService.Save(_settings.LibraryPath!, _catalog);
        RefreshVersions(SelectedLiveSave()?.Id);
    }

    private void ClearSnapshotAlias_Click(object sender, RoutedEventArgs e)
    {
        var version = SelectedVersion();
        if (version is null)
        {
            MessageBox.Show("Select a version first.", "Clear Alias", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        version.Alias = null;
        version.Name = null;
        _catalogService.Save(_settings.LibraryPath!, _catalog);
        RefreshVersions(SelectedLiveSave()?.Id);
    }

    private void DeleteSnapshot_Click(object sender, RoutedEventArgs e)
    {
        var selectedItems = SelectedSnapshotItems();
        if (selectedItems.Count == 0)
        {
            MessageBox.Show("Select at least one version first.", "Delete Version", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var versionText = selectedItems.Count == 1
            ? $"version \"{selectedItems[0].Version.VersionName}\""
            : $"{selectedItems.Count} selected versions";

        var allPlaceholders = selectedItems.All(item => item.Version.IsPlaceholder);
        if (!allPlaceholders)
        {
            var result = MessageBox.Show(
                $"Delete {versionText}?\n\nThis cannot be undone.",
                "Delete Version",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }
        }

        foreach (var item in selectedItems)
        {
            _snapshotService.DeleteSnapshot(_settings.LibraryPath!, _catalog, item.Version);
        }

        ReportOperationStatus(allPlaceholders
            ? $"Removed {selectedItems.Count:N0} empty slot{(selectedItems.Count == 1 ? "" : "s")}."
            : $"Deleted {selectedItems.Count:N0} version{(selectedItems.Count == 1 ? "" : "s")}.");
        RefreshVersions(SelectedLiveSave()?.Id);
    }

    private void DeleteScope_Click(object sender, RoutedEventArgs e)
    {
        var selected = RequireGameAndLiveSave();
        if (selected is null)
        {
            return;
        }

        var (game, liveSave) = selected.Value;
        var result = MessageBox.Show(
            $"Delete live save entry \"{liveSave.DisplayName}\" and every version under it?\n\nThis cannot be undone.",
            "Delete Save Entry",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _snapshotService.DeleteLiveSave(_settings.LibraryPath!, _catalog, game, liveSave);
        RefreshAll(game.Id);
    }

    private void DeleteGame_Click(object sender, RoutedEventArgs e)
    {
        var games = SelectedGames();
        if (games.Count == 0)
        {
            MessageBox.Show("Select at least one game first.", "Remove Game From Library", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var singleGame = games.Count == 1 ? games[0] : null;
        var gameIds = games.Select(game => game.Id).ToHashSet(StringComparer.Ordinal);
        var realBackupGameCount = _catalog.Versions
            .Where(version => gameIds.Contains(version.GameId))
            .Where(version => !version.IsPlaceholder)
            .Select(version => version.GameId)
            .Distinct(StringComparer.Ordinal)
            .Count();

        if (realBackupGameCount > 0)
        {
            var noBackupGameCount = games.Count - realBackupGameCount;
            var backupSummary = noBackupGameCount > 0
                ? $"\n\n{realBackupGameCount:N0} selected game{(realBackupGameCount == 1 ? " has" : "s have")} backups; {noBackupGameCount:N0} do not."
                : "";

            var result = MessageBox.Show(
                singleGame is not null
                    ? $"Remove \"{singleGame}\" from this backup library?\n\nThis removes the game's backup entries and version archives from the selected library.\n\nIt does not delete real/live game saves. This cannot be undone."
                    : $"Remove {games.Count:N0} games from this backup library?\n\n{FormatGameList(games)}{backupSummary}\n\nThis removes those games' backup entries and version archives from the selected library.\n\nIt does not delete real/live game saves. This cannot be undone.",
                "Remove Game From Library",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            var confirmed = TimedConfirmationDialog.Confirm(
                this,
                "Confirm Game Backup Removal",
                singleGame is not null
                    ? $"Final confirmation: you are deleting the entire backup history for \"{singleGame}\" from this backup library.\n\nThis includes all live save entries, manual versions, safety versions, and archive files for this game.\n\nYour real/live game save data on disk will not be deleted."
                    : $"Final confirmation: you are deleting the entire backup history for {games.Count:N0} games from this backup library.\n\n{FormatGameList(games)}{backupSummary}\n\nThis includes all live save entries, manual versions, safety versions, and archive files for these games.\n\nYour real/live game save data on disk will not be deleted.",
                delaySeconds: 10);

            if (!confirmed)
            {
                return;
            }
        }

        foreach (var game in games)
        {
            _snapshotService.DeleteGame(_settings.LibraryPath!, _catalog, game);
        }

        ReportOperationStatus(realBackupGameCount == 0
            ? $"Removed {games.Count:N0} game{(games.Count == 1 ? "" : "s")} with no backups from library."
            : $"Removed {games.Count:N0} game{(games.Count == 1 ? "" : "s")} from library.");
        RefreshAll();
    }

    private void GamesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshLiveSaves();
    }

    private void LiveSavesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var liveSave = SelectedLiveSave();
        RefreshVersions(liveSave?.Id);
    }

    private void LiveSavesListItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource) is { } item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void SnapshotListItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>((DependencyObject)e.OriginalSource) is { } item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void ManualSnapshotHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Column.Header: string header })
        {
            ToggleSort(ref _manualSortColumn, ref _manualSortAscending, header);
            RefreshVersions(SelectedLiveSave()?.Id);
        }
    }

    private void SafetySnapshotHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Column.Header: string header })
        {
            ToggleSort(ref _safetySortColumn, ref _safetySortAscending, header);
            RefreshVersions(SelectedLiveSave()?.Id);
        }
    }

    private void NotesText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingNotesText)
        {
            return;
        }

        var version = SelectedVersion();
        if (version is null || string.IsNullOrWhiteSpace(_settings.LibraryPath))
        {
            return;
        }

        version.Notes = string.IsNullOrWhiteSpace(NotesText.Text) ? null : NotesText.Text;
        _catalogService.Save(_settings.LibraryPath, _catalog);
    }

    private void AliasText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingAliasText)
        {
            return;
        }

        var version = SelectedVersion();
        if (version is null || string.IsNullOrWhiteSpace(_settings.LibraryPath))
        {
            return;
        }

        var alias = string.IsNullOrWhiteSpace(AliasText.Text) ? null : AliasText.Text;
        version.Alias = string.Equals(alias, version.OriginalName, StringComparison.Ordinal) ? null : alias;
        version.Name = version.Alias;
        _catalogService.Save(_settings.LibraryPath, _catalog);
        RefreshSelectedVersionsList();
    }

    private void AliasText_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingAliasText)
        {
            return;
        }

        var version = SelectedVersion();
        if (version is null || string.IsNullOrWhiteSpace(_settings.LibraryPath))
        {
            return;
        }

        var alias = string.IsNullOrWhiteSpace(AliasText.Text) ? null : AliasText.Text.Trim();
        version.Alias = string.Equals(alias, version.OriginalName, StringComparison.Ordinal) ? null : alias;
        version.Name = version.Alias;
        _catalogService.Save(_settings.LibraryPath, _catalog);

        _isUpdatingAliasText = true;
        AliasText.Text = version.Alias ?? "";
        AliasText.CaretIndex = AliasText.Text.Length;
        _isUpdatingAliasText = false;
        RefreshSelectedVersionsList();
    }

    private void VersionsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == VersionsTabs)
        {
            UpdateSnapshotDetails();
        }
    }

    private void VersionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender == ManualVersionsList && ManualVersionsList.SelectedItem is not null)
        {
            SafetyVersionsList.SelectedItem = null;
        }
        else if (sender == SafetyVersionsList && SafetyVersionsList.SelectedItem is not null)
        {
            ManualVersionsList.SelectedItem = null;
        }

        UpdateSnapshotDetails();
    }

    private void RefreshAll(string? gameId = null, string? liveSaveId = null)
    {
        LibraryText.Text = string.IsNullOrWhiteSpace(_settings.LibraryPath)
            ? "Library: No library selected"
            : $"Library: {_settings.LibraryPath}";

        var versionCountsByGame = _catalog.Versions
            .Where(version => !version.IsPlaceholder)
            .GroupBy(version => version.GameId)
            .ToDictionary(group => group.Key, group => group.Count());
        var gameItems = _catalog.Games
            .OrderBy(game => game.Name)
            .ThenBy(game => game.DisplayName)
            .Select(game => new GameListItem
            {
                Entry = game,
                VersionCount = versionCountsByGame.GetValueOrDefault(game.Id)
            })
            .ToList();

        GamesList.ItemsSource = gameItems;
        if (gameId is not null)
        {
            GamesList.SelectedItem = gameItems.FirstOrDefault(game => game.Entry.Id == gameId);
        }
        else if (GamesList.Items.Count > 0 && GamesList.SelectedItem is null)
        {
            GamesList.SelectedIndex = 0;
        }

        RefreshLiveSaves(liveSaveId);
    }

    private void RefreshLiveSaves(string? liveSaveId = null)
    {
        var game = SelectedGame();
        var liveSaveItems = game?.LiveSaves
            .OrderBy(save => save.Kind)
            .ThenBy(save => save.DisplayName)
            .Select(save => new LiveSaveListItem { Entry = save })
            .ToList() ?? [];

        var latest = liveSaveItems
            .Where(item => item.Entry.Kind != LiveSaveEntryKind.AllSaveData)
            .Select(item => item.ModifiedSort)
            .Where(modified => modified is not null)
            .DefaultIfEmpty()
            .Max();

        foreach (var item in liveSaveItems)
        {
            item.IsLatest = item.ModifiedSort is not null && latest is not null && item.ModifiedSort == latest;
        }

        LiveSavesList.ItemsSource = liveSaveItems;

        if (liveSaveId is not null && game is not null)
        {
            LiveSavesList.SelectedItem = liveSaveItems.FirstOrDefault(item => item.Entry.Id == liveSaveId);
        }
        else if (LiveSavesList.Items.Count > 0 && LiveSavesList.SelectedItem is null)
        {
            LiveSavesList.SelectedIndex = 0;
        }

        RefreshVersions(SelectedLiveSave()?.Id);
    }

    private void RefreshVersions(string? liveSaveId)
    {
        if (liveSaveId is null)
        {
            ManualVersionsList.ItemsSource = null;
            SafetyVersionsList.ItemsSource = null;
            UpdateSnapshotDetails();
            return;
        }

        var game = SelectedGame();
        var selectedLiveSave = SelectedLiveSave();
        if (game is null || selectedLiveSave is null)
        {
            ManualVersionsList.ItemsSource = null;
            SafetyVersionsList.ItemsSource = null;
            UpdateSnapshotDetails();
            return;
        }

        var liveSavesById = game.LiveSaves.ToDictionary(liveSave => liveSave.Id);
        var visibleLiveSaveEntryIds = selectedLiveSave.Kind == LiveSaveEntryKind.AllSaveData
            ? liveSavesById.Keys.ToHashSet()
            : new HashSet<string> { selectedLiveSave.Id };

        var selectedVersionId = SelectedVersion()?.Id;

        var manualItems = _catalog.Versions
            .Where(version => version.GameId == game.Id
                && version.Kind == SaveVersionKind.Manual
                && visibleLiveSaveEntryIds.Contains(version.LiveSaveEntryId)
                && liveSavesById.ContainsKey(version.LiveSaveEntryId))
            .Select(version => new SnapshotListItem
            {
                Version = version,
                LiveSave = liveSavesById[version.LiveSaveEntryId]
            })
            .ToList();

        var safetyItems = _catalog.Versions
            .Where(version => version.GameId == game.Id
                && version.Kind == SaveVersionKind.Safety
                && visibleLiveSaveEntryIds.Contains(version.LiveSaveEntryId)
                && liveSavesById.ContainsKey(version.LiveSaveEntryId))
            .Select(version => new SnapshotListItem
            {
                Version = version,
                LiveSave = liveSavesById[version.LiveSaveEntryId]
            })
            .ToList();

        ManualVersionsList.ItemsSource = SortSnapshotItems(manualItems, _manualSortColumn, _manualSortAscending).ToList();
        SafetyVersionsList.ItemsSource = SortSnapshotItems(safetyItems, _safetySortColumn, _safetySortAscending).ToList();

        if (!string.IsNullOrWhiteSpace(selectedVersionId))
        {
            ManualVersionsList.SelectedItem = ManualVersionsList.Items
                .OfType<SnapshotListItem>()
                .FirstOrDefault(item => item.Version.Id == selectedVersionId);

            SafetyVersionsList.SelectedItem = SafetyVersionsList.Items
                .OfType<SnapshotListItem>()
                .FirstOrDefault(item => item.Version.Id == selectedVersionId);
        }

        UpdateSnapshotDetails();
    }

    private void UpdateSnapshotDetails()
    {
        var version = SelectedVersion();
        if (version is null)
        {
            VersionDetailsText.Text = "Select a version to see details.";
            _isUpdatingAliasText = true;
            AliasText.Text = "";
            AliasText.IsEnabled = false;
            _isUpdatingAliasText = false;
            _isUpdatingNotesText = true;
            NotesText.Text = "";
            NotesText.IsEnabled = false;
            _isUpdatingNotesText = false;
            return;
        }

        VersionDetailsText.Text = version.IsPlaceholder
            ? $"{version.VersionName}\nEmpty slot - import or overwrite a save into this slot before restoring."
            : $"{version.VersionName}\nCreated: {version.CreatedAtUtc.ToLocalTime():F}\nFiles: {version.FileCount:N0}\nArchive size: {FileSizeFormatter.Format(version.SizeBytes)}";
        _isUpdatingAliasText = true;
        AliasText.Text = version.Alias ?? "";
        AliasText.IsEnabled = true;
        _isUpdatingAliasText = false;
        _isUpdatingNotesText = true;
        NotesText.Text = version.Notes ?? "";
        NotesText.IsEnabled = true;
        _isUpdatingNotesText = false;
    }

    private GameEntry? SelectedGame() => (GamesList.SelectedItem as GameListItem)?.Entry;

    private List<GameEntry> SelectedGames() => GamesList.SelectedItems.OfType<GameListItem>().Select(item => item.Entry).ToList();

    private LiveSaveEntry? SelectedLiveSave() => (LiveSavesList.SelectedItem as LiveSaveListItem)?.Entry;

    private static LiveSaveEntry? AllSaveDataEntry(GameEntry game) =>
        game.LiveSaves.FirstOrDefault(liveSave => liveSave.Kind == LiveSaveEntryKind.AllSaveData);

    private SaveVersion? SelectedVersion()
    {
        if (VersionsTabs.SelectedIndex == 1)
        {
            return (SafetyVersionsList.SelectedItem as SnapshotListItem)?.Version;
        }

        return (ManualVersionsList.SelectedItem as SnapshotListItem)?.Version;
    }

    private LiveSaveEntry? SelectedVersionLiveSave()
    {
        if (VersionsTabs.SelectedIndex == 1)
        {
            return (SafetyVersionsList.SelectedItem as SnapshotListItem)?.LiveSave;
        }

        return (ManualVersionsList.SelectedItem as SnapshotListItem)?.LiveSave;
    }

    private List<SnapshotListItem> SelectedSnapshotItems()
    {
        var list = VersionsTabs.SelectedIndex == 1 ? SafetyVersionsList : ManualVersionsList;
        return list.SelectedItems.OfType<SnapshotListItem>().ToList();
    }

    private void RefreshSelectedVersionsList()
    {
        var list = VersionsTabs.SelectedIndex == 1 ? SafetyVersionsList : ManualVersionsList;
        list.Items.Refresh();
    }

    private void RefreshSelectedVersionsView()
    {
        RefreshSelectedVersionsList();
        UpdateSnapshotDetails();
    }

    private (GameEntry Game, LiveSaveEntry LiveSave)? RequireGameAndLiveSave()
    {
        if (!EnsureLibrary())
        {
            return null;
        }

        var game = SelectedGame();
        var liveSave = SelectedLiveSave();
        if (game is null || liveSave is null)
        {
            MessageBox.Show("Select a game and save entry first.", "Versioned Game Saver", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        return (game, liveSave);
    }

    private (GameEntry Game, LiveSaveEntry LiveSave, SaveVersion Version)? RequireGameLiveSaveAndVersion()
    {
        var selected = RequireGameAndLiveSave();
        if (selected is null)
        {
            return null;
        }

        var version = SelectedVersion();
        if (version is null)
        {
            MessageBox.Show("Select a version first.", "Versioned Game Saver", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        return (selected.Value.Game, SelectedVersionLiveSave() ?? selected.Value.LiveSave, version);
    }

    private SaveVersion? LatestManualVersion(string gameId, string liveSaveId) =>
        _catalog.Versions
            .Where(version => version.GameId == gameId
                && version.LiveSaveEntryId == liveSaveId
                && version.Kind == SaveVersionKind.Manual)
            .OrderByDescending(version => version.CreatedAtUtc)
            .FirstOrDefault();

    private SaveVersion? FindManualVersion(string gameId, string liveSaveId, int slotNumber) =>
        _catalog.Versions.FirstOrDefault(version => version.GameId == gameId
            && version.LiveSaveEntryId == liveSaveId
            && version.Kind == SaveVersionKind.Manual
            && version.SlotNumber == slotNumber);

    private int NextAvailableSlot(string gameId, string liveSaveId)
    {
        var used = _catalog.Versions
            .Where(version => version.GameId == gameId
                && version.LiveSaveEntryId == liveSaveId
                && version.Kind == SaveVersionKind.Manual
                && version.SlotNumber is not null)
            .Select(version => version.SlotNumber!.Value)
            .ToHashSet();

        var slot = 1;
        while (used.Contains(slot))
        {
            slot++;
        }

        return slot;
    }

    private bool CanUseSlotTools(LiveSaveEntry liveSave, string title)
    {
        if (!EnsureSupportedLiveSave(liveSave, title))
        {
            return false;
        }

        if (liveSave.Kind == LiveSaveEntryKind.AllSaveData || liveSave.Items.Count != 1)
        {
            MessageBox.Show("This action is only available for save entries with one folder or one file.", title, MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    private int? AskSlotNumber(string title, string prompt, string? defaultValue = null)
    {
        var value = TextInputDialog.Ask(this, title, prompt, defaultValue);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value, out var slot) && slot >= 0)
        {
            return slot;
        }

        MessageBox.Show("Enter a slot number of 0 or greater.", title, MessageBoxButton.OK, MessageBoxImage.Information);
        return null;
    }

    private ImportSource? ChooseImportSource(LiveSaveEntry liveSave)
    {
        var item = liveSave.Items[0];
        if (item.Kind == LiveSaveItemKind.Directory)
        {
            var sourceKind = FolderImportSourceDialog.Choose(this);
            if (sourceKind is null)
            {
                return null;
            }

            if (sourceKind == FolderImportSourceKind.Zip)
            {
                var zipDialog = new OpenFileDialog
                {
                    Title = "Choose prior save ZIP",
                    CheckFileExists = true,
                    Filter = "ZIP archives (*.zip)|*.zip|All files (*.*)|*.*"
                };

                return zipDialog.ShowDialog(this) == true
                    ? new ImportSource(zipDialog.FileName, SnapshotImportKind.Zip)
                    : null;
            }

            var folderDialog = new OpenFolderDialog
            {
                Title = "Choose prior save folder"
            };

            return folderDialog.ShowDialog(this) == true
                ? new ImportSource(folderDialog.FolderName, SnapshotImportKind.FileOrFolder)
                : null;
        }

        var fileDialog = new OpenFileDialog
        {
            Title = "Choose prior save file",
            CheckFileExists = true
        };

        return fileDialog.ShowDialog(this) == true
            ? new ImportSource(fileDialog.FileName, SnapshotImportKind.FileOrFolder)
            : null;
    }

    private static string SlotName(SaveVersion version) =>
        version.IsPlaceholder && version.SlotNumber is not null
            ? $"Empty Slot {version.SlotNumber}"
            : version.Kind == SaveVersionKind.Manual && version.SlotNumber is not null
                ? $"Slot {version.SlotNumber}"
                : version.OriginalName;

    private bool EnsureSupportedLiveSave(LiveSaveEntry liveSave, string title)
    {
        if (liveSave.Items.Any(item => item.Kind == LiveSaveItemKind.RegistryKey))
        {
            const string message = "Registry save entries are disabled in this version. Use folder or file save entries instead.";
            ReportOperationStatus(message);
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    private static ExplorerTarget? ResolveExplorerTarget(LiveSaveEntry liveSave)
    {
        var locations = liveSave.Items
            .Select(ExistingLocationForExplorer)
            .OfType<ExplorerLocation>()
            .DistinctBy(location => $"{location.IsFile}:{location.Path}", StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (locations.Count == 0)
        {
            return null;
        }

        if (locations.Count == 1)
        {
            return locations[0].IsFile
                ? ExplorerTarget.OpenFolder(locations[0].Path)
                : ExplorerTarget.SelectFolder(locations[0].Path);
        }

        var common = DeepestCommonDirectory(locations.Select(location => location.Path).ToList());
        return common is null ? null : ExplorerTarget.SelectFolder(common);
    }

    private static ExplorerLocation? ExistingLocationForExplorer(LiveSaveItem item)
    {
        if (item.Kind == LiveSaveItemKind.Directory && Directory.Exists(item.SourcePath))
        {
            return new ExplorerLocation(Path.GetFullPath(item.SourcePath), IsFile: false);
        }

        if (item.Kind == LiveSaveItemKind.File && File.Exists(item.SourcePath))
        {
            var parent = Path.GetDirectoryName(item.SourcePath);
            return string.IsNullOrWhiteSpace(parent)
                ? null
                : new ExplorerLocation(Path.GetFullPath(parent), IsFile: true);
        }

        return null;
    }

    private static string? DeepestCommonDirectory(IReadOnlyCollection<string> paths)
    {
        var root = Path.GetPathRoot(paths.First());
        if (string.IsNullOrWhiteSpace(root) || paths.Any(path => !string.Equals(Path.GetPathRoot(path), root, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var splitPaths = paths
            .Select(path => Path.GetRelativePath(root, Path.GetFullPath(path))
                .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            .ToList();

        if (splitPaths.Count == 0)
        {
            return null;
        }

        var commonParts = new List<string>();
        var shortest = splitPaths.Min(parts => parts.Length);
        for (var index = 0; index < shortest; index++)
        {
            var value = splitPaths[0][index];
            if (splitPaths.Any(parts => !string.Equals(parts[index], value, StringComparison.OrdinalIgnoreCase)))
            {
                break;
            }

            commonParts.Add(value);
        }

        if (commonParts.Count == 0)
        {
            return root;
        }

        var common = Path.Combine([root, .. commonParts]);
        return Directory.Exists(common) ? common : root;
    }

    private static string QuoteArgument(string value) =>
        $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private sealed record ExplorerLocation(string Path, bool IsFile);

    private sealed record ExplorerTarget(string Arguments, string DisplayPath)
    {
        public static ExplorerTarget OpenFolder(string folder) =>
            new(QuoteArgument(folder), folder);

        public static ExplorerTarget SelectFolder(string folder) =>
            new($"/select,{QuoteArgument(folder)}", folder);
    }

    private sealed record ImportSource(string Path, SnapshotImportKind Kind);

    private static string FormatGameList(IReadOnlyCollection<GameEntry> games)
    {
        var names = games
            .OrderBy(game => game.DisplayName)
            .Take(8)
            .Select(game => $"• {game.DisplayName}")
            .ToList();

        var remaining = games.Count - names.Count;
        if (remaining > 0)
        {
            names.Add($"• ...and {remaining:N0} more");
        }

        return string.Join(Environment.NewLine, names);
    }

    private bool EnsureLibrary()
    {
        if (!string.IsNullOrWhiteSpace(_settings.LibraryPath) && Directory.Exists(_settings.LibraryPath))
        {
            return true;
        }

        MessageBox.Show("Choose a backup library folder first.", "Versioned Game Saver", MessageBoxButton.OK, MessageBoxImage.Information);
        ChooseLibrary();
        return !string.IsNullOrWhiteSpace(_settings.LibraryPath) && Directory.Exists(_settings.LibraryPath);
    }

    private void ReportOperationStatus(string message)
    {
        StatusText.Text = message;
        AppendOperationLog(message);
    }

    private void AppendOperationLog(string message)
    {
        _operationLogLines.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
        while (_operationLogLines.Count > MaxOperationLogLines)
        {
            _operationLogLines.RemoveAt(0);
        }

        OutputLogText.Text = string.Join(Environment.NewLine, _operationLogLines);
        OutputLogText.ScrollToEnd();
    }

    private void ClearOperationLog()
    {
        _operationLogLines.Clear();
        OutputLogText.Text = "";
    }

    private void SetBusy(string message, bool canCancelScan = false)
    {
        _isBusy = true;
        StatusText.Text = message;
        SetActionsEnabled(false);
        CancelScanButton.Visibility = canCancelScan ? Visibility.Visible : Visibility.Collapsed;
        CancelScanButton.IsEnabled = canCancelScan;
    }

    private void ClearBusy()
    {
        _isBusy = false;
        CancelScanButton.Visibility = Visibility.Collapsed;
        CancelScanButton.IsEnabled = false;
        SetActionsEnabled(true);
    }

    private void SetActionsEnabled(bool enabled)
    {
        ChooseLibraryButton.IsEnabled = enabled;
        ScanGamesButton.IsEnabled = enabled;
        BackupAllGamesButton.IsEnabled = enabled;
        AddCustomGameButton.IsEnabled = enabled;
        RemoveGameButton.IsEnabled = enabled;
        BackupSelectedButton.IsEnabled = enabled;
        AddFolderButton.IsEnabled = enabled;
        AddFileButton.IsEnabled = enabled;
        AddEmptySlotButton.IsEnabled = enabled;
        ImportPriorSaveButton.IsEnabled = enabled;
        ChangeSlotButton.IsEnabled = enabled;
        RestoreButton.IsEnabled = enabled;
        OverwriteButton.IsEnabled = enabled;
        DeleteVersionButton.IsEnabled = enabled;
        LiveSavesList.IsEnabled = enabled;
        ManualVersionsList.IsEnabled = enabled;
        SafetyVersionsList.IsEnabled = enabled;
    }

    private static void ShowError(string title, Exception exception)
    {
        MessageBox.Show(
            $"{title}.\n\n{exception.Message}",
            "Versioned Game Saver",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static string Slug(string value)
    {
        var chars = value.ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray();
        return new string(chars).Trim('-');
    }

    private static string FormatLiveSaveKind(LiveSaveEntryKind kind) => kind switch
    {
        LiveSaveEntryKind.AllSaveData => "All save data",
        LiveSaveEntryKind.DetectedLocation => "Detected path",
        LiveSaveEntryKind.DetectedChildFolder => "Detected save folder",
        LiveSaveEntryKind.Registry => "Registry",
        LiveSaveEntryKind.CustomFolder => "Custom folder",
        LiveSaveEntryKind.CustomFile => "Custom file",
        _ => "Live save data"
    };

    private static void ToggleSort(ref string sortColumn, ref bool ascending, string header)
    {
        if (string.Equals(sortColumn, header, StringComparison.Ordinal))
        {
            ascending = !ascending;
            return;
        }

        sortColumn = header;
        ascending = true;
    }

    private static IEnumerable<SnapshotListItem> SortSnapshotItems(
        IEnumerable<SnapshotListItem> items,
        string sortColumn,
        bool ascending)
    {
        return sortColumn switch
        {
            "Slot" => ascending
                ? items.OrderBy(item => item.SlotSort).ThenByDescending(item => item.CreatedSort)
                : items.OrderByDescending(item => item.SlotSort).ThenByDescending(item => item.CreatedSort),
            "Alias" => ascending
                ? items.OrderBy(item => item.AliasSort).ThenByDescending(item => item.CreatedSort)
                : items.OrderByDescending(item => item.AliasSort).ThenByDescending(item => item.CreatedSort),
            "Save Entry" => ascending
                ? items.OrderBy(item => item.SaveEntrySort).ThenByDescending(item => item.CreatedSort)
                : items.OrderByDescending(item => item.SaveEntrySort).ThenByDescending(item => item.CreatedSort),
            "Files" => ascending
                ? items.OrderBy(item => item.FileCountSort).ThenByDescending(item => item.CreatedSort)
                : items.OrderByDescending(item => item.FileCountSort).ThenByDescending(item => item.CreatedSort),
            "Size" => ascending
                ? items.OrderBy(item => item.SizeSort).ThenByDescending(item => item.CreatedSort)
                : items.OrderByDescending(item => item.SizeSort).ThenByDescending(item => item.CreatedSort),
            _ => ascending
                ? items.OrderBy(item => item.CreatedSort)
                : items.OrderByDescending(item => item.CreatedSort)
        };
    }

    private void SelectSnapshotById(string snapshotId)
    {
        ManualVersionsList.SelectedItem = ManualVersionsList.Items
            .OfType<SnapshotListItem>()
            .FirstOrDefault(item => item.Version.Id == snapshotId);

        SafetyVersionsList.SelectedItem = SafetyVersionsList.Items
            .OfType<SnapshotListItem>()
            .FirstOrDefault(item => item.Version.Id == snapshotId);
    }

    private static T? FindAncestor<T>(DependencyObject current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private sealed record BackupAllCandidate(GameEntry Game, LiveSaveEntry? LiveSave);

    private sealed class BackupAllSummary
    {
        public int Created { get; set; }
        public int Overwritten { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public bool Canceled { get; set; }
    }
}



