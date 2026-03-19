using PakStudio.Core.Nodes;

namespace PakStudio.App.ViewModels;

public sealed class ArchiveItemViewModel : ViewModelBase
{
    public ArchiveItemViewModel(ArchiveNode node, string iconGlyph)
    {
        Node = node;
        IconGlyph = iconGlyph;
    }

    public ArchiveNode Node { get; }

    public string IconGlyph { get; }

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
