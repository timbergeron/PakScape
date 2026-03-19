namespace PakStudio.Core.Nodes;

public sealed class ArchiveFileNode : ArchiveNode
{
    public ArchiveFileNode(string name, byte[] data) : base(name)
    {
        Data = data;
    }

    public byte[] Data { get; set; }

    public long Size => Data.LongLength;

    public DateTime? ModifiedUtc { get; set; }

    public string Extension => Path.GetExtension(Name);
}
