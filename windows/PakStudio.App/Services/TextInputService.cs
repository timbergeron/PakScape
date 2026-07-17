using System.Windows;
using PakStudio.App.Views;
using PakStudio.Core.Interfaces;

namespace PakStudio.App.Services;

public sealed class TextInputService : ITextInputService
{
    public string? Prompt(string title, string message, string initialValue)
    {
        var dialog = new TextInputDialog(title, message, initialValue)
        {
            Owner = Application.Current.MainWindow,
        };

        return dialog.ShowDialog() == true ? dialog.Value : null;
    }
}
