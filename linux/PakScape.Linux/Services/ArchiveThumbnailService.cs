using Avalonia.Media.Imaging;
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
        ".lmp", ".mdl", ".spr", ".pcx", ".tga", ".bsp", ".wad",
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
                    var preview = ArchivePreviewBuilder.Build(file, includeInteractiveModels: false);
                    if (PreviewImageFactory.TryCreate(preview, ThumbnailDimension, out var generated))
                    {
                        thumbnail = generated;
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

        var source = ThumbnailExtensions.Contains(file.Extension)
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
