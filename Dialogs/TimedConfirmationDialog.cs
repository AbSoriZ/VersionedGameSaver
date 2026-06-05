using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VersionedGameSaver.Dialogs;

public sealed class TimedConfirmationDialog : Window
{
    private readonly Button _proceedButton = new()
    {
        MinWidth = 90,
        IsDefault = true,
        Margin = new Thickness(0, 0, 8, 0),
        IsEnabled = false
    };

    private readonly DispatcherTimer _timer = new()
    {
        Interval = TimeSpan.FromSeconds(1)
    };

    private int _secondsRemaining;

    private TimedConfirmationDialog(string title, string message, int delaySeconds)
    {
        Title = title;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 460;
        _secondsRemaining = delaySeconds;

        _proceedButton.Content = $"Proceed ({_secondsRemaining})";
        _proceedButton.Click += (_, _) => { DialogResult = true; Close(); };

        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 90,
            IsCancel = true
        };
        cancelButton.Click += (_, _) => { DialogResult = false; Close(); };

        _timer.Tick += (_, _) =>
        {
            _secondsRemaining--;
            if (_secondsRemaining <= 0)
            {
                _timer.Stop();
                _proceedButton.Content = "Proceed";
                _proceedButton.IsEnabled = true;
                return;
            }

            _proceedButton.Content = $"Proceed ({_secondsRemaining})";
        };

        Closed += (_, _) => _timer.Stop();

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 520,
                    Margin = new Thickness(0, 0, 0, 16)
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { _proceedButton, cancelButton }
                }
            }
        };

        _timer.Start();
    }

    public static bool Confirm(Window owner, string title, string message, int delaySeconds)
    {
        var dialog = new TimedConfirmationDialog(title, message, delaySeconds)
        {
            Owner = owner
        };

        return dialog.ShowDialog() == true;
    }
}
