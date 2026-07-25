using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using PakScape.Linux.Services;
using PakStudio.Core.Models;
using PakStudio.Core.Preview;

namespace PakScape.Linux.Controls;

/// <summary>
/// Interactive MDL, MD3, and MD5 preview: drag to orbit, right-drag to pan, wheel
/// to zoom, and a turntable that starts on its own once the pane goes idle.
/// </summary>
public sealed class ModelPreviewControl : UserControl, IDisposable
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    private readonly Image _image;
    private readonly Border _hint;
    private readonly ComboBox _skinPicker;
    private readonly DispatcherTimer _timer;
    private readonly ModelPreviewSession _session;

    private WriteableBitmap? _bitmap;
    private int _bufferWidth;
    private int _bufferHeight;
    private DateTime _lastFrame = DateTime.UtcNow;
    private bool _isOrbiting;
    private bool _isPanning;
    private Point _lastPosition;
    private bool _disposed;

    public ModelPreviewControl(ModelPreviewSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));

        _image = new Image
        {
            Stretch = Stretch.Uniform,
        };

        _hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x99, 0x10, 0x12, 0x16)),
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(12, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 18),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = "Drag to orbit • Scroll to zoom • R to reset",
                Foreground = Brushes.White,
                FontSize = 12,
            },
        };

        _skinPicker = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(12),
            MinWidth = 96,
            IsVisible = _session.SkinCount > 1,
        };
        ToolTip.SetTip(_skinPicker, "Skin");
        var skins = new List<string>();
        for (var index = 0; index < _session.SkinCount; index++)
        {
            skins.Add($"Skin {index + 1}");
        }
        _skinPicker.ItemsSource = skins;
        if (skins.Count > 0)
        {
            _skinPicker.SelectedIndex = 0;
        }
        _skinPicker.SelectionChanged += OnSkinChanged;

        var resetButton = new Button
        {
            Content = "Reset view",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(12),
            Padding = new Thickness(10, 4),
        };
        resetButton.Click += (_, _) => _session.Reset();

        Content = new Grid
        {
            Background = Brushes.Transparent,
            Children = { _image, _hint, _skinPicker, resetButton },
        };

        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        _session.DarkBackground = IsDarkTheme();

        _timer = new DispatcherTimer { Interval = FrameInterval };
        _timer.Tick += OnFrame;

        AttachedToVisualTree += (_, _) => Start();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
        SizeChanged += (_, _) => ResizeBuffer();
    }

    public string StatusLine => _session.StatusLine;

    /// <summary>
    /// The preview window forwards keys here so that arrow keys steer the camera
    /// instead of stepping to the next file.
    /// </summary>
    public bool HandleKey(Key key)
    {
        switch (key)
        {
            case Key.Left:
                _session.Nudge(ModelNudge.Left);
                return true;
            case Key.Right:
                _session.Nudge(ModelNudge.Right);
                return true;
            case Key.Up:
                _session.Nudge(ModelNudge.Up);
                return true;
            case Key.Down:
                _session.Nudge(ModelNudge.Down);
                return true;
            case Key.OemPlus:
            case Key.Add:
                _session.Nudge(ModelNudge.In);
                return true;
            case Key.OemMinus:
            case Key.Subtract:
                _session.Nudge(ModelNudge.Out);
                return true;
            case Key.R:
            case Key.Home:
                _session.Reset();
                return true;
            default:
                return false;
        }
    }

    private void Start()
    {
        if (_disposed)
        {
            return;
        }
        _lastFrame = DateTime.UtcNow;
        ResizeBuffer();
        _timer.Start();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (_disposed || _bitmap is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var elapsed = (now - _lastFrame).TotalSeconds;
        _lastFrame = now;

        if (_session.Advance(elapsed))
        {
            RenderFrame();
        }

        var wanted = _session.ShowInteractionPrompt;
        if (_hint.IsVisible != wanted)
        {
            _hint.IsVisible = wanted;
        }
    }

    private void ResizeBuffer()
    {
        if (_disposed)
        {
            return;
        }

        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        if (double.IsNaN(scale) || scale <= 0)
        {
            scale = 1.0;
        }

        var (width, height) = ModelPreviewSession.ClampRenderSize(
            Bounds.Width * scale,
            Bounds.Height * scale);
        if (width == _bufferWidth && height == _bufferHeight && _bitmap is not null)
        {
            return;
        }

        var previous = _bitmap;
        _bufferWidth = width;
        _bufferHeight = height;
        _bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormats.Bgra8888,
            AlphaFormat.Opaque);
        _image.Source = _bitmap;
        previous?.Dispose();
        RenderFrame();
    }

    private void RenderFrame()
    {
        if (_bitmap is null || _bufferWidth <= 0 || _bufferHeight <= 0)
        {
            return;
        }

        using (var framebuffer = _bitmap.Lock())
        {
            _session.Render(
                framebuffer.Address,
                _bufferWidth,
                _bufferHeight,
                framebuffer.RowBytes);
        }
        _image.InvalidateVisual();
    }

    /// <summary>Input arrives in logical units; the camera works in buffer pixels.</summary>
    private double InputScale => Bounds.Width > 0 ? _bufferWidth / Bounds.Width : 1.0;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        if (e.ClickCount == 2)
        {
            _session.Reset();
            e.Handled = true;
            return;
        }

        var point = e.GetCurrentPoint(this);
        var pan = point.Properties.IsRightButtonPressed ||
                  point.Properties.IsMiddleButtonPressed ||
                  e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        _isOrbiting = !pan;
        _isPanning = pan;
        _lastPosition = point.Position;
        _session.BeginInteraction();
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_isOrbiting && !_isPanning)
        {
            return;
        }

        var position = e.GetPosition(this);
        var dx = (position.X - _lastPosition.X) * InputScale;
        var dy = (position.Y - _lastPosition.Y) * InputScale;
        _lastPosition = position;

        if (_isPanning)
        {
            _session.Pan(dx, dy);
        }
        else
        {
            _session.Orbit(dx, dy);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_isOrbiting && !_isPanning)
        {
            return;
        }

        _isOrbiting = false;
        _isPanning = false;
        _session.EndInteraction();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _session.Zoom(e.Delta.Y);
        e.Handled = true;
    }

    private void OnSkinChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_skinPicker.SelectedIndex >= 0)
        {
            _session.TrySelectSkin(_skinPicker.SelectedIndex);
        }
    }

    /// <summary>The backdrop follows the app theme, read from its panel colour.</summary>
    private bool IsDarkTheme()
    {
        if (Application.Current?.ActualThemeVariant is { } variant)
        {
            return variant != Avalonia.Styling.ThemeVariant.Light;
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnFrame;
        _image.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        _session.Dispose();
    }

    /// <summary>Opens a model preview, decoding PNG and JPEG skins with Avalonia.</summary>
    public static ModelPreviewControl Create(PreviewModel model) =>
        new(ModelPreviewSession.Create(model, ModelTextureDecoder.Decode));
}
