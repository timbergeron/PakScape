using System.Text;
using PakStudio.Core.Nodes;

namespace PakStudio.Core.Preview;

public static class ArchivePreviewBuilder
{
    public const int MaximumItemCount = 1_000;
    public const long MaximumFileSize = 128L * 1024 * 1024;
    public const long MaximumSelectionSize = 256L * 1024 * 1024;
    public const int MaximumTextBytes = 2 * 1024 * 1024;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cfg", ".txt", ".log", ".md", ".json", ".xml", ".yaml", ".yml",
        ".ini", ".csv", ".qc", ".map", ".ent", ".rc", ".shader", ".def",
        ".menu", ".arena",
    };

    private static readonly HashSet<string> EncodedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff",
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".flac", ".ogg", ".opus",
        ".it", ".s3m", ".xm", ".mod", ".umx",
    };

    public static void ValidateSelection(IReadOnlyCollection<ArchiveNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Count > MaximumItemCount)
        {
            throw new ArchivePreviewException(
                $"Quick Preview supports up to {MaximumItemCount:N0} selected items at a time.");
        }

        long totalSize = 0;
        foreach (var file in nodes.OfType<ArchiveFileNode>())
        {
            if (file.Size > MaximumFileSize)
            {
                throw new ArchivePreviewException(
                    $"'{file.Name}' is larger than the {FormatSize(MaximumFileSize)} preview limit.");
            }

            if (file.Size > MaximumSelectionSize - totalSize)
            {
                throw new ArchivePreviewException(
                    $"The selection is larger than the {FormatSize(MaximumSelectionSize)} combined preview limit.");
            }
            totalSize += file.Size;
        }
    }

    public static bool SupportsAudioExtension(string extension) =>
        !string.IsNullOrWhiteSpace(extension) && AudioExtensions.Contains(extension);

    public static ArchivePreview Build(ArchiveNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (node is ArchiveFolderNode folder)
        {
            var childCount = folder.Folders.Count + folder.Files.Count;
            var childLabel = childCount == 1 ? "item" : "items";
            return new ArchivePreview(
                folder.Name,
                "Folder",
                0,
                ArchivePreviewKind.Metadata,
                Message: $"{childCount:N0} {childLabel}");
        }

        var file = (ArchiveFileNode)node;
        if (file.Size > MaximumFileSize)
        {
            throw new ArchivePreviewException(
                $"'{file.Name}' is larger than the {FormatSize(MaximumFileSize)} preview limit.");
        }

        var extension = file.Extension;
        var typeDescription = string.IsNullOrWhiteSpace(extension)
            ? "File"
            : $"{extension.TrimStart('.').ToUpperInvariant()} file";

        if (TextExtensions.Contains(extension))
        {
            var byteCount = Math.Min(file.Data.Length, MaximumTextBytes);
            var text = DecodeText(file.Data.AsSpan(0, byteCount));
            var truncated = file.Data.Length > byteCount;
            return new ArchivePreview(
                file.Name,
                typeDescription,
                file.Size,
                ArchivePreviewKind.Text,
                Text: text,
                Message: truncated ? $"Preview truncated after {FormatSize(byteCount)}." : null);
        }

        if (SupportsAudioExtension(extension))
        {
            return new ArchivePreview(
                file.Name,
                typeDescription,
                file.Size,
                ArchivePreviewKind.Audio,
                EncodedAudio: file.Data);
        }

        if (QuakePreviewDecoder.TryDecode(file.Name, file.Data, out var bitmap))
        {
            return new ArchivePreview(
                file.Name,
                typeDescription,
                file.Size,
                ArchivePreviewKind.Bitmap,
                Bitmap: bitmap);
        }

        if (EncodedImageExtensions.Contains(extension))
        {
            if (!EncodedImageInspector.TryGetSafeDimensions(file.Data, out var width, out var height))
            {
                return new ArchivePreview(
                    file.Name,
                    typeDescription,
                    file.Size,
                    ArchivePreviewKind.Metadata,
                    Message: "The image header is invalid, unsupported, or exceeds the safe preview dimensions.");
            }
            return new ArchivePreview(
                file.Name,
                typeDescription,
                file.Size,
                ArchivePreviewKind.EncodedImage,
                EncodedImage: file.Data,
                ImageWidth: width,
                ImageHeight: height);
        }

        return new ArchivePreview(
            file.Name,
            typeDescription,
            file.Size,
            ArchivePreviewKind.Metadata,
            Message: "No rich preview is available for this file type.");
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }

    private static string DecodeText(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(data[2..]);
        }
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(data[2..]);
        }
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            data = data[3..];
        }
        return Encoding.UTF8.GetString(data);
    }
}
