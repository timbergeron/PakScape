using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PakStudio.Core.Nodes;
using PakStudio.Core.Preview;

namespace PakScape.Linux.Views;

public sealed class PreviewWindow : Window
{
    private readonly IReadOnlyList<ArchiveNode> _nodes;
    private readonly TextBlock _titleText;
    private readonly TextBlock _subtitleText;
    private readonly Border _imagePanel;
    private readonly Image _imagePreview;
    private readonly TextBox _textPreview;
    private readonly Grid _metadataPanel;
    private readonly TextBlock _metadataTypeText;
    private readonly TextBlock _metadataSizeText;
    private readonly TextBlock _metadataMessageText;
    private readonly TextBlock _positionText;
    private readonly Button _previousButton;
    private readonly Button _nextButton;
    private Bitmap? _currentImage;
    private int _index;

    public PreviewWindow(IReadOnlyList<ArchiveNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArchivePreviewBuilder.ValidateSelection(nodes);
        if (nodes.Count == 0)
        {
            throw new ArchivePreviewException("Select an item to preview.");
        }

        _nodes = nodes;
        Title = "Quick Preview";
        Width = 860;
        Height = 640;
        MinWidth = 520;
        MinHeight = 360;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _titleText = new TextBlock
        {
            FontSize = 19,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _subtitleText = new TextBlock
        {
            Margin = new Thickness(0, 3, 0, 0),
            Opacity = 0.68,
        };

        var closeButton = new Button
        {
            Content = "✕",
            Width = 36,
            Height = 32,
            Margin = new Thickness(12, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        closeButton.Click += (_, _) => Close();

        var header = new Grid
        {
            Margin = new Thickness(4, 0, 4, 12),
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
        };
        header.Children.Add(new StackPanel
        {
            Children = { _titleText, _subtitleText },
        });
        Grid.SetColumn(closeButton, 1);
        header.Children.Add(closeButton);

        _imagePreview = new Image
        {
            Margin = new Thickness(16),
            Stretch = Stretch.Uniform,
        };
        _imagePanel = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#181B20")),
            Child = _imagePreview,
            IsVisible = false,
        };

        _textPreview = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            FontFamily = new FontFamily("monospace"),
            FontSize = 13,
            TextWrapping = TextWrapping.NoWrap,
            Padding = new Thickness(14),
            IsVisible = false,
        };
        _textPreview.SetValue(
            ScrollViewer.HorizontalScrollBarVisibilityProperty,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        _textPreview.SetValue(
            ScrollViewer.VerticalScrollBarVisibilityProperty,
            Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        _metadataTypeText = new TextBlock
        {
            Margin = new Thickness(0, 18, 0, 0),
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _metadataSizeText = new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            Opacity = 0.68,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _metadataMessageText = new TextBlock
        {
            Margin = new Thickness(0, 18, 0, 0),
            MaxWidth = 520,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.68,
        };
        _metadataPanel = new Grid
        {
            Margin = new Thickness(30),
            IsVisible = false,
            Children =
            {
                new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "📄",
                            FontSize = 72,
                            HorizontalAlignment = HorizontalAlignment.Center,
                        },
                        _metadataTypeText,
                        _metadataSizeText,
                        _metadataMessageText,
                    },
                },
            },
        };

        var contentGrid = new Grid
        {
            Children = { _imagePanel, _textPreview, _metadataPanel },
        };
        var contentBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#40808080")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            ClipToBounds = true,
            Child = contentGrid,
        };

        _previousButton = new Button
        {
            Content = "←",
            Width = 36,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        _previousButton.Click += (_, _) => Move(-1);
        _nextButton = new Button
        {
            Content = "→",
            Width = 36,
            Margin = new Thickness(6, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        _nextButton.Click += (_, _) => Move(1);
        _positionText = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.68,
        };

        var navigation = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _previousButton, _nextButton },
        };
        var closeHint = new TextBlock
        {
            Text = "Space or Esc to close",
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.68,
        };
        var footer = new Grid
        {
            Margin = new Thickness(4, 12, 4, 0),
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
            },
            Children = { navigation, _positionText, closeHint },
        };
        Grid.SetColumn(_positionText, 1);
        Grid.SetColumn(closeHint, 2);

