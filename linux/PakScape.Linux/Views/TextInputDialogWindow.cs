using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace PakScape.Linux.Views;

public sealed class TextInputDialogWindow : Window
{
    private readonly TextBox _textBox;

    public TextInputDialogWindow(
        string title,
        string message,
        string initialValue)
    {
        Title = title;
        Width = 480;
        MinWidth = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _textBox = new TextBox
        {
            Text = initialValue,
            MinWidth = 360,
        };

        var cancelButton = new Button
        {
            Content = "Cancel",
            MinWidth = 88,
            IsCancel = true,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        cancelButton.Click += (_, _) => Close(null);

        var okButton = new Button
        {
            Content = "OK",
            MinWidth = 88,
            IsDefault = true,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        okButton.Click += (_, _) => Close(_textBox.Text);

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                },
                _textBox,
                new StackPanel
                {
                    Margin = new Thickness(0, 8, 0, 0),
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancelButton, okButton },
                },
            },
        };

        Opened += (_, _) =>
        {
            _textBox.Focus();
            _textBox.SelectAll();
        };
    }
}
