using System.Text;
using PakStudio.Core.Models;
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
        ".menu", ".arena", ".h", ".c", ".cc", ".cpp", ".hpp", ".cs", ".js",
        ".ts", ".css", ".html", ".htm", ".bat", ".cmd", ".scr", ".skin",
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

    /*
     * The model formats QSS-M loads. MDL, SPR, and BSP keep their flat preview as a
     * fallback, which is what a file the viewer cannot parse falls back to. BSP is
     * conditional: only a brush model goes to the viewer, never a playable level.
     */
    private static readonly HashSet<string> ModelExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mdl", ".md3", ".md5mesh", ".md5", ".spr", ".spr32", ".bsp",
    };

    private static readonly HashSet<string> QuickPreviewOnOpenExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".lmp",
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

    public static bool SupportsTextExtension(string extension) =>
        !string.IsNullOrWhiteSpace(extension) && TextExtensions.Contains(extension);

    public static bool SupportsModelExtension(string extension) =>
        !string.IsNullOrWhiteSpace(extension) && ModelExtensions.Contains(extension);

    /// <summary>
    /// True when a node opens in the interactive model viewer, which is what
    /// double-clicking it does instead of handing it to another application.
    /// </summary>
    public static bool SupportsInteractiveModel(ArchiveNode? node) =>
        node is ArchiveFileNode file &&
        file.Size > 0 &&
        file.Size <= MaximumFileSize &&
        SupportsModelExtension(file.Extension) &&
        NativeModelViewer.SupportsExtension(file.Extension) &&
        /* A .bsp is ours only when it holds a brush model rather than a level. */
        (!file.Extension.Equals(".bsp", StringComparison.OrdinalIgnoreCase) ||
            NativeModelViewer.IsBspBrushModel(file.Data));

    /// <summary>
    /// True when opening a node should keep it inside Quick Preview instead of
    /// handing it to an external application.
    /// </summary>
    public static bool OpensInQuickPreview(ArchiveNode? node) =>
        SupportsInteractiveModel(node) ||
        node is ArchiveFileNode { Size: <= MaximumFileSize } file &&
        QuickPreviewOnOpenExtensions.Contains(file.Extension);

    /// <summary>
    /// Builds a preview. Generic bitmap fallbacks pass
    /// <paramref name="includeInteractiveModels"/> as false so that a model is
    /// decoded to its flat skin instead of the interactive viewer. Platform thumbnail
    /// services may render a deterministic native model frame before using this path.
    /// </summary>
    public static ArchivePreview Build(
        ArchiveNode node,
        bool includeInteractiveModels = true,
        BspLevelPreviewOptions bspOptions = default)
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

        if (includeInteractiveModels && SupportsInteractiveModel(file))
        {
            return new ArchivePreview(
                file.Name,
                typeDescription,
                file.Size,
                ArchivePreviewKind.Model,
                Model: new PreviewModel(file.Data, extension, new ModelTextureResolver(file)));
        }

        if (QuakePreviewDecoder.TryDecode(file.Name, file.Data, out var bitmap, bspOptions))
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
