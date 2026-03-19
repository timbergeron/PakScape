using PakStudio.Core.Nodes;
using PakStudio.Core.Operations;
using PakStudio.Core.Validation;
using Xunit;

namespace PakStudio.Tests;

public sealed class ArchiveTreeBuilderTests
{
    [Fact]
    public void AddFile_CreatesNestedFolders()
    {
        var root = ArchiveFolderNode.CreateRoot();

        ArchiveTreeBuilder.AddFile(root, "maps/start.bsp", [1, 2, 3]);

        var maps = Assert.Single(root.Folders);
        Assert.Equal("maps", maps.Name);

        var start = Assert.Single(maps.Files);
        Assert.Equal("start.bsp", start.Name);
        Assert.Equal(3, start.Size);
    }

    [Fact]
    public void AddFile_RejectsDuplicatePaths()
    {
        var root = ArchiveFolderNode.CreateRoot();
        ArchiveTreeBuilder.AddFile(root, "maps/start.bsp", [1, 2, 3]);

        Assert.Throws<ArchivePathConflictException>(() =>
            ArchiveTreeBuilder.AddFile(root, "maps/start.bsp", [4, 5, 6]));
    }
}
