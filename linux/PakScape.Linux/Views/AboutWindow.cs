using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace PakScape.Linux.Views;

public sealed class AboutWindow : Window, IDisposable
{
    private const string ProjectUrl = "https://github.com/timbergeron/PakScape";
    private readonly Bitmap _iconBitmap;
    private bool _isDisposed;

    public AboutWindow()
    {
        Title = "About PakScape";
        Width = 440;
        Height = 360;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        using (var iconStream = AssetLoader.Open(new Uri("avares://PakScape/Assets/pakscape.png")))
        {
            Icon = new WindowIcon(iconStream);
        }

        using (var imageStream = AssetLoader.Open(new Uri("avares://PakScape/Assets/pakscape.png")))
        {
            _iconBitmap = new Bitmap(imageStream);
        }

        var projectButton = new Button
        {
            Content = "github.com/timbergeron/PakScape",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        projectButton.Classes.Add("link-button");
        projectButton.Click += OpenProjectPage;

        Content = new Grid
        {
            Margin = new Thickness(28, 24),
            Children =
            {
                new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Spacing = 12,
                    Children =
                    {
                        new Border
                        {
                            Width = 120,
                            Height = 120,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            CornerRadius = new CornerRadius(18),
                            ClipToBounds = true,
                            Child = new Image
                            {
                                Source = _iconBitmap,
                                Stretch = Stretch.Uniform,
                            },
                        },
                        new TextBlock
                        {
                            Text = "PakScape",
                            HorizontalAlignment = HorizontalAlignment.Center,
                            FontSize = 20,
                            FontWeight = FontWeight.SemiBold,
                        },
                        new TextBlock
                        {
                            Text = "Simple Quake .pak & .pk3 explorer inspired by PakScape and originally developed by Peter Engström.",
                            MaxWidth = 360,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            TextAlignment = TextAlignment.Center,
                            TextWrapping = TextWrapping.Wrap,
                            Opacity = 0.72,
                        },
                        new TextBlock
                        {
                            Text = $"Version {GetDisplayVersion()}",
                            HorizontalAlignment = HorizontalAlignment.Center,
                            FontSize = 12,
                            Opacity = 0.72,
                        },
                        projectButton,
                    },
                },
            },
        };

        Closed += (_, _) => Dispose();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _iconBitmap.Dispose();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    private static string GetDisplayVersion()
    {
        var informationalVersion = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return (informationalVersion ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "Unknown")
            .Split('+')[0];
    }

    private async void OpenProjectPage(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            _ = Process.Start(new ProcessStartInfo(ProjectUrl) { UseShellExecute = true })
                ?? throw new InvalidOperationException("The desktop could not open the project page.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            var dialog = new MessageDialogWindow(
                "Unable to open link",
                exception.Message,
                MessageDialogButtons.Ok);
            await dialog.ShowDialog<MessageDialogResult>(this);
        }
    }
}
