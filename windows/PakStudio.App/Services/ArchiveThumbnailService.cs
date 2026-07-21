using System.Runtime.CompilerServices;
using System.Windows.Media;
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
        ".lmp", ".mdl", ".spr", ".pcx", ".tga", ".bsp", ".wad",
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
        return node is ArchiveFileNode file &&
               file.Size <= MaximumThumbnailSourceSize &&
               ThumbnailExtensions.Contains(file.Extension);
    }

    private static CacheEntry CreateCacheEntry(ArchiveNode node)
    {
        if (node is not ArchiveFileNode file ||
            file.Size > MaximumThumbnailSourceSize ||
            !ThumbnailExtensions.Contains(file.Extension))
        {
            return new CacheEntry(null);
        }

        GenerationSlots.Wait();
        try
        {
            var preview = ArchivePreviewBuilder.Build(file);
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

    private sealed record CacheEntry(ImageSource? Image);
}
