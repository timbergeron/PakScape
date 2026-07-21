using PakStudio.Core.Nodes;
using PakStudio.Core.Operations;
using PakStudio.Core.Validation;
using Xunit;

namespace PakStudio.Tests;

public sealed class ArchiveTreeEditorTests
{
    [Fact]
    public void AddFile_GeneratesCaseInsensitiveUniqueName()
    {
        var root = ArchiveFolderNode.CreateRoot();

        ArchiveTreeEditor.AddFile(root, "readme.txt", [1]);
        var duplicate = ArchiveTreeEditor.AddFile(root, "README.txt", [2]);

        Assert.Equal("README (2).txt", duplicate.Name);
        Assert.Equal(2, root.Files.Count);
        Assert.All(root.Files, file => Assert.Same(root, file.Parent));
    }

    [Fact]
    public void CreateFolder_GeneratesUniqueNameAcrossFilesAndFolders()
    {
        var root = ArchiveFolderNode.CreateRoot();
        ArchiveTreeEditor.AddFile(root, "New Folder", [1]);

        var folder = ArchiveTreeEditor.CreateFolder(root);

        Assert.Equal("New Folder (2)", folder.Name);
        Assert.Same(root, folder.Parent);
    }

    [Fact]
    public void Rename_RejectsSiblingConflictWithoutChangingNode()
    {
        var root = ArchiveFolderNode.CreateRoot();
        var first = ArchiveTreeEditor.AddFile(root, "first.txt", [1]);
        ArchiveTreeEditor.AddFile(root, "second.txt", [2]);

        Assert.Throws<ArchivePathConflictException>(() =>
            ArchiveTreeEditor.Rename(first, "SECOND.TXT"));
        Assert.Equal("first.txt", first.Name);
    }

    [Fact]
    public void Remove_DetachesNodeFromParent()
    {
        var root = ArchiveFolderNode.CreateRoot();
        var folder = ArchiveTreeEditor.CreateFolder(root, "maps");

        ArchiveTreeEditor.Remove(folder);

        Assert.Empty(root.Folders);
        Assert.Null(folder.Parent);
    }

    [Fact]
    public void Rename_RejectsArchiveRoot()
    {
        var root = ArchiveFolderNode.CreateRoot();

        Assert.Throws<ArchiveValidationException>(() =>
            ArchiveTreeEditor.Rename(root, "renamed"));
    }

    [Fact]
    public void CopyTo_DeepCopiesFoldersAndGeneratesUniqueNames()
    {
        var root = ArchiveFolderNode.CreateRoot();
        var source = ArchiveTreeEditor.CreateFolder(root, "maps");
        ArchiveTreeEditor.AddFile(source, "start.bsp", [1, 2, 3]);

        var copy = Assert.IsType<ArchiveFolderNode>(Assert.Single(ArchiveTreeEditor.CopyTo([source], root)));

        Assert.Equal("maps (2)", copy.Name);
        var copiedFile = Assert.Single(copy.Files);
        Assert.Equal(new byte[] { 1, 2, 3 }, copiedFile.Data);
        Assert.NotSame(source.Files[0].Data, copiedFile.Data);
        Assert.Same(copy, copiedFile.Parent);
    }

    [Fact]
    public void MoveTo_RejectsMovingFolderIntoItsDescendant()
    {
        var root = ArchiveFolderNode.CreateRoot();
        var parent = ArchiveTreeEditor.CreateFolder(root, "parent");
        var child = ArchiveTreeEditor.CreateFolder(parent, "child");

        Assert.Throws<ArchiveValidationException>(() =>
            ArchiveTreeEditor.MoveTo([parent], child));
        Assert.Same(root, parent.Parent);
        Assert.Same(parent, child.Parent);
    }

    [Fact]
    public void MoveTo_ReparentsItemsAndResolvesConflicts()
    {
        var root = ArchiveFolderNode.CreateRoot();
        var source = ArchiveTreeEditor.CreateFolder(root, "source");
        var destination = ArchiveTreeEditor.CreateFolder(root, "destination");
        var moved = ArchiveTreeEditor.AddFile(source, "readme.txt", [1]);
        ArchiveTreeEditor.AddFile(destination, "readme.txt", [2]);

        ArchiveTreeEditor.MoveTo([moved], destination);

        Assert.Empty(source.Files);
        Assert.Equal("readme (2).txt", moved.Name);
        Assert.Same(destination, moved.Parent);
    }
}
