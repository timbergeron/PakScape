using PakStudio.Core.Nodes;
using PakStudio.Core.Operations;
using PakStudio.Core.Validation;
using Xunit;

namespace PakStudio.Tests;

public sealed class ArchiveTreeEditorTests
{
    [Fact]
    public void ReplacementAtTheEntryLimitCountsOnlyTheResultingTree()
    {
        var root = ArchiveFolderNode.CreateRoot();
        var existing = ArchiveTreeEditor.AddFile(root, "original.txt", [1]);
        for (var index = 1; index < ArchiveSafetyLimits.MaximumEntryCount; index++)
        {
            root.Files.Add(new ArchiveFileNode($"file{index}", []));
        }

        var replacement = Assert.IsType<ArchiveFileNode>(
            ArchiveTreeEditor.ReplaceWith(existing, new ArchiveFileNode("incoming.txt", [2, 3])));

        Assert.Equal("original.txt", replacement.Name);
        Assert.Equal(new byte[] { 2, 3 }, replacement.Data);
        Assert.Same(root, replacement.Parent);
        Assert.Null(existing.Parent);
        Assert.Equal(ArchiveSafetyLimits.MaximumEntryCount, root.Files.Count);
    }

    [Fact]
    public void ReplacementRejectsExcessiveNetEntriesWithoutRemovingTheOriginal()
    {
        var root = ArchiveFolderNode.CreateRoot();
        var existing = ArchiveTreeEditor.AddFile(root, "original.txt", [1]);
        for (var index = 1; index < ArchiveSafetyLimits.MaximumEntryCount; index++)
        {
            root.Files.Add(new ArchiveFileNode($"file{index}", []));
        }
        var incoming = new ArchiveFolderNode("incoming");
        incoming.Files.Add(new ArchiveFileNode("file", [2]));

        Assert.Throws<ArchiveValidationException>(() => ArchiveTreeEditor.ReplaceWith(existing, incoming));
        Assert.Contains(existing, root.Files);
        Assert.Same(root, existing.Parent);
        Assert.Empty(root.Folders);
    }

    [Fact]
    public void FolderReplacementReleasesTheWholeOldSubtreeBudget()
    {
        var root = ArchiveFolderNode.CreateRoot();
        var existing = ArchiveTreeEditor.CreateFolder(root, "maps");
        for (var index = 1; index < ArchiveSafetyLimits.MaximumEntryCount; index++)
        {
            existing.Files.Add(new ArchiveFileNode($"file{index}", []));
        }
        var staged = ArchiveFolderNode.CreateRoot();
        var incoming = ArchiveTreeEditor.CreateFolder(staged, "incoming");
        ArchiveTreeEditor.AddFile(incoming, "new.bsp", [2]);

        var replacement = Assert.IsType<ArchiveFolderNode>(ArchiveTreeEditor.ReplaceWith(existing, incoming));

        Assert.Same(replacement, Assert.Single(root.Folders));
        Assert.Equal("maps", replacement.Name);
        Assert.Equal("new.bsp", Assert.Single(replacement.Files).Name);
        Assert.Null(existing.Parent);
        Assert.Same(staged, incoming.Parent);
    }

    [Fact]
    public void ReplacementChecksDestinationDepthBeforeRemovingTheOriginal()
    {
        var destination = ArchiveFolderNode.CreateRoot();
        for (var depth = 1; depth < ArchiveSafetyLimits.MaximumPathDepth; depth++)
        {
            destination = ArchiveTreeEditor.CreateFolder(destination, "a");
        }
        var existing = ArchiveTreeEditor.AddFile(destination, "original", [1]);
        var incoming = new ArchiveFolderNode("incoming");
        incoming.Files.Add(new ArchiveFileNode("too-deep", [2]));

        Assert.Throws<ArchiveValidationException>(() => ArchiveTreeEditor.ReplaceWith(existing, incoming));
        Assert.Same(existing, Assert.Single(destination.Files));
        Assert.Empty(destination.Folders);
    }

    [Fact]
    public void AdditionsRejectTheEntryLimitWithoutChangingTheTree()
    {
        var root = CreateFullArchive();

        Assert.Throws<ArchiveValidationException>(() => ArchiveTreeEditor.CreateFolder(root));
        Assert.Throws<ArchiveValidationException>(() => ArchiveTreeEditor.AddFile(root, "extra", [1]));
        Assert.Equal(ArchiveSafetyLimits.MaximumEntryCount, root.Files.Count);
        Assert.Empty(root.Folders);
    }

