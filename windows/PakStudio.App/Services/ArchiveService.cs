using PakStudio.Core.Documents;
using PakStudio.Core.Interfaces;

namespace PakStudio.App.Services;

public sealed class ArchiveService : IArchiveService
{
    private readonly IArchiveFormatRegistry _formatRegistry;

    public ArchiveService(IArchiveFormatRegistry formatRegistry)
    {
        _formatRegistry = formatRegistry;
    }

    public Task<ArchiveDocument> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        var handler = _formatRegistry.ResolveForOpen(path);
        return handler.OpenAsync(path, cancellationToken);
    }

    public Task SaveAsync(ArchiveDocument document, string path, CancellationToken cancellationToken = default)
    {
        var handler = _formatRegistry.ResolveForSave(document.FormatId);
        return handler.SaveAsync(document, path, cancellationToken);
    }
}
