using System.IO.Compression;
using PakStudio.Core.Documents;
using PakStudio.Core.Operations;
using PakStudio.Core.Playback;
using Xunit;

namespace PakStudio.Tests;

public sealed class DemoPlaybackPackagesTests
{
    [Theory]
    [InlineData("pak")]
    [InlineData("pk3")]
    [InlineData("kpf")]
    public void PackagesIncludeCurrentMapsAndCompanionAssetsInUnsavedDocuments(string format)
    {
        var document = new ArchiveDocument { FormatId = format, IsDirty = true };
        var map = ArchiveTreeBuilder.AddFile(document.Root, "maps/custom.bsp", [1]);
        ArchiveTreeBuilder.AddFile(document.Root, "progs.dat", [2, 3]);
        map.Data = [4, 5, 6];

        var package = Assert.Single(DemoPlaybackPackages.Build(document, ["CUSTOM"], 4096));

        Assert.Equal("Untitled.pk3", package.FileName);
        using var stream = new MemoryStream(package.Data);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Equal(new byte[] { 4, 5, 6 }, ReadEntry(archive, "maps/custom.bsp"));
        Assert.Equal(new byte[] { 2, 3 }, ReadEntry(archive, "progs.dat"));
        Assert.Null(document.FilePath);
        Assert.True(document.IsDirty);
        Assert.Equal(format, document.FormatId);
        Assert.Same(map, Assert.Single(Assert.Single(document.Root.Folders).Files));
    }

    [Fact]
    public void PackagesUseCurrentContentsWhenTheSavedArchiveIsUnavailable()
    {
        var document = new ArchiveDocument { FilePath = "/missing/edited.kpf" };
        ArchiveTreeBuilder.AddFile(document.Root, "maps/custom.bsp", [9]);

        var package = Assert.Single(DemoPlaybackPackages.Build(document, ["custom"], 4096));

        Assert.Equal("edited.pk3", package.FileName);
        using var stream = new MemoryStream(package.Data);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Equal(new byte[] { 9 }, ReadEntry(archive, "maps/custom.bsp"));
        Assert.Equal("/missing/edited.kpf", document.FilePath);
        Assert.False(document.IsDirty);
    }

    [Fact]
    public void StockMapsDoNotPackageUnrelatedArchiveContents()
    {
        var document = new ArchiveDocument();
        ArchiveTreeBuilder.AddFile(document.Root, "maps/custom.bsp", [1]);

        Assert.Empty(DemoPlaybackPackages.Build(document, ["e1m1"], 0));
        Assert.Empty(DemoPlaybackPackages.Build(document, [], 0));
        Assert.Empty(DemoPlaybackPackages.Build(null, ["custom"], 0));
    }

    [Fact]
    public void PackagesRejectExpandedPayloadsOverTheRemainingSessionBudget()
    {
        var document = new ArchiveDocument();
        ArchiveTreeBuilder.AddFile(document.Root, "maps/custom.bsp", new byte[32]);

        Assert.Throws<DemoPlaybackException>(() => DemoPlaybackPackages.Build(document, ["custom"], 31));
    }

    [Fact]
    public void PackagesAlsoBoundZipHeadersAndDirectoryRecords()
    {
        var document = new ArchiveDocument();
        ArchiveTreeBuilder.AddFile(document.Root, "maps/custom.bsp", [1]);

        Assert.Throws<DemoPlaybackException>(() => DemoPlaybackPackages.Build(document, ["custom"], 64));
    }

    private static byte[] ReadEntry(ZipArchive archive, string path)
    {
        using var input = Assert.IsType<ZipArchiveEntry>(archive.GetEntry(path)).Open();
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }
}
