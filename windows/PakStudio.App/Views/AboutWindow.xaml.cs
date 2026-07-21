using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace PakStudio.App.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
    }

    public string VersionText => $"Version {GetDisplayVersion()}";

    private static string GetDisplayVersion()
    {
        var informationalVersion = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return (informationalVersion ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "Unknown")
            .Split('+')[0];
    }

    private void ProjectLink_OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            _ = Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true })
                ?? throw new InvalidOperationException("Windows could not open the project page.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var dialog = new MessageDialogWindow(
                "Unable to Open Link",
                exception.Message,
                MessageDialogButtons.Ok)
            {
                Owner = this,
            };
            _ = dialog.ShowDialogResult();
        }
    }
}
