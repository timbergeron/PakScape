using PakStudio.Core.Documents;

namespace PakStudio.Core.Interfaces;

public interface IArchiveFormatHandler
{
    string FormatId { get; }

    string DisplayName { get; }

    IReadOnlyList<string> Extensions { get; }

    bool CanOpen(string path);

    Task<ArchiveDocument> OpenAsync(string path, CancellationToken cancellationToken = default);

    Task SaveAsync(ArchiveDocument document, string path, CancellationToken cancellationToken = default);
}
