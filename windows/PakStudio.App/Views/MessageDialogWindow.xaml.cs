using System.Windows;
using System.Windows.Controls;

namespace PakStudio.App.Views;

public enum MessageDialogButtons
{
    Ok,
    ConfirmCancel,
    SaveDiscardCancel,
}

public enum MessageDialogResult
{
    Ok,
    Confirm,
    Save,
    Discard,
    Cancel,
}

public partial class MessageDialogWindow : Window
{
    private MessageDialogResult _result;

    public MessageDialogWindow(
        string title,
        string message,
        MessageDialogButtons buttons)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;

        switch (buttons)
        {
            case MessageDialogButtons.Ok:
                _result = MessageDialogResult.Ok;
                AddButton("OK", MessageDialogResult.Ok, isDefault: true, isCancel: true);
                break;
            case MessageDialogButtons.ConfirmCancel:
                _result = MessageDialogResult.Cancel;
                AddButton("Cancel", MessageDialogResult.Cancel, isCancel: true);
                AddButton("Yes", MessageDialogResult.Confirm, isDefault: true);
                break;
            case MessageDialogButtons.SaveDiscardCancel:
                _result = MessageDialogResult.Cancel;
                AddButton("Cancel", MessageDialogResult.Cancel, isCancel: true);
                AddButton("Don't Save", MessageDialogResult.Discard);
                AddButton("Save", MessageDialogResult.Save, isDefault: true);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(buttons));
        }
    }

    public MessageDialogResult ShowDialogResult()
    {
        _ = ShowDialog();
        return _result;
    }

    private void AddButton(
        string label,
        MessageDialogResult result,
        bool isDefault = false,
        bool isCancel = false)
    {
        var button = new Button
        {
            Content = label,
            MinWidth = 88,
            IsDefault = isDefault,
            IsCancel = isCancel,
        };
        button.Click += (_, _) =>
        {
            _result = result;
            Close();
        };
        ButtonPanel.Children.Add(button);
    }
}
