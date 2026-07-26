using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Runtime.InteropServices;
using PakScape.Linux.Services;
using PakScape.Linux.Views;
using PakStudio.Core.Models;
using PakStudio.Core.Preview;

namespace PakScape.Linux.Controls;

/// <summary>
/// Interactive MDL, MD3, MD5, sprite, and BSP brush model preview: drag to orbit,
/// right-drag to pan, wheel to zoom, and a turntable that starts on its own once the
/// pane goes idle. Sprites skip the turntable and play their frames instead.
/// </summary>
public sealed class ModelPreviewControl : UserControl, IDisposable
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    private readonly Image _image;
    private readonly Image _skinImage;
    private readonly Border _hint;
    private readonly ComboBox _skinPicker;
    private readonly Button _viewSkinButton;
    private readonly DispatcherTimer _timer;
    private readonly ModelPreviewSession _session;
    private readonly string _modelName;

    private WriteableBitmap? _bitmap;
    private WriteableBitmap? _skinBitmap;
    private int _bufferWidth;
    private int _bufferHeight;
    private DateTime _lastFrame = DateTime.UtcNow;
    private bool _isOrbiting;
    private bool _isPanning;
    private bool _isViewingSkin;
    private Point _lastPosition;
    private bool _disposed;

    public ModelPreviewControl(ModelPreviewSession session, string modelName = "model")
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _modelName = modelName;

        _image = new Image
        {
            Stretch = Stretch.Uniform,
        };

        _skinImage = new Image
        {
            Stretch = Stretch.Uniform,
            IsVisible = false,
            IsHitTestVisible = false,
        };
        RenderOptions.SetBitmapInterpolationMode(_skinImage, BitmapInterpolationMode.None);

        _hint = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x99, 0x10, 0x12, 0x16)),
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(12, 6),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
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

        _viewSkinButton = new Button
        {
            Content = "View skin",
            Padding = new Thickness(10, 4),
            IsVisible = _session.SkinCount > 0,
        };
        ToolTip.SetTip(_viewSkinButton, "View the selected MDL skin");
        _viewSkinButton.Click += (_, _) => ToggleSkinView();

        var copySkinButton = new Button
        {
            Content = "Copy skin",
            Padding = new Thickness(10, 4),
            IsVisible = _session.SkinCount > 0,
        };
        ToolTip.SetTip(copySkinButton, "Copy the selected MDL skin image");
        copySkinButton.Click += async (_, _) => await CopySelectedSkinAsync();

        var skinControls = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
            Margin = new Thickness(12),
            Children = { _skinPicker, _viewSkinButton, copySkinButton },
        };

        var animateCheck = new CheckBox
        {
            Content = "Animate",
            IsChecked = true,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        animateCheck.IsCheckedChanged += (_, _) =>
            _session.AnimationEnabled = animateCheck.IsChecked == true;

        var animationSpeeds = new[] { 0.25, 0.5, 1.0, 2.0, 4.0 };
        var speedPicker = new ComboBox
        {
            MinWidth = 72,
            ItemsSource = new[] { "0.25×", "0.5×", "1×", "2×", "4×" },
            SelectedIndex = 2,
        };
        ToolTip.SetTip(speedPicker, "Animation speed");
        speedPicker.SelectionChanged += (_, _) =>
        {
            if (speedPicker.SelectedIndex >= 0)
            {
                _session.AnimationSpeed = animationSpeeds[speedPicker.SelectedIndex];
            }
        };
        var animationControls = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Margin = new Thickness(12),
            IsVisible = _session.Statistics.FrameCount > 1,
            Children = { animateCheck, speedPicker },
        };

        var resetButton = new Button
        {
            Content = "Reset view",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
            Margin = new Thickness(12),
            Padding = new Thickness(10, 4),
        };
        resetButton.Click += (_, _) => _session.Reset();

        Content = new Grid
        {
            Background = Brushes.Transparent,
            Children = { _image, _hint, _skinImage, skinControls, resetButton, animationControls },
        };
        ContextMenu = CreateSkinContextMenu();

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

        var wanted = _session.ShowInteractionPrompt && !_isViewingSkin;
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
            if (_isViewingSkin)
            {
                ShowSelectedSkin();
            }
        }
    }

    private void ToggleSkinView()
    {
        if (_isViewingSkin)
        {
            _isViewingSkin = false;
            _skinImage.IsVisible = false;
            _skinImage.Source = null;
            _image.Effect = null;
            _viewSkinButton.Content = "View skin";
            _skinBitmap?.Dispose();
            _skinBitmap = null;
            Focus();
            return;
        }

        if (ShowSelectedSkin())
        {
            _isViewingSkin = true;
            _viewSkinButton.Content = "View model";
        }
    }

    private bool ShowSelectedSkin()
    {
        var skin = _session.GetSelectedSkin();
        if (skin is null)
        {
            return false;
        }

        var previous = _skinBitmap;
        _skinBitmap = CreateSkinBitmap(skin);
        _skinImage.Source = _skinBitmap;
        _skinImage.IsVisible = true;
        _image.Effect ??= new BlurEffect { Radius = 14 };
        previous?.Dispose();
        return true;
    }

    private async Task CopySelectedSkinAsync()
    {
        var skin = _session.GetSelectedSkin();
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (skin is null || clipboard is null)
        {
            return;
        }

        var bitmap = CreateSkinBitmap(skin);
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create<Bitmap>(DataFormat.Bitmap, bitmap));
        try
        {
            await clipboard.SetDataAsync(transfer);
        }
        catch
        {
            ((IDataTransfer)transfer).Dispose();
        }
    }

    private ContextMenu? CreateSkinContextMenu()
    {
        if (_session.SkinCount == 0)
        {
            return null;
        }

        var formats = new[]
        {
            ("LMP…", "lmp"),
            ("JPEG…", "jpeg"),
            ("PNG…", "png"),
            ("TGA…", "tga"),
        };
        var saveAs = new MenuItem
        {
            Header = "Save Skin As",
            ItemsSource = formats.Select(entry =>
            {
                var item = new MenuItem { Header = entry.Item1 };
                item.Click += async (_, _) => await SaveSelectedSkinAsAsync(entry.Item2);
                return item;
            }).ToArray(),
        };
        return new ContextMenu { ItemsSource = new object[] { saveAs } };
    }

    private async Task SaveSelectedSkinAsAsync(string formatId)
    {
        var skin = _session.GetSelectedSkin();
        var topLevel = TopLevel.GetTopLevel(this);
        if (skin is null || topLevel is null ||
            !ImageFormatConverter.TryParseFormat(formatId, out var format))
        {
            return;
        }

        var extension = ImageFormatConverter.ExtensionFor(format);
        var baseName = Path.GetFileNameWithoutExtension(_modelName);
        var skinSuffix = _session.SkinCount > 1 ? $"_skin{_session.SkinIndex + 1}" : "_skin";
        var fileType = new FilePickerFileType($"{formatId.ToUpperInvariant()} image")
        {
            Patterns = [$"*{extension}"],
        };
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Skin As",
            SuggestedFileName = baseName + skinSuffix + extension,
            DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices = [fileType],
            SuggestedFileType = fileType,
            ShowOverwritePrompt = true,
        });
        var outputPath = file?.TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        try
        {
            var data = await Task.Run(() =>
                ImageFormatConverter.EncodeRgba(skin.Width, skin.Height, skin.RgbaPixels, format));
            await File.WriteAllBytesAsync(outputPath, data);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (topLevel is Window owner)
            {
                var dialog = new MessageDialogWindow(
                    "Save Skin As failed",
                    exception.Message,
                    MessageDialogButtons.Ok);
                _ = await dialog.ShowDialog<MessageDialogResult>(owner);
            }
        }
    }

    private static WriteableBitmap CreateSkinBitmap(ModelSkin skin)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(skin.Width, skin.Height),
            new Vector(96, 96),
            PixelFormats.Rgba8888,
            AlphaFormat.Unpremul);
        using (var framebuffer = bitmap.Lock())
        {
            for (var row = 0; row < skin.Height; row++)
            {
                Marshal.Copy(
                    skin.RgbaPixels,
                    row * skin.Width * 4,
                    framebuffer.Address + row * framebuffer.RowBytes,
                    skin.Width * 4);
            }
        }

        return bitmap;
    }

    /// <summary>The backdrop follows the app theme, read from its panel colour.</summary>
    private static bool IsDarkTheme()
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
        _skinImage.Source = null;
        _skinBitmap?.Dispose();
        _skinBitmap = null;
        _bitmap?.Dispose();
        _bitmap = null;
        _session.Dispose();
    }

    /// <summary>Opens a model preview, decoding PNG and JPEG skins with Avalonia.</summary>
    public static ModelPreviewControl Create(PreviewModel model, string modelName = "model") =>
        new(ModelPreviewSession.Create(model, ModelTextureDecoder.Decode), modelName);
}
