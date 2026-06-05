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
    private readonly PresetScanner _presetScanner = new();
    private readonly SnapshotService _snapshotService;

    private AppSettings _settings = new();
    private BackupCatalog _catalog = new();
    private bool _isUpdatingNotesText;
    private bool _isBusy;
    private string _manualSortColumn = "Date";
    private bool _manualSortAscending;
    private string _safetySortColumn = "Date";
    private bool _safetySortAscending;
    private string? _aliasBeforeEdit;
    private bool _isCancellingAliasEdit;

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

    private void ScanGames_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLibrary())
        {
            return;
        }

        var detected = _presetScanner.Scan();
        var addedProfiles = 0;
        var addedScopes = 0;

        foreach (var profile in detected)
        {
            var existing = _catalog.Profiles.FirstOrDefault(p =>
                string.Equals(p.GameId, profile.GameId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.SaveRootPath, profile.SaveRootPath, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                _catalog.Profiles.Add(profile);
                addedProfiles++;
                addedScopes += profile.Scopes.Count;
                continue;
            }

            foreach (var scope in profile.Scopes)
            {
                var sourcePaths = scope.Items.Select(i => i.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var alreadyExists = existing.Scopes.Any(existingScope =>
                    existingScope.Items.Select(i => i.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(sourcePaths));

                if (!alreadyExists)
                {
                    existing.Scopes.Add(scope);
                    addedScopes++;
                }
            }
        }

        _catalogService.Save(_settings.LibraryPath!, _catalog);
        RefreshAll();
        MessageBox.Show(
            $"Scan complete.\n\nAdded games: {addedProfiles}\nAdded save entries: {addedScopes}",
            "Scan Games",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void AddManualProfile_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLibrary())
        {
            return;
        }

        var folderDialog = new OpenFolderDialog
        {
            Title = "Choose the game's save root folder"
        };

        if (folderDialog.ShowDialog(this) != true)
        {
            return;
        }

        var gameName = TextInputDialog.Ask(this, "Game Name", "Enter the game name:", Path.GetFileName(folderDialog.FolderName));
        if (string.IsNullOrWhiteSpace(gameName))
        {
            return;
        }

        var label = TextInputDialog.Ask(this, "Profile Label", "Enter the label to show in the game list:", gameName);
        if (string.IsNullOrWhiteSpace(label))
        {
            label = gameName;
        }

        var profile = new GameProfile
        {
            GameId = Slug(gameName),
            GameName = gameName,
            Label = label,
            SaveRootPath = folderDialog.FolderName,
            IsPreset = false
        };

        profile.Scopes.Add(new SaveScope
        {
            Label = $"All {gameName} saves",
            OriginalName = $"All {gameName} saves",
            Kind = SaveScopeKind.WholeGame,
            Items = [new ScopeItem { SourcePath = folderDialog.FolderName }]
        });

        _catalog.Profiles.Add(profile);
        _catalogService.Save(_settings.LibraryPath!, _catalog);
        RefreshAll(profile.Id);
    }

    private void AddFolderScope_Click(object sender, RoutedEventArgs e)
    {
        var profile = SelectedProfile();
        if (profile is null)
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

        AddCustomScope(profile, folderDialog.FolderName, SaveScopeKind.CustomFolder);
    }

    private void AddFileScope_Click(object sender, RoutedEventArgs e)
    {
        var profile = SelectedProfile();
        if (profile is null)
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

        AddCustomScope(profile, dialog.FileName, SaveScopeKind.CustomFile);
    }

    private void AddCustomScope(GameProfile profile, string sourcePath, SaveScopeKind kind)
    {
        var originalName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var scope = new SaveScope
        {
            Label = originalName,
            OriginalName = originalName,
            Kind = kind,
            Items = [new ScopeItem { SourcePath = sourcePath }]
        };

        profile.Scopes.Add(scope);
        _catalogService.Save(_settings.LibraryPath!, _catalog);
        RefreshAll(profile.Id, scope.Id);
    }

    private void ShowScopeDetails_Click(object sender, RoutedEventArgs e)
    {
        var scope = SelectedScope();
        if (scope is null)
        {
            MessageBox.Show("Select a save entry first.", "Save Entry Details", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var paths = scope.Items.Count == 0
            ? "No tracked paths"
            : string.Join(Environment.NewLine, scope.Items.Select(item => item.SourcePath));

        MessageBox.Show(
            $"Display name: {scope.DisplayName}\nOriginal name: {scope.OriginalName}\nEntry type: {FormatScopeKind(scope.Kind)}\n\nTracked paths:\n{paths}",
            "Save Entry Details",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void EditScopeAlias_Click(object sender, RoutedEventArgs e)
    {
        var selected = RequireProfileAndScope();
        if (selected is null)
        {
            return;
        }

        var (profile, scope) = selected.Value;
        var alias = TextInputDialog.Ask(this, "Edit Alias", "Enter an alias for this save entry:", scope.DisplayName);
        if (string.IsNullOrWhiteSpace(alias))
        {
            return;
        }

        scope.Alias = string.Equals(alias, scope.OriginalName, StringComparison.Ordinal) ? null : alias;
        scope.Label = scope.DisplayName;
        _catalogService.Save(_settings.LibraryPath!, _catalog);
        RefreshAll(profile.Id, scope.Id);
    }

    private void ClearScopeAlias_Click(object sender, RoutedEventArgs e)
    {
        var selected = RequireProfileAndScope();
        if (selected is null)
        {
            return;
        }

        var (profile, scope) = selected.Value;
        if (string.IsNullOrWhiteSpace(scope.Alias))
        {
            return;
        }

        scope.Alias = null;
        scope.Label = scope.DisplayName;
        _catalogService.Save(_settings.LibraryPath!, _catalog);
        RefreshAll(profile.Id, scope.Id);
    }

    private async void BackupSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var selected = RequireProfileAndScope();
        if (selected is null)
        {
            return;
        }

        var (profile, scope) = selected.Value;
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
            SetBusy($"Checking {scope.DisplayName}...");
            var estimate = await Task.Run(() => _snapshotService.Estimate(scope));
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

                SetBusy($"Backing up {scope.DisplayName}...");
            }
            else
            {
                SetBusy($"Backing up {scope.DisplayName}...");
            }

            await Task.Run(() => _snapshotService.CreateSnapshot(_settings.LibraryPath!, _catalog, profile, scope, SnapshotKind.Manual));
            RefreshVersions(scope.Id);
            StatusText.Text = $"Backup complete: {scope.DisplayName}";
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

    private void RestoreSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = RequireProfileScopeAndSnapshot();
        if (selected is null)
        {
            return;
        }

        var (profile, scope, snapshot) = selected.Value;
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
            _snapshotService.RestoreSnapshot(_settings.LibraryPath!, _catalog, profile, scope, snapshot);
            RefreshVersions(SelectedScope()?.Id);
            MessageBox.Show("Restore complete. A safety snapshot was created first.", "Restore Save", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            ShowError("Restore failed", exception);
        }
    }

    private async void OverwriteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        var selected = RequireProfileScopeAndSnapshot();
        if (selected is null)
        {
            return;
        }

        var (profile, scope, snapshot) = selected.Value;
        if (snapshot.Kind == SnapshotKind.Safety)
        {
            MessageBox.Show("Safety snapshots cannot be overwritten.", "Overwrite Version", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"Overwrite \"{snapshot.VersionName}\" with the current live save?\n\nThe slot number will stay the same.",
            "Overwrite Version",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            SetBusy($"Overwriting {snapshot.VersionName}...");
            await Task.Run(() => _snapshotService.CreateSnapshot(_settings.LibraryPath!, _catalog, profile, scope, SnapshotKind.Manual, snapshot));
            RefreshVersions(SelectedScope()?.Id);
            StatusText.Text = $"Overwrite complete: {scope.DisplayName}";
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

    private void ShowSnapshotDetails_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = SelectedSnapshot();
        var scope = SelectedSnapshotScope();
        if (snapshot is null)
        {
            MessageBox.Show("Select a version first.", "Version Details", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var archivePath = string.IsNullOrWhiteSpace(_settings.LibraryPath)
            ? snapshot.ArchiveRelativePath
            : Path.Combine(_settings.LibraryPath, snapshot.ArchiveRelativePath);
        var notes = string.IsNullOrWhiteSpace(snapshot.Notes) ? "None" : snapshot.Notes;

        MessageBox.Show(
            $"Version: {snapshot.VersionName}\nSlot: {(snapshot.SlotNumber?.ToString() ?? "Auto")}\nSave entry: {scope?.DisplayName ?? "Unknown"}\nType: {snapshot.Kind}\nCreated: {snapshot.CreatedAtUtc.ToLocalTime():F}\nFiles: {snapshot.FileCount:N0}\nArchive size: {FileSizeFormatter.Format(snapshot.SizeBytes)}\nArchive path: {archivePath}\n\nNotes:\n{notes}",
            "Version Details",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void EditSnapshotAlias_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = SelectedSnapshot();
        if (snapshot is null)
        {
            MessageBox.Show("Select a version first.", "Edit Alias", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var alias = TextInputDialog.Ask(this, "Edit Alias", "Enter an alias for this version:", snapshot.VersionName);
        if (string.IsNullOrWhiteSpace(alias))
        {
            return;
        }

        snapshot.Alias = string.Equals(alias, snapshot.OriginalName, StringComparison.Ordinal) ? null : alias;
        snapshot.Name = snapshot.Alias;
        _catalogService.Save(_settings.LibraryPath!, _catalog);
        RefreshVersions(SelectedScope()?.Id);
    }

    private void ClearSnapshotAlias_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = SelectedSnapshot();
        if (snapshot is null)
        {
            MessageBox.Show("Select a version first.", "Clear Alias", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        snapshot.Alias = null;
        snapshot.Name = null;
        _catalogService.Save(_settings.LibraryPath!, _catalog);
        RefreshVersions(SelectedScope()?.Id);
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
            ? $"version \"{selectedItems[0].Snapshot.VersionName}\""
            : $"{selectedItems.Count} selected versions";

        var result = MessageBox.Show(
            $"Delete {versionText}?\n\nThis cannot be undone.",
            "Delete Version",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var item in selectedItems)
        {
            _snapshotService.DeleteSnapshot(_settings.LibraryPath!, _catalog, item.Snapshot);
        }

        RefreshVersions(SelectedScope()?.Id);
    }

    private void DeleteScope_Click(object sender, RoutedEventArgs e)
    {
        var selected = RequireProfileAndScope();
        if (selected is null)
        {
            return;
        }

        var (profile, scope) = selected.Value;
        var result = MessageBox.Show(
            $"Delete save entry \"{scope.DisplayName}\" and every version under it?\n\nThis cannot be undone.",
            "Delete Save Entry",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _snapshotService.DeleteScope(_settings.LibraryPath!, _catalog, profile, scope);
        RefreshAll(profile.Id);
    }

    private void DeleteGame_Click(object sender, RoutedEventArgs e)
    {
        var profile = SelectedProfile();
        if (profile is null)
        {
            MessageBox.Show("Select a game first.", "Remove Game From Library", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"Remove \"{profile}\" from this backup library?\n\nThis removes the game's backup entries and snapshot archives from the selected library.\n\nIt does not delete real/live game saves. This cannot be undone.",
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
            $"Final confirmation: you are deleting the entire backup history for \"{profile}\" from this backup library.\n\nThis includes all tracked save entries, manual versions, safety versions, and snapshot archives for this game.\n\nYour real/live game save data on disk will not be deleted.",
            delaySeconds: 10);

        if (!confirmed)
        {
            return;
        }

        _snapshotService.DeleteProfile(_settings.LibraryPath!, _catalog, profile);
        RefreshAll();
    }

    private void GamesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshScopes();
    }

    private void ScopesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var scope = SelectedScope();
        RefreshVersions(scope?.Id);
    }

    private void ScopesListItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
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

    private void AliasTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: SnapshotListItem item })
        {
            _aliasBeforeEdit = item.Snapshot.Alias;
            _isCancellingAliasEdit = false;
        }
    }

    private void AliasTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            CommitAliasEdit(textBox);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape)
        {
            _isCancellingAliasEdit = true;
            if (textBox.DataContext is SnapshotListItem item)
            {
                item.Snapshot.Alias = _aliasBeforeEdit;
                item.Snapshot.Name = item.Snapshot.Alias;
                textBox.Text = item.Snapshot.Alias ?? "";
            }

            textBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
        }
    }

    private void AliasTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        if (_isCancellingAliasEdit)
        {
            _isCancellingAliasEdit = false;
            return;
        }

        CommitAliasEdit(textBox);
    }

    private void ManualSnapshotHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Column.Header: string header })
        {
            ToggleSort(ref _manualSortColumn, ref _manualSortAscending, header);
            RefreshVersions(SelectedScope()?.Id);
        }
    }

    private void SafetySnapshotHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Column.Header: string header })
        {
            ToggleSort(ref _safetySortColumn, ref _safetySortAscending, header);
            RefreshVersions(SelectedScope()?.Id);
        }
    }

    private void NotesText_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isUpdatingNotesText)
        {
            return;
        }

        var snapshot = SelectedSnapshot();
        if (snapshot is null || string.IsNullOrWhiteSpace(_settings.LibraryPath))
        {
            return;
        }

        snapshot.Notes = string.IsNullOrWhiteSpace(NotesText.Text) ? null : NotesText.Text;
        _catalogService.Save(_settings.LibraryPath, _catalog);
    }

    private void VersionsTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source == VersionsTabs)
        {
            UpdateSnapshotDetails();
        }
    }

    private void SnapshotsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender == ManualSnapshotsList && ManualSnapshotsList.SelectedItem is not null)
        {
            SafetySnapshotsList.SelectedItem = null;
        }
        else if (sender == SafetySnapshotsList && SafetySnapshotsList.SelectedItem is not null)
        {
            ManualSnapshotsList.SelectedItem = null;
        }

        UpdateSnapshotDetails();
    }

    private void RefreshAll(string? profileId = null, string? scopeId = null)
    {
        LibraryText.Text = string.IsNullOrWhiteSpace(_settings.LibraryPath)
            ? "No library selected"
            : _settings.LibraryPath;

        GamesList.ItemsSource = _catalog.Profiles.OrderBy(p => p.GameName).ThenBy(p => p.Label).ToList();
        if (profileId is not null)
        {
            GamesList.SelectedItem = _catalog.Profiles.FirstOrDefault(p => p.Id == profileId);
        }
        else if (GamesList.Items.Count > 0 && GamesList.SelectedItem is null)
        {
            GamesList.SelectedIndex = 0;
        }

        RefreshScopes(scopeId);
    }

    private void RefreshScopes(string? scopeId = null)
    {
        var profile = SelectedProfile();
        ScopesList.ItemsSource = profile?.Scopes.OrderBy(s => s.Kind).ThenBy(s => s.DisplayName).ToList() ?? [];

        if (scopeId is not null && profile is not null)
        {
            ScopesList.SelectedItem = profile.Scopes.FirstOrDefault(s => s.Id == scopeId);
        }
        else if (ScopesList.Items.Count > 0 && ScopesList.SelectedItem is null)
        {
            ScopesList.SelectedIndex = 0;
        }

        RefreshVersions(SelectedScope()?.Id);
    }

    private void RefreshVersions(string? scopeId)
    {
        if (scopeId is null)
        {
            ManualSnapshotsList.ItemsSource = null;
            SafetySnapshotsList.ItemsSource = null;
            UpdateSnapshotDetails();
            return;
        }

        var profile = SelectedProfile();
        var selectedScope = SelectedScope();
        if (profile is null || selectedScope is null)
        {
            ManualSnapshotsList.ItemsSource = null;
            SafetySnapshotsList.ItemsSource = null;
            UpdateSnapshotDetails();
            return;
        }

        var scopesById = profile.Scopes.ToDictionary(scope => scope.Id);
        var visibleScopeIds = selectedScope.Kind == SaveScopeKind.WholeGame
            ? scopesById.Keys.ToHashSet()
            : new HashSet<string> { selectedScope.Id };

        var selectedSnapshotId = SelectedSnapshot()?.Id;

        var manualItems = _catalog.Snapshots
            .Where(snapshot => snapshot.ProfileId == profile.Id
                && snapshot.Kind == SnapshotKind.Manual
                && visibleScopeIds.Contains(snapshot.ScopeId)
                && scopesById.ContainsKey(snapshot.ScopeId))
            .Select(snapshot => new SnapshotListItem
            {
                Snapshot = snapshot,
                Scope = scopesById[snapshot.ScopeId]
            })
            .ToList();

        var safetyItems = _catalog.Snapshots
            .Where(snapshot => snapshot.ProfileId == profile.Id
                && snapshot.Kind == SnapshotKind.Safety
                && visibleScopeIds.Contains(snapshot.ScopeId)
                && scopesById.ContainsKey(snapshot.ScopeId))
            .Select(snapshot => new SnapshotListItem
            {
                Snapshot = snapshot,
                Scope = scopesById[snapshot.ScopeId]
            })
            .ToList();

        ManualSnapshotsList.ItemsSource = SortSnapshotItems(manualItems, _manualSortColumn, _manualSortAscending).ToList();
        SafetySnapshotsList.ItemsSource = SortSnapshotItems(safetyItems, _safetySortColumn, _safetySortAscending).ToList();

        if (!string.IsNullOrWhiteSpace(selectedSnapshotId))
        {
            ManualSnapshotsList.SelectedItem = ManualSnapshotsList.Items
                .OfType<SnapshotListItem>()
                .FirstOrDefault(item => item.Snapshot.Id == selectedSnapshotId);

            SafetySnapshotsList.SelectedItem = SafetySnapshotsList.Items
                .OfType<SnapshotListItem>()
                .FirstOrDefault(item => item.Snapshot.Id == selectedSnapshotId);
        }

        UpdateSnapshotDetails();
    }

    private void UpdateSnapshotDetails()
    {
        var snapshot = SelectedSnapshot();
        if (snapshot is null)
        {
            VersionDetailsText.Text = "Select a version to see details.";
            _isUpdatingNotesText = true;
            NotesText.Text = "";
            _isUpdatingNotesText = false;
            return;
        }

        VersionDetailsText.Text =
            $"{snapshot.VersionName}\nCreated: {snapshot.CreatedAtUtc.ToLocalTime():F}\nFiles: {snapshot.FileCount:N0}\nArchive size: {FileSizeFormatter.Format(snapshot.SizeBytes)}";
        _isUpdatingNotesText = true;
        NotesText.Text = snapshot.Notes ?? "";
        _isUpdatingNotesText = false;
    }

    private GameProfile? SelectedProfile() => GamesList.SelectedItem as GameProfile;

    private SaveScope? SelectedScope() => ScopesList.SelectedItem as SaveScope;

    private SnapshotRecord? SelectedSnapshot()
    {
        if (VersionsTabs.SelectedIndex == 1)
        {
            return (SafetySnapshotsList.SelectedItem as SnapshotListItem)?.Snapshot;
        }

        return (ManualSnapshotsList.SelectedItem as SnapshotListItem)?.Snapshot;
    }

    private SaveScope? SelectedSnapshotScope()
    {
        if (VersionsTabs.SelectedIndex == 1)
        {
            return (SafetySnapshotsList.SelectedItem as SnapshotListItem)?.Scope;
        }

        return (ManualSnapshotsList.SelectedItem as SnapshotListItem)?.Scope;
    }

    private List<SnapshotListItem> SelectedSnapshotItems()
    {
        var list = VersionsTabs.SelectedIndex == 1 ? SafetySnapshotsList : ManualSnapshotsList;
        return list.SelectedItems.OfType<SnapshotListItem>().ToList();
    }

    private (GameProfile Profile, SaveScope Scope)? RequireProfileAndScope()
    {
        if (!EnsureLibrary())
        {
            return null;
        }

        var profile = SelectedProfile();
        var scope = SelectedScope();
        if (profile is null || scope is null)
        {
            MessageBox.Show("Select a game and save entry first.", "Versioned Game Saver", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        return (profile, scope);
    }

    private (GameProfile Profile, SaveScope Scope, SnapshotRecord Snapshot)? RequireProfileScopeAndSnapshot()
    {
        var selected = RequireProfileAndScope();
        if (selected is null)
        {
            return null;
        }

        var snapshot = SelectedSnapshot();
        if (snapshot is null)
        {
            MessageBox.Show("Select a version first.", "Versioned Game Saver", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        return (selected.Value.Profile, SelectedSnapshotScope() ?? selected.Value.Scope, snapshot);
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

    private void SetBusy(string message)
    {
        _isBusy = true;
        StatusText.Text = message;
        SetActionsEnabled(false);
    }

    private void ClearBusy()
    {
        _isBusy = false;
        SetActionsEnabled(true);
    }

    private void SetActionsEnabled(bool enabled)
    {
        ChooseLibraryButton.IsEnabled = enabled;
        ScanGamesButton.IsEnabled = enabled;
        AddManualProfileButton.IsEnabled = enabled;
        RemoveGameButton.IsEnabled = enabled;
        BackupSelectedButton.IsEnabled = enabled;
        AddFolderButton.IsEnabled = enabled;
        AddFileButton.IsEnabled = enabled;
        RestoreButton.IsEnabled = enabled;
        OverwriteButton.IsEnabled = enabled;
        DeleteVersionButton.IsEnabled = enabled;
        ScopesList.IsEnabled = enabled;
        ManualSnapshotsList.IsEnabled = enabled;
        SafetySnapshotsList.IsEnabled = enabled;
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

    private static string FormatScopeKind(SaveScopeKind kind) => kind switch
    {
        SaveScopeKind.WholeGame => "Whole game save folder",
        SaveScopeKind.DetectedWorld => "Detected save folder",
        SaveScopeKind.CustomFolder => "Custom save folder",
        SaveScopeKind.CustomFile => "Custom save file",
        _ => "Save entry"
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

    private void CommitAliasEdit(TextBox textBox)
    {
        if (textBox.DataContext is not SnapshotListItem item || string.IsNullOrWhiteSpace(_settings.LibraryPath))
        {
            return;
        }

        item.Snapshot.Alias = string.IsNullOrWhiteSpace(textBox.Text) ? null : textBox.Text.Trim();
        item.Snapshot.Name = item.Snapshot.Alias;
        _catalogService.Save(_settings.LibraryPath, _catalog);

        var editedSnapshotId = item.Snapshot.Id;
        RefreshVersions(SelectedScope()?.Id);
        SelectSnapshotById(editedSnapshotId);
    }

    private void SelectSnapshotById(string snapshotId)
    {
        ManualSnapshotsList.SelectedItem = ManualSnapshotsList.Items
            .OfType<SnapshotListItem>()
            .FirstOrDefault(item => item.Snapshot.Id == snapshotId);

        SafetySnapshotsList.SelectedItem = SafetySnapshotsList.Items
            .OfType<SnapshotListItem>()
            .FirstOrDefault(item => item.Snapshot.Id == snapshotId);
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
}
