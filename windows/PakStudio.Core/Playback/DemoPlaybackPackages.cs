using System.IO.Compression;
using PakStudio.Core.Documents;
using PakStudio.Core.Nodes;
using PakStudio.Core.Validation;

namespace PakStudio.Core.Playback;

/// <summary>Builds a bounded player package from the current archive, including unsaved edits.</summary>
public static class DemoPlaybackPackages
{
    public static IReadOnlyList<DemoPlaybackAsset> Build(
        ArchiveDocument? document,
        IEnumerable<string> maps,
        int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(maps);
        var wanted = new HashSet<string>(maps.Where(map => map.Length > 0), StringComparer.OrdinalIgnoreCase);
        if (document is null || wanted.Count == 0 || !ContainsMap(document.Root, wanted))
        {
            return [];
        }

        var entries = new List<(string Path, ArchiveFileNode? File)>();
        long payloadSize = 0;
        Collect(document.Root, string.Empty, 1, entries, ref payloadSize, maximumBytes);

        using var stream = new LimitedMemoryStream(maximumBytes);
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, file) in entries)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
                if (file is not null)
                {
                    using var output = entry.Open();
                    output.Write(file.Data);
                }
            }
        }

        // A ZIP package supports paths from PAK, PK3, and KPF documents alike.
        var name = Path.GetFileNameWithoutExtension(document.DisplayName) + ".pk3";
        return [new DemoPlaybackAsset(name, stream.ToArray())];
    }

    private static bool ContainsMap(ArchiveFolderNode folder, HashSet<string> wanted) =>
        folder.Files.Any(file =>
            file.Extension.Equals(".bsp", StringComparison.OrdinalIgnoreCase) &&
            wanted.Contains(Path.GetFileNameWithoutExtension(file.Name))) ||
        folder.Folders.Any(child => ContainsMap(child, wanted));

    private static void Collect(
        ArchiveFolderNode folder,
        string parentPath,
        int depth,
        List<(string Path, ArchiveFileNode? File)> entries,
        ref long payloadSize,
        int maximumBytes)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in folder.Children)
        {
            ArchiveNameValidator.ValidateNodeName(node.Name);
            ArchiveSafetyLimits.EnsurePathDepth(depth, $"'{node.Name}'");
            if (!names.Add(node.Name))
            {
                throw new ArchivePathConflictException($"An item named '{node.Name}' already exists in this folder.");
            }
            var path = parentPath + node.Name;
            if (node is ArchiveFileNode file)
            {
                ArchiveSafetyLimits.EnsureFileSize(file.Size, $"'{path}'");
                if (file.Size > maximumBytes - payloadSize)
                {
                    throw TooLarge();
                }
                payloadSize += file.Size;
                entries.Add((path, file));
            }
            else
            {
                entries.Add((path + "/", null));
            }
            ArchiveSafetyLimits.EnsureEntryCount(entries.Count, "The playback archive");
            if (node is ArchiveFolderNode child)
            {
                Collect(child, path + "/", depth + 1, entries, ref payloadSize, maximumBytes);
            }
        }
    }

    private static DemoPlaybackException TooLarge() => new(
        $"This demo and its archive are larger than the {DemoPlaybackHandoff.MaximumSessionBytes / (1024 * 1024)} MB playback limit.");

    private sealed class LimitedMemoryStream(int maximumBytes) : MemoryStream
    {
        private void EnsureFits(long length)
        {
            if (length > maximumBytes)
            {
                throw TooLarge();
            }
            if (length > Capacity)
            {
                Capacity = (int)Math.Min(maximumBytes, Math.Max(length, Math.Max(256L, Capacity * 2L)));
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureFits(Position + count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureFits(Position + buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            EnsureFits(Position + 1);
            base.WriteByte(value);
        }

        public override void SetLength(long value)
        {
            EnsureFits(value);
            base.SetLength(value);
        }
    }
}
