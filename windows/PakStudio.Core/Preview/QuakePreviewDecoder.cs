using System.Buffers.Binary;

namespace PakStudio.Core.Preview;

internal static class QuakePreviewDecoder
{
    private const int MaximumDimension = 8_192;
    private const int MaximumPixelCount = 16_777_216;
    private const int MaximumGeometryElements = 1_000_000;

    private static readonly byte[] Palette = Convert.FromBase64String(
        "AAAADw8PHx8fLy8vPz8/S0tLW1tba2tre3t7i4uLm5ubq6uru7u7y8vL29vb6+vrDwsHFw8LHxcLJxsPLyMTNysXPy8XSzcbUzsbW0MfY0sfa1Mfc1cfe18jg2cjj28jCwsPExMbGxsnJyczLy8/NzdLPz9XR0dnT09zW1t/Y2OLa2uXc3Oje3uvg4O7i4vLAAAABwcACwsAExMAGxsAIyMAKysHLy8HNzcHPz8HR0cHS0sLU1MLW1sLY2MLa2sPBwAADwAAFwAAHwAAJwAALwAANwAAPwAARwAATwAAVwAAXwAAZwAAbwAAdwAAfwAAExMAGxsAIyMALysANy8AQzcASzsHV0MHX0cHa0sLd1MPg1cTi1sTl18bo2Mfr2cjIxMHLxcLOx8PSyMTVysXYy8fczcjfzsrj0Mzn08zr2Mvv3cvz48r36sn78sf//MbCwcAGxMAKyMPNysTRzMbUzcjYz8rb0czf1M/i19Hm2tTp3tft4drw5N706OL47OXq4ujn3+Xk3OHi2d7f1tvd1Nja0tXXz9LVzdDSy83QycvNx8jKxcbIxMTFwsLDwcHu3Ofr2uPo1+Dl1d3i09rf0tfc0NTaztLXzM/Uys3RyMrOx8jLxcbIxMTFwsLDwcH28O7y7Onv6Obr5eLo4d7l3tvh29fe2NTa1dHX0s7Uz8zQzMnNysfJx8XGxMPDwsHb4N7Z3tvX3NnV2tfT2NXR1tPP1NHN0s/L0M3KzsvIzMnHysfFyMXDxsTCxMLBwsH//Mb798X28sTy7cPu6cPq5cLm4MHi3MHe2MHa1MAW0cASzcAOysAKx8AGw8ACwcAAAD/CwvvExPfGxvPIyO/KyuvLy+fLy+PLy9/Ly9vLy9fKytPIyM/GxsvExMfCwsPKwAAOwAASwcAXwcAbw8AfxcHkx8HoycLtzMPw0sbz2Mr238745dP56tf779399OLp3s7t5s3x8M35+NXf7//q+f/1///ZwAAiwAAswAA1wAA/wAA//OT//fH////n1tT");

