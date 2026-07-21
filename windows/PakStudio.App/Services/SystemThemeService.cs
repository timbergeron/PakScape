using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace PakStudio.App.Services;

internal sealed class SystemThemeService : IDisposable
{
    private const string PersonalizeKey =
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string AppsUseLightTheme = "AppsUseLightTheme";
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmWindowCornerRound = 2;

    private static readonly Uri LightColors = new("Themes/Colors.xaml", UriKind.Relative);
    private static readonly Uri DarkColors = new("Themes/DarkColors.xaml", UriKind.Relative);

    private readonly Application _application;
    private bool _isDarkMode;
    private bool _disposed;

    public SystemThemeService(Application application)
    {
        _application = application;
        _isDarkMode = ShouldUseDarkMode();

        ApplyApplicationTheme();
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(Window_OnLoaded));
        SystemEvents.UserPreferenceChanged += SystemEvents_OnUserPreferenceChanged;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        SystemEvents.UserPreferenceChanged -= SystemEvents_OnUserPreferenceChanged;
        _disposed = true;
    }

    private void SystemEvents_OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        _application.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposed)
            {
                return;
            }

            var isDarkMode = ShouldUseDarkMode();
            if (isDarkMode == _isDarkMode)
            {
                return;
            }

            _isDarkMode = isDarkMode;
            ApplyApplicationTheme();

            foreach (Window window in _application.Windows)
            {
                ApplyTitleBarTheme(window);
            }
        }));
    }

    private void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Window window)
        {
            ApplyTitleBarTheme(window);
        }
    }

    private void ApplyApplicationTheme()
    {
        var dictionaries = _application.Resources.MergedDictionaries;
        var colors = dictionaries.FirstOrDefault(dictionary =>
            dictionary.Source?.OriginalString is "Themes/Colors.xaml" or "Themes/DarkColors.xaml");

        if (colors is not null)
        {
            colors.Source = _isDarkMode ? DarkColors : LightColors;
        }

        // WPF's built-in control templates consume these system brushes. Overriding them
        // prevents text boxes, menus, list selections, and other controls from staying light.
        _application.Resources[SystemColors.WindowBrushKey] = ThemeBrush("PanelBackgroundBrush");
        _application.Resources[SystemColors.WindowTextBrushKey] = ThemeBrush("TextForegroundBrush");
        _application.Resources[SystemColors.ControlBrushKey] = ThemeBrush("PanelBackgroundBrush");
        _application.Resources[SystemColors.ControlTextBrushKey] = ThemeBrush("TextForegroundBrush");
        _application.Resources[SystemColors.MenuBrushKey] = ThemeBrush("PanelBackgroundBrush");
        _application.Resources[SystemColors.MenuTextBrushKey] = ThemeBrush("TextForegroundBrush");
        _application.Resources[SystemColors.HighlightBrushKey] = ThemeBrush("MenuHoverBrush");
        _application.Resources[SystemColors.HighlightTextBrushKey] = ThemeBrush("TextForegroundBrush");
        _application.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = ThemeBrush("MenuHoverBrush");
        _application.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = ThemeBrush("TextForegroundBrush");
        _application.Resources[SystemColors.GrayTextBrushKey] = ThemeBrush("MutedForegroundBrush");
        _application.Resources[SystemColors.ControlDarkBrushKey] = ThemeBrush("PanelBorderBrush");
    }

    private Brush ThemeBrush(string key) => (Brush)_application.FindResource(key);

    private void ApplyTitleBarTheme(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = _isDarkMode ? 1 : 0;
        if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
        }

        var cornerPreference = DwmWindowCornerRound;
        _ = DwmSetWindowAttribute(
            handle,
            DwmWindowCornerPreference,
            ref cornerPreference,
            sizeof(int));
    }

    private static bool ShouldUseDarkMode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return Registry.GetValue(PersonalizeKey, AppsUseLightTheme, 1) is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
