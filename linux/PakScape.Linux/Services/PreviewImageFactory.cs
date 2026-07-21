using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PakStudio.Core.Preview;

namespace PakScape.Linux.Services;

internal static class PreviewImageFactory
{
    public static bool TryCreate(ArchivePreview preview, int maximumDimension, out Bitmap bitmap)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDimension);
        try
        {
            switch (preview.Kind)
            {
                case ArchivePreviewKind.EncodedImage when preview.EncodedImage is { } encodedImage:
                    using (var stream = new MemoryStream(encodedImage, writable: false))
                    {
                        bitmap = preview.ImageWidth >= preview.ImageHeight
                            ? Bitmap.DecodeToWidth(stream, Math.Min(preview.ImageWidth, maximumDimension), BitmapInterpolationMode.HighQuality)
                            : Bitmap.DecodeToHeight(stream, Math.Min(preview.ImageHeight, maximumDimension), BitmapInterpolationMode.HighQuality);
                    }
                    return true;
                case ArchivePreviewKind.Bitmap when preview.Bitmap is { } previewBitmap:
                    bitmap = CreateBitmap(previewBitmap, maximumDimension);
                    return true;
                default:
                    bitmap = null!;
                    return false;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            bitmap = null!;
            return false;
        }
    }

    private static Bitmap CreateBitmap(PreviewBitmap preview, int maximumDimension)
    {
        var source = new WriteableBitmap(
            new PixelSize(preview.Width, preview.Height),
            new Vector(96, 96),
            PixelFormats.Bgra8888,
            AlphaFormat.Unpremul);
        using (var framebuffer = source.Lock())
        {
            for (var row = 0; row < preview.Height; row++)
            {
                Marshal.Copy(
                    preview.BgraPixels,
                    row * preview.Stride,
                    IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes),
                    preview.Stride);
            }
        }

        var largestDimension = Math.Max(preview.Width, preview.Height);
        if (largestDimension <= maximumDimension)
        {
            return source;
        }

        var scale = (double)maximumDimension / largestDimension;
        try
        {
            return source.CreateScaledBitmap(
                new PixelSize(
                    Math.Max(1, (int)Math.Round(preview.Width * scale)),
                    Math.Max(1, (int)Math.Round(preview.Height * scale))),
                BitmapInterpolationMode.HighQuality);
        }
        finally
        {
            source.Dispose();
        }
    }
}
