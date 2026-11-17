using Avalonia.Controls;
using PakScape.Linux.Views;

namespace PakScape.Linux.Services;

/// <summary>Keeps a single settings window alive, like the macOS Settings scene.</summary>
public static class SettingsWindowService
{
    private static SettingsWindow? _window;

    public static void Show(Window? owner)
    {
        if (_window is { } existing)
        {
            existing.Activate();
            return;
        }

        var window = new SettingsWindow();
        window.Closed += (_, _) => _window = null;
        _window = window;
        if (owner is not null)
        {
            window.Show(owner);
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Show();
        }
    }
}
