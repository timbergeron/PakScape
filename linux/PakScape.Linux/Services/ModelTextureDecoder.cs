using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PakStudio.Core.Preview;

namespace PakScape.Linux.Services;

/// <summary>
/// Decodes the skins that ship as PNG or JPEG into the straight RGBA the native
/// model viewer uploads. Quake's own image formats are decoded in the core.
/// </summary>
internal static class ModelTextureDecoder
{
    public static ModelTextureData? Decode(byte[] encodedImage)
    {
        if (encodedImage is null || encodedImage.Length == 0)
        {
            return null;
        }

        try
        {
            if (!EncodedImageInspector.TryGetSafeDimensions(encodedImage, out _, out _))
            {
                return null;
            }

            using var stream = new MemoryStream(encodedImage, writable: false);
            using var decoded = WriteableBitmap.Decode(stream);
            var width = decoded.PixelSize.Width;
            var height = decoded.PixelSize.Height;
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            var pixels = new byte[checked(width * height * 4)];
            using (var framebuffer = decoded.Lock())
            {
                var swapRedAndBlue = framebuffer.Format == PixelFormats.Bgra8888;
                if (!swapRedAndBlue && framebuffer.Format != PixelFormats.Rgba8888)
                {
                    return null;
                }

                var row = new byte[checked(width * 4)];
                for (var y = 0; y < height; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(
                        IntPtr.Add(framebuffer.Address, y * framebuffer.RowBytes),
                        row,
                        0,
                        row.Length);

                    var offset = y * width * 4;
                    if (swapRedAndBlue)
                    {
                        for (var x = 0; x < row.Length; x += 4)
                        {
                            pixels[offset + x] = row[x + 2];
                            pixels[offset + x + 1] = row[x + 1];
                            pixels[offset + x + 2] = row[x];
                            pixels[offset + x + 3] = row[x + 3];
                        }
                    }
                    else
                    {
                        Array.Copy(row, 0, pixels, offset, row.Length);
                    }
                }
            }

            return new ModelTextureData(width, height, pixels);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }
}
