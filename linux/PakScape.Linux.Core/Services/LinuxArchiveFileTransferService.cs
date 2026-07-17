using System.Diagnostics;
using PakStudio.Core.Interfaces;
using PakStudio.Core.Nodes;
using PakStudio.Core.Operations;
using PakStudio.Core.Validation;

namespace PakScape.Linux.Services;

public sealed class LinuxArchiveFileTransferService : IArchiveFileTransferService, IDisposable
{
    private const int MaximumEntryCount = 50_000;
    private const long MaximumFileSize = 1L * 1024 * 1024 * 1024;
    private const long MaximumImportSize = 2L * 1024 * 1024 * 1024;
    private readonly string _previewDirectory = CreatePrivatePreviewDirectory();
    private bool _disposed;

    public ArchiveFileNode ImportFile(ArchiveFolderNode destination, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var info = new FileInfo(sourcePath);
        RejectLink(info);
        if (!info.Exists)
        {
            throw new FileNotFoundException("The selected file does not exist.", sourcePath);
        }

        var budget = new ImportBudget();
        budget.RegisterEntry();
        var data = ReadFileWithLimits(info, budget);

        return ArchiveTreeEditor.AddFile(
            destination,
            info.Name,
            data,
            info.LastWriteTimeUtc);
    }

    public ArchiveFolderNode ImportDirectory(ArchiveFolderNode destination, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var source = new DirectoryInfo(sourcePath);
        RejectLink(source);
        if (!source.Exists)
        {
            throw new DirectoryNotFoundException($"The selected folder does not exist: {sourcePath}");
        }

        PreflightDirectory(source.FullName);
        var importedRoot = ArchiveTreeEditor.CreateFolder(destination, source.Name);
        try
        {
            PopulateFolder(importedRoot, source.FullName, new ImportBudget());
            return importedRoot;
        }
        catch
        {
            ArchiveTreeEditor.Remove(importedRoot);
            throw;
        }
    }

