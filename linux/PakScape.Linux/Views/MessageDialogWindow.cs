using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using PakStudio.Core.Documents;

namespace PakScape.Linux.Views;

public enum MessageDialogButtons
{
    Ok,
    ConfirmCancel,
    SaveDiscardCancel,
}

public enum MessageDialogResult
{
    None,
    Ok,
    Confirm,
    Save,
    Discard,
    Cancel,
}

public sealed class MessageDialogWindow : Window
{
    public MessageDialogWindow(
        string title,
        string message,
        MessageDialogButtons buttons,
        string confirmText = "Continue")
    {
        Title = title;
        Width = 480;
        MinWidth = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };

        switch (buttons)
        {
            case MessageDialogButtons.Ok:
                buttonPanel.Children.Add(CreateButton("OK", MessageDialogResult.Ok, isDefault: true));
                break;
            case MessageDialogButtons.ConfirmCancel:
                buttonPanel.Children.Add(CreateButton("Cancel", MessageDialogResult.Cancel, isCancel: true));
                buttonPanel.Children.Add(CreateButton(confirmText, MessageDialogResult.Confirm, isDefault: true));
                break;
            case MessageDialogButtons.SaveDiscardCancel:
                buttonPanel.Children.Add(CreateButton("Cancel", MessageDialogResult.Cancel, isCancel: true));
                buttonPanel.Children.Add(CreateButton("Discard", MessageDialogResult.Discard));
                buttonPanel.Children.Add(CreateButton("Save", MessageDialogResult.Save, isDefault: true));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(buttons));
        }

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 22,
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    MaxWidth = 560,
                },
                buttonPanel,
            },
        };
    }

    public static SaveChangesDecision ToSaveChangesDecision(MessageDialogResult result)
    {
        return result switch
        {
            MessageDialogResult.Save => SaveChangesDecision.Save,
            MessageDialogResult.Discard => SaveChangesDecision.Discard,
            _ => SaveChangesDecision.Cancel,
        };
    }

    private Button CreateButton(
        string text,
        MessageDialogResult result,
        bool isDefault = false,
        bool isCancel = false)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = isDefault,
            IsCancel = isCancel,
        };
        button.Click += (_, _) => Close(result);
        return button;
    }
}
