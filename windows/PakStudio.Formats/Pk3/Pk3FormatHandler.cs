using System.IO.Compression;
using PakStudio.Core.Documents;
using PakStudio.Core.Interfaces;
using PakStudio.Core.Nodes;
using PakStudio.Core.Operations;
using PakStudio.Core.Validation;

namespace PakStudio.Formats.Pk3;

public sealed class Pk3FormatHandler : IArchiveFormatHandler
{
    public string FormatId => "pk3";

    public string DisplayName => "Quake PK3 Archive";

    public IReadOnlyList<string> Extensions { get; } = [".pk3"];

    public bool CanOpen(string path)
    {
        var extension = Path.GetExtension(path);
        return Extensions.Any(candidate =>
            string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ArchiveDocument> OpenAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                throw new FileNotFoundException("The selected PK3 does not exist.", path);
            }
            ArchiveSafetyLimits.EnsureTotalSize(0, file.Length, "The PK3 archive");

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 128 * 1024,
                useAsync: true);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

            ArchiveSafetyLimits.EnsureEntryCount(archive.Entries.Count, "The PK3 archive");

            var document = new ArchiveDocument
            {
                FormatId = FormatId,
            };
            var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var folderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var explicitFolderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long totalExpandedSize = 0;

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (entryPath, isDirectory) = ValidatePath(entry.FullName);
                RegisterPath(
                    entryPath,
                    isDirectory,
                    filePaths,
                    folderPaths,
                    explicitFolderPaths);
                RejectSymbolicLink(entry, entryPath);

                if (isDirectory)
                {
                    ArchiveTreeBuilder.EnsureFolder(document.Root, entryPath);
                    continue;
                }

                ArchiveSafetyLimits.EnsureFileSize(entry.Length, $"PK3 entry '{entryPath}'");
                ArchiveSafetyLimits.EnsureTotalSize(
                    totalExpandedSize,
                    entry.Length,
                    "The expanded PK3 archive");

