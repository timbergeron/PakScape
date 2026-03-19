using System.Windows;
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
}
