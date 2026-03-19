namespace PakStudio.Core.Interfaces;

public interface IArchiveFormatRegistry
{
    IReadOnlyList<IArchiveFormatHandler> All { get; }

    IArchiveFormatHandler ResolveForOpen(string path);

    IArchiveFormatHandler ResolveForSave(string formatId);
}
