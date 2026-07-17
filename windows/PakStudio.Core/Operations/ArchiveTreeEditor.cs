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
}
