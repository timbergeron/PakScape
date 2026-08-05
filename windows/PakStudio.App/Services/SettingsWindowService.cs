using System.Windows;
using PakStudio.App.Views;

namespace PakStudio.App.Services;

/// <summary>Keeps a single settings window alive, like the macOS Settings scene.</summary>
public static class SettingsWindowService
{
    private static SettingsWindow? _window;

    public static void Show()
    {
        if (_window is { IsLoaded: true })
        {
            if (_window.WindowState == WindowState.Minimized)
            {
                _window.WindowState = WindowState.Normal;
            }
            _window.Activate();
            return;
        }

        var owner = WindowOwnership.ActiveMainWindow();
        _window = new SettingsWindow();
        if (owner is { IsVisible: true })
        {
            _window.Owner = owner;
        }
        else
        {
            _window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        _window.Closed += (_, _) => _window = null;
        _window.Show();
    }
}
