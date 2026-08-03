using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PakStudio.Core.Audio;
using PakStudio.Core.Models;
using PakStudio.Core.Nodes;
using PakStudio.Core.Preview;

namespace PakStudio.App.Services;

public sealed class ArchiveThumbnailService
{
    private const int ThumbnailDimension = 192;
    private const long MaximumThumbnailSourceSize = 32L * 1024 * 1024;

    private static readonly HashSet<string> ThumbnailExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff",
        ".lmp", ".mdl", ".spr", ".spr32", ".pcx", ".tga", ".bsp", ".wad",
    };

    private readonly ConditionalWeakTable<ArchiveNode, CacheEntry> _cache = new();
    private static readonly SemaphoreSlim GenerationSlots = new(initialCount: 2, maxCount: 2);

    public ImageSource? GetThumbnail(ArchiveNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return _cache.GetValue(node, CreateCacheEntry).Image;
    }

    public static bool CanCreateThumbnail(ArchiveNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return FindThumbnailSource(node) is not null;
    }

    private static CacheEntry CreateCacheEntry(ArchiveNode node)
    {
        if (FindThumbnailSource(node) is not { } file)
        {
            return new CacheEntry(null);
        }

        GenerationSlots.Wait();
        try
        {
            if (IsNativeModelThumbnail(file) &&
                CreateModelThumbnail(file) is { } modelImage)
            {
                return new CacheEntry(modelImage);
            }

            var preview = ArchivePreviewBuilder.Build(file, includeInteractiveModels: false);
            if (preview.Kind == ArchivePreviewKind.Text)
            {
                return new CacheEntry(CreateTextThumbnail(preview));
            }

            if (preview.Kind == ArchivePreviewKind.Audio)
            {
                return new CacheEntry(CreateAudioThumbnail(preview));
            }

            return PreviewImageFactory.TryCreate(preview, ThumbnailDimension, out var image)
                ? new CacheEntry(image)
                : new CacheEntry(null);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new CacheEntry(null);
        }
        finally
        {
            GenerationSlots.Release();
        }
    }

    private static bool IsNativeModelThumbnail(ArchiveFileNode file) =>
        file.Extension.Equals(".mdl", StringComparison.OrdinalIgnoreCase) ||
        file.Extension.Equals(".spr", StringComparison.OrdinalIgnoreCase) ||
        file.Extension.Equals(".spr32", StringComparison.OrdinalIgnoreCase) ||
        (file.Extension.Equals(".bsp", StringComparison.OrdinalIgnoreCase) &&
            NativeModelViewer.IsBspBrushModel(file.Data));

    private static ImageSource? CreateModelThumbnail(ArchiveFileNode file)
    {
        try
        {
            var model = new PreviewModel(
                file.Data,
                file.Extension,
                new ModelTextureResolver(file));
            using var session = ModelPreviewSession.Create(model, ModelTextureDecoder.Decode);
            session.DarkBackground = true;
            session.AutoRotate = false;
            session.AnimationEnabled = false;

            var bitmap = session.RenderBitmap(ThumbnailDimension, ThumbnailDimension);
            if (bitmap is null)
            {
                return null;
            }

            var preview = new ArchivePreview(
                file.Name,
                file.Extension.ToUpperInvariant().TrimStart('.') + " file",
                file.Size,
                ArchivePreviewKind.Bitmap,
                Bitmap: bitmap);
            return PreviewImageFactory.TryCreate(preview, ThumbnailDimension, out var image)
                ? image
                : null;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            /* Keep the flat preview when the native model backend cannot render. */
            return null;
        }
    }

    private static ArchiveFileNode? FindThumbnailSource(ArchiveNode node)
    {
        if (node is not ArchiveFileNode file)
        {
            return null;
        }

        var source = ThumbnailExtensions.Contains(file.Extension) ||
                     ArchivePreviewBuilder.SupportsTextExtension(file.Extension) ||
                     ArchivePreviewBuilder.SupportsAudioExtension(file.Extension)
            ? file
            : ModelTextureResolver.FindCompanionThumbnail(file);
        return source is { Size: <= MaximumThumbnailSourceSize } ? source : null;
    }

    private static ImageSource? CreateTextThumbnail(ArchivePreview preview)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return null;
        }

        return dispatcher.CheckAccess()
            ? RenderTextThumbnail(preview)
            : dispatcher.Invoke(() => RenderTextThumbnail(preview));
    }

    private static ImageSource RenderTextThumbnail(ArchivePreview preview)
    {
        const int dimension = ThumbnailDimension;
        var pixelsPerDip = Application.Current?.MainWindow is { } window
            ? VisualTreeHelper.GetDpi(window).PixelsPerDip
            : 1.0;
        var visual = new DrawingVisual();

        using (var drawing = visual.RenderOpen())
        {
            var outerBackground = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));
            var paper = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF2));
            var paperEdge = new Pen(new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC4)), 1);
            var header = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xDF));
            var code = new SolidColorBrush(Color.FromRgb(0x3B, 0x3B, 0x3B));
            var lineNumber = new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0x9A));

            drawing.DrawRoundedRectangle(
                outerBackground,
                null,
                new Rect(0, 0, dimension, dimension),
                10,
                10);

            var paperRect = new Rect(25, 14, 142, 164);
            drawing.DrawRoundedRectangle(paper, paperEdge, paperRect, 4, 4);
            drawing.DrawRoundedRectangle(
                header,
                null,
                new Rect(paperRect.X, paperRect.Y, paperRect.Width, 18),
                4,
                4);
            drawing.DrawRectangle(header, null, new Rect(paperRect.X, paperRect.Y + 10, paperRect.Width, 8));

            var title = new FormattedText(
                preview.Title,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                6.5,
                code,
                pixelsPerDip)
            {
                MaxTextWidth = paperRect.Width - 16,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            drawing.DrawText(title, new Point(paperRect.X + 8, paperRect.Y + 5));

            var text = (preview.Text ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            var lines = text.Split('\n').Take(14).ToArray();
            var typeface = new Typeface("Consolas");
            const double fontSize = 6.7;
            const double lineHeight = 8.7;
            var firstLineY = paperRect.Y + 27;

            drawing.PushClip(new RectangleGeometry(new Rect(
                paperRect.X + 7,
                firstLineY,
                paperRect.Width - 14,
                paperRect.Height - 34)));
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].Replace('\t', ' ');
                if (line.Length > 27)
                {
                    line = line[..27];
                }

                var number = new FormattedText(
                    (index + 1).ToString(CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    lineNumber,
                    pixelsPerDip);
                var content = new FormattedText(
                    line,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    fontSize,
                    code,
                    pixelsPerDip);
                var y = firstLineY + (index * lineHeight);
                drawing.DrawText(number, new Point(paperRect.X + 8, y));
                drawing.DrawText(content, new Point(paperRect.X + 22, y));
            }
            drawing.Pop();

            outerBackground.Freeze();
            paper.Freeze();
            paperEdge.Brush.Freeze();
            header.Freeze();
            code.Freeze();
            lineNumber.Freeze();
        }

        var bitmap = new RenderTargetBitmap(
            dimension,
            dimension,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static ImageSource? CreateAudioThumbnail(ArchivePreview preview)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return null;
        }

        return dispatcher.CheckAccess()
            ? RenderAudioThumbnail(preview)
            : dispatcher.Invoke(() => RenderAudioThumbnail(preview));
    }

    private static ImageSource RenderAudioThumbnail(ArchivePreview preview)
    {
        const int dimension = ThumbnailDimension;
        var pixelsPerDip = Application.Current?.MainWindow is { } window
            ? VisualTreeHelper.GetDpi(window).PixelsPerDip
            : 1.0;
        var visual = new DrawingVisual();

        using (var drawing = visual.RenderOpen())
        {
            var background = new SolidColorBrush(Color.FromRgb(0x20, 0x25, 0x2D));
            var panel = new SolidColorBrush(Color.FromRgb(0x27, 0x35, 0x46));
            var muted = new SolidColorBrush(Color.FromRgb(0xA8, 0xB8, 0xC7));
            var outline = new Pen(new SolidColorBrush(Color.FromRgb(0x3E, 0x57, 0x70)), 1);

            drawing.DrawRoundedRectangle(
                background,
                null,
                new Rect(0, 0, dimension, dimension),
                10,
                10);

            var center = new Point(96, 88);
            const double panelRadius = 70;
            drawing.DrawEllipse(panel, outline, center, panelRadius, panelRadius);

            var waveform = AudioWaveformBuilder.Build(preview.EncodedAudio ?? [], 48);
            const double innerRadius = 38;
            const double minimumBarLength = 10;
            const double maximumBarLength = 27;
            const double barWidth = 3;
            for (var index = 0; index < waveform.Count; index++)
            {
                var angle = -Math.PI / 2 + index * (Math.PI * 2 / waveform.Count);
                var outerRadius = innerRadius + minimumBarLength + waveform[index] * maximumBarLength;
                var start = new Point(
                    center.X + Math.Cos(angle) * innerRadius,
                    center.Y + Math.Sin(angle) * innerRadius);
                var end = new Point(
                    center.X + Math.Cos(angle) * outerRadius,
                    center.Y + Math.Sin(angle) * outerRadius);
                var gradientPosition = (Math.Cos(angle) + 1) / 2;
                var accent = new SolidColorBrush(InterpolateColor(
                    Color.FromRgb(0x9B, 0x3D, 0xFF),
                    Color.FromRgb(0x38, 0xB8, 0xF4),
                    gradientPosition));
                var bar = new Pen(accent, barWidth)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                };
                drawing.DrawLine(bar, start, end);
            }

            drawing.DrawEllipse(background, null, center, innerRadius - 4, innerRadius - 4);

            var title = new FormattedText(
                preview.Title,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                7,
                muted,
                pixelsPerDip)
            {
                MaxTextWidth = 144,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            drawing.DrawText(title, new Point(24, 164));

            background.Freeze();
            panel.Freeze();
            muted.Freeze();
            outline.Brush.Freeze();
        }

        var bitmap = new RenderTargetBitmap(
            dimension,
            dimension,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static Color InterpolateColor(Color first, Color second, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)(first.R + (second.R - first.R) * amount),
            (byte)(first.G + (second.G - first.G) * amount),
            (byte)(first.B + (second.B - first.B) * amount));
    }

    private sealed record CacheEntry(ImageSource? Image);
}
