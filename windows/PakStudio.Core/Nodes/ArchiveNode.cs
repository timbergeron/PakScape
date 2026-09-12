using PakStudio.Core.Pathing;

namespace PakStudio.Core.Nodes;

public abstract class ArchiveNode
{
    private string _name = string.Empty;

    protected ArchiveNode(string name)
    {
        Name = name;
    }

    public string Name
    {
        get => _name;
        internal set => _name = value ?? string.Empty;
    }

    public ArchiveFolderNode? Parent { get; internal set; }

    public int Depth
    {
        get
        {
            var depth = 0;
            for (var parent = Parent; parent is not null; parent = parent.Parent)
            {
                depth++;
            }
            return depth;
        }
    }

    public string FullPath =>
        Parent is null
            ? "/"
            : PathHelper.CombineArchivePath(Parent.FullPath, Name);
}
