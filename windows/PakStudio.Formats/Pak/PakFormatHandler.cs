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

        var buffer = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var document = Parse(buffer);
        document.FilePath = path;
        document.IsDirty = false;
        return document;
    }

    public async Task SaveAsync(ArchiveDocument document, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var output = Serialize(document);
        var tempPath = path + ".tmp";

        await File.WriteAllBytesAsync(tempPath, output, cancellationToken).ConfigureAwait(false);

        if (File.Exists(path))
        {
            File.Replace(tempPath, path, null, true);
        }
        else
        {
            File.Move(tempPath, path, overwrite: true);
        }

        document.FilePath = path;
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
        if (directoryOffset < 0 || directoryLength < 0 || directoryEnd > bytes.Length)
        {
            throw new ArchiveCorruptException("The directory table lies outside the archive bounds.");
        }

        if (directoryLength % DirectoryEntrySize != 0)
        {
            throw new ArchiveCorruptException("The PAK directory table length is invalid.");
        }

        var entryCount = directoryLength / DirectoryEntrySize;
        var directoryEntries = new List<PakDirectoryEntry>(entryCount);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < entryCount; index++)
        {
            var entryOffset = directoryOffset + (index * DirectoryEntrySize);
            var entryName = ReadEntryName(bytes.Slice(entryOffset, EntryNameLength));
            var normalizedPath = PathHelper.NormalizeArchivePath(entryName);

            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                throw new ArchiveCorruptException("The archive contains an empty directory entry.");
            }

            var fileOffset = LittleEndianReader.ReadInt32(bytes, entryOffset + EntryNameLength);
            var fileLength = LittleEndianReader.ReadInt32(bytes, entryOffset + EntryNameLength + sizeof(int));
            var fileEnd = (long)fileOffset + fileLength;
            if (fileOffset < 0 || fileLength < 0 || fileEnd > bytes.Length)
            {
                throw new ArchiveCorruptException($"Entry '{normalizedPath}' has invalid data bounds.");
            }

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

        foreach (var entry in directoryEntries)
        {
            var payload = bytes.Slice(entry.Offset, entry.Length).ToArray();
            ArchiveTreeBuilder.AddFile(document.Root, entry.Path, payload);
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

        using var stream = new MemoryStream();
        stream.Write(new byte[HeaderSize]);

        var directory = new List<PakDirectoryEntry>(files.Count);
        foreach (var entry in files)
        {
            var relativePath = PathHelper.ToRelativeArchivePath(entry.Path);
            if (relativePath.Length == 0)
            {
                continue;
            }

            var data = entry.File.Data ?? Array.Empty<byte>();
            var offset = checked((int)stream.Position);
            stream.Write(data, 0, data.Length);
            directory.Add(new PakDirectoryEntry(relativePath, offset, data.Length));
        }

        var directoryOffset = checked((int)stream.Position);
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
        return Encoding.ASCII.GetString(slice);
    }

    private static byte[] EncodeEntryName(string path)
    {
        var bytes = new byte[EntryNameLength];
        var encoded = new List<byte>(MaxStoredPathLength);

        foreach (var character in path)
        {
            if (encoded.Count >= MaxStoredPathLength)
            {
                break;
            }

            encoded.Add(character is >= ' ' and <= '~' ? (byte)character : (byte)'?');
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
