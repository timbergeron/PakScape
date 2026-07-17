using System.Windows;
using PakStudio.Core.Documents;
using PakStudio.Core.Interfaces;

namespace PakStudio.App.Services;

public sealed class MessageBoxService : IMessageBoxService
{
    public void ShowInfo(string title, string message)
    {
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public void ShowError(string title, string message)
    {
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public bool Confirm(string title, string message)
    {
        return MessageBox.Show(
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    public SaveChangesDecision ConfirmSaveChanges(string displayName)
    {
        var result = MessageBox.Show(
            $"Save changes to '{displayName}' before continuing?",
            "Unsaved Changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Yes);

        return result switch
        {
            MessageBoxResult.Yes => SaveChangesDecision.Save,
            MessageBoxResult.No => SaveChangesDecision.Discard,
            _ => SaveChangesDecision.Cancel,
        };
    }
}
