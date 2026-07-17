using System.Globalization;
using PakStudio.Core.Nodes;

namespace PakScape.Linux.Models;

public sealed class ArchiveItemViewModel
{
    public ArchiveItemViewModel(ArchiveNode node)
    {
        Node = node;
    }

    public ArchiveNode Node { get; }

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

    public string ModifiedText => Node is ArchiveFileNode { ModifiedUtc: { } modified }
        ? modified.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
        : "—";

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
