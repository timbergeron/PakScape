using System.Text;
using System.Buffers.Binary;
using PakStudio.Core.Documents;
using PakStudio.Core.Interfaces;
using PakStudio.Core.Operations;
using PakStudio.Core.Pathing;
using PakStudio.Core.Validation;
using PakStudio.Formats.Common.Binary;

namespace PakStudio.Formats.Pak;

public sealed class PakFormatHandler : IArchiveFormatHandler
{
    private const string Signature = "PACK";
    private const int HeaderSize = 12;
    private const int DirectoryEntrySize = 64;
    private const int EntryNameLength = 56;
    private const int MaxStoredPathLength = 55;

    public string FormatId => "pak";

    public string DisplayName => "Quake PAK Archive";

    public IReadOnlyList<string> Extensions { get; } = [".pak"];

    public bool CanOpen(string path)
    {
        var extension = Path.GetExtension(path);
        return Extensions.Any(candidate => string.Equals(candidate, extension, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ArchiveDocument> OpenAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The selected PAK does not exist.", path);
        }
        ArchiveSafetyLimits.EnsureTotalSize(0, file.Length, "The PAK archive");

        var buffer = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var document = Parse(buffer);
        document.FilePath = Path.GetFullPath(path);
        document.IsDirty = false;
        return document;
    }

    public async Task SaveAsync(ArchiveDocument document, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var output = Serialize(document);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("The destination path has no parent directory.", nameof(path));
        Directory.CreateDirectory(directory);

        var tempPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 128 * 1024,
                             useAsync: true))
            {
                await stream.WriteAsync(output, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(fullPath))
            {
                File.Replace(tempPath, fullPath, null, true);
            }
            else
            {
                File.Move(tempPath, fullPath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        document.FilePath = fullPath;
        document.FormatId = FormatId;
        document.IsDirty = false;
    }

    public ArchiveDocument Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize)
        {
            throw new ArchiveCorruptException("Not a valid Quake PAK archive. The header is truncated.");
        }

        var ident = Encoding.ASCII.GetString(bytes.Slice(0, 4));
        if (!string.Equals(ident, Signature, StringComparison.Ordinal))
        {
            throw new ArchiveCorruptException("Not a valid Quake PAK archive. The PACK signature is missing.");
        }

        var directoryOffset = LittleEndianReader.ReadInt32(bytes, 4);
        var directoryLength = LittleEndianReader.ReadInt32(bytes, 8);
        var directoryEnd = (long)directoryOffset + directoryLength;
        if (directoryOffset < HeaderSize || directoryLength < 0 || directoryEnd > bytes.Length)
        {
            throw new ArchiveCorruptException("The directory table lies outside the archive bounds.");
        }

        if (directoryLength % DirectoryEntrySize != 0)
        {
            throw new ArchiveCorruptException("The PAK directory table length is invalid.");
        }

        var entryCount = directoryLength / DirectoryEntrySize;
        ArchiveSafetyLimits.EnsureEntryCount(entryCount, "The PAK archive");
        var directoryEntries = new List<PakDirectoryEntry>(entryCount);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < entryCount; index++)
        {
            var entryOffset = directoryOffset + (index * DirectoryEntrySize);
            var entryName = ReadEntryName(bytes.Slice(entryOffset, EntryNameLength));
            var normalizedPath = ValidateAndNormalizeEntryPath(entryName);

            var fileOffset = LittleEndianReader.ReadInt32(bytes, entryOffset + EntryNameLength);
            var fileLength = LittleEndianReader.ReadInt32(bytes, entryOffset + EntryNameLength + sizeof(int));
            var fileEnd = (long)fileOffset + fileLength;
            if (fileOffset < 0 || fileLength < 0 || fileEnd > bytes.Length)
            {
                throw new ArchiveCorruptException($"Entry '{normalizedPath}' has invalid data bounds.");
            }
            ArchiveSafetyLimits.EnsureFileSize(fileLength, $"PAK entry '{normalizedPath}'");

            if (fileLength > 0 && fileOffset < HeaderSize)
            {
                throw new ArchiveCorruptException($"Entry '{normalizedPath}' overlaps the PAK header.");
            }

            if (fileLength > 0 && RangesOverlap(fileOffset, fileLength, directoryOffset, directoryLength))
            {
                throw new ArchiveCorruptException($"Entry '{normalizedPath}' overlaps the directory table.");
            }

            if (!seenPaths.Add(normalizedPath))
            {
                throw new ArchiveCorruptException($"The archive contains duplicate paths: '{normalizedPath}'.");
            }

            directoryEntries.Add(new PakDirectoryEntry(normalizedPath, fileOffset, fileLength));
        }

        ValidateNoOverlaps(directoryEntries);

        var document = new ArchiveDocument
        {
            FormatId = FormatId,
        };

        try
        {
            foreach (var entry in directoryEntries)
            {
                var payload = bytes.Slice(entry.Offset, entry.Length).ToArray();
                ArchiveTreeBuilder.AddFile(document.Root, entry.Path, payload);
            }
        }
        catch (ArchiveException exception) when (exception is not ArchiveCorruptException)
        {
            throw new ArchiveCorruptException($"The archive directory is invalid: {exception.Message}");
        }

