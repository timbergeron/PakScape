using PakStudio.Core.Documents;
using PakStudio.Core.Interfaces;

namespace PakScape.Linux.Services;

public sealed class ArchiveService(IArchiveFormatRegistry formatRegistry) : IArchiveService
{
    public Task<ArchiveDocument> OpenAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        return formatRegistry.ResolveForOpen(path).OpenAsync(path, cancellationToken);
    }

    public Task SaveAsync(
        ArchiveDocument document,
        string path,
        CancellationToken cancellationToken = default)
    {
        var handler = formatRegistry.All.FirstOrDefault(candidate => candidate.CanOpen(path))
            ?? formatRegistry.ResolveForSave(document.FormatId);
        return handler.SaveAsync(document, path, cancellationToken);
    }
}
