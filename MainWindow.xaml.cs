using System.IO;
using System.Windows;
using System.Windows.Controls;
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
            $"Scan complete.\n\nAdded games: {addedProfiles}\nAdded saves/worlds: {addedScopes}",
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
            Title = "Choose a save/world folder to track"
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
        var defaultLabel = Path.GetFileName(sourcePath);
        var label = TextInputDialog.Ask(this, "Scope Label", "Enter a label for this save/world:", defaultLabel);
        if (string.IsNullOrWhiteSpace(label))
        {
            label = defaultLabel;
        }

        var scope = new SaveScope
        {
            Label = label,
            Kind = kind,
            Items = [new ScopeItem { SourcePath = sourcePath }]
        };

        profile.Scopes.Add(scope);
        _catalogService.Save(_settings.LibraryPath!, _catalog);
        RefreshAll(profile.Id, scope.Id);
    }

    private void BackupSelected_Click(object sender, RoutedEventArgs e)
    {
        var selected = RequireProfileAndScope();
        if (selected is null)
        {
            return;
        }

        var (profile, scope) = selected.Value;
        var runningWarning = MessageBox.Show(
            "If the game is running, it may still be writing save files.\n\nCreate a snapshot anyway?",
            "Back Up Save",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (runningWarning != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var estimate = _snapshotService.Estimate(scope);
            if (estimate.IsLarge)
            {
                var result = MessageBox.Show(
                    $"This snapshot is estimated at {FileSizeFormatter.Format(estimate.Bytes)} across {estimate.Files:N0} files.\n\nCreate it anyway?",
                    "Large Snapshot",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            _snapshotService.CreateSnapshot(_settings.LibraryPath!, _catalog, profile, scope, SnapshotKind.Manual);
            RefreshVersions(scope.Id);
        }
        catch (Exception exception)
        {
            ShowError("Backup failed", exception);
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
            RefreshVersions(scope.Id);
            MessageBox.Show("Restore complete. A safety snapshot was created first.", "Restore Save", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            ShowError("Restore failed", exception);
        }
    }

    private void OverwriteSelected_Click(object sender, RoutedEventArgs e)
    {
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
            $"Overwrite \"{snapshot.DisplayName}\" with the current live save?",
            "Overwrite Version",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _snapshotService.CreateSnapshot(_settings.LibraryPath!, _catalog, profile, scope, SnapshotKind.Manual, snapshot);
            RefreshVersions(scope.Id);
        }
        catch (Exception exception)
        {
            ShowError("Overwrite failed", exception);
        }
    }

    private void EditSnapshot_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = SelectedSnapshot();
        if (snapshot is null)
        {
            MessageBox.Show("Select a version first.", "Edit Version", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (SnapshotEditDialog.Edit(this, snapshot))
        {
            _catalogService.Save(_settings.LibraryPath!, _catalog);
            RefreshVersions(snapshot.ScopeId);
        }
    }

    private void DeleteSnapshot_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = SelectedSnapshot();
        if (snapshot is null)
        {
            MessageBox.Show("Select a version first.", "Delete Version", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"Delete version \"{snapshot.DisplayName}\"?\n\nThis cannot be undone.",
            "Delete Version",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        _snapshotService.DeleteSnapshot(_settings.LibraryPath!, _catalog, snapshot);
        RefreshVersions(snapshot.ScopeId);
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
            $"Delete backup scope \"{scope.Label}\" and every version under it?\n\nThis cannot be undone.",
            "Delete Scope",
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
            MessageBox.Show("Select a game first.", "Delete Game Backup", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var result = MessageBox.Show(
            $"Delete the backup entry for \"{profile}\" and every version under it?\n\nThis does not delete live game saves. This cannot be undone.",
            "Delete Game Backup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
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
        ScopesList.ItemsSource = profile?.Scopes.OrderBy(s => s.Kind).ThenBy(s => s.Label).ToList() ?? [];

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

        ManualSnapshotsList.ItemsSource = _catalog.Snapshots
            .Where(s => s.ScopeId == scopeId && s.Kind == SnapshotKind.Manual)
            .OrderByDescending(s => s.CreatedAtUtc)
            .ToList();

        SafetySnapshotsList.ItemsSource = _catalog.Snapshots
            .Where(s => s.ScopeId == scopeId && s.Kind == SnapshotKind.Safety)
            .OrderByDescending(s => s.CreatedAtUtc)
            .ToList();

        UpdateSnapshotDetails();
    }

    private void UpdateSnapshotDetails()
    {
        var snapshot = SelectedSnapshot();
        if (snapshot is null)
        {
            VersionDetailsText.Text = "Select a version to see details.";
            NotesText.Text = "";
            return;
        }

        VersionDetailsText.Text =
            $"{snapshot.DisplayName}\nCreated: {snapshot.CreatedAtUtc.ToLocalTime():F}\nFiles: {snapshot.FileCount:N0}\nArchive size: {FileSizeFormatter.Format(snapshot.SizeBytes)}";
        NotesText.Text = snapshot.Notes ?? "";
    }

    private GameProfile? SelectedProfile() => GamesList.SelectedItem as GameProfile;

    private SaveScope? SelectedScope() => ScopesList.SelectedItem as SaveScope;

    private SnapshotRecord? SelectedSnapshot()
    {
        if (VersionsTabs.SelectedIndex == 1)
        {
            return SafetySnapshotsList.SelectedItem as SnapshotRecord;
        }

        return ManualSnapshotsList.SelectedItem as SnapshotRecord;
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
            MessageBox.Show("Select a game and save/world first.", "Versioned Game Saver", MessageBoxButton.OK, MessageBoxImage.Information);
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

        return (selected.Value.Profile, selected.Value.Scope, snapshot);
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
}
