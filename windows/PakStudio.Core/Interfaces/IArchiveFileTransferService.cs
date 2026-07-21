using PakStudio.Core.Nodes;

namespace PakStudio.Core.Interfaces;

public interface IArchiveFileTransferService
{
    ArchiveFileNode ImportFile(ArchiveFolderNode destination, string sourcePath);

    ArchiveFolderNode ImportDirectory(ArchiveFolderNode destination, string sourcePath);

    string Export(ArchiveNode node, string destinationDirectory);

    IReadOnlyList<string> ExportToTemporaryLocation(IReadOnlyList<ArchiveNode> nodes);

    void ReleaseTemporaryLocation(IReadOnlyList<string> paths);

    void OpenWithDefaultApplication(ArchiveFileNode file);
}
