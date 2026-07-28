using System.Windows;
using PakStudio.App.Views;

namespace PakStudio.App.Services;

internal static class WindowOwnership
{
    public static MainWindow? ActiveMainWindow()
    {
        if (Application.Current is not { } application)
        {
            return null;
        }

        return application.Windows
                   .OfType<MainWindow>()
                   .FirstOrDefault(window => window.IsActive)
               ?? application.Windows
                   .OfType<MainWindow>()
                   .FirstOrDefault(window => window.IsVisible);
    }
}
