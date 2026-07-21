using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PakStudio.Core.Preview;

namespace PakStudio.App.Services;

internal static class PreviewImageFactory
{
    public static bool TryCreate(ArchivePreview preview, int maximumDimension, out ImageSource image)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDimension);

        try
        {
            switch (preview.Kind)
            {
                case ArchivePreviewKind.EncodedImage when preview.EncodedImage is { } encodedImage:
                    return TryCreateEncodedImage(
                        encodedImage,
                        preview.ImageWidth,
                        preview.ImageHeight,
                        maximumDimension,
                        out image);
                case ArchivePreviewKind.Bitmap when preview.Bitmap is { } bitmap:
                    image = CreateBitmap(bitmap, maximumDimension);
                    return true;
                default:
                    image = null!;
                    return false;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            image = null!;
            return false;
        }
    }

    private static bool TryCreateEncodedImage(
        byte[] data,
        int width,
        int height,
        int maximumDimension,
        out ImageSource image)
    {
        using var stream = new MemoryStream(data, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (width >= height)
        {
            bitmap.DecodePixelWidth = Math.Min(width, maximumDimension);
        }
        else
        {
            bitmap.DecodePixelHeight = Math.Min(height, maximumDimension);
        }
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        image = bitmap;
        return true;
    }

    private static ImageSource CreateBitmap(PreviewBitmap bitmap, int maximumDimension)
    {
        BitmapSource source = BitmapSource.Create(
            bitmap.Width,
            bitmap.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            bitmap.BgraPixels,
            bitmap.Stride);

        var largestDimension = Math.Max(bitmap.Width, bitmap.Height);
        if (largestDimension > maximumDimension)
        {
            var scale = (double)maximumDimension / largestDimension;
            source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        }

        source.Freeze();
        return source;
    }
}
