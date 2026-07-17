using PakScape.Linux.Services;
using PakStudio.Core.Nodes;
using PakStudio.Core.Operations;
using PakStudio.Core.Validation;
using Xunit;

namespace PakScape.Linux.Tests;

public sealed class LinuxArchiveFileTransferServiceTests
{
    [Fact]
    public void RecentFilesDegradeGracefullyWhenStateStorageIsReadOnly()
    {
        var service = new XdgRecentFilesService("/proc/pakscape-read-only-test");

        service.Add("/tmp/example.pak");

        Assert.Empty(service.GetRecentFiles());
    }

    [Fact]
    public void ImportDirectoryRemovesPartialTreeWhenAnEntryNameIsInvalid()
    {
        var source = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(source, "valid.txt"), "valid");
            File.WriteAllText(Path.Combine(source, "invalid\\name.txt"), "invalid");
            var destination = ArchiveFolderNode.CreateRoot();
            using var service = new LinuxArchiveFileTransferService();

            _ = Assert.Throws<ArchiveValidationException>(() =>
                service.ImportDirectory(destination, source));

            Assert.Empty(destination.Children);
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [Fact]
    public void ImportFileRejectsSymbolicLinks()
    {
        var source = CreateTemporaryDirectory();
        try
        {
            var target = Path.Combine(source, "target.txt");
            var link = Path.Combine(source, "link.txt");
            File.WriteAllText(target, "target");
            _ = File.CreateSymbolicLink(link, target);
            var destination = ArchiveFolderNode.CreateRoot();
            using var service = new LinuxArchiveFileTransferService();

            _ = Assert.Throws<ArchiveValidationException>(() =>
                service.ImportFile(destination, link));

            Assert.Empty(destination.Files);
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [Fact]
    public void ExportUsesANewNameInsteadOfOverwritingAnExistingFile()
    {
        var destination = CreateTemporaryDirectory();
        try
        {
            var archiveRoot = ArchiveFolderNode.CreateRoot();
            var file = ArchiveTreeEditor.AddFile(archiveRoot, "readme.txt", [1, 2, 3]);
            File.WriteAllText(Path.Combine(destination, file.Name), "existing");
            using var service = new LinuxArchiveFileTransferService();

            var output = service.Export(file, destination);

            Assert.Equal("readme (2).txt", Path.GetFileName(output));
            Assert.Equal([1, 2, 3], File.ReadAllBytes(output));
            Assert.Equal("existing", File.ReadAllText(Path.Combine(destination, file.Name)));
        }
        finally
        {
            Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public void UntitledPk3DocumentUsesTheCorrectDisplayName()
    {
        var document = new PakStudio.Core.Documents.ArchiveDocument { FormatId = "pk3" };

        Assert.Equal("Untitled.pk3", document.DisplayName);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pakscape-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
