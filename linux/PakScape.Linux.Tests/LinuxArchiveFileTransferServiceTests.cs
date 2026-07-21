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
    public void RecentFilesRejectOversizedOrInvalidState()
    {
        var stateHome = CreateTemporaryDirectory();
        try
        {
            var service = new XdgRecentFilesService(stateHome);
            var statePath = Path.Combine(stateHome, "pakscape", "recent-files.json");
            File.WriteAllBytes(statePath, new byte[(1024 * 1024) + 1]);
            Assert.Empty(service.GetRecentFiles());

            File.WriteAllBytes(statePath, [0xFF]);
            Assert.Empty(service.GetRecentFiles());
        }
        finally
        {
            Directory.Delete(stateHome, recursive: true);
        }
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
    public void ExportToTemporaryLocationStagesFilesAndFoldersForDesktopTransfer()
    {
        var archiveRoot = ArchiveFolderNode.CreateRoot();
        var file = ArchiveTreeEditor.AddFile(archiveRoot, "readme.txt", [1, 2, 3]);
        var folder = ArchiveTreeEditor.CreateFolder(archiveRoot, "maps");
        _ = ArchiveTreeEditor.AddFile(folder, "start.bsp", [4, 5]);
        using var service = new LinuxArchiveFileTransferService();

        var outputs = service.ExportToTemporaryLocation([file, folder]);

        Assert.Equal(2, outputs.Count);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(outputs[0]));
        Assert.Equal([4, 5], File.ReadAllBytes(Path.Combine(outputs[1], "start.bsp")));
    }

    [Fact]
    public void ReleaseTemporaryLocationRemovesOnlyTheOwnedOperationDirectory()
    {
        var unrelatedDirectory = CreateTemporaryDirectory();
        var unrelatedFile = Path.Combine(unrelatedDirectory, "keep.txt");
        File.WriteAllText(unrelatedFile, "keep");
        try
        {
            var archiveRoot = ArchiveFolderNode.CreateRoot();
            var file = ArchiveTreeEditor.AddFile(archiveRoot, "readme.txt", [1, 2, 3]);
            using var service = new LinuxArchiveFileTransferService();
            var outputs = service.ExportToTemporaryLocation([file]);
            var operationDirectory = Path.GetDirectoryName(Assert.Single(outputs));
            Assert.NotNull(operationDirectory);

            service.ReleaseTemporaryLocation([.. outputs, unrelatedFile]);

            Assert.False(Directory.Exists(operationDirectory));
            Assert.True(File.Exists(unrelatedFile));
        }
        finally
        {
            Directory.Delete(unrelatedDirectory, recursive: true);
        }
    }

    [Fact]
    public void UntitledPk3DocumentUsesTheCorrectDisplayName()
    {
        var document = new PakStudio.Core.Documents.ArchiveDocument { FormatId = "pk3" };

        Assert.Equal("Untitled.pk3", document.DisplayName);
    }

    [Fact]
    public void ImportFileRejectsSparseFilesOverThePerFileLimitBeforeReading()
    {
        var source = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(source, "oversized.bin");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                stream.SetLength(ArchiveSafetyLimits.MaximumFileSize + 1);
            }
            var destination = ArchiveFolderNode.CreateRoot();
            using var service = new LinuxArchiveFileTransferService();

            Assert.Throws<ArchiveValidationException>(() => service.ImportFile(destination, path));
            Assert.Empty(destination.Files);
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [Fact]
    public void ImportAccountsForEntriesAlreadyInTheArchive()
    {
        var source = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(source, "one-more.txt");
            File.WriteAllText(path, "content");
            var destination = ArchiveFolderNode.CreateRoot();
            for (var index = 0; index < ArchiveSafetyLimits.MaximumEntryCount; index++)
            {
                destination.Files.Add(new ArchiveFileNode($"file-{index}", []));
            }
            using var service = new LinuxArchiveFileTransferService();

            Assert.Throws<ArchiveValidationException>(() => service.ImportFile(destination, path));
            Assert.Equal(ArchiveSafetyLimits.MaximumEntryCount, destination.Files.Count);
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pakscape-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
