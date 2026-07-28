using System.Windows.Media;
using PakStudio.App.Services;
using PakStudio.Core.Nodes;
using PakStudio.Core.Preview;

namespace PakStudio.App.ViewModels;

public sealed class ItemInfoViewModel
{
    public ItemInfoViewModel(
        ArchiveNode node,
        string archiveName,
        string iconGlyph,
        ArchiveThumbnailService thumbnailService)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveName);
        ArgumentNullException.ThrowIfNull(thumbnailService);

        Node = node;
        Name = node.Name.Length == 0 ? archiveName : node.Name;
        Type = GetTypeText(node);
        ArchiveName = archiveName;
        Path = node.FullPath;
        IconGlyph = iconGlyph;
        Thumbnail = TryGetThumbnail(node, thumbnailService);
        FormatDetails = ArchiveMetadataInspector.Inspect(node).Details;
        Size = FormatExactSize(0);

        if (node is ArchiveFileNode file)
        {
            Size = FormatExactSize(file.Size);
            Modified = file.ModifiedUtc?.ToLocalTime().ToString("g");
        }
        else if (node is ArchiveFolderNode folder)
        {
            var summary = SummarizeFolder(folder);
            Size = FormatExactSize(summary.Bytes);
            Contents =
                $"{summary.Files:N0} {(summary.Files == 1 ? "file" : "files")}, " +
                $"{summary.Folders:N0} {(summary.Folders == 1 ? "folder" : "folders")}";
        }
    }

    public ArchiveNode Node { get; }

    public string Name { get; }

    public string Type { get; }

    public string ArchiveName { get; }

    public string Path { get; }

    public string Size { get; }

    public string? Contents { get; }

    public bool HasContents => !string.IsNullOrWhiteSpace(Contents);

    public string? Modified { get; }

    public bool HasModified => !string.IsNullOrWhiteSpace(Modified);

    public string IconGlyph { get; }

    public ImageSource? Thumbnail { get; }

    public IReadOnlyList<ArchiveMetadataDetail> FormatDetails { get; }

    public bool HasFormatDetails => FormatDetails.Count > 0;

    private static string GetTypeText(ArchiveNode node) => node switch
    {
        ArchiveFolderNode => "Folder",
        ArchiveFileNode file when string.IsNullOrWhiteSpace(file.Extension) => "File",
        ArchiveFileNode file => $"{file.Extension.TrimStart('.').ToUpperInvariant()} File",
        _ => "Item",
    };

    private static string FormatExactSize(long bytes) =>
        $"{ArchivePreviewBuilder.FormatSize(bytes)} ({bytes:N0} bytes)";

    private static ImageSource? TryGetThumbnail(
        ArchiveNode node,
        ArchiveThumbnailService thumbnailService)
    {
        if (!ArchiveThumbnailService.CanCreateThumbnail(node))
        {
            return null;
        }

        try
        {
            return thumbnailService.GetThumbnail(node);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static (long Bytes, int Files, int Folders) SummarizeFolder(
        ArchiveFolderNode folder)
    {
        long bytes = 0;
        var files = 0;
        var folders = 0;
        var pending = new Stack<ArchiveFolderNode>();
        pending.Push(folder);

        while (pending.TryPop(out var current))
        {
            foreach (var file in current.Files)
            {
                bytes = bytes > long.MaxValue - file.Size ? long.MaxValue : bytes + file.Size;
                files = files == int.MaxValue ? int.MaxValue : files + 1;
            }
            foreach (var child in current.Folders)
            {
                folders = folders == int.MaxValue ? int.MaxValue : folders + 1;
                pending.Push(child);
            }
        }

        return (bytes, files, folders);
    }
}
