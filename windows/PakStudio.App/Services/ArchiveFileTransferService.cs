using System.Diagnostics;
using System.IO;
using PakStudio.Core.Interfaces;
using PakStudio.Core.Nodes;
using PakStudio.Core.Operations;
using PakStudio.Core.Validation;

namespace PakStudio.App.Services;

public sealed class ArchiveFileTransferService : IArchiveFileTransferService
{
    public ArchiveFileNode ImportFile(ArchiveFolderNode destination, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var attributes = File.GetAttributes(sourcePath);
        if (attributes.HasFlag(FileAttributes.Directory))
        {
            throw new ArchiveValidationException($"'{sourcePath}' is a folder, not a file.");
        }
        RejectReparsePoint(sourcePath, attributes);

        var data = File.ReadAllBytes(sourcePath);
        return ArchiveTreeEditor.AddFile(
            destination,
            Path.GetFileName(sourcePath),
            data,
            File.GetLastWriteTimeUtc(sourcePath));
    }

    public ArchiveFolderNode ImportDirectory(ArchiveFolderNode destination, string sourcePath)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var attributes = File.GetAttributes(sourcePath);
        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            throw new ArchiveValidationException($"'{sourcePath}' is not a folder.");
        }
        RejectReparsePoint(sourcePath, attributes);

        var name = new DirectoryInfo(sourcePath).Name;
        var importedRoot = ArchiveTreeEditor.CreateFolder(destination, name);
        try
        {
            PopulateFolder(importedRoot, sourcePath);
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

        Directory.CreateDirectory(outputPath);
        try
        {
            WriteFolder((ArchiveFolderNode)node, outputPath);
            return outputPath;
        }
        catch
        {
            if (Directory.Exists(outputPath))
            {
                Directory.Delete(outputPath, recursive: true);
            }
            throw;
        }
    }

    public void OpenWithDefaultApplication(ArchiveFileNode file)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArchiveNameValidator.ValidateNodeName(file.Name);

        var directory = Path.Combine(
            Path.GetTempPath(),
            "PakStudio",
            "Preview",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, file.Name);
        WriteFileAtomically(path, file.Data);

        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
        });
    }

    private static void PopulateFolder(ArchiveFolderNode destination, string sourcePath)
    {
        var entries = Directory
            .EnumerateFileSystemEntries(sourcePath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var attributes = File.GetAttributes(entry);
            RejectReparsePoint(entry, attributes);

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                var folder = ArchiveTreeEditor.CreateFolder(destination, Path.GetFileName(entry));
                PopulateFolder(folder, entry);
            }
            else
            {
                var data = File.ReadAllBytes(entry);
                ArchiveTreeEditor.AddFile(
                    destination,
                    Path.GetFileName(entry),
                    data,
                    File.GetLastWriteTimeUtc(entry));
            }
        }
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
            File.WriteAllBytes(temporaryPath, data);
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

    private static void RejectReparsePoint(string path, FileAttributes attributes)
    {
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new ArchiveValidationException(
                $"Symbolic links and junctions cannot be imported: {path}");
        }
    }
}
