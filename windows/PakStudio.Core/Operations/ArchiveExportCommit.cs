namespace PakStudio.Core.Operations;

/// <summary>Commits a completed export without deleting the previous destination during preparation.</summary>
public static class ArchiveExportCommit
{
    public static void Commit(string stagingPath, string destinationPath, bool replaceExisting)
    {
        var stagedDirectory = Directory.Exists(stagingPath);
        if (!stagedDirectory && !File.Exists(stagingPath))
        {
            throw new FileNotFoundException("The prepared export is no longer available.", stagingPath);
        }
        var destinationDirectory = Directory.Exists(destinationPath);
        var destinationFile = File.Exists(destinationPath);
        if (!destinationDirectory && !destinationFile)
        {
            Move(stagingPath, destinationPath, stagedDirectory);
            return;
        }
        if (!replaceExisting)
        {
            throw new IOException("The export destination already exists.");
        }
        if (!stagedDirectory && destinationFile && !destinationDirectory)
        {
            File.Move(stagingPath, destinationPath, overwrite: true);
            return;
        }

        var backup = Path.Combine(
            Path.GetDirectoryName(destinationPath)!, $".pakscape-replaced-{Guid.NewGuid():N}.tmp");
        Move(destinationPath, backup, destinationDirectory);
        try
        {
            Move(stagingPath, destinationPath, stagedDirectory);
        }
        catch (Exception commitFailure)
        {
            try
            {
                Move(backup, destinationPath, destinationDirectory);
            }
            catch (Exception restoreFailure)
            {
                throw new IOException(
                    $"The export failed. The previous item is preserved at '{backup}'.",
                    new AggregateException(commitFailure, restoreFailure));
            }
            throw;
        }
        Cleanup(backup);
    }

    public static void Cleanup(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // A completed export or its original error must survive cleanup failures.
        }
    }

    private static void Move(string source, string destination, bool directory)
    {
        if (directory)
        {
            Directory.Move(source, destination);
        }
        else
        {
            File.Move(source, destination);
        }
    }
}
