using System.Windows;
using System.Windows.Controls;

namespace VersionedGameSaver.Dialogs;

public enum BackupAllMode
{
    NewSlotForAll,
    OverwriteLastForEach,
    AskPerGame
}

public sealed class BackupAllModeDialog : Window
{
    private BackupAllModeDialog()
    {
        Title = "Back Up All Games";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 460;

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new TextBlock
                {
                    Text = "How should snapshots be created for all games?",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 520,
                    Margin = new Thickness(0, 0, 0, 16)
                },
                Button("New save slot for all", BackupAllMode.NewSlotForAll),
                Button("Overwrite last for each", BackupAllMode.OverwriteLastForEach),
                Button("Ask per game", BackupAllMode.AskPerGame),
                new Button
                {
                    Content = "Cancel",
                    MinWidth = 120,
                    IsCancel = true,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 10, 0, 0)
                }
            }
        };
    }

    public BackupAllMode SelectedMode { get; private set; }

    public static BackupAllMode? Choose(Window owner)
    {
        var dialog = new BackupAllModeDialog
        {
            Owner = owner
        };

        return dialog.ShowDialog() == true ? dialog.SelectedMode : null;
    }

    private Button Button(string text, BackupAllMode mode)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 220,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 8)
        };
        button.Click += (_, _) =>
        {
            SelectedMode = mode;
            DialogResult = true;
            Close();
        };
        return button;
    }
}
