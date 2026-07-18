using System.Buffers.Binary;

namespace PakStudio.Core.Preview;

public static class EncodedImageInspector
{
    private const int MaximumJpegHeaderBytes = 1 * 1024 * 1024;
    private const int MaximumJpegSegments = 4_096;

    public const int MaximumDimension = 8_192;
    public const int MaximumPixelCount = 16_777_216;
    public const int MaximumRenderedDimension = 2_048;

    public static bool TryGetSafeDimensions(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;

        var recognized = TryReadPng(data, out width, out height) ||
                         TryReadJpeg(data, out width, out height) ||
                         TryReadGif(data, out width, out height) ||
                         TryReadBmp(data, out width, out height) ||
                         TryReadTiff(data, out width, out height);
        return recognized && IsSafe(width, height);
    }

    private static bool TryReadPng(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (data.Length < 24 || !data[..8].SequenceEqual(signature) ||
            BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4)) != 13 ||
            !data.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return false;
        }

        var rawWidth = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
        var rawHeight = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
        if (rawWidth > int.MaxValue || rawHeight > int.MaxValue)
        {
            return false;
        }
        width = (int)rawWidth;
        height = (int)rawHeight;
        return true;
    }

    private static bool TryReadJpeg(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
        {
            return false;
        }

        var limit = Math.Min(data.Length, MaximumJpegHeaderBytes);
        var offset = 2;
        var segmentCount = 0;
        while (offset < limit && segmentCount++ < MaximumJpegSegments)
        {
            while (offset < limit && data[offset] == 0xFF)
            {
                offset++;
            }
            if (offset >= limit)
            {
                return false;
            }

            var marker = data[offset++];
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD9)
            {
                continue;
            }
            if (marker is 0xDA or 0xD9 || offset > limit - 2)
            {
                return false;
            }

            var segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
            if (segmentLength < 2 || offset > limit - segmentLength)
            {
                return false;
            }
            if (IsStartOfFrame(marker))
            {
                if (segmentLength < 7)
                {
                    return false;
                }
                height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 3, 2));
                width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 5, 2));
                return true;
            }
            offset += segmentLength;
        }
        return false;
    }

    private static bool TryReadGif(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (data.Length < 10 ||
            (!data[..6].SequenceEqual("GIF87a"u8) && !data[..6].SequenceEqual("GIF89a"u8)))
        {
            return false;
        }
        width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2));
        height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2));
        return true;
    }

    private static bool TryReadBmp(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (data.Length < 26 || data[0] != (byte)'B' || data[1] != (byte)'M')
        {
            return false;
        }

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(14, 4));
        if (headerSize == 12)
        {
            width = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(18, 2));
            height = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(20, 2));
            return true;
        }
        if (headerSize < 40)
        {
            return false;
        }

        width = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(18, 4));
        var signedHeight = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(22, 4));
        if (signedHeight == int.MinValue)
        {
            return false;
        }
        height = Math.Abs(signedHeight);
        return true;
    }

    private static bool TryReadTiff(ReadOnlySpan<byte> data, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (data.Length < 8)
        {
            return false;
        }

        var littleEndian = data[0] == (byte)'I' && data[1] == (byte)'I';
        var bigEndian = data[0] == (byte)'M' && data[1] == (byte)'M';
        if ((!littleEndian && !bigEndian) || ReadUInt16(data, 2, littleEndian) != 42)
        {
            return false;
        }

        var directoryOffset = ReadUInt32(data, 4, littleEndian);
        if (directoryOffset > int.MaxValue || directoryOffset > data.Length - 2)
        {
            return false;
        }
        var entryCount = ReadUInt16(data, (int)directoryOffset, littleEndian);
        var entriesOffset = (long)directoryOffset + 2;
        if (entriesOffset + (long)entryCount * 12 > data.Length)
        {
            return false;
        }

        for (var index = 0; index < entryCount; index++)
        {
            var entryOffset = (int)(entriesOffset + index * 12L);
            var tag = ReadUInt16(data, entryOffset, littleEndian);
            if (tag is not (256 or 257) ||
                !TryReadTiffDimension(data, entryOffset, littleEndian, out var value))
            {
                continue;
            }
            if (tag == 256)
            {
                width = value;
            }
            else
            {
                height = value;
            }
            if (width > 0 && height > 0)
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryReadTiffDimension(
        ReadOnlySpan<byte> data,
        int entryOffset,
        bool littleEndian,
        out int value)
    {
        value = 0;
        var type = ReadUInt16(data, entryOffset + 2, littleEndian);
        var count = ReadUInt32(data, entryOffset + 4, littleEndian);
        if (count != 1)
        {
            return false;
        }

        uint rawValue;
        if (type == 3)
        {
            rawValue = ReadUInt16(data, entryOffset + 8, littleEndian);
        }
        else if (type == 4)
        {
            rawValue = ReadUInt32(data, entryOffset + 8, littleEndian);
        }
        else
        {
            return false;
        }
        if (rawValue > int.MaxValue)
        {
            return false;
        }
        value = (int)rawValue;
        return true;
    }

    private static bool IsStartOfFrame(byte marker) =>
        marker is >= 0xC0 and <= 0xC3 or
            >= 0xC5 and <= 0xC7 or
            >= 0xC9 and <= 0xCB or
            >= 0xCD and <= 0xCF;

    private static bool IsSafe(int width, int height) =>
        width > 0 && height > 0 && width <= MaximumDimension && height <= MaximumDimension &&
        (long)width * height <= MaximumPixelCount;

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2))
            : BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset, bool littleEndian) =>
        littleEndian
            ? BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4))
            : BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
}
