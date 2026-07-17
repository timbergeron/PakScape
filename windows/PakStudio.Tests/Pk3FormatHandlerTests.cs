using System.IO.Compression;
using PakStudio.Core.Documents;
using PakStudio.Core.Operations;
using PakStudio.Core.Validation;
using PakStudio.Formats.Pk3;
using Xunit;

namespace PakStudio.Tests;

public sealed class Pk3FormatHandlerTests
{
    private readonly Pk3FormatHandler _handler = new();

    [Fact]
    public async Task SaveThenOpen_RoundTripsFilesAndEmptyFolders()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "roundtrip.pk3");

        try
        {
            var document = new ArchiveDocument { FormatId = "pk3" };
            var maps = ArchiveTreeEditor.CreateFolder(document.Root, "maps");
            ArchiveTreeEditor.CreateFolder(maps, "empty");
            var sourceFile = ArchiveTreeEditor.AddFile(maps, "start.txt", [1, 2, 3]);

            await _handler.SaveAsync(document, path, TestContext.Current.CancellationToken);
            sourceFile.Data = [4, 5, 6];
            await _handler.SaveAsync(document, path, TestContext.Current.CancellationToken);
            var reopened = await _handler.OpenAsync(path, TestContext.Current.CancellationToken);

            var reopenedMaps = Assert.Single(reopened.Root.Folders);
            Assert.Equal("maps", reopenedMaps.Name);
            Assert.Equal("empty", Assert.Single(reopenedMaps.Folders).Name);
            var file = Assert.Single(reopenedMaps.Files);
            Assert.Equal("start.txt", file.Name);
            Assert.Equal(new byte[] { 4, 5, 6 }, file.Data);
            Assert.False(reopened.IsDirty);
            Assert.Equal("pk3", reopened.FormatId);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("maps//bad.txt")]
    public async Task Open_RejectsUnsafePaths(string entryPath)
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "unsafe.pk3");

        try
        {
            CreateZip(path, archive =>
            {
                var entry = archive.CreateEntry(entryPath);
                using var output = entry.Open();
                output.WriteByte(1);
            });

            await Assert.ThrowsAsync<ArchiveCorruptException>(() =>
                _handler.OpenAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Open_RejectsSymbolicLinkEntry()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "symlink.pk3");

        try
        {
            CreateZip(path, archive =>
            {
                var entry = archive.CreateEntry("link");
                entry.ExternalAttributes = unchecked((int)0xA000_0000);
                using var output = entry.Open();
                output.WriteByte(1);
            });

            await Assert.ThrowsAsync<ArchiveCorruptException>(() =>
                _handler.OpenAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Open_RejectsCaseInsensitiveDuplicatePaths()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "duplicate.pk3");

        try
        {
            CreateZip(path, archive =>
            {
                archive.CreateEntry("readme.txt");
                archive.CreateEntry("README.TXT");
            });

            await Assert.ThrowsAsync<ArchiveCorruptException>(() =>
                _handler.OpenAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Save_RejectsCaseInsensitivePathConflictInMalformedDocument()
    {
        var directory = CreateTemporaryDirectory();
        var path = Path.Combine(directory, "conflict.pk3");

        try
        {
            var document = new ArchiveDocument { FormatId = "pk3" };
            document.Root.Files.Add(new("readme.txt", [1]));
            document.Root.Files.Add(new("README.TXT", [2]));

            await Assert.ThrowsAsync<ArchiveValidationException>(() =>
                _handler.SaveAsync(document, path, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CreateZip(string path, Action<ZipArchive> writeEntries)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        writeEntries(archive);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"PakStudioTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
