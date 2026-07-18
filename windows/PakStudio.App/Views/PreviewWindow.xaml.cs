using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PakStudio.Core.Nodes;
using PakStudio.Core.Preview;

namespace PakStudio.App.Views;

public partial class PreviewWindow : Window
{
    private readonly IReadOnlyList<ArchiveNode> _nodes;
    private int _index;

    public PreviewWindow(IReadOnlyList<ArchiveNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArchivePreviewBuilder.ValidateSelection(nodes);
        if (nodes.Count == 0)
        {
            throw new ArchivePreviewException("Select an item to preview.");
        }

        InitializeComponent();
        _nodes = nodes;
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
        TitleText.Text = preview.Title;
        SubtitleText.Text = $"{preview.TypeDescription} • {ArchivePreviewBuilder.FormatSize(preview.Size)}";
        PositionText.Text = _nodes.Count == 1 ? "1 of 1" : $"{_index + 1:N0} of {_nodes.Count:N0}";
        PreviousButton.IsEnabled = _nodes.Count > 1;
        NextButton.IsEnabled = _nodes.Count > 1;

        switch (preview.Kind)
        {
            case ArchivePreviewKind.Text:
                TextPreview.Text = preview.Text ?? string.Empty;
                TextPreview.Visibility = Visibility.Visible;
                if (!string.IsNullOrWhiteSpace(preview.Message))
                {
                    SubtitleText.Text += $" • {preview.Message}";
                }
                break;
            case ArchivePreviewKind.EncodedImage:
                if (TryLoadEncodedImage(
                        preview.EncodedImage,
                        preview.ImageWidth,
                        preview.ImageHeight,
                        out var encodedImage))
                {
                    ImagePreview.Source = encodedImage;
                    ImagePanel.Visibility = Visibility.Visible;
                }
                else
                {
                    ShowMetadata(preview with { Message = "The native image decoder could not read this file." });
                }
                break;
            case ArchivePreviewKind.Bitmap when preview.Bitmap is { } bitmap:
                ImagePreview.Source = BitmapSource.Create(
                    bitmap.Width,
                    bitmap.Height,
                    96,
                    96,
                    PixelFormats.Bgra32,
                    null,
                    bitmap.BgraPixels,
                    bitmap.Stride);
                ImagePanel.Visibility = Visibility.Visible;
                break;
            default:
                ShowMetadata(preview);
                break;
        }
    }

    private void ShowMetadata(ArchivePreview preview)
    {
        MetadataTypeText.Text = preview.TypeDescription;
        MetadataSizeText.Text = ArchivePreviewBuilder.FormatSize(preview.Size);
        MetadataMessageText.Text = preview.Message ?? string.Empty;
        MetadataPanel.Visibility = Visibility.Visible;
    }

    private void ResetContent()
    {
        ImagePreview.Source = null;
        TextPreview.Text = string.Empty;
        ImagePanel.Visibility = Visibility.Collapsed;
        TextPreview.Visibility = Visibility.Collapsed;
        MetadataPanel.Visibility = Visibility.Collapsed;
    }

    private static bool TryLoadEncodedImage(
        byte[]? data,
        int width,
        int height,
        out ImageSource image)
    {
        image = null!;
        if (data is null)
        {
            return false;
        }

        try
        {
            using var stream = new MemoryStream(data, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            if (width >= height)
            {
                bitmap.DecodePixelWidth = Math.Min(width, EncodedImageInspector.MaximumRenderedDimension);
            }
            else
            {
                bitmap.DecodePixelHeight = Math.Min(height, EncodedImageInspector.MaximumRenderedDimension);
            }
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            image = bitmap;
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }

    private void Move(int delta)
    {
        _index = (_index + delta + _nodes.Count) % _nodes.Count;
        ShowCurrentPreview();
    }

    private void Window_OnKeyDown(object sender, KeyEventArgs e)
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

    private void PreviousButton_OnClick(object sender, RoutedEventArgs e) => Move(-1);

    private void NextButton_OnClick(object sender, RoutedEventArgs e) => Move(1);

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();
}
