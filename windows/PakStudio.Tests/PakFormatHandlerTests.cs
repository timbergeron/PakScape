using System.Text;
using PakStudio.Core.Documents;
using PakStudio.Core.Operations;
using PakStudio.Formats.Pak;
using PakStudio.Core.Validation;
using Xunit;

namespace PakStudio.Tests;

public sealed class PakFormatHandlerTests
{
    private readonly PakFormatHandler _handler = new();

    [Fact]
    public void Parse_InvalidHeader_Throws()
    {
        var bytes = Encoding.ASCII.GetBytes("NOPE");

        Assert.Throws<ArchiveCorruptException>(() => _handler.Parse(bytes));
    }

    [Fact]
    public void Parse_OverlappingEntries_Throws()
    {
        var bytes = CreatePak(
            ("maps/a.txt", 12, [1, 2, 3, 4]),
            ("maps/b.txt", 14, [5, 6, 7, 8]));

        Assert.Throws<ArchiveCorruptException>(() => _handler.Parse(bytes));
    }

    [Fact]
    public void Parse_ParentTraversalPath_Throws()
    {
        var bytes = CreatePak(("../outside.txt", 12, [1, 2, 3, 4]));

        Assert.Throws<ArchiveCorruptException>(() => _handler.Parse(bytes));
    }

    [Theory]
    [InlineData("/absolute.txt")]
    [InlineData("maps//bad.txt")]
    [InlineData("maps/")]
    public void Parse_MalformedPath_ThrowsInsteadOfNormalizing(string path)
    {
        var bytes = CreatePak((path, 12, [1]));

        Assert.Throws<ArchiveCorruptException>(() => _handler.Parse(bytes));
    }

    [Fact]
    public void Parse_NonAsciiPathByte_ThrowsInsteadOfReplacingCharacter()
    {
        var bytes = CreatePak(("maps/a.txt", 12, [1]));
        var directoryOffset = BitConverter.ToInt32(bytes, 4);
        bytes[directoryOffset + 5] = 0xFF;

        Assert.Throws<ArchiveCorruptException>(() => _handler.Parse(bytes));
    }

    [Fact]
    public void Parse_FileAndFolderPathConflict_ThrowsCorruptArchive()
    {
        var bytes = CreatePak(
            ("maps", 12, [1]),
            ("maps/start.bsp", 13, [2]));

        Assert.Throws<ArchiveCorruptException>(() => _handler.Parse(bytes));
    }

    [Fact]
    public void Serialize_ThenParse_RoundTripsArchive()
    {
        var document = new ArchiveDocument
        {
            FormatId = "pak",
        };

        ArchiveTreeBuilder.AddFile(document.Root, "maps/e1m1.bsp", [0x42, 0x53, 0x50]);
        ArchiveTreeBuilder.AddFile(document.Root, "progs/player.mdl", [0x49, 0x44, 0x50, 0x4F]);
        ArchiveTreeBuilder.SortRecursively(document.Root);

        var bytes = _handler.Serialize(document);
        var parsed = _handler.Parse(bytes);
        var files = ArchiveTreeBuilder.FlattenFiles(parsed.Root)
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.Collection(
            files,
            first =>
            {
                Assert.Equal("maps/e1m1.bsp", first.Path);
                Assert.Equal(new byte[] { 0x42, 0x53, 0x50 }, first.File.Data);
            },
            second =>
            {
                Assert.Equal("progs/player.mdl", second.Path);
                Assert.Equal(new byte[] { 0x49, 0x44, 0x50, 0x4F }, second.File.Data);
            });
    }

    [Fact]
    public void Serialize_PathLongerThanFormatLimit_Throws()
    {
        var document = new ArchiveDocument
        {
            FormatId = "pak",
        };
        var path = new string('a', 56);
        ArchiveTreeBuilder.AddFile(document.Root, path, [1]);

        Assert.Throws<ArchiveValidationException>(() => _handler.Serialize(document));
    }

    [Fact]
    public void Serialize_UnicodePath_ThrowsInsteadOfRenamingIt()
    {
        var document = new ArchiveDocument
        {
            FormatId = "pak",
        };
        ArchiveTreeBuilder.AddFile(document.Root, "maps/é.txt", [1]);

        Assert.Throws<ArchiveValidationException>(() => _handler.Serialize(document));
    }

    [Fact]
    public async Task SaveAsync_ReplacesAtomicallyWithoutLeavingTemporaryFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"PakStudioTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "test.pak");

        try
        {
            var document = new ArchiveDocument
            {
                FormatId = "pak",
            };
            var file = ArchiveTreeBuilder.AddFile(document.Root, "maps/start.txt", [1, 2, 3]);

            await _handler.SaveAsync(document, path, TestContext.Current.CancellationToken);
            file.Data = [4, 5, 6];
            await _handler.SaveAsync(document, path, TestContext.Current.CancellationToken);

            var reloaded = await _handler.OpenAsync(path, TestContext.Current.CancellationToken);
            var savedFile = Assert.Single(ArchiveTreeBuilder.FlattenFiles(reloaded.Root));
            Assert.Equal(new byte[] { 4, 5, 6 }, savedFile.File.Data);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static byte[] CreatePak(params (string Path, int Offset, byte[] Data)[] entries)
    {
        const int headerSize = 12;
        const int directoryEntrySize = 64;

        var directoryOffset = Math.Max(
            headerSize,
            entries.Max(entry => entry.Offset + entry.Data.Length));

        var totalLength = directoryOffset + (entries.Length * directoryEntrySize);
        var bytes = new byte[totalLength];

        Encoding.ASCII.GetBytes("PACK").CopyTo(bytes, 0);
        BitConverter.GetBytes(directoryOffset).CopyTo(bytes, 4);
        BitConverter.GetBytes(entries.Length * directoryEntrySize).CopyTo(bytes, 8);

        foreach (var entry in entries)
        {
            entry.Data.CopyTo(bytes, entry.Offset);
        }

        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            var directoryEntryOffset = directoryOffset + (index * directoryEntrySize);

            var nameBytes = Encoding.ASCII.GetBytes(entry.Path);
            Array.Copy(nameBytes, 0, bytes, directoryEntryOffset, Math.Min(nameBytes.Length, 55));
            BitConverter.GetBytes(entry.Offset).CopyTo(bytes, directoryEntryOffset + 56);
            BitConverter.GetBytes(entry.Data.Length).CopyTo(bytes, directoryEntryOffset + 60);
        }

        return bytes;
    }
}
