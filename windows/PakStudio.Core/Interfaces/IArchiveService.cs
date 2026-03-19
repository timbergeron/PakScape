using PakStudio.Core.Documents;

namespace PakStudio.Core.Interfaces;

public interface IArchiveService
{
    Task<ArchiveDocument> OpenAsync(string path, CancellationToken cancellationToken = default);

    Task SaveAsync(ArchiveDocument document, string path, CancellationToken cancellationToken = default);
}
