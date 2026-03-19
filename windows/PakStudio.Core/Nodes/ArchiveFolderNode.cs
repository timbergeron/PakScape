namespace PakStudio.Core.Nodes;

public sealed class ArchiveFolderNode : ArchiveNode
{
    public ArchiveFolderNode(string name) : base(name)
    {
    }

    public List<ArchiveFolderNode> Folders { get; } = [];

    public List<ArchiveFileNode> Files { get; } = [];

    public IEnumerable<ArchiveNode> Children => Folders.Cast<ArchiveNode>().Concat(Files);

    public static ArchiveFolderNode CreateRoot() => new(string.Empty);
}
