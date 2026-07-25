using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PakStudio.Core.Preview;

namespace PakStudio.App.Services;

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
            var frame = BitmapFrame.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            var stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];
            converted.CopyPixels(pixels, stride, 0);

            for (var index = 0; index + 3 < pixels.Length; index += 4)
            {
                (pixels[index], pixels[index + 2]) = (pixels[index + 2], pixels[index]);
            }

            return new ModelTextureData(width, height, pixels);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return null;
        }
    }
}
