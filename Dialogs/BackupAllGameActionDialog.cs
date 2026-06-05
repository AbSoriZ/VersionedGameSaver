using System.Windows;
using System.Windows.Controls;

namespace VersionedGameSaver.Dialogs;

public enum BackupAllGameAction
{
    NewSlot,
    OverwriteLast,
    Skip,
    SkipRemaining,
    CancelAll
}

public sealed class BackupAllGameActionDialog : Window
{
    private BackupAllGameActionDialog(string gameName, string saveName, string overwriteText)
    {
        Title = "Back Up Game";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 500;

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new TextBlock
                {
                    Text = $"Choose how to back up:\n\n{gameName}\n{saveName}",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 560,
                    Margin = new Thickness(0, 0, 0, 16)
                },
                Button("New Slot", BackupAllGameAction.NewSlot),
                Button(overwriteText, BackupAllGameAction.OverwriteLast),
                Button("Skip", BackupAllGameAction.Skip),
                Button("Skip Remaining", BackupAllGameAction.SkipRemaining),
                Button("Cancel Entire Backup", BackupAllGameAction.CancelAll)
            }
        };
    }

    public BackupAllGameAction SelectedAction { get; private set; }

    public static BackupAllGameAction Choose(Window owner, string gameName, string saveName, bool hasOverwriteTarget)
    {
        var dialog = new BackupAllGameActionDialog(
            gameName,
            saveName,
            hasOverwriteTarget ? "Overwrite Last" : "Overwrite Last (creates new slot)")
        {
            Owner = owner
        };

        return dialog.ShowDialog() == true ? dialog.SelectedAction : BackupAllGameAction.CancelAll;
    }

    private Button Button(string text, BackupAllGameAction action)
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
            SelectedAction = action;
            DialogResult = true;
            Close();
        };
        return button;
    }
}
