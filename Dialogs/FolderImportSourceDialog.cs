using System.Windows;
using System.Windows.Controls;

namespace VersionedGameSaver.Dialogs;

public enum FolderImportSourceKind
{
    Folder,
    Zip
}

public sealed class FolderImportSourceDialog : Window
{
    private FolderImportSourceDialog()
    {
        Title = "Import Prior Save";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        MinWidth = 380;

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Children =
            {
                new TextBlock
                {
                    Text = "Choose the prior save source:",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420,
                    Margin = new Thickness(0, 0, 0, 16)
                },
                Button("Choose Folder", FolderImportSourceKind.Folder),
                Button("Choose ZIP", FolderImportSourceKind.Zip),
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

    public FolderImportSourceKind SelectedKind { get; private set; }

    public static FolderImportSourceKind? Choose(Window owner)
    {
        var dialog = new FolderImportSourceDialog
        {
            Owner = owner
        };

        return dialog.ShowDialog() == true ? dialog.SelectedKind : null;
    }

    private Button Button(string text, FolderImportSourceKind kind)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 180,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 0, 0, 8)
        };
        button.Click += (_, _) =>
        {
            SelectedKind = kind;
            DialogResult = true;
            Close();
        };
        return button;
    }
}
