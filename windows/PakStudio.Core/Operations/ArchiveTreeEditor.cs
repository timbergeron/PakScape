using PakStudio.Core.Nodes;
using PakStudio.Core.Validation;

namespace PakStudio.Core.Operations;

public static class ArchiveTreeEditor
{
    public static ArchiveFolderNode CreateFolder(
        ArchiveFolderNode parent,
        string suggestedName = "New Folder")
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArchiveNameValidator.ValidateNodeName(suggestedName);

        var name = GetAvailableName(parent, suggestedName, preserveExtension: false);
        var folder = new ArchiveFolderNode(name)
        {
            Parent = parent,
        };
        parent.Folders.Add(folder);
        return folder;
    }

    public static ArchiveFileNode AddFile(
        ArchiveFolderNode parent,
        string suggestedName,
        byte[] data,
        DateTime? modifiedUtc = null)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(data);
        ArchiveNameValidator.ValidateNodeName(suggestedName);

        var name = GetAvailableName(parent, suggestedName, preserveExtension: true);
        var file = new ArchiveFileNode(name, data.ToArray())
        {
            Parent = parent,
            ModifiedUtc = modifiedUtc,
        };
        parent.Files.Add(file);
        return file;
    }

    public static void Rename(ArchiveNode node, string newName)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArchiveNameValidator.ValidateNodeName(newName);

        var parent = node.Parent
            ?? throw new ArchiveValidationException("The archive root cannot be renamed.");

        if (string.Equals(node.Name, newName, StringComparison.Ordinal))
        {
            return;
        }

        if (parent.Children.Any(sibling =>
                !ReferenceEquals(sibling, node) &&
                string.Equals(sibling.Name, newName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArchivePathConflictException(
                $"An item named '{newName}' already exists in this folder.");
        }

        node.Name = newName;
    }

    public static void Remove(ArchiveNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var parent = node.Parent
            ?? throw new ArchiveValidationException("The archive root cannot be removed.");

        var removed = node switch
        {
            ArchiveFolderNode folder => parent.Folders.Remove(folder),
            ArchiveFileNode file => parent.Files.Remove(file),
            _ => false,
        };

        if (!removed)
        {
            throw new ArchiveValidationException("The item is no longer present in its parent folder.");
        }

        node.Parent = null;
    }

    public static IReadOnlyList<ArchiveNode> CreateSnapshot(IEnumerable<ArchiveNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        return NormalizeSelection(nodes).Select(node => CloneDetached(node, copyFileData: false)).ToList();
    }

    public static IReadOnlyList<ArchiveNode> CopyTo(
        IEnumerable<ArchiveNode> nodes,
        ArchiveFolderNode destination)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(destination);

        var sources = NormalizeSelection(nodes);
        EnsureCopyFits(sources, destination);

        var inserted = new List<ArchiveNode>(sources.Count);
        foreach (var source in sources)
        {
            var clone = CloneDetached(source, copyFileData: true);
            clone.Name = GetAvailableName(
                destination,
                clone.Name,
                preserveExtension: clone is ArchiveFileNode);
            Attach(destination, clone);
            inserted.Add(clone);
        }

        return inserted;
    }

    public static IReadOnlyList<ArchiveNode> MoveTo(
        IEnumerable<ArchiveNode> nodes,
        ArchiveFolderNode destination)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(destination);

        var sources = NormalizeSelection(nodes);
        if (sources.Any(source => source.Parent is null))
        {
            throw new ArchiveValidationException("The archive root cannot be moved.");
        }

        foreach (var folder in sources.OfType<ArchiveFolderNode>())
        {
            if (IsDescendantOrSelf(destination, folder))
            {
                throw new ArchiveValidationException(
                    $"'{folder.Name}' cannot be moved into itself or one of its subfolders.");
            }
        }

        EnsureDepthFits(sources, destination);
        if (sources.All(source => ReferenceEquals(source.Parent, destination)))
        {
            return sources;
        }

        foreach (var source in sources)
        {
            Remove(source);
        }

        foreach (var source in sources)
        {
            source.Name = GetAvailableName(
                destination,
                source.Name,
                preserveExtension: source is ArchiveFileNode);
            Attach(destination, source);
        }

        return sources;
    }

    public static string GetAvailableName(
        ArchiveFolderNode parent,
        string suggestedName,
        bool preserveExtension)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArchiveNameValidator.ValidateNodeName(suggestedName);

        if (!ContainsName(parent, suggestedName))
        {
            return suggestedName;
        }

        var extension = preserveExtension ? Path.GetExtension(suggestedName) : string.Empty;
        var stem = preserveExtension ? Path.GetFileNameWithoutExtension(suggestedName) : suggestedName;
        if (string.IsNullOrEmpty(stem))
        {
            stem = suggestedName;
            extension = string.Empty;
        }

        for (var suffix = 2; suffix < int.MaxValue; suffix++)
        {
            var candidate = $"{stem} ({suffix}){extension}";
            if (!ContainsName(parent, candidate))
            {
                return candidate;
            }
        }

        throw new ArchiveValidationException("Could not generate a unique archive name.");
    }

    private static bool ContainsName(ArchiveFolderNode parent, string name)
    {
        return parent.Children.Any(child =>
            string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static List<ArchiveNode> NormalizeSelection(IEnumerable<ArchiveNode> nodes)
    {
        var distinct = nodes.Distinct().ToList();
        return distinct
            .Where(node => !distinct.Any(candidate =>
                !ReferenceEquals(candidate, node) && IsDescendantOrSelf(node, candidate)))
            .ToList();
    }

    private static ArchiveNode CloneDetached(ArchiveNode node, bool copyFileData)
    {
        return node switch
        {
            ArchiveFileNode file => new ArchiveFileNode(
                file.Name,
                copyFileData ? file.Data.ToArray() : file.Data)
            {
                ModifiedUtc = file.ModifiedUtc,
            },
            ArchiveFolderNode folder => CloneFolder(folder, copyFileData),
            _ => throw new ArchiveValidationException("The selected archive item is unsupported."),
        };
    }

    private static ArchiveFolderNode CloneFolder(ArchiveFolderNode source, bool copyFileData)
    {
        var clone = new ArchiveFolderNode(source.Name);
        foreach (var folder in source.Folders)
        {
            Attach(clone, CloneFolder(folder, copyFileData));
        }
        foreach (var file in source.Files)
        {
            Attach(clone, CloneDetached(file, copyFileData));
        }
        return clone;
    }

    private static void Attach(ArchiveFolderNode destination, ArchiveNode node)
    {
        node.Parent = destination;
        switch (node)
        {
            case ArchiveFolderNode folder:
                destination.Folders.Add(folder);
                break;
            case ArchiveFileNode file:
                destination.Files.Add(file);
                break;
            default:
                throw new ArchiveValidationException("The selected archive item is unsupported.");
        }
    }

    private static void EnsureCopyFits(
        IReadOnlyCollection<ArchiveNode> sources,
        ArchiveFolderNode destination)
    {
        var root = destination;
        while (root.Parent is { } parent)
        {
            root = parent;
        }

        var entryCount = 0;
        long totalSize = 0;
        foreach (var child in root.Children)
        {
            AccumulateStatistics(child, ref entryCount, ref totalSize);
        }
        foreach (var source in sources)
        {
            AccumulateStatistics(source, ref entryCount, ref totalSize);
        }
        EnsureDepthFits(sources, destination);
    }

    private static void AccumulateStatistics(ArchiveNode node, ref int entryCount, ref long totalSize)
    {
        entryCount++;
        ArchiveSafetyLimits.EnsureEntryCount(entryCount, "The resulting archive");
        if (node is ArchiveFileNode file)
        {
            ArchiveSafetyLimits.EnsureFileSize(file.Size, $"'{file.Name}'");
            ArchiveSafetyLimits.EnsureTotalSize(totalSize, file.Size, "The resulting archive");
            totalSize += file.Size;
            return;
        }

        foreach (var child in ((ArchiveFolderNode)node).Children)
        {
            AccumulateStatistics(child, ref entryCount, ref totalSize);
        }
    }

    private static void EnsureDepthFits(
        IEnumerable<ArchiveNode> sources,
        ArchiveFolderNode destination)
    {
        var destinationDepth = 0;
        for (var current = destination; current.Parent is not null; current = current.Parent)
        {
            destinationDepth++;
        }

        foreach (var source in sources)
        {
            ArchiveSafetyLimits.EnsurePathDepth(
                destinationDepth + GetSubtreeDepth(source),
                $"'{source.Name}'");
        }
    }

    private static int GetSubtreeDepth(ArchiveNode node)
    {
        return node is ArchiveFolderNode folder && folder.Children.Any()
            ? 1 + folder.Children.Max(GetSubtreeDepth)
            : 1;
    }

    private static bool IsDescendantOrSelf(ArchiveNode node, ArchiveNode possibleAncestor)
    {
        for (ArchiveNode? current = node; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, possibleAncestor))
            {
                return true;
            }
        }
        return false;
    }
}