                var payload = await ReadEntryAsync(entry, entryPath, cancellationToken)
                    .ConfigureAwait(false);
                totalExpandedSize += payload.LongLength;
                var modifiedUtc = GetModifiedUtc(entry);
                ArchiveTreeBuilder.AddFile(document.Root, entryPath, payload, modifiedUtc);
            }

            ArchiveTreeBuilder.SortRecursively(document.Root);
            document.FilePath = Path.GetFullPath(path);
            document.IsDirty = false;
            return document;
        }
        catch (ArchiveException)
        {
            throw;
        }
        catch (InvalidDataException exception)
        {
            throw new ArchiveCorruptException($"The PK3 is invalid: {exception.Message}");
        }
    }

    public async Task SaveAsync(
        ArchiveDocument document,
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateDocumentForWrite(document);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The destination path has no parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             bufferSize: 128 * 1024,
                             useAsync: true))
            {
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    await WriteFolderAsync(
                            archive,
                            document.Root,
                            parentPath: string.Empty,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                File.Replace(temporaryPath, fullPath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, fullPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        document.FilePath = fullPath;
        document.FormatId = FormatId;
        document.IsDirty = false;
    }

    private static async Task WriteFolderAsync(
        ZipArchive archive,
        ArchiveFolderNode folder,
        string parentPath,
        CancellationToken cancellationToken)
    {
        foreach (var childFolder in folder.Folders.OrderBy(
                     child => child.Name,
                     StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArchiveNameValidator.ValidateNodeName(childFolder.Name);
            var folderPath = CombinePath(parentPath, childFolder.Name);
            archive.CreateEntry(folderPath + "/", CompressionLevel.NoCompression);
            await WriteFolderAsync(archive, childFolder, folderPath, cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var file in folder.Files.OrderBy(
                     child => child.Name,
                     StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArchiveNameValidator.ValidateNodeName(file.Name);
            var entryPath = CombinePath(parentPath, file.Name);
            var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
            SetModifiedTime(entry, file.ModifiedUtc);
            await using var output = entry.Open();
            await output.WriteAsync(file.Data, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateDocumentForWrite(ArchiveDocument document)
    {
        var filePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var explicitFolderPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entryCount = 0;
        long totalExpandedSize = 0;

        try
        {
            ValidateFolder(document.Root, string.Empty);
        }
        catch (ArchiveCorruptException exception)
        {
            throw new ArchiveValidationException(exception.Message);
        }

        void ValidateFolder(ArchiveFolderNode folder, string parentPath)
        {
            foreach (var childFolder in folder.Folders)
            {
                ArchiveNameValidator.ValidateNodeName(childFolder.Name);
                var path = CombinePath(parentPath, childFolder.Name);
                ArchiveSafetyLimits.EnsurePathDepth(path.Count(character => character == '/') + 1, $"PK3 entry '{path}'");
                RegisterPath(path, isDirectory: true, filePaths, folderPaths, explicitFolderPaths);
                entryCount++;
                ArchiveSafetyLimits.EnsureEntryCount(entryCount, "The PK3 archive");
                ValidateFolder(childFolder, path);
            }

            foreach (var file in folder.Files)
            {
                ArchiveNameValidator.ValidateNodeName(file.Name);
                var path = CombinePath(parentPath, file.Name);
                ArchiveSafetyLimits.EnsurePathDepth(path.Count(character => character == '/') + 1, $"PK3 entry '{path}'");
                RegisterPath(path, isDirectory: false, filePaths, folderPaths, explicitFolderPaths);
                entryCount++;
                ArchiveSafetyLimits.EnsureEntryCount(entryCount, "The PK3 archive");
                ArchiveSafetyLimits.EnsureFileSize(file.Data.LongLength, $"PK3 entry '{path}'");
                ArchiveSafetyLimits.EnsureTotalSize(
                    totalExpandedSize,
                    file.Data.LongLength,
                    "The expanded PK3 archive");
                totalExpandedSize += file.Data.LongLength;
            }
        }
    }

    private static async Task<byte[]> ReadEntryAsync(
        ZipArchiveEntry entry,
        string entryPath,
        CancellationToken cancellationToken)
    {
        var expectedLength = checked((int)entry.Length);
        using var output = new MemoryStream(expectedLength);
        await using var input = entry.Open();
        var buffer = new byte[128 * 1024];
        var actualLength = 0L;

        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            actualLength += read;
            if (actualLength > ArchiveSafetyLimits.MaximumFileSize || actualLength > entry.Length)
            {
                throw new ArchiveCorruptException(
                    $"PK3 entry '{entryPath}' expands beyond its declared safe size.");
            }
            output.Write(buffer, 0, read);
        }

        if (actualLength != entry.Length)
        {
            throw new ArchiveCorruptException(
                $"PK3 entry '{entryPath}' does not match its declared expanded size.");
        }
        return output.ToArray();
    }

    private static (string Path, bool IsDirectory) ValidatePath(string rawPath)
    {
        var normalized = rawPath.Replace('\\', '/');
        var isDirectory = normalized.EndsWith('/');
        var path = isDirectory ? normalized[..^1] : normalized;
        var segments = path.Split('/', StringSplitOptions.None);

        if (path.Length == 0 ||
            path.StartsWith('/') ||
            path.EndsWith('/') ||
            segments.Any(string.IsNullOrEmpty))
        {
            throw new ArchiveCorruptException($"PK3 entry '{rawPath}' has an unsafe path.");
        }

        try
        {
            ArchiveSafetyLimits.EnsurePathDepth(segments.Length, $"PK3 entry '{rawPath}'");
            foreach (var segment in segments)
            {
                ArchiveNameValidator.ValidateNodeName(segment);
            }
        }
        catch (ArchiveValidationException exception)
        {
            throw new ArchiveCorruptException(
                $"PK3 entry '{rawPath}' has an unsafe path: {exception.Message}");
        }

        return (string.Join('/', segments), isDirectory);
    }

    private static void RegisterPath(
        string path,
        bool isDirectory,
        ISet<string> filePaths,
        ISet<string> folderPaths,
        ISet<string> explicitFolderPaths)
    {
        var segments = path.Split('/');
        var prefix = string.Empty;
        foreach (var segment in segments.SkipLast(1))
        {
            prefix = prefix.Length == 0 ? segment : $"{prefix}/{segment}";
            if (filePaths.Contains(prefix))
            {
                throw new ArchiveCorruptException(
                    $"PK3 entry '{path}' conflicts with an existing file path.");
            }
            folderPaths.Add(prefix);
        }

        if (isDirectory)
        {
            if (filePaths.Contains(path) || !explicitFolderPaths.Add(path))
            {
                throw new ArchiveCorruptException($"The PK3 contains duplicate path '{path}'.");
            }
            folderPaths.Add(path);
        }
        else
        {
            if (folderPaths.Contains(path) || !filePaths.Add(path))
            {
                throw new ArchiveCorruptException($"The PK3 contains duplicate path '{path}'.");
            }
        }
    }

    private static void RejectSymbolicLink(ZipArchiveEntry entry, string path)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixMode == 0xA000)
        {
            throw new ArchiveCorruptException($"PK3 entry '{path}' is a symbolic link.");
        }
    }

    private static DateTime? GetModifiedUtc(ZipArchiveEntry entry)
    {
        var value = entry.LastWriteTime;
        return value.Year >= 1980 ? value.UtcDateTime : null;
    }

    private static void SetModifiedTime(ZipArchiveEntry entry, DateTime? modifiedUtc)
    {
        if (modifiedUtc is not { Year: >= 1980 and <= 2107 })
        {
            return;
        }

        var utc = DateTime.SpecifyKind(modifiedUtc.Value, DateTimeKind.Utc);
        entry.LastWriteTime = new DateTimeOffset(utc);
    }

    private static string CombinePath(string parentPath, string name)
    {
        return parentPath.Length == 0 ? name : $"{parentPath}/{name}";
    }
}
