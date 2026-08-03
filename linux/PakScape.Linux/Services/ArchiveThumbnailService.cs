using Avalonia.Media.Imaging;
using PakStudio.Core.Audio;
using PakStudio.Core.Models;
using PakStudio.Core.Nodes;
using PakStudio.Core.Preview;

namespace PakScape.Linux.Services;

public sealed class ArchiveThumbnailService : IDisposable
{
    private const int ThumbnailDimension = 192;
    private const long MaximumThumbnailSourceSize = 32L * 1024 * 1024;
    private static readonly HashSet<string> ThumbnailExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff",
        ".lmp", ".mdl", ".spr", ".spr32", ".pcx", ".tga", ".bsp", ".wad",
    };
    private static readonly SemaphoreSlim GenerationSlots = new(initialCount: 2, maxCount: 2);
    private readonly object _sync = new();
    private readonly Dictionary<ArchiveNode, Bitmap?> _cache = [];
    private int _generation;
    private bool _disposed;

    public Bitmap? GetThumbnail(ArchiveNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        int generation;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cache.TryGetValue(node, out var cached))
            {
                return cached;
            }
            generation = _generation;
        }

        Bitmap? thumbnail = null;
        GenerationSlots.Wait();
        try
        {
            if (FindThumbnailSource(node) is { } file)
            {
                try
                {
                    thumbnail = IsNativeModelThumbnail(file)
                        ? CreateModelThumbnail(file)
                        : null;
                    if (thumbnail is null)
                    {
                        var preview = ArchivePreviewBuilder.Build(file, includeInteractiveModels: false);
                        if (preview.Kind == ArchivePreviewKind.Audio)
                        {
                            thumbnail = AudioThumbnailRenderer.Create(preview, ThumbnailDimension);
                        }
                        else if (PreviewImageFactory.TryCreate(preview, ThumbnailDimension, out var generated))
                        {
                            thumbnail = generated;
                        }
                    }
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    thumbnail = null;
                }
            }
        }
        finally
        {
            GenerationSlots.Release();
        }

        lock (_sync)
        {
            if (_disposed || generation != _generation)
            {
                thumbnail?.Dispose();
                return null;
            }

            if (_cache.TryGetValue(node, out var cached))
            {
                thumbnail?.Dispose();
                return cached;
            }

            _cache[node] = thumbnail;
            return thumbnail;
        }
    }

    private static bool IsNativeModelThumbnail(ArchiveFileNode file) =>
        file.Extension.Equals(".mdl", StringComparison.OrdinalIgnoreCase) ||
        file.Extension.Equals(".spr", StringComparison.OrdinalIgnoreCase) ||
        file.Extension.Equals(".spr32", StringComparison.OrdinalIgnoreCase) ||
        (file.Extension.Equals(".bsp", StringComparison.OrdinalIgnoreCase) &&
            NativeModelViewer.IsBspBrushModel(file.Data));

    private static Bitmap? CreateModelThumbnail(ArchiveFileNode file)
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

    public static bool CanCreateThumbnail(ArchiveNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return FindThumbnailSource(node) is not null;
    }

    private static ArchiveFileNode? FindThumbnailSource(ArchiveNode node)
    {
        if (node is not ArchiveFileNode file)
        {
            return null;
        }

        var source = ThumbnailExtensions.Contains(file.Extension) ||
                     ArchivePreviewBuilder.SupportsAudioExtension(file.Extension)
            ? file
            : ModelTextureResolver.FindCompanionThumbnail(file);
        return source is { Size: <= MaximumThumbnailSourceSize } ? source : null;
    }

    public void Reset()
    {
        List<Bitmap> bitmaps;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _generation++;
            bitmaps = _cache.Values.OfType<Bitmap>().ToList();
            _cache.Clear();
        }
        foreach (var bitmap in bitmaps)
        {
            bitmap.Dispose();
        }
    }

    public void Dispose()
    {
        List<Bitmap> bitmaps;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _generation++;
            bitmaps = _cache.Values.OfType<Bitmap>().ToList();
            _cache.Clear();
        }
        foreach (var bitmap in bitmaps)
        {
            bitmap.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