    [Fact]
    public void MoveBetweenArchivesChecksTheDestinationEntryLimitBeforeDetaching()
    {
        var destination = CreateFullArchive();
        var source = ArchiveFolderNode.CreateRoot();
        var file = ArchiveTreeEditor.AddFile(source, "extra", [1]);

        Assert.Throws<ArchiveValidationException>(() => ArchiveTreeEditor.MoveTo([file], destination));
        Assert.Same(file, Assert.Single(source.Files));
        Assert.Same(source, file.Parent);
        Assert.Equal(ArchiveSafetyLimits.MaximumEntryCount, destination.Files.Count);
    }

    [Fact]
    public void MoveRejectsAStaleSelectionBeforeRemovingOtherItems()
    {
        var root = ArchiveFolderNode.CreateRoot();
        var destination = ArchiveTreeEditor.CreateFolder(root, "destination");
        var valid = ArchiveTreeEditor.AddFile(root, "valid.txt", [1]);
        var stale = ArchiveTreeEditor.AddFile(root, "stale.txt", [2]);
        root.Files.Remove(stale);

        Assert.Throws<ArchiveValidationException>(() => ArchiveTreeEditor.MoveTo([valid, stale], destination));
        Assert.Same(valid, Assert.Single(root.Files));
        Assert.Same(root, valid.Parent);
        Assert.Empty(destination.Children);
    }

    private static ArchiveFolderNode CreateFullArchive()
    {
        var root = ArchiveFolderNode.CreateRoot();
        // Populate directly to avoid repeatedly scanning the whole fixture.
        for (var index = 0; index < ArchiveSafetyLimits.MaximumEntryCount; index++)
        {
            root.Files.Add(new ArchiveFileNode($"file{index}", []));
        }
        return root;
    }

    [Fact]
    public void AdditionsRejectExcessiveDestinationDepthWithoutChangingTheTree()
    {
        var destination = ArchiveFolderNode.CreateRoot();
        for (var depth = 0; depth < ArchiveSafetyLimits.MaximumPathDepth; depth++)
        {
            destination = ArchiveTreeEditor.CreateFolder(destination, "a");
        }

        Assert.Throws<ArchiveValidationException>(() => ArchiveTreeEditor.CreateFolder(destination));
        Assert.Throws<ArchiveValidationException>(() => ArchiveTreeEditor.AddFile(destination, "x", [1]));
        Assert.Empty(destination.Children);
    }

    [Fact]
    public void AddFileAllowsTheMaximumPathDepth()
    {
        var destination = ArchiveFolderNode.CreateRoot();
        for (var depth = 1; depth < ArchiveSafetyLimits.MaximumPathDepth; depth++)
        {
            destination = ArchiveTreeEditor.CreateFolder(destination, "a");
        }

        var file = ArchiveTreeEditor.AddFile(destination, "x", [1]);

        Assert.Same(file, Assert.Single(destination.Files));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MoveToPreservesItemsAlreadyAtTheDestination(bool incomingFirst)
    {
        var root = ArchiveFolderNode.CreateRoot();
        var source = ArchiveTreeEditor.CreateFolder(root, "source");
        var destination = ArchiveTreeEditor.CreateFolder(root, "destination");
        var incoming = ArchiveTreeEditor.AddFile(source, "readme.txt", [1]);
        var resident = ArchiveTreeEditor.AddFile(destination, "readme.txt", [2]);

        ArchiveTreeEditor.MoveTo(
            incomingFirst ? [incoming, resident] : [resident, incoming], destination);

        Assert.Equal("readme.txt", resident.Name);
        Assert.Equal("readme (2).txt", incoming.Name);
        Assert.Same(destination, incoming.Parent);
        Assert.Same(destination, resident.Parent);
        Assert.Empty(source.Files);
    }

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

    [Fact]
    public void RestoreFolderSnapshot_RevertsStructureWithoutDuplicatingPayloads()
    {
        var root = ArchiveFolderNode.CreateRoot();
        var maps = ArchiveTreeEditor.CreateFolder(root, "maps");
        var start = ArchiveTreeEditor.AddFile(maps, "start.bsp", [1, 2, 3]);
        var snapshot = ArchiveTreeEditor.CreateFolderSnapshot(root);

        ArchiveTreeEditor.Rename(start, "changed.bsp");
        ArchiveTreeEditor.CreateFolder(root, "extra");
        ArchiveTreeEditor.RestoreFolderSnapshot(root, snapshot);

        var restoredMaps = Assert.Single(root.Folders);
        Assert.Equal("maps", restoredMaps.Name);
        Assert.Same(root, restoredMaps.Parent);
        var restoredStart = Assert.Single(restoredMaps.Files);
        Assert.Equal("start.bsp", restoredStart.Name);
        Assert.Same(restoredMaps, restoredStart.Parent);
        Assert.Same(start.Data, restoredStart.Data);
    }
}
