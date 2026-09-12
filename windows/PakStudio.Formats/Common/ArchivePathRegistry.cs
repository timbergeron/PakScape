using PakStudio.Core.Validation;

namespace PakStudio.Formats.Common;

internal sealed class ArchivePathRegistry(string archiveLabel)
{
    private readonly HashSet<string> _filePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _folderPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _explicitFolderPaths = new(StringComparer.OrdinalIgnoreCase);
    private string ArchiveLabel => archiveLabel;

    public void Register(string path, bool isDirectory)
    {
        var segments = path.Split('/');
        var prefix = string.Empty;
        foreach (var segment in segments.SkipLast(1))
        {
            prefix = prefix.Length == 0 ? segment : $"{prefix}/{segment}";
            if (_filePaths.Contains(prefix))
            {
                throw new ArchiveCorruptException(
                    $"{ArchiveLabel} entry '{path}' conflicts with an existing file path.");
            }
            _folderPaths.Add(prefix);
        }

        if (isDirectory)
        {
            if (_filePaths.Contains(path) || !_explicitFolderPaths.Add(path))
            {
                throw new ArchiveCorruptException($"The {ArchiveLabel} contains duplicate path '{path}'.");
            }
            _folderPaths.Add(path);
        }
        else
        {
            if (_folderPaths.Contains(path) || !_filePaths.Add(path))
            {
                throw new ArchiveCorruptException($"The {ArchiveLabel} contains duplicate path '{path}'.");
            }
        }

        ArchiveSafetyLimits.EnsureEntryCount(_filePaths.Count + _folderPaths.Count, $"The {ArchiveLabel} archive");
    }
}
