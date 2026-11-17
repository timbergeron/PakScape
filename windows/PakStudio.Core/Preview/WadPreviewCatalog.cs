using System.Buffers.Binary;
using System.Text;

namespace PakStudio.Core.Preview;

/// <summary>
/// One directory entry of a WAD, with a downscaled thumbnail when the lump holds an image.
/// </summary>
public sealed record WadPreviewItem(
    string Name,
    string TypeDescription,
    int Width,
    int Height,
    PreviewBitmap? Thumbnail);

/// <summary>
/// Lists every entry of a WAD2 or WAD3 file for the Quick Preview gallery. Thumbnails are
/// sampled straight from the palette indices so a large texture WAD never decodes its
/// full-size images.
/// </summary>
public static class WadPreviewCatalog
{
    public const int MaximumThumbnailDimension = 128;
    private const int MaximumEntries = 4_096;
    private const int MaximumDimension = 8_192;
    private const int MaximumPixelCount = 16_777_216;

    public static bool SupportsExtension(string extension) =>
        extension.Equals(".wad", StringComparison.OrdinalIgnoreCase);

    public static bool TryRead(byte[] data, out IReadOnlyList<WadPreviewItem> items)
    {
        ArgumentNullException.ThrowIfNull(data);
        items = [];
        if (data.Length < 12)
        {
            return false;
        }

        var isWad2 = data.AsSpan(0, 4).SequenceEqual("WAD2"u8);
        var isWad3 = data.AsSpan(0, 4).SequenceEqual("WAD3"u8);
        if (!isWad2 && !isWad3)
        {
            return false;
        }

        var entryCount = ReadInt32(data, 4);
        var directoryOffset = ReadInt32(data, 8);
        if (entryCount <= 0 || entryCount > MaximumEntries || directoryOffset < 0 ||
            !HasRange(data, directoryOffset, entryCount * 32))
        {
            return false;
        }

        var list = new List<WadPreviewItem>(entryCount);
        for (var index = 0; index < entryCount; index++)
        {
            var entry = directoryOffset + index * 32;
            var lumpOffset = ReadInt32(data, entry);
            var diskSize = ReadInt32(data, entry + 4);
            var type = data[entry + 12];
            var compression = data[entry + 13];
            var name = ReadName(data.AsSpan(entry + 16, 16));
            if (name.Length == 0)
            {
                name = $"(unnamed {index + 1})";
            }

            var lumpIsValid = lumpOffset >= 0 && diskSize > 0 && HasRange(data, lumpOffset, diskSize);
            var image = lumpIsValid && compression == 0
                ? TryDecodeThumbnail(data, name, type, lumpOffset, diskSize, isWad3)
                : null;
            list.Add(new WadPreviewItem(
                name,
                DescribeType(type, compression),
                image?.Width ?? 0,
                image?.Height ?? 0,
                image?.Thumbnail));
        }

        items = list;
        return true;
    }

    private readonly record struct DecodedImage(int Width, int Height, PreviewBitmap Thumbnail);

    private static DecodedImage? TryDecodeThumbnail(
        byte[] data,
        string name,
        byte type,
        int lumpOffset,
        int diskSize,
        bool isWad3)
    {
        var quakePalette = QuakePreviewDecoder.PaletteBytes;

        /* Quake's gfx.wad stores conchars and conback as raw pixels regardless of type. */
        if (!isWad3 &&
            (name.Equals("conchars", StringComparison.OrdinalIgnoreCase) ||
             (type == (byte)'E' && diskSize == 128 * 128)) &&
            diskSize >= 128 * 128)
        {
            return Sample(data, lumpOffset, 128, 128, quakePalette, transparentIndex: 0);
        }
        if (!isWad3 &&
            (name.Equals("conback", StringComparison.OrdinalIgnoreCase) ||
             (type == (byte)'E' && diskSize == 320 * 200)) &&
            diskSize >= 320 * 200)
        {
            return Sample(data, lumpOffset, 320, 200, quakePalette, transparentIndex: null);
        }

        switch (type)
        {
            case (byte)'@' when diskSize >= 768:
                return PaletteSwatch(data.AsSpan(lumpOffset, 768));

            case (byte)'B':
            {
                if (diskSize < 8)
                {
                    return null;
                }
                var width = ReadInt32(data, lumpOffset);
                var height = ReadInt32(data, lumpOffset + 4);
                var pixelOffset = lumpOffset + 8;
                if (!IsSafeImageSize(width, height) ||
                    !HasRangeWithinLump(lumpOffset, diskSize, pixelOffset, (long)width * height))
                {
                    return null;
                }
                var palette = isWad3
                    ? FindTrailingPalette(data, lumpOffset, diskSize, pixelOffset + (long)width * height)
                    : quakePalette;
                return palette.Length < 768
                    ? null
                    : Sample(data, pixelOffset, width, height, palette, transparentIndex: 255);
            }

            case (byte)'C' or (byte)'D':
            {
                if (diskSize < 40)
                {
                    return null;
                }
                var width = ReadInt32(data, lumpOffset + 16);
                var height = ReadInt32(data, lumpOffset + 20);
                var mipOffset = ReadInt32(data, lumpOffset + 24);
                var pixelOffset = (long)lumpOffset + mipOffset;
                if (!IsSafeImageSize(width, height) || mipOffset < 40 ||
                    !HasRangeWithinLump(lumpOffset, diskSize, pixelOffset, (long)width * height))
                {
                    return null;
                }

                var palette = quakePalette;
                if (isWad3)
                {
                    var lastMipOffset = ReadInt32(data, lumpOffset + 36);
                    var lastMipEnd = (long)lumpOffset + lastMipOffset + (long)width * height / 64;
                    palette = FindTrailingPalette(data, lumpOffset, diskSize, lastMipEnd);
                    if (palette.Length < 768)
                    {
                        return null;
                    }
                }

                /* Half-Life's '{' textures are alpha-tested on the last palette entry. */
                int? transparentIndex = isWad3 && name.StartsWith('{') ? 255 : null;
                return Sample(data, (int)pixelOffset, width, height, palette, transparentIndex);
            }

            case (byte)'F' when isWad3:
            {
                /* Half-Life font: width, height, row count, row height, 256 char infos. */
                const int headerSize = 16 + 256 * 4;
                if (diskSize < headerSize)
                {
                    return null;
                }
                var width = ReadInt32(data, lumpOffset);
                var height = ReadInt32(data, lumpOffset + 4);
                var pixelOffset = lumpOffset + headerSize;
                if (!IsSafeImageSize(width, height) ||
                    !HasRangeWithinLump(lumpOffset, diskSize, pixelOffset, (long)width * height))
                {
                    return null;
                }
                var palette = FindTrailingPalette(
                    data,
                    lumpOffset,
                    diskSize,
                    pixelOffset + (long)width * height);
                return palette.Length < 768
                    ? null
                    : Sample(data, pixelOffset, width, height, palette, transparentIndex: 255);
            }

            default:
                return null;
        }
    }

