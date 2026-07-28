using PakStudio.Core.Nodes;
using PakStudio.Core.Preview;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace PakStudio.App.ViewModels;

public sealed class ArchiveItemViewModel : ViewModelBase
{
    private readonly Func<ImageSource?>? _thumbnailFactory;
    private readonly ArchiveMetadata _metadata;
    private ImageSource? _thumbnail;
    private int _thumbnailLoadStarted;
    private bool _isRenaming;
    private string _editName = string.Empty;

    public ArchiveItemViewModel(
        ArchiveNode node,
        string iconGlyph,
        Func<ImageSource?>? thumbnailFactory,
        string? searchPath = null)
    {
        Node = node;
        IconGlyph = iconGlyph;
        _thumbnailFactory = thumbnailFactory;
        _metadata = ArchiveMetadataInspector.Inspect(node);
        SearchPath = searchPath;
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

    public bool IsRenaming
    {
        get => _isRenaming;
        private set => SetProperty(ref _isRenaming, value);
    }

    public string EditName
    {
        get => _editName;
        set => SetProperty(ref _editName, value);
    }

    public void BeginRenaming()
    {
        EditName = Name;
        IsRenaming = true;
    }

    public void EndRenaming()
    {
        IsRenaming = false;
    }

    public bool IsFolder => Node is ArchiveFolderNode;

    public string TypeText =>
        Node switch
        {
            ArchiveFolderNode => "Folder",
            ArchiveFileNode file when string.IsNullOrWhiteSpace(file.Extension) => "File",
            ArchiveFileNode file => $"{file.Extension.TrimStart('.').ToUpperInvariant()} File",
            _ => "Item",
        };

    public string? SearchPath { get; }

    public string PrimaryText => SearchPath ?? Name;

    public string SecondaryText => SearchPath ?? TypeText;

    public string DetailsText => _metadata.DetailsColumnText;

    public string SearchableMetadata => _metadata.SearchText;

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