    public static bool TryDecode(string fileName, byte[] data, out PreviewBitmap bitmap)
    {
        bitmap = null!;
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".lmp" => TryDecodeLmp(fileName, data, out bitmap),
            ".mdl" => TryDecodeMdl(data, out bitmap),
            ".spr" => TryDecodeSpr(data, out bitmap),
            ".pcx" => TryDecodePcx(data, out bitmap),
            ".tga" => TryDecodeTga(data, out bitmap),
            ".bsp" => TryDecodeBsp(data, out bitmap),
            ".wad" when fileName.Equals("gfx.wad", StringComparison.OrdinalIgnoreCase) =>
                TryDecodeWad(data, out bitmap),
            _ => false,
        };
    }

    private static bool TryDecodeLmp(string fileName, byte[] data, out PreviewBitmap bitmap)
    {
        bitmap = null!;
        var lowerName = Path.GetFileName(fileName).ToLowerInvariant();
        var width = 0;
        var height = 0;
        var offset = 8;
        int? transparentIndex = 255;

        if (lowerName is "conchars" or "conchars.lmp")
        {
            width = 128;
            height = 128;
            offset = 0;
            transparentIndex = 0;
        }
        else if (lowerName == "pop.lmp")
        {
            width = 16;
            height = 16;
            offset = 0;
        }
        else if (lowerName == "colormap.lmp")
        {
            width = 256;
            height = 64;
            offset = 0;
        }
        else if (data.Length == 768)
        {
            var pixels = new byte[16 * 16 * 4];
            for (var index = 0; index < 256; index++)
            {
                var source = index * 3;
                var destination = index * 4;
                pixels[destination] = data[source + 2];
                pixels[destination + 1] = data[source + 1];
                pixels[destination + 2] = data[source];
                pixels[destination + 3] = 255;
            }
            bitmap = new PreviewBitmap(16, 16, pixels);
            return true;
        }
        else if (!TryReadInt32(data, 0, out width) || !TryReadInt32(data, 4, out height))
        {
            return false;
        }

        return TryCreatePalettedBitmap(data, offset, width, height, transparentIndex, out bitmap);
    }

    private static bool TryDecodeMdl(byte[] data, out PreviewBitmap bitmap)
    {
        bitmap = null!;
        if (data.Length < 84 ||
            !TryReadInt32(data, 48, out var skinCount) || skinCount <= 0 ||
            !TryReadInt32(data, 52, out var width) ||
            !TryReadInt32(data, 56, out var height) ||
            !IsSafeImageSize(width, height))
        {
            return false;
        }

        var skinBytes = checked(width * height);
        var cursor = 84;
        if (!TryReadInt32(data, cursor, out var group))
        {
            return false;
        }
        cursor += 4;
        if (group is not (0 or 1))
        {
            cursor -= 4;
            group = 0;
        }

        if (group == 1)
        {
            if (!TryReadInt32(data, cursor, out var groupCount) || groupCount <= 0)
            {
                return false;
            }
            cursor += 4;
            var intervalBytes = (long)groupCount * 4;
            if (intervalBytes > int.MaxValue || !HasRange(data, cursor, (int)intervalBytes))
            {
                return false;
            }
            cursor += (int)intervalBytes;
        }

        return HasRange(data, cursor, skinBytes) &&
               TryCreatePalettedBitmap(data, cursor, width, height, 255, out bitmap);
    }

    private static bool TryDecodeSpr(byte[] data, out PreviewBitmap bitmap)
    {
        bitmap = null!;
        if (data.Length < 36 ||
            !TryReadInt32(data, 16, out var defaultWidth) ||
            !TryReadInt32(data, 20, out var defaultHeight) ||
            !TryReadInt32(data, 24, out var frameCount) || frameCount <= 0 ||
            !IsSafeImageSize(defaultWidth, defaultHeight))
        {
            return false;
        }

        var cursor = 36;
        if (!TryReadInt32(data, cursor, out var frameType))
        {
            return false;
        }
        cursor += 4;
        if (frameType is not (0 or 1))
        {
            cursor -= 4;
            frameType = 0;
        }
        if (frameType == 1)
        {
            if (!TryReadInt32(data, cursor, out var groupCount) || groupCount <= 0)
            {
                return false;
            }
            cursor += 4;
            var intervalBytes = (long)groupCount * 4;
            if (intervalBytes > int.MaxValue || !HasRange(data, cursor, (int)intervalBytes))
            {
                return false;
            }
            cursor += (int)intervalBytes;
        }

        if (!HasRange(data, cursor, 16) ||
            !TryReadInt32(data, cursor + 8, out var width) ||
            !TryReadInt32(data, cursor + 12, out var height) ||
            !IsSafeImageSize(width, height))
        {
            return false;
        }
        cursor += 16;
        return TryCreatePalettedBitmap(data, cursor, width, height, null, out bitmap);
    }

    private static bool TryDecodePcx(byte[] data, out PreviewBitmap bitmap)
    {
        bitmap = null!;
        if (data.Length < 128 || data[0] != 0x0A || data[2] is not (0 or 1))
        {
            return false;
        }

        var bitsPerPixel = data[3];
        var width = ReadUInt16(data, 8) - ReadUInt16(data, 4) + 1;
        var height = ReadUInt16(data, 10) - ReadUInt16(data, 6) + 1;
        var planes = data[65];
        var bytesPerLine = ReadUInt16(data, 66);
        if (!IsSafeImageSize(width, height) || bitsPerPixel != 8 ||
            planes is not (1 or 3 or 4) || bytesPerLine < width)
        {
            return false;
        }

        var rowStrideLong = (long)planes * bytesPerLine;
        var decodedSizeLong = rowStrideLong * height;
        if (decodedSizeLong <= 0 || decodedSizeLong > MaximumPixelCount * 4L)
        {
            return false;
        }

        var sourceLimit = data.Length;
        var paletteOffset = -1;
        if (planes == 1 && data.Length >= 897 && data[^769] == 0x0C)
        {
            sourceLimit = data.Length - 769;
            paletteOffset = sourceLimit + 1;
        }
        if (planes == 1 && paletteOffset < 0)
        {
            return false;
        }

        if (!TryDecodePcxBytes(data, 128, sourceLimit, (int)decodedSizeLong, data[2] == 1, out var decoded))
        {
            return false;
        }

        var pixels = new byte[checked(width * height * 4)];
        var rowStride = (int)rowStrideLong;
        for (var y = 0; y < height; y++)
        {
            var row = y * rowStride;
            for (var x = 0; x < width; x++)
            {
                var destination = (y * width + x) * 4;
                if (planes == 1)
                {
                    var color = paletteOffset + decoded[row + x] * 3;
                    pixels[destination] = data[color + 2];
                    pixels[destination + 1] = data[color + 1];
                    pixels[destination + 2] = data[color];
                    pixels[destination + 3] = 255;
                }
                else
                {
                    pixels[destination] = decoded[row + bytesPerLine * 2 + x];
                    pixels[destination + 1] = decoded[row + bytesPerLine + x];
                    pixels[destination + 2] = decoded[row + x];
                    pixels[destination + 3] = planes == 4
                        ? decoded[row + bytesPerLine * 3 + x]
                        : (byte)255;
                }
            }
        }

        bitmap = new PreviewBitmap(width, height, pixels);
        return true;
    }

    private static bool TryDecodePcxBytes(
        byte[] data,
        int start,
        int limit,
        int expectedSize,
        bool isRle,
        out byte[] decoded)
    {
        decoded = new byte[expectedSize];
        if (!isRle)
        {
            if (limit - start < expectedSize)
            {
                return false;
            }
            data.AsSpan(start, expectedSize).CopyTo(decoded);
            return true;
        }

        var source = start;
        var destination = 0;
        while (destination < expectedSize)
        {
            if (source >= limit)
            {
                return false;
            }
            var value = data[source++];
            if ((value & 0xC0) == 0xC0)
            {
                var count = value & 0x3F;
                if (count == 0 || source >= limit || destination > expectedSize - count)
                {
                    return false;
                }
                Array.Fill(decoded, data[source++], destination, count);
                destination += count;
            }
            else
            {
                decoded[destination++] = value;
            }
        }
        return true;
    }

    private static bool TryDecodeTga(byte[] data, out PreviewBitmap bitmap)
    {
        bitmap = null!;
        if (data.Length < 18 || data[1] != 0)
        {
            return false;
        }

        var imageType = data[2];
        var isGray = imageType is 3 or 11;
        var isRle = imageType is 10 or 11;
        if (imageType is not (2 or 3 or 10 or 11))
        {
            return false;
        }

        var width = ReadUInt16(data, 12);
        var height = ReadUInt16(data, 14);
        var depth = data[16];
        var descriptor = data[17];
        if (!IsSafeImageSize(width, height))
        {
            return false;
        }

        var bytesPerPixel = (isGray, depth) switch
        {
            (true, 8) => 1,
            (true, 16) => 2,
            (false, 16) => 2,
            (false, 24) => 3,
            (false, 32) => 4,
            _ => 0,
        };
        if (bytesPerPixel == 0)
        {
            return false;
        }

        var source = 18 + data[0];
        if (source > data.Length)
        {
            return false;
        }

        var pixelCount = checked(width * height);
        var pixels = new byte[checked(pixelCount * 4)];
        var pixelIndex = 0;
        while (pixelIndex < pixelCount)
        {
            var runCount = 1;
            var repeated = false;
            if (isRle)
            {
                if (source >= data.Length)
                {
                    return false;
                }
                var packet = data[source++];
                runCount = (packet & 0x7F) + 1;
                repeated = (packet & 0x80) != 0;
                if (runCount > pixelCount - pixelIndex)
                {
                    return false;
                }
            }

            if (repeated)
            {
                if (!TryReadTgaPixel(data, source, bytesPerPixel, isGray, descriptor, out var color))
                {
                    return false;
                }
                source += bytesPerPixel;
                for (var index = 0; index < runCount; index++)
                {
                    WriteTgaPixel(pixels, width, height, pixelIndex++, descriptor, color);
                }
            }
            else
            {
                for (var index = 0; index < runCount; index++)
                {
                    if (!TryReadTgaPixel(data, source, bytesPerPixel, isGray, descriptor, out var color))
                    {
                        return false;
                    }
                    source += bytesPerPixel;
                    WriteTgaPixel(pixels, width, height, pixelIndex++, descriptor, color);
                }
            }
        }

        bitmap = new PreviewBitmap(width, height, pixels);
        return true;
    }

    private static bool TryReadTgaPixel(
        byte[] data,
        int offset,
        int bytesPerPixel,
        bool grayscale,
        byte descriptor,
        out (byte B, byte G, byte R, byte A) color)
    {
        color = default;
        if (!HasRange(data, offset, bytesPerPixel))
        {
            return false;
        }
        if (grayscale)
        {
            color = (data[offset], data[offset], data[offset],
                bytesPerPixel == 2 ? data[offset + 1] : (byte)255);
            return true;
        }
        if (bytesPerPixel == 2)
        {
            var raw = ReadUInt16(data, offset);
            var blue = (byte)(((raw & 0x1F) << 3) | ((raw & 0x1F) >> 2));
            var greenBits = (raw >> 5) & 0x1F;
            var green = (byte)((greenBits << 3) | (greenBits >> 2));
            var redBits = (raw >> 10) & 0x1F;
            var red = (byte)((redBits << 3) | (redBits >> 2));
            var alpha = (descriptor & 0x0F) > 0 && (raw & 0x8000) == 0 ? (byte)0 : (byte)255;
            color = (blue, green, red, alpha);
            return true;
        }
        color = (data[offset], data[offset + 1], data[offset + 2],
            bytesPerPixel == 4 ? data[offset + 3] : (byte)255);
        return true;
    }

    private static void WriteTgaPixel(
        byte[] pixels,
        int width,
        int height,
        int sourceIndex,
        byte descriptor,
        (byte B, byte G, byte R, byte A) color)
    {
        var x = sourceIndex % width;
        var y = sourceIndex / width;
        if ((descriptor & 0x10) != 0)
        {
            x = width - 1 - x;
        }
        if ((descriptor & 0x20) == 0)
        {
            y = height - 1 - y;
        }
        var destination = (y * width + x) * 4;
        pixels[destination] = color.B;
        pixels[destination + 1] = color.G;
        pixels[destination + 2] = color.R;
        pixels[destination + 3] = color.A;
    }

    private static bool TryDecodeBsp(byte[] data, out PreviewBitmap bitmap)
    {
        bitmap = null!;
        const int lumpCount = 15;
        if (data.Length < 4 + lumpCount * 8 ||
            !TryReadInt32(data, 0, out var version) || version is not (29 or 30) ||
            !TryReadLump(data, 3, out var vertexOffset, out var vertexLength) ||
            !TryReadLump(data, 7, out var faceOffset, out var faceLength) ||
            !TryReadLump(data, 12, out var edgeOffset, out var edgeLength) ||
            !TryReadLump(data, 13, out var surfEdgeOffset, out var surfEdgeLength))
        {
            return false;
        }

        var vertexCount = vertexLength / 12;
        var edgeCount = edgeLength / 4;
        var surfEdgeCount = surfEdgeLength / 4;
        var faceCount = faceLength / 20;
        if (vertexCount <= 1 || edgeCount <= 0 || surfEdgeCount <= 0 || faceCount <= 0 ||
            vertexCount > MaximumGeometryElements || edgeCount > MaximumGeometryElements ||
            surfEdgeCount > MaximumGeometryElements || faceCount > MaximumGeometryElements)
        {
            return false;
        }

        var vertices = new (float X, float Y)[vertexCount];
        var minX = float.PositiveInfinity;
        var minY = float.PositiveInfinity;
        var maxX = float.NegativeInfinity;
        var maxY = float.NegativeInfinity;
        for (var index = 0; index < vertexCount; index++)
        {
            var offset = vertexOffset + index * 12;
            if (!TryReadSingle(data, offset, out var x) || !TryReadSingle(data, offset + 4, out var y) ||
                !float.IsFinite(x) || !float.IsFinite(y))
            {
                return false;
            }
            vertices[index] = (x, y);
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }
        var rangeX = (double)maxX - minX;
        var rangeY = (double)maxY - minY;
        if (!double.IsFinite(rangeX) || !double.IsFinite(rangeY) || rangeX <= 0 || rangeY <= 0)
        {
            return false;
        }

        const int size = 768;
        const int padding = 24;
        var pixels = new byte[size * size * 4];
        Fill(pixels, 24, 27, 32, 255);
        var scale = Math.Min(
            (size - padding * 2) / rangeX,
            (size - padding * 2) / rangeY);

        var linesDrawn = 0;
        const int maximumLines = 50_000;
        for (var face = 0; face < faceCount && linesDrawn < maximumLines; face++)
        {
            var offset = faceOffset + face * 20;
            if (!TryReadInt32(data, offset + 4, out var firstEdge))
            {
                continue;
            }
            var numberOfEdges = ReadUInt16(data, offset + 8);
            if (numberOfEdges < 2 || firstEdge < 0 || firstEdge > surfEdgeCount - numberOfEdges)
            {
                continue;
            }

            for (var localEdge = 0; localEdge < numberOfEdges && linesDrawn < maximumLines; localEdge++)
            {
                if (!TryReadInt32(data, surfEdgeOffset + (firstEdge + localEdge) * 4, out var signedEdge))
                {
                    break;
                }
                if (signedEdge == int.MinValue)
                {
                    continue;
                }
                var edgeIndex = Math.Abs(signedEdge);
                if (edgeIndex >= edgeCount)
                {
                    continue;
                }
                var edgeBase = edgeOffset + edgeIndex * 4;
                var firstVertex = ReadUInt16(data, edgeBase);
                var secondVertex = ReadUInt16(data, edgeBase + 2);
                if (firstVertex >= vertexCount || secondVertex >= vertexCount)
                {
                    continue;
                }
                DrawMapLine(pixels, size, vertices[firstVertex], vertices[secondVertex],
                    minX, maxY, scale, padding);
                linesDrawn++;
            }
        }
        if (linesDrawn == 0)
        {
            return false;
        }

        bitmap = new PreviewBitmap(size, size, pixels);
        return true;
    }

    private static void DrawMapLine(
        byte[] pixels,
        int size,
        (float X, float Y) first,
        (float X, float Y) second,
        float minX,
        float maxY,
        double scale,
        int padding)
    {
        var x0 = padding + (int)Math.Round(((double)first.X - minX) * scale);
        var y0 = padding + (int)Math.Round(((double)maxY - first.Y) * scale);
        var x1 = padding + (int)Math.Round(((double)second.X - minX) * scale);
        var y1 = padding + (int)Math.Round(((double)maxY - second.Y) * scale);
        var dx = Math.Abs(x1 - x0);
        var sx = x0 < x1 ? 1 : -1;
        var dy = -Math.Abs(y1 - y0);
        var sy = y0 < y1 ? 1 : -1;
        var error = dx + dy;
        while (true)
        {
            SetPixel(pixels, size, x0, y0, 76, 190, 230, 220);
            if (x0 == x1 && y0 == y1)
            {
                break;
            }
            var doubled = error * 2;
            if (doubled >= dy)
            {
                error += dy;
                x0 += sx;
            }
            if (doubled <= dx)
            {
                error += dx;
                y0 += sy;
            }
        }
    }

    private static bool TryDecodeWad(byte[] data, out PreviewBitmap bitmap)
    {
        bitmap = null!;
        if (data.Length < 12 ||
            (!data.AsSpan(0, 4).SequenceEqual("WAD2"u8) &&
             !data.AsSpan(0, 4).SequenceEqual("WAD3"u8)) ||
            !TryReadInt32(data, 4, out var entryCount) ||
            !TryReadInt32(data, 8, out var directoryOffset) ||
            entryCount <= 0 || entryCount > 4_096 || directoryOffset < 0 ||
            !HasRange(data, directoryOffset, checked(entryCount * 32)))
        {
            return false;
        }

        for (var index = 0; index < entryCount; index++)
        {
            var entry = directoryOffset + index * 32;
            if (!TryReadInt32(data, entry, out var offset) ||
                !TryReadInt32(data, entry + 4, out var diskSize) ||
                offset < 0 || diskSize <= 0 || !HasRange(data, offset, diskSize) ||
                data[entry + 13] != 0)
            {
                continue;
            }
            var type = data[entry + 12];
            if (type == (byte)'@')
            {
                continue;
            }

            if (type == (byte)'D')
            {
                if (!HasRange(data, offset, 40) ||
                    !TryReadInt32(data, offset + 16, out var width) ||
                    !TryReadInt32(data, offset + 20, out var height) ||
                    !TryReadInt32(data, offset + 24, out var pixelOffset) || pixelOffset < 0)
                {
                    continue;
                }
                var pixelStart = (long)offset + pixelOffset;
                if (!IsRangeWithinLump(offset, diskSize, pixelStart, width, height) ||
                    !TryCreatePalettedBitmap(data, (int)pixelStart, width, height, 255, out bitmap))
                {
                    continue;
                }
                return true;
            }

            if (HasRange(data, offset, 8) &&
                TryReadInt32(data, offset, out var simpleWidth) &&
                TryReadInt32(data, offset + 4, out var simpleHeight) &&
                IsRangeWithinLump(offset, diskSize, (long)offset + 8, simpleWidth, simpleHeight) &&
                TryCreatePalettedBitmap(data, offset + 8, simpleWidth, simpleHeight, 255, out bitmap))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsRangeWithinLump(
        int lumpOffset,
        int lumpSize,
        long pixelOffset,
        int width,
        int height)
    {
        if (!IsSafeImageSize(width, height))
        {
            return false;
        }
        var lumpEnd = (long)lumpOffset + lumpSize;
        var pixelEnd = pixelOffset + (long)width * height;
        return pixelOffset >= lumpOffset && pixelOffset <= int.MaxValue && pixelEnd <= lumpEnd;
    }

    private static bool TryCreatePalettedBitmap(
        byte[] data,
        int offset,
        int width,
        int height,
        int? transparentIndex,
        out PreviewBitmap bitmap)
    {
        bitmap = null!;
        if (!IsSafeImageSize(width, height))
        {
            return false;
        }
        var pixelCount = checked(width * height);
        if (!HasRange(data, offset, pixelCount))
        {
            return false;
        }

        var pixels = new byte[checked(pixelCount * 4)];
        for (var index = 0; index < pixelCount; index++)
        {
            var paletteIndex = data[offset + index];
            var paletteOffset = paletteIndex * 3;
            var destination = index * 4;
            pixels[destination] = Palette[paletteOffset + 2];
            pixels[destination + 1] = Palette[paletteOffset + 1];
            pixels[destination + 2] = Palette[paletteOffset];
            pixels[destination + 3] = transparentIndex == paletteIndex ? (byte)0 : (byte)255;
        }
        bitmap = new PreviewBitmap(width, height, pixels);
        return true;
    }

    private static bool TryReadLump(byte[] data, int index, out int offset, out int length)
    {
        offset = 0;
        length = 0;
        var headerOffset = 4 + index * 8;
        return TryReadInt32(data, headerOffset, out offset) &&
               TryReadInt32(data, headerOffset + 4, out length) &&
               offset >= 0 && length >= 0 && HasRange(data, offset, length);
    }

    private static bool TryReadInt32(byte[] data, int offset, out int value)
    {
        value = 0;
        if (!HasRange(data, offset, 4))
        {
            return false;
        }
        value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
        return true;
    }

    private static bool TryReadSingle(byte[] data, int offset, out float value)
    {
        value = 0;
        if (!TryReadInt32(data, offset, out var bits))
        {
            return false;
        }
        value = BitConverter.Int32BitsToSingle(bits);
        return true;
    }

    private static int ReadUInt16(byte[] data, int offset)
    {
        return HasRange(data, offset, 2)
            ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2))
            : 0;
    }

    private static bool HasRange(byte[] data, int offset, int length)
    {
        return offset >= 0 && length >= 0 && offset <= data.Length - length;
    }

    private static bool IsSafeImageSize(int width, int height)
    {
        return width > 0 && height > 0 && width <= MaximumDimension && height <= MaximumDimension &&
               (long)width * height <= MaximumPixelCount;
    }

    private static void Fill(byte[] pixels, byte blue, byte green, byte red, byte alpha)
    {
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = blue;
            pixels[offset + 1] = green;
            pixels[offset + 2] = red;
            pixels[offset + 3] = alpha;
        }
    }

    private static void SetPixel(
        byte[] pixels,
        int size,
        int x,
        int y,
        byte blue,
        byte green,
        byte red,
        byte alpha)
    {
        if ((uint)x >= (uint)size || (uint)y >= (uint)size)
        {
            return;
        }
        var offset = (y * size + x) * 4;
        pixels[offset] = blue;
        pixels[offset + 1] = green;
        pixels[offset + 2] = red;
        pixels[offset + 3] = alpha;
    }
}
