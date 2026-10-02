using PakScape.Linux.Services;
using PakStudio.Core.Nodes;
using PakStudio.Core.Operations;
using PakStudio.Core.Validation;
using Xunit;

namespace PakScape.Linux.Tests;

public sealed class LinuxArchiveFileTransferServiceTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ExportReplacementSupportsFilesAndFolders(bool sourceFolder, bool destinationFolder)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var destination = Path.Combine(directory, "item");
            if (destinationFolder)
            {
                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(destination, "old.txt"), "old");
            }
            else
            {
                File.WriteAllText(destination, "old");
            }
            var root = ArchiveFolderNode.CreateRoot();
            ArchiveNode item;
            if (sourceFolder)
            {
                var folder = ArchiveTreeEditor.CreateFolder(root, "item");
                ArchiveTreeEditor.AddFile(folder, "new.txt", [1, 2]);
                item = folder;
            }
            else
            {
                item = ArchiveTreeEditor.AddFile(root, "item", [1, 2]);
            }
            using var service = new LinuxArchiveFileTransferService();

            Assert.Equal(destination, service.Export(item, directory, replaceExisting: true));

            Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(
                sourceFolder ? Path.Combine(destination, "new.txt") : destination));
            Assert.False(File.Exists(Path.Combine(destination, "old.txt")));
            Assert.Single(Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FailedFolderReplacementPreservesTheOriginalAndRemovesStaging()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var destination = Directory.CreateDirectory(Path.Combine(directory, "item"));
            File.WriteAllText(Path.Combine(destination.FullName, "old.txt"), "original");
            var folder = new ArchiveFolderNode("item");
            folder.Files.Add(new ArchiveFileNode("valid.txt", [1]));
            folder.Files.Add(new ArchiveFileNode("invalid\\name", [2]));
            using var service = new LinuxArchiveFileTransferService();

            Assert.Throws<ArchiveValidationException>(() => service.Export(folder, directory, replaceExisting: true));

            Assert.Equal("original", File.ReadAllText(Path.Combine(destination.FullName, "old.txt")));
            Assert.Single(Directory.EnumerateFileSystemEntries(destination.FullName));
            Assert.Single(Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FailedFileReplacementPreservesTheOriginal()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var destination = Path.Combine(directory, "item");
            File.WriteAllText(destination, "original");
            var file = new ArchiveFileNode("item", null!);
            using var service = new LinuxArchiveFileTransferService();

            Assert.ThrowsAny<ArgumentException>(() => service.Export(file, directory, replaceExisting: true));

            Assert.Equal("original", File.ReadAllText(destination));
            Assert.Single(Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DirectoryImportPreservesCaseCollisionsAcrossFilesAndFolders()
    {
        var source = CreateTemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(source, "maps"));
            Directory.CreateDirectory(Path.Combine(source, "MAPS"));
            File.WriteAllBytes(Path.Combine(source, "maps", "first.bsp"), [1]);
            File.WriteAllBytes(Path.Combine(source, "MAPS", "second.bsp"), [2]);
            File.WriteAllBytes(Path.Combine(source, "readme"), [3]);
            File.WriteAllBytes(Path.Combine(source, "README"), [4]);
            var root = ArchiveFolderNode.CreateRoot();
            using var service = new LinuxArchiveFileTransferService();

            var imported = service.ImportDirectory(root, source);

            Assert.Equal(2, imported.Folders.Count);
            Assert.Equal(2, imported.Files.Count);
            Assert.Equal(4, imported.Children.Select(node => node.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, ArchiveTreeBuilder.FlattenFiles(imported)
                .Select(entry => Assert.Single(entry.File.Data)).Order().ToArray());
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

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

    [Fact]
    public void ImportFileRejectsAnExcessivelyDeepDestination()
    {
        var source = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(source, "file.txt");
            File.WriteAllText(path, "content");
            var destination = CreateDeepDestination(ArchiveSafetyLimits.MaximumPathDepth);
            using var service = new LinuxArchiveFileTransferService();

            Assert.Throws<ArchiveValidationException>(() => service.ImportFile(destination, path));
            Assert.Empty(destination.Children);
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImportDirectoryIncludesDestinationDepthAndLeavesNoPartialTree(bool childIsFolder)
    {
        var source = CreateTemporaryDirectory();
        try
        {
            var childPath = Path.Combine(source, "child");
            if (childIsFolder)
            {
                Directory.CreateDirectory(childPath);
            }
            else
            {
                File.WriteAllText(childPath, "content");
            }
            var destination = CreateDeepDestination(ArchiveSafetyLimits.MaximumPathDepth - 1);
            using var service = new LinuxArchiveFileTransferService();

            Assert.Throws<ArchiveValidationException>(() => service.ImportDirectory(destination, source));
            Assert.Empty(destination.Children);
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    [Fact]
    public void ImportDirectoryAllowsFilesAtTheMaximumPathDepth()
    {
        var source = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(source, "child"), "content");
            var destination = CreateDeepDestination(ArchiveSafetyLimits.MaximumPathDepth - 2);
            using var service = new LinuxArchiveFileTransferService();

            var folder = service.ImportDirectory(destination, source);

            Assert.Equal("content", System.Text.Encoding.UTF8.GetString(Assert.Single(folder.Files).Data));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
        }
    }

    private static ArchiveFolderNode CreateDeepDestination(int depth)
    {
        var folder = ArchiveFolderNode.CreateRoot();
        for (var index = 0; index < depth; index++)
        {
            folder = ArchiveTreeEditor.CreateFolder(folder, "a");
        }
        return folder;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pakscape-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