    /// <summary>WAD3 images end with a 16-bit color count followed by an RGB palette.</summary>
    private static ReadOnlySpan<byte> FindTrailingPalette(
        byte[] data,
        int lumpOffset,
        int diskSize,
        long countOffset)
    {
        if (!HasRangeWithinLump(lumpOffset, diskSize, countOffset, 2))
        {
            return [];
        }
        var colorCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)countOffset, 2));
        var paletteOffset = countOffset + 2;
        return colorCount == 256 && HasRangeWithinLump(lumpOffset, diskSize, paletteOffset, 768)
            ? data.AsSpan((int)paletteOffset, 768)
            : [];
    }

    private static DecodedImage PaletteSwatch(ReadOnlySpan<byte> palette)
    {
        const int cell = 8;
        const int size = 16 * cell;
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var source = ((y / cell) * 16 + x / cell) * 3;
                var destination = (y * size + x) * 4;
                pixels[destination] = palette[source + 2];
                pixels[destination + 1] = palette[source + 1];
                pixels[destination + 2] = palette[source];
                pixels[destination + 3] = 255;
            }
        }
        return new DecodedImage(16, 16, new PreviewBitmap(size, size, pixels));
    }

    /// <summary>Nearest-neighbour samples paletted pixels into a thumbnail no larger than the cap.</summary>
    private static DecodedImage Sample(
        byte[] data,
        int pixelOffset,
        int width,
        int height,
        ReadOnlySpan<byte> palette,
        int? transparentIndex)
    {
        var scale = Math.Min(1.0, (double)MaximumThumbnailDimension / Math.Max(width, height));
        var thumbnailWidth = Math.Max(1, (int)Math.Round(width * scale));
        var thumbnailHeight = Math.Max(1, (int)Math.Round(height * scale));
        var pixels = new byte[thumbnailWidth * thumbnailHeight * 4];
        for (var y = 0; y < thumbnailHeight; y++)
        {
            var sourceRow = pixelOffset + (int)((long)y * height / thumbnailHeight) * width;
            for (var x = 0; x < thumbnailWidth; x++)
            {
                var paletteIndex = data[sourceRow + (int)((long)x * width / thumbnailWidth)];
                var source = paletteIndex * 3;
                var destination = (y * thumbnailWidth + x) * 4;
                pixels[destination] = palette[source + 2];
                pixels[destination + 1] = palette[source + 1];
                pixels[destination + 2] = palette[source];
                pixels[destination + 3] = paletteIndex == transparentIndex ? (byte)0 : (byte)255;
            }
        }
        return new DecodedImage(width, height, new PreviewBitmap(thumbnailWidth, thumbnailHeight, pixels));
    }

    private static string DescribeType(byte type, byte compression)
    {
        var description = type switch
        {
            (byte)'@' => "Palette",
            (byte)'B' => "Picture",
            (byte)'C' or (byte)'D' => "Mip texture",
            (byte)'E' => "Console picture",
            (byte)'F' => "Font",
            _ => $"Type 0x{type:X2}",
        };
        return compression == 0 ? description : $"{description} (compressed)";
    }

    private static string ReadName(ReadOnlySpan<byte> bytes)
    {
        var length = bytes.IndexOf((byte)0);
        if (length < 0)
        {
            length = bytes.Length;
        }
        return Encoding.ASCII.GetString(bytes[..length]).Trim();
    }

    private static int ReadInt32(byte[] data, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));

    private static bool HasRange(byte[] data, int offset, int count) =>
        offset >= 0 && count >= 0 && offset <= data.Length - count;

    private static bool HasRangeWithinLump(int lumpOffset, int lumpSize, long offset, long count) =>
        offset >= lumpOffset && count >= 0 && offset <= int.MaxValue &&
        offset + count <= (long)lumpOffset + lumpSize;

    private static bool IsSafeImageSize(int width, int height) =>
        width > 0 && height > 0 && width <= MaximumDimension && height <= MaximumDimension &&
        (long)width * height <= MaximumPixelCount;
}
