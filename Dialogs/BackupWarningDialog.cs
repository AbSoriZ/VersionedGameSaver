using System.Windows;
using System.Windows.Controls;

namespace VersionedGameSaver.Dialogs;

public sealed class BackupWarningDialog : Window
{
    private readonly CheckBox _suppressWarning = new()
    {
        Content = "Do not show this warning again",
        Margin = new Thickness(0, 14, 0, 16)
    };

    private BackupWarningDialog()
    {
        Title = "Back Up Save";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 420;

        var continueButton = new Button
        {
            Content = "Continue",
            MinWidth = 90,
            IsDefault = true,
            Margin = new Thickness(0, 0, 8, 0)
        };
        continueButton.Click += (_, _) => { DialogResult = true; Close(); };

        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 90,
            IsCancel = true
        };
        cancelButton.Click += (_, _) => { DialogResult = false; Close(); };

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new TextBlock
                {
                    Text = "If the game is running, it may still be writing save files.\n\nCreate a snapshot anyway?",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 460
                },
                _suppressWarning,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { continueButton, cancelButton }
                }
            }
        };
    }

    public bool SuppressWarning => _suppressWarning.IsChecked == true;

    public static bool Confirm(Window owner, out bool suppressWarning)
    {
        var dialog = new BackupWarningDialog
        {
            Owner = owner
        };

        var result = dialog.ShowDialog() == true;
        suppressWarning = result && dialog.SuppressWarning;
        return result;
    }
}
