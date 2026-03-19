using PakStudio.Core.Nodes;
using PakStudio.Core.Pathing;
using PakStudio.Core.Validation;

namespace PakStudio.Core.Operations;

public static class ArchiveTreeBuilder
{
    public static ArchiveFolderNode EnsureFolder(ArchiveFolderNode root, string? folderPath)
    {
        ArgumentNullException.ThrowIfNull(root);

        var segments = PathHelper.SplitArchivePath(folderPath);
        var current = root;

        foreach (var segment in segments)
        {
            ArchiveNameValidator.ValidateNodeName(segment);

            if (current.Files.Any(file => string.Equals(file.Name, segment, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArchivePathConflictException($"A file already exists at '{segment}'.");
            }

            var existing = current.Folders.FirstOrDefault(
                folder => string.Equals(folder.Name, segment, StringComparison.OrdinalIgnoreCase));

            if (existing is null)
            {
                existing = new ArchiveFolderNode(segment)
                {
                    Parent = current,
                };
                current.Folders.Add(existing);
            }

            current = existing;
        }

        return current;
    }

    public static ArchiveFileNode AddFile(
        ArchiveFolderNode root,
        string path,
        byte[] data,
        DateTime? modifiedUtc = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(data);

        var segments = PathHelper.SplitArchivePath(path);
        if (segments.Count == 0)
        {
            throw new ArchiveValidationException("File path cannot be empty.");
        }

        var fileName = segments[^1];
        ArchiveNameValidator.ValidateNodeName(fileName);

        var folderPath = string.Join('/', segments.Take(segments.Count - 1));
        var folder = EnsureFolder(root, folderPath);

        if (folder.Folders.Any(existing => string.Equals(existing.Name, fileName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArchivePathConflictException($"A folder already exists at '{path}'.");
        }

        if (folder.Files.Any(existing => string.Equals(existing.Name, fileName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArchivePathConflictException($"A file already exists at '{path}'.");
        }

        var file = new ArchiveFileNode(fileName, data.ToArray())
        {
            Parent = folder,
            ModifiedUtc = modifiedUtc,
        };

        folder.Files.Add(file);
        return file;
    }

    public static IReadOnlyList<ArchiveFileEntry> FlattenFiles(ArchiveFolderNode root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var files = new List<ArchiveFileEntry>();
        FlattenFiles(root, files);
        return files;
    }

    public static void SortRecursively(ArchiveFolderNode folder)
    {
        folder.Folders.Sort(static (left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
        folder.Files.Sort(static (left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));

        foreach (var child in folder.Folders)
        {
            SortRecursively(child);
        }
    }

    private static void FlattenFiles(ArchiveFolderNode folder, ICollection<ArchiveFileEntry> files)
    {
        foreach (var childFolder in folder.Folders)
        {
            FlattenFiles(childFolder, files);
        }

        foreach (var file in folder.Files)
        {
            files.Add(new ArchiveFileEntry(
                PathHelper.ToRelativeArchivePath(file.FullPath),
                file));
        }
    }
}
