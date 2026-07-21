using PakStudio.Core.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace PakStudio.App.ViewModels;

public sealed class ArchiveItemViewModel : ViewModelBase
{
    private readonly Func<ImageSource?>? _thumbnailFactory;
    private ImageSource? _thumbnail;
    private int _thumbnailLoadStarted;

    public ArchiveItemViewModel(ArchiveNode node, string iconGlyph, Func<ImageSource?>? thumbnailFactory)
    {
        Node = node;
        IconGlyph = iconGlyph;
        _thumbnailFactory = thumbnailFactory;
    }

    public ArchiveNode Node { get; }

    public string IconGlyph { get; }

    public ImageSource? Thumbnail
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
        ImageSource? thumbnail;
        try
        {
            thumbnail = await Task.Run(_thumbnailFactory!).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            thumbnail = null;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            return;
        }

        try
        {
            await dispatcher.InvokeAsync(() =>
            {
                _thumbnail = thumbnail;
                OnPropertyChanged(nameof(Thumbnail));
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The window may have closed while thumbnail generation was in flight.
        }
    }

    public string Name => Node.Name;

    public bool IsFolder => Node is ArchiveFolderNode;

    public string TypeText =>
        Node switch
        {
            ArchiveFolderNode => "Folder",
            ArchiveFileNode file when string.IsNullOrWhiteSpace(file.Extension) => "File",
            ArchiveFileNode file => $"{file.Extension.TrimStart('.').ToUpperInvariant()} File",
            _ => "Item",
        };

    public long SizeBytes => Node is ArchiveFileNode file ? file.Size : 0;

    public string SizeText => IsFolder ? "--" : FormatSize(SizeBytes);

    public DateTime? ModifiedUtc => Node is ArchiveFileNode file ? file.ModifiedUtc : null;

    public string ModifiedText => ModifiedUtc?.ToLocalTime().ToString("g") ?? "--";

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
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
