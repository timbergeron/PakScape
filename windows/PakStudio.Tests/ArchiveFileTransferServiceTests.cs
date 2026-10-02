using PakStudio.App.Services;
using PakStudio.Core.Nodes;
using PakStudio.Core.Operations;
using PakStudio.Core.Validation;
using Xunit;

namespace PakStudio.Tests;

public sealed class ArchiveFileTransferServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ExportReplacementSupportsFilesAndFolders(bool sourceFolder, bool destinationFolder)
    {
        var directory = CreateDirectory();
        try
        {
            var path = Path.Combine(directory, "item");
            if (destinationFolder)
            {
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "old.txt"), "original");
            }
            else
            {
                File.WriteAllText(path, "original");
            }
            ArchiveNode node;
            if (sourceFolder)
            {
                var folder = new ArchiveFolderNode("item");
                ArchiveTreeEditor.AddFile(folder, "new.txt", [1, 2]);
                node = folder;
            }
            else
            {
                node = new ArchiveFileNode("item", [1, 2]);
            }
            using var service = new ArchiveFileTransferService();

            Assert.Equal(path, service.Export(node, directory, replaceExisting: true));

            Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(
                sourceFolder ? Path.Combine(path, "new.txt") : path));
            Assert.Single(Directory.EnumerateFileSystemEntries(directory));
            Assert.False(File.Exists(Path.Combine(path, "old.txt")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FailedFolderPreparationPreservesTheOriginal()
    {
        var directory = CreateDirectory();
        try
        {
            var path = Path.Combine(directory, "item");
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "old.txt"), "original");
            var folder = new ArchiveFolderNode("item");
            folder.Files.Add(new ArchiveFileNode("valid.txt", [1]));
            folder.Files.Add(new ArchiveFileNode("CON", [2]));
            using var service = new ArchiveFileTransferService();

            Assert.Throws<ArchiveValidationException>(() => service.Export(folder, directory, replaceExisting: true));

            Assert.Equal("original", File.ReadAllText(Path.Combine(path, "old.txt")));
            Assert.Single(Directory.EnumerateFileSystemEntries(directory));
            Assert.Single(Directory.EnumerateFileSystemEntries(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingPayloadCannotReplaceAnExistingFileWithEmptyData()
    {
        var directory = CreateDirectory();
        try
        {
            var path = Path.Combine(directory, "item");
            File.WriteAllText(path, "original");
            using var service = new ArchiveFileTransferService();

            Assert.ThrowsAny<ArgumentException>(() => service.Export(
                new ArchiveFileNode("item", null!), directory, replaceExisting: true));

            Assert.Equal("original", File.ReadAllText(path));
            Assert.Single(Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pakscape-windows-transfer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
