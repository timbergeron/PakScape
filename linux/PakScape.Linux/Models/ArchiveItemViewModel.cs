using System.Globalization;
using System.Threading;
using Avalonia.Threading;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using PakStudio.Core.Nodes;

namespace PakScape.Linux.Models;

public sealed class ArchiveItemViewModel : ObservableObject
{
    private readonly Func<Bitmap?>? _thumbnailFactory;
    private Bitmap? _thumbnail;
    private int _thumbnailLoadStarted;

    public ArchiveItemViewModel(ArchiveNode node, Func<Bitmap?>? thumbnailFactory)
    {
        Node = node;
        _thumbnailFactory = thumbnailFactory;
    }

    public ArchiveNode Node { get; }

    public Bitmap? Thumbnail
    {
        get
        {
            if (_thumbnailFactory is not null &&
                Interlocked.Exchange(ref _thumbnailLoadStarted, 1) == 0)
            {
                _ = LoadThumbnailAsync();
            }
            return _thumbnail;
        }
    }

    private async Task LoadThumbnailAsync()
    {
        Bitmap? thumbnail;
        try
        {
            thumbnail = await Task.Run(_thumbnailFactory!);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            thumbnail = null;
        }

        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _thumbnail = thumbnail;
                OnPropertyChanged(nameof(Thumbnail));
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The desktop dispatcher may be shutting down while generation is in flight.
        }
    }

    public string Icon => Node switch
    {
        ArchiveFolderNode => "📁",
        ArchiveFileNode file when file.Extension.Equals(".bsp", StringComparison.OrdinalIgnoreCase) => "🗺",
        ArchiveFileNode file when file.Extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) => "🔊",
        ArchiveFileNode file when IsImageExtension(file.Extension) => "🖼",
        _ => "📄",
    };

    public string Name => Node.Name;

    public bool IsFolder => Node is ArchiveFolderNode;

    public string TypeText => Node switch
    {
        ArchiveFolderNode => "Folder",
        ArchiveFileNode file when string.IsNullOrWhiteSpace(file.Extension) => "File",
        ArchiveFileNode file => $"{file.Extension.TrimStart('.').ToUpperInvariant()} file",
        _ => "Item",
    };

    public string SizeText => Node is ArchiveFileNode file ? FormatSize(file.Size) : "—";

    public long SizeBytes => Node is ArchiveFileNode file ? file.Size : 0;

    public string ModifiedText => Node is ArchiveFileNode { ModifiedUtc: { } modified }
        ? modified.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : "—";

    public DateTime ModifiedSortValue => Node is ArchiveFileNode file
        ? file.ModifiedUtc ?? DateTime.MinValue
        : DateTime.MinValue;

    private static bool IsImageExtension(string extension)
    {
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".pcx", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tga", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".lmp", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        double value = bytes;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.#} {units[unitIndex]}";
    }
}
