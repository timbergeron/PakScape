using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PakStudio.App.Services;
using PakStudio.Core.Models;
using PakStudio.Core.Preview;

namespace PakStudio.App.Controls;

/// <summary>
/// Interactive MDL, MD3, MD5, sprite, and BSP brush model preview: drag to orbit,
/// right-drag to pan, wheel to zoom, and a turntable that starts on its own once the
/// pane goes idle. Sprites skip the turntable and play their frames instead.
/// </summary>
public sealed class ModelPreviewControl : UserControl, IDisposable
{
    private readonly Image _image;
    private readonly Border _hint;
    private readonly ComboBox _skinPicker;
    private readonly Button _resetButton;
    private readonly ModelPreviewSession _session;

    private WriteableBitmap? _bitmap;
    private int _bufferWidth;
    private int _bufferHeight;
    private TimeSpan _lastRenderTime;
    private bool _isHooked;
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
            SnapsToDevicePixels = true,
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);

        _hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x99, 0x10, 0x12, 0x16)),
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(12, 6, 12, 6),
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
            Visibility = _session.SkinCount > 1 ? Visibility.Visible : Visibility.Collapsed,
            ToolTip = "Skin",
        };
        for (var index = 0; index < _session.SkinCount; index++)
        {
            _skinPicker.Items.Add($"Skin {index + 1}");
        }
        if (_session.SkinCount > 0)
        {
            _skinPicker.SelectedIndex = 0;
        }
        _skinPicker.SelectionChanged += OnSkinChanged;

        _resetButton = new Button
        {
            Content = "Reset view",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(12),
            Padding = new Thickness(10, 4, 10, 4),
        };
        _resetButton.Click += (_, _) => _session.Reset();

        Content = new Grid
        {
            Background = Brushes.Transparent,
            Children = { _image, _hint, _skinPicker, _resetButton },
        };

        Focusable = true;
        IsManipulationEnabled = true;
        Cursor = Cursors.Hand;
        _session.DarkBackground = IsDarkTheme();

        Loaded += (_, _) => Hook();
        Unloaded += (_, _) => Unhook();
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

    /// <summary>The backdrop follows the app theme, read from its panel colour.</summary>
    private bool IsDarkTheme()
    {
        if (TryFindResource("PanelBackgroundBrush") is not SolidColorBrush brush)
        {
            return true;
        }

        var color = brush.Color;
        var luminance = (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
        return luminance < 0.5;
    }

    private void Hook()
    {
        if (_isHooked || _disposed)
        {
            return;
        }
        _isHooked = true;
        _lastRenderTime = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
        ResizeBuffer();
    }

    private void Unhook()
    {
        if (!_isHooked)
        {
            return;
        }
        _isHooked = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_disposed || _bitmap is null)
        {
            return;
        }

        var elapsed = 1.0 / 60.0;
        if (e is RenderingEventArgs rendering)
        {
            if (_lastRenderTime != TimeSpan.Zero)
            {
                elapsed = (rendering.RenderingTime - _lastRenderTime).TotalSeconds;
            }
            _lastRenderTime = rendering.RenderingTime;
        }

        if (_session.Advance(elapsed))
        {
            RenderFrame();
        }
        UpdateHint();
    }

    private void ResizeBuffer()
    {
        if (_disposed)
        {
            return;
        }

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (double.IsNaN(scale) || scale <= 0)
        {
            scale = 1.0;
        }

        var (width, height) = ModelPreviewSession.ClampRenderSize(
            ActualWidth * scale,
            ActualHeight * scale);
        if (width == _bufferWidth && height == _bufferHeight && _bitmap is not null)
        {
            return;
        }

        _bufferWidth = width;
        _bufferHeight = height;
        _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        _image.Source = _bitmap;
        RenderFrame();
    }

    private void RenderFrame()
    {
        if (_bitmap is null || _bufferWidth <= 0 || _bufferHeight <= 0)
        {
            return;
        }

        _bitmap.Lock();
        try
        {
            _session.Render(
                _bitmap.BackBuffer,
                _bufferWidth,
                _bufferHeight,
                _bitmap.BackBufferStride);
            _bitmap.AddDirtyRect(new Int32Rect(0, 0, _bufferWidth, _bufferHeight));
        }
        finally
        {
            _bitmap.Unlock();
        }
    }

    private void UpdateHint()
    {
        var wanted = _session.ShowInteractionPrompt ? Visibility.Visible : Visibility.Collapsed;
        if (_hint.Visibility != wanted)
        {
            _hint.Visibility = wanted;
        }
    }

    /// <summary>Input arrives in logical units; the camera works in buffer pixels.</summary>
    private double InputScale =>
        ActualWidth > 0 ? _bufferWidth / ActualWidth : 1.0;

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            _session.Reset();
            e.Handled = true;
            return;
        }

        var pan = e.ChangedButton is MouseButton.Right or MouseButton.Middle ||
                  Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right or MouseButton.Middle))
        {
            return;
        }

        _isOrbiting = !pan;
        _isPanning = pan;
        _lastPosition = e.GetPosition(this);
        _session.BeginInteraction();
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
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

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_isOrbiting && !_isPanning)
        {
            return;
        }

        _isOrbiting = false;
        _isPanning = false;
        _session.EndInteraction();
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        _session.Zoom(e.Delta / 120.0);
        e.Handled = true;
    }

    protected override void OnManipulationStarting(ManipulationStartingEventArgs e)
    {
        base.OnManipulationStarting(e);
        e.ManipulationContainer = this;
        e.Mode = ManipulationModes.Scale | ManipulationModes.Translate;
        _session.BeginInteraction();
    }

    protected override void OnManipulationDelta(ManipulationDeltaEventArgs e)
    {
        base.OnManipulationDelta(e);

        var scale = e.DeltaManipulation.Scale.X;
        if (scale > 0 && Math.Abs(scale - 1.0) > 0.0001)
        {
            _session.Zoom(Math.Log(scale) * 4.0);
        }

        var translation = e.DeltaManipulation.Translation;
        if (e.Manipulators.Count() > 1)
        {
            _session.Pan(translation.X * InputScale, translation.Y * InputScale);
        }
        else
        {
            _session.Orbit(translation.X * InputScale, translation.Y * InputScale);
        }
        e.Handled = true;
    }

    protected override void OnManipulationCompleted(ManipulationCompletedEventArgs e)
    {
        base.OnManipulationCompleted(e);
        _session.EndInteraction();
    }

    private void OnSkinChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_skinPicker.SelectedIndex >= 0)
        {
            _session.TrySelectSkin(_skinPicker.SelectedIndex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unhook();
        _session.Dispose();
    }

    /// <summary>Opens a model preview, decoding PNG and JPEG skins with WPF.</summary>
    public static ModelPreviewControl Create(PreviewModel model) =>
        new(ModelPreviewSession.Create(model, ModelTextureDecoder.Decode));
}
