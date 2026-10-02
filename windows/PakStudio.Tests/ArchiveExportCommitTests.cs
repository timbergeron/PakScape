using PakStudio.Core.Operations;
using Xunit;

namespace PakStudio.Tests;

public sealed class ArchiveExportCommitTests
{
    [Fact]
    public void CommitRequiresExplicitReplacementAndPreservesBothItemsWhenDeclined()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pakscape-export-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var staging = Path.Combine(directory, "staged");
            var destination = Path.Combine(directory, "existing");
            File.WriteAllText(staging, "new");
            File.WriteAllText(destination, "original");

            Assert.Throws<IOException>(() => ArchiveExportCommit.Commit(staging, destination, replaceExisting: false));

            Assert.Equal("new", File.ReadAllText(staging));
            Assert.Equal("original", File.ReadAllText(destination));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingPreparedExportDoesNotMoveTheOriginalDestination()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"pakscape-export-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var destination = Path.Combine(directory, "existing");
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "old.txt"), "original");

            Assert.Throws<FileNotFoundException>(() => ArchiveExportCommit.Commit(
                Path.Combine(directory, "missing"), destination, replaceExisting: true));

            Assert.Equal("original", File.ReadAllText(Path.Combine(destination, "old.txt")));
            Assert.Single(Directory.EnumerateFileSystemEntries(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