        var root = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
                new RowDefinition(GridLength.Auto),
            },
            Children = { header, contentBorder, footer },
        };
        Grid.SetRow(contentBorder, 1);
        Grid.SetRow(footer, 2);
        Content = root;

        AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Closed += (_, _) => DisposeCurrentImage();
        ShowCurrentPreview();
    }

    private void ShowCurrentPreview()
    {
        ResetContent();
        ArchivePreview preview;
        try
        {
            preview = ArchivePreviewBuilder.Build(_nodes[_index]);
        }
        catch (Exception exception)
        {
            preview = new ArchivePreview(
                _nodes[_index].Name,
                "Preview unavailable",
                0,
                ArchivePreviewKind.Metadata,
                Message: exception.Message);
        }

        Title = $"{preview.Title} — Quick Preview";
        _titleText.Text = preview.Title;
        _subtitleText.Text = $"{preview.TypeDescription} • {ArchivePreviewBuilder.FormatSize(preview.Size)}";
        _positionText.Text = _nodes.Count == 1 ? "1 of 1" : $"{_index + 1:N0} of {_nodes.Count:N0}";
        _previousButton.IsEnabled = _nodes.Count > 1;
        _nextButton.IsEnabled = _nodes.Count > 1;

        switch (preview.Kind)
        {
            case ArchivePreviewKind.Text:
                _textPreview.Text = preview.Text ?? string.Empty;
                _textPreview.IsVisible = true;
                if (!string.IsNullOrWhiteSpace(preview.Message))
                {
                    _subtitleText.Text += $" • {preview.Message}";
                }
                break;
            case ArchivePreviewKind.EncodedImage:
                if (TryLoadEncodedImage(
                        preview.EncodedImage,
                        preview.ImageWidth,
                        preview.ImageHeight,
                        out var encodedImage))
                {
                    SetImage(encodedImage);
                }
                else
                {
                    ShowMetadata(preview with { Message = "The native image decoder could not read this file." });
                }
                break;
            case ArchivePreviewKind.Bitmap when preview.Bitmap is { } bitmap:
                SetImage(CreateBitmap(bitmap));
                break;
            default:
                ShowMetadata(preview);
                break;
        }
    }

    private void ShowMetadata(ArchivePreview preview)
    {
        _metadataTypeText.Text = preview.TypeDescription;
        _metadataSizeText.Text = ArchivePreviewBuilder.FormatSize(preview.Size);
        _metadataMessageText.Text = preview.Message ?? string.Empty;
        _metadataPanel.IsVisible = true;
    }

    private void ResetContent()
    {
        DisposeCurrentImage();
        _textPreview.Text = string.Empty;
        _imagePanel.IsVisible = false;
        _textPreview.IsVisible = false;
        _metadataPanel.IsVisible = false;
    }

    private void SetImage(Bitmap bitmap)
    {
        _currentImage = bitmap;
        _imagePreview.Source = bitmap;
        _imagePanel.IsVisible = true;
    }

    private void DisposeCurrentImage()
    {
        _imagePreview.Source = null;
        _currentImage?.Dispose();
        _currentImage = null;
    }

    private static bool TryLoadEncodedImage(byte[]? data, int width, int height, out Bitmap bitmap)
    {
        bitmap = null!;
        if (data is null)
        {
            return false;
        }
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            if (width >= height)
            {
                bitmap = Bitmap.DecodeToWidth(
                    stream,
                    Math.Min(width, EncodedImageInspector.MaximumRenderedDimension),
                    BitmapInterpolationMode.HighQuality);
            }
            else
            {
                bitmap = Bitmap.DecodeToHeight(
                    stream,
                    Math.Min(height, EncodedImageInspector.MaximumRenderedDimension),
                    BitmapInterpolationMode.HighQuality);
            }
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static WriteableBitmap CreateBitmap(PreviewBitmap preview)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(preview.Width, preview.Height),
            new Vector(96, 96),
            PixelFormats.Bgra8888,
            AlphaFormat.Unpremul);
        using var framebuffer = bitmap.Lock();
        for (var row = 0; row < preview.Height; row++)
        {
            Marshal.Copy(
                preview.BgraPixels,
                row * preview.Stride,
                IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes),
                preview.Stride);
        }
        return bitmap;
    }

    private void Move(int delta)
    {
        _index = (_index + delta + _nodes.Count) % _nodes.Count;
        ShowCurrentPreview();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Left when _nodes.Count > 1:
            case Key.Up when _nodes.Count > 1:
                Move(-1);
                e.Handled = true;
                break;
            case Key.Right when _nodes.Count > 1:
            case Key.Down when _nodes.Count > 1:
                Move(1);
                e.Handled = true;
                break;
        }
    }
}
