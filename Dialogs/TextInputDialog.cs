using System.Windows;
using System.Windows.Controls;

namespace VersionedGameSaver.Dialogs;

public sealed class TextInputDialog : Window
{
    private readonly System.Windows.Controls.TextBox _input = new();

    private TextInputDialog(string title, string prompt, string? defaultValue)
    {
        Title = title;
        Owner = Application.Current.MainWindow;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 360;

        _input.Text = defaultValue ?? "";
        _input.Margin = new Thickness(0, 8, 0, 14);
        _input.MinWidth = 320;

        var ok = new Button { Content = "OK", MinWidth = 76, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => { DialogResult = true; Close(); };

        var cancel = new Button { Content = "Cancel", MinWidth = 76, IsCancel = true };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new TextBlock { Text = prompt },
                _input,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { ok, cancel }
                }
            }
        };
    }

    public string Value => _input.Text.Trim();

    public static string? Ask(Window owner, string title, string prompt, string? defaultValue = null)
    {
        var dialog = new TextInputDialog(title, prompt, defaultValue)
        {
            Owner = owner
        };

        return dialog.ShowDialog() == true ? dialog.Value : null;
    }
}
