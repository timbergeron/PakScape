using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PakScape.Linux.Controls;
using PakStudio.Core.Preview;

namespace PakScape.Linux.Views;

public sealed class SkyboxPreviewWindow : Window, IDisposable
{
    private readonly SkyboxPreviewControl _preview;
    private bool _isDisposed;

    public SkyboxPreviewWindow(SkyboxFaceSet faceSet)
    {
        _preview = new SkyboxPreviewControl(faceSet);
        Title = $"{faceSet.Name} — Skybox Preview";
        Width = 900;
        Height = 650;
        MinWidth = 520;
        MinHeight = 360;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var reset = new Button { Content = "Reset View" };
        reset.Click += (_, _) => _preview.ResetView();
        var hint = new TextBlock
        {
            Text = "Drag to look • Scroll to zoom",
            Opacity = 0.72,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var toolbar = new Grid
        {
            Margin = new Thickness(12, 10),
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { hint, reset },
        };
        Grid.SetColumn(reset, 1);
        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            Children =
            {
                toolbar,
                new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#181B20")),
                    Child = _preview,
                },
            },
        };
        Grid.SetRow(layout.Children[1], 1);
        Content = layout;
        Closed += (_, _) => Dispose();
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }
        _preview.Dispose();
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }
}