    public string Export(ArchiveNode node, string destinationDirectory)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);

        var outputPath = GetAvailableFileSystemPath(
            destinationDirectory,
            node.Name,
            node is ArchiveFileNode);
        if (node is ArchiveFileNode file)
        {
            WriteFileAtomically(outputPath, file.Data);
            return outputPath;
        }

        var stagingPath = Path.Combine(
            destinationDirectory,
            $".pakscape-export-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(stagingPath);
        try
        {
            WriteFolder((ArchiveFolderNode)node, stagingPath);
            Directory.Move(stagingPath, outputPath);
            return outputPath;
        }
        finally
        {
            if (Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }
        }
    }

    public void OpenWithDefaultApplication(ArchiveFileNode file)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArchiveNameValidator.ValidateNodeName(file.Name);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var path = GetAvailableFileSystemPath(_previewDirectory, file.Name, preserveExtension: true);
        WriteFileAtomically(path, file.Data);

        var startInfo = new ProcessStartInfo("xdg-open")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(path);
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start xdg-open.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            if (Directory.Exists(_previewDirectory))
            {
                Directory.Delete(_previewDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Preview cleanup is best effort; the directory name is unguessable and private.
        }
        catch (UnauthorizedAccessException)
        {
            // Preview cleanup is best effort.
        }
    }

    private static void PreflightDirectory(string sourcePath)
    {
        var pending = new Stack<string>();
        pending.Push(sourcePath);
        var entryCount = 0;
        long totalSize = 0;

        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                FileSystemInfo info = Directory.Exists(path)
                    ? new DirectoryInfo(path)
                    : new FileInfo(path);
                RejectLink(info);

                entryCount++;
                if (entryCount > MaximumEntryCount)
                {
                    throw new ArchiveValidationException(
                        $"The selected folder contains more than {MaximumEntryCount:N0} entries.");
                }

                if (info is DirectoryInfo childDirectory)
                {
                    pending.Push(childDirectory.FullName);
                    continue;
                }

                var file = (FileInfo)info;
                if (file.Length > MaximumFileSize)
                {
                    throw new ArchiveValidationException(
                        $"'{file.Name}' exceeds the 1 GiB per-file import limit.");
                }
                if (totalSize > MaximumImportSize - file.Length)
                {
                    throw new ArchiveValidationException(
                        "The selected folder exceeds the 2 GiB total import limit.");
                }
                totalSize += file.Length;
            }
        }
    }

    private static void PopulateFolder(
        ArchiveFolderNode destination,
        string sourcePath,
        ImportBudget budget)
    {
        foreach (var entry in Directory
                     .EnumerateFileSystemEntries(sourcePath)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            FileSystemInfo info = Directory.Exists(entry)
                ? new DirectoryInfo(entry)
                : new FileInfo(entry);
            RejectLink(info);
            budget.RegisterEntry();

            if (info is DirectoryInfo directory)
            {
                var folder = ArchiveTreeEditor.CreateFolder(destination, directory.Name);
                PopulateFolder(folder, directory.FullName, budget);
            }
            else
            {
                var file = (FileInfo)info;
                ArchiveTreeEditor.AddFile(
                    destination,
                    file.Name,
                    ReadFileWithLimits(file, budget),
                    file.LastWriteTimeUtc);
            }
        }
    }

    private static byte[] ReadFileWithLimits(FileInfo file, ImportBudget budget)
    {
        file.Refresh();
        RejectLink(file);
        using var stream = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        var length = stream.Length;
        if (length > MaximumFileSize)
        {
            throw new ArchiveValidationException(
                $"'{file.Name}' exceeds the 1 GiB per-file import limit.");
        }
        budget.EnsureSizeAvailable(length);

        var data = new byte[checked((int)length)];
        stream.ReadExactly(data);
        if (stream.ReadByte() != -1)
        {
            throw new ArchiveValidationException(
                $"'{file.Name}' changed size while it was being imported.");
        }

        budget.CommitSize(data.LongLength);
        return data;
    }

    private static void WriteFolder(ArchiveFolderNode folder, string destinationPath)
    {
        foreach (var childFolder in folder.Folders)
        {
            ArchiveNameValidator.ValidateNodeName(childFolder.Name);
            var childPath = Path.Combine(destinationPath, childFolder.Name);
            Directory.CreateDirectory(childPath);
            WriteFolder(childFolder, childPath);
        }

        foreach (var file in folder.Files)
        {
            ArchiveNameValidator.ValidateNodeName(file.Name);
            WriteFileAtomically(Path.Combine(destinationPath, file.Name), file.Data);
        }
    }

    private static void WriteFileAtomically(string path, byte[] data)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new ArchiveValidationException("The output path has no parent folder.");
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string GetAvailableFileSystemPath(
        string directory,
        string suggestedName,
        bool preserveExtension)
    {
        ArchiveNameValidator.ValidateNodeName(suggestedName);
        var candidate = Path.Combine(directory, suggestedName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        var extension = preserveExtension ? Path.GetExtension(suggestedName) : string.Empty;
        var stem = preserveExtension ? Path.GetFileNameWithoutExtension(suggestedName) : suggestedName;
        if (string.IsNullOrEmpty(stem))
        {
            stem = suggestedName;
            extension = string.Empty;
        }

        for (var suffix = 2; suffix < int.MaxValue; suffix++)
        {
            candidate = Path.Combine(directory, $"{stem} ({suffix}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new ArchiveValidationException("Could not generate a unique output name.");
    }

    private static void RejectLink(FileSystemInfo info)
    {
        info.Refresh();
        if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new ArchiveValidationException(
                $"Symbolic links cannot be imported: {info.FullName}");
        }
    }

    private static string CreatePrivatePreviewDirectory()
    {
        var runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(runtimeDirectory) &&
            Path.IsPathFullyQualified(runtimeDirectory) &&
            Directory.Exists(runtimeDirectory))
        {
            try
            {
                return CreatePrivateDirectory(runtimeDirectory);
            }
            catch (IOException)
            {
                // Some containers expose an XDG runtime path that is not writable.
            }
            catch (UnauthorizedAccessException)
            {
                // Fall back to a private, randomly named directory in the temp root.
            }
        }

        return CreatePrivateDirectory(Path.GetTempPath());
    }

    private static string CreatePrivateDirectory(string baseDirectory)
    {
        var previewDirectory = Path.Combine(
            baseDirectory,
            $"pakscape-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(previewDirectory);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                previewDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return previewDirectory;
    }

    private sealed class ImportBudget
    {
        private int _entryCount;
        private long _totalSize;

        public void RegisterEntry()
        {
            _entryCount++;
            if (_entryCount > MaximumEntryCount)
            {
                throw new ArchiveValidationException(
                    $"The selected folder contains more than {MaximumEntryCount:N0} entries.");
            }
        }

        public void EnsureSizeAvailable(long length)
        {
            if (_totalSize > MaximumImportSize - length)
            {
                throw new ArchiveValidationException(
                    "The selected folder exceeds the 2 GiB total import limit.");
            }
        }

        public void CommitSize(long length)
        {
            EnsureSizeAvailable(length);
            _totalSize += length;
        }
    }
}
