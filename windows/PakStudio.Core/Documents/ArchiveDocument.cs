using PakStudio.Core.Nodes;

namespace PakStudio.Core.Documents;

public sealed class ArchiveDocument
{
    public string? FilePath { get; set; }

    public string FormatId { get; set; } = "pak";

    public bool IsDirty { get; set; }

    public ArchiveFolderNode Root { get; } = ArchiveFolderNode.CreateRoot();

    public string DisplayName =>
        string.IsNullOrWhiteSpace(FilePath)
            ? "Untitled.pak"
            : Path.GetFileName(FilePath);
}