        ArchiveTreeBuilder.SortRecursively(document.Root);
        return document;
    }

    public byte[] Serialize(ArchiveDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var files = ArchiveTreeBuilder
            .FlattenFiles(document.Root)
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ArchiveSafetyLimits.EnsureEntryCount(files.Count, "The PAK archive");

        using var stream = new MemoryStream();
        stream.Write(new byte[HeaderSize]);

        var directory = new List<PakDirectoryEntry>(files.Count);
        var encodedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalPayloadSize = 0;
        foreach (var entry in files)
        {
            var relativePath = PathHelper.ToRelativeArchivePath(entry.Path);
            if (relativePath.Length == 0)
            {
                continue;
            }

            var encodedName = EncodeEntryName(relativePath);
            var storedPath = Encoding.ASCII.GetString(encodedName).TrimEnd('\0');
            if (!encodedPaths.Add(storedPath))
            {
                throw new ArchiveValidationException(
                    $"Path '{relativePath}' conflicts with another path when encoded as a PAK name.");
            }

            var data = entry.File.Data;
            ArchiveSafetyLimits.EnsureFileSize(data.LongLength, $"PAK entry '{relativePath}'");
            ArchiveSafetyLimits.EnsureTotalSize(
                totalPayloadSize,
                data.LongLength,
                "The PAK archive");
            totalPayloadSize += data.LongLength;

            var projectedSize = stream.Position + data.LongLength;
            if (projectedSize > int.MaxValue)
            {
                throw new ArchiveValidationException("The PAK archive exceeds the format's 2 GiB limit.");
            }
            var offset = checked((int)stream.Position);
            stream.Write(data, 0, data.Length);
            directory.Add(new PakDirectoryEntry(relativePath, offset, data.Length));
        }

        var directoryOffset = checked((int)stream.Position);
        var projectedFinalSize = (long)directoryOffset + ((long)directory.Count * DirectoryEntrySize);
        if (projectedFinalSize > int.MaxValue)
        {
            throw new ArchiveValidationException("The PAK archive exceeds the format's 2 GiB limit.");
        }
        foreach (var entry in directory)
        {
            stream.Write(EncodeEntryName(entry.Path));
            WriteInt32(stream, entry.Offset);
            WriteInt32(stream, entry.Length);
        }

        var directoryLength = checked((int)stream.Position - directoryOffset);

        stream.Position = 0;
        stream.Write(Encoding.ASCII.GetBytes(Signature));
        WriteInt32(stream, directoryOffset);
        WriteInt32(stream, directoryLength);

        return stream.ToArray();
    }

    private static void ValidateNoOverlaps(IEnumerable<PakDirectoryEntry> entries)
    {
        var ordered = entries.OrderBy(entry => entry.Offset).ToList();
        long previousEnd = 0;

        foreach (var entry in ordered)
        {
            if (entry.Length == 0)
            {
                continue;
            }

            if (entry.Offset < previousEnd)
            {
                throw new ArchiveCorruptException($"The archive contains overlapping data for '{entry.Path}'.");
            }

            previousEnd = (long)entry.Offset + entry.Length;
        }
    }

    private static bool RangesOverlap(int leftOffset, int leftLength, int rightOffset, int rightLength)
    {
        var leftEnd = (long)leftOffset + leftLength;
        var rightEnd = (long)rightOffset + rightLength;
        return leftOffset < rightEnd && rightOffset < leftEnd;
    }

    private static string ReadEntryName(ReadOnlySpan<byte> bytes)
    {
        var terminator = bytes.IndexOf((byte)0);
        var slice = terminator >= 0 ? bytes[..terminator] : bytes;

        foreach (var value in slice)
        {
            if (value is < 0x20 or > 0x7E)
            {
                throw new ArchiveCorruptException(
                    "The archive contains a PAK path outside printable ASCII.");
            }
        }

        return Encoding.ASCII.GetString(slice);
    }

    private static string ValidateAndNormalizeEntryPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.None);

        if (normalized.Length == 0 ||
            normalized.StartsWith('/') ||
            normalized.EndsWith('/') ||
            segments.Any(string.IsNullOrEmpty))
        {
            throw new ArchiveCorruptException($"Entry '{path}' has an unsafe archive path.");
        }

        try
        {
            foreach (var segment in segments)
            {
                ArchiveNameValidator.ValidateNodeName(segment);
            }
        }
        catch (ArchiveValidationException exception)
        {
            throw new ArchiveCorruptException($"Entry '{path}' has an unsafe archive path: {exception.Message}");
        }

        return string.Join('/', segments);
    }

    private static byte[] EncodeEntryName(string path)
    {
        var bytes = new byte[EntryNameLength];
        var encoded = new List<byte>(path.Length);

        foreach (var character in path)
        {
            if (character is < ' ' or > '~')
            {
                throw new ArchiveValidationException(
                    $"PAK path '{path}' contains characters outside printable ASCII.");
            }

            encoded.Add((byte)character);
        }

        if (encoded.Count > MaxStoredPathLength)
        {
            throw new ArchiveValidationException(
                $"PAK path '{path}' exceeds the {MaxStoredPathLength}-byte format limit.");
        }

        encoded.CopyTo(bytes, 0);
        return bytes;
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private sealed record PakDirectoryEntry(string Path, int Offset, int Length);
}
