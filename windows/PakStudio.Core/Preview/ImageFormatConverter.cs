using System.Buffers.Binary;
using StbImageSharp;
using StbImageWriteSharp;
using ReadColorComponents = StbImageSharp.ColorComponents;
using WriteColorComponents = StbImageWriteSharp.ColorComponents;

namespace PakStudio.Core.Preview;

public enum ImageSaveFormat
{
    Lmp,
    Jpeg,
    Png,
    Tga,
}

public static class ImageFormatConverter
{
    private const int MaximumDimension = 8_192;
    private const long MaximumPixelCount = 16_777_216;

    private static readonly HashSet<string> SupportedSourceExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".lmp", ".jpg", ".jpeg", ".png", ".tga",
        };

    public static bool IsSupportedSource(string fileName) =>
        SupportedSourceExtensions.Contains(Path.GetExtension(fileName));

    public static bool TryParseFormat(string? formatId, out ImageSaveFormat format)
    {
        format = formatId?.ToLowerInvariant() switch
        {
            "lmp" => ImageSaveFormat.Lmp,
            "jpg" or "jpeg" => ImageSaveFormat.Jpeg,
            "png" => ImageSaveFormat.Png,
            "tga" => ImageSaveFormat.Tga,
            _ => default,
        };
        return formatId?.ToLowerInvariant() is "lmp" or "jpg" or "jpeg" or "png" or "tga";
    }

    public static string ExtensionFor(ImageSaveFormat format) => format switch
    {
        ImageSaveFormat.Lmp => ".lmp",
        ImageSaveFormat.Jpeg => ".jpg",
        ImageSaveFormat.Png => ".png",
        ImageSaveFormat.Tga => ".tga",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    public static byte[] Convert(string fileName, byte[] data, ImageSaveFormat format)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(data);
        if (!IsSupportedSource(fileName))
        {
            throw new InvalidDataException("Save As supports LMP, JPEG, PNG, and TGA images.");
        }

        var image = Decode(fileName, data);
        return format == ImageSaveFormat.Lmp
            ? EncodeLmp(image)
            : EncodeStandardImage(image, format);
    }

    private static RgbaImage Decode(string fileName, byte[] data)
    {
        if (Path.GetExtension(fileName).Equals(".lmp", StringComparison.OrdinalIgnoreCase))
        {
            if (!QuakePreviewDecoder.TryDecode(fileName, data, out var bitmap))
            {
                throw new InvalidDataException("The LMP image data could not be decoded.");
            }

            ValidateDimensions(bitmap.Width, bitmap.Height);
            var rgba = new byte[checked(bitmap.Width * bitmap.Height * 4)];
            for (var offset = 0; offset < rgba.Length; offset += 4)
            {
                rgba[offset] = bitmap.BgraPixels[offset + 2];
                rgba[offset + 1] = bitmap.BgraPixels[offset + 1];
                rgba[offset + 2] = bitmap.BgraPixels[offset];
                rgba[offset + 3] = bitmap.BgraPixels[offset + 3];
            }
            return new RgbaImage(bitmap.Width, bitmap.Height, rgba);
        }

        try
        {
            using var informationStream = new MemoryStream(data, writable: false);
            var information = ImageInfo.FromStream(informationStream);
            if (information is not { } info)
            {
                throw new InvalidDataException("The image data could not be decoded.");
            }
            ValidateDimensions(info.Width, info.Height);

            var decoded = ImageResult.FromMemory(data, ReadColorComponents.RedGreenBlueAlpha);
            ValidateDimensions(decoded.Width, decoded.Height);
            return new RgbaImage(decoded.Width, decoded.Height, decoded.Data);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new InvalidDataException("The image data could not be decoded.", exception);
        }
    }

    private static byte[] EncodeStandardImage(RgbaImage image, ImageSaveFormat format)
    {
        using var output = new MemoryStream();
        var writer = new ImageWriter();
        switch (format)
        {
            case ImageSaveFormat.Jpeg:
                writer.WriteJpg(
                    image.Pixels,
                    image.Width,
                    image.Height,
                    WriteColorComponents.RedGreenBlueAlpha,
                    output,
                    90);
                break;
            case ImageSaveFormat.Png:
                writer.WritePng(
                    image.Pixels,
                    image.Width,
                    image.Height,
                    WriteColorComponents.RedGreenBlueAlpha,
                    output);
                break;
            case ImageSaveFormat.Tga:
                writer.WriteTga(
                    image.Pixels,
                    image.Width,
                    image.Height,
                    WriteColorComponents.RedGreenBlueAlpha,
                    output);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
        return output.ToArray();
    }

    private static byte[] EncodeLmp(RgbaImage image)
    {
        var pixelCount = checked(image.Width * image.Height);
        var output = new byte[checked(8 + pixelCount)];
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0, 4), (uint)image.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4, 4), (uint)image.Height);

        var palette = QuakePreviewDecoder.PaletteBytes;
        var colorCache = new Dictionary<uint, byte>(Math.Min(pixelCount, 65_536));
        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            var sourceOffset = pixelIndex * 4;
            var alpha = image.Pixels[sourceOffset + 3];
            if (alpha <= 128)
            {
                output[8 + pixelIndex] = 255;
                continue;
            }

            var red = image.Pixels[sourceOffset];
            var green = image.Pixels[sourceOffset + 1];
            var blue = image.Pixels[sourceOffset + 2];
            var key = (uint)(red << 16 | green << 8 | blue);
            if (!colorCache.TryGetValue(key, out var paletteIndex))
            {
                paletteIndex = FindNearestPaletteIndex(red, green, blue, palette);
                colorCache[key] = paletteIndex;
            }
            output[8 + pixelIndex] = paletteIndex;
        }
        return output;
    }

    private static byte FindNearestPaletteIndex(
        byte red,
        byte green,
        byte blue,
        ReadOnlySpan<byte> palette)
    {
        var bestIndex = 1;
        var bestDistance = int.MaxValue;
        for (var index = 1; index <= 254; index++)
        {
            var offset = index * 3;
            var deltaRed = red - palette[offset];
            var deltaGreen = green - palette[offset + 1];
            var deltaBlue = blue - palette[offset + 2];
            var distance =
                deltaRed * deltaRed +
                deltaGreen * deltaGreen +
                deltaBlue * deltaBlue;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = index;
                if (distance == 0)
                {
                    break;
                }
            }
        }
        return (byte)bestIndex;
    }

    private static void ValidateDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0 ||
            width > MaximumDimension || height > MaximumDimension ||
            (long)width * height > MaximumPixelCount)
        {
            throw new InvalidDataException("The image dimensions are too large to convert safely.");
        }
    }

    private sealed record RgbaImage(int Width, int Height, byte[] Pixels);
}
