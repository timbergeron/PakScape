using System.Buffers.Binary;
using System.Text;

namespace PakStudio.Core.Preview;

public sealed record WadTexture(string Name, int Width, int Height, byte[] RgbaPixels);

public static class WadTextureExtractor
{
    private const int MaximumEntries = 4_096;

    public static IReadOnlyList<WadTexture> Extract(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 12 ||
            (!data.AsSpan(0, 4).SequenceEqual("WAD2"u8) &&
             !data.AsSpan(0, 4).SequenceEqual("WAD3"u8)))
        {
            throw new InvalidDataException("Only Quake WAD2 and WAD3 files are supported.");
        }

        var entryCount = ReadInt32(data, 4);
        var directoryOffset = ReadInt32(data, 8);
        if (entryCount < 0 || entryCount > MaximumEntries || directoryOffset < 0 ||
            !HasRange(data, directoryOffset, checked(entryCount * 32)))
        {
            throw new InvalidDataException("The WAD texture directory is invalid.");
        }

        var isWad3 = data.AsSpan(0, 4).SequenceEqual("WAD3"u8);
        var textures = new List<WadTexture>();
        for (var index = 0; index < entryCount; index++)
        {
            var entry = directoryOffset + index * 32;
            var lumpOffset = ReadInt32(data, entry);
            var diskSize = ReadInt32(data, entry + 4);
            var type = data[entry + 12];
            var compression = data[entry + 13];
            if (type != (byte)'D' || compression != 0 || lumpOffset < 0 || diskSize < 40 ||
                !HasRange(data, lumpOffset, diskSize))
            {
                continue;
            }

            var width = ReadInt32(data, lumpOffset + 16);
            var height = ReadInt32(data, lumpOffset + 20);
            var pixelOffset = ReadInt32(data, lumpOffset + 24);
            if (!IsSafeImageSize(width, height) || pixelOffset < 40 ||
                !HasRangeWithinLump(lumpOffset, diskSize, lumpOffset + pixelOffset, width * height))
            {
                continue;
            }

            var name = ReadName(data.AsSpan(entry + 16, 16));
            if (name.Length == 0)
            {
                name = ReadName(data.AsSpan(lumpOffset, 16));
            }
            var palette = isWad3
                ? FindWad3Palette(data, lumpOffset, diskSize, width, height)
                : QuakePreviewDecoder.PaletteBytes;
            if (palette.Length < 768)
            {
                continue;
            }

            var pixels = new byte[checked(width * height * 4)];
            var source = lumpOffset + pixelOffset;
            for (var pixel = 0; pixel < width * height; pixel++)
            {
                var paletteOffset = data[source + pixel] * 3;
                var destination = pixel * 4;
                pixels[destination] = palette[paletteOffset];
                pixels[destination + 1] = palette[paletteOffset + 1];
                pixels[destination + 2] = palette[paletteOffset + 2];
                pixels[destination + 3] = 255;
            }
            textures.Add(new WadTexture(name, width, height, pixels));
        }
        return textures;
    }

    private static ReadOnlySpan<byte> FindWad3Palette(
        byte[] data,
        int lumpOffset,
        int diskSize,
        int width,
        int height)
    {
        var lastMipOffset = ReadInt32(data, lumpOffset + 36);
        var countOffset = (long)lumpOffset + lastMipOffset + (long)width * height / 64;
        if (countOffset < 0 || countOffset > int.MaxValue ||
            !HasRangeWithinLump(lumpOffset, diskSize, (int)countOffset, 2))
        {
            return [];
        }
        var colorCount = BinaryPrimitives.ReadUInt16LittleEndian(
            data.AsSpan((int)countOffset, 2));
        var paletteOffset = (int)countOffset + 2;
        return colorCount == 256 &&
               HasRangeWithinLump(lumpOffset, diskSize, paletteOffset, 768)
            ? data.AsSpan(paletteOffset, 768)
            : [];
    }

    private static string ReadName(ReadOnlySpan<byte> bytes)
    {
        var length = bytes.IndexOf((byte)0);
        if (length < 0)
        {
            length = bytes.Length;
        }
        return Encoding.ASCII.GetString(bytes[..length]);
    }

    private static int ReadInt32(byte[] data, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));

    private static bool HasRange(byte[] data, int offset, int count) =>
        offset >= 0 && count >= 0 && offset <= data.Length - count;

    private static bool HasRangeWithinLump(int lumpOffset, int lumpSize, int offset, int count) =>
        offset >= lumpOffset && count >= 0 &&
        (long)offset + count <= (long)lumpOffset + lumpSize;

    private static bool IsSafeImageSize(int width, int height) =>
        width > 0 && height > 0 && width <= 8_192 && height <= 8_192 &&
        (long)width * height <= 16_777_216;
}
