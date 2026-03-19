using PakStudio.Core.Interfaces;
using PakStudio.Core.Validation;

namespace PakStudio.Formats.Pak;

public sealed class ArchiveFormatRegistry : IArchiveFormatRegistry
{
    public ArchiveFormatRegistry(IEnumerable<IArchiveFormatHandler> handlers)
    {
        All = handlers.ToList();
    }

    public IReadOnlyList<IArchiveFormatHandler> All { get; }

    public IArchiveFormatHandler ResolveForOpen(string path)
    {
        var handler = All.FirstOrDefault(candidate => candidate.CanOpen(path));
        return handler ?? throw new UnsupportedArchiveFormatException($"No archive handler is registered for '{path}'.");
    }

    public IArchiveFormatHandler ResolveForSave(string formatId)
    {
        var handler = All.FirstOrDefault(candidate =>
            string.Equals(candidate.FormatId, formatId, StringComparison.OrdinalIgnoreCase));

        return handler ?? throw new UnsupportedArchiveFormatException($"No archive handler is registered for format '{formatId}'.");
    }
}
