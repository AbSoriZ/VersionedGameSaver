using System.Windows;
using System.Windows.Controls;
using VersionedGameSaver.Models;

namespace VersionedGameSaver.Dialogs;

public sealed class SnapshotEditDialog : Window
{
    private readonly System.Windows.Controls.TextBox _name = new();
    private readonly System.Windows.Controls.TextBox _notes = new();

    private SnapshotEditDialog(SnapshotRecord snapshot)
    {
        Title = "Edit Version";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        Width = 460;
        Height = 360;

        _name.Text = snapshot.Name ?? "";
        _notes.Text = snapshot.Notes ?? "";
        _notes.AcceptsReturn = true;
        _notes.TextWrapping = TextWrapping.Wrap;
        _notes.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _notes.MinHeight = 140;

        var ok = new Button { Content = "Save", MinWidth = 76, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => { DialogResult = true; Close(); };

        var cancel = new Button { Content = "Cancel", MinWidth = 76, IsCancel = true };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };

        Content = new DockPanel { Margin = new Thickness(18) };
        var root = (DockPanel)Content;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { ok, cancel }
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        root.Children.Add(new StackPanel
        {
            Children =
            {
                new TextBlock { Text = "Name", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) },
                _name,
                new TextBlock { Text = "Notes", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) },
                _notes
            }
        });
    }

    public static bool Edit(Window owner, SnapshotRecord snapshot)
    {
        var dialog = new SnapshotEditDialog(snapshot)
        {
            Owner = owner
        };

        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        snapshot.Name = string.IsNullOrWhiteSpace(dialog._name.Text) ? null : dialog._name.Text.Trim();
        snapshot.Notes = string.IsNullOrWhiteSpace(dialog._notes.Text) ? null : dialog._notes.Text.Trim();
        return true;
    }
}
