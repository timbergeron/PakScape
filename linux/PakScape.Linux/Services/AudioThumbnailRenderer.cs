using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PakStudio.Core.Audio;
using PakStudio.Core.Preview;

namespace PakScape.Linux.Services;

internal static class AudioThumbnailRenderer
{
    public static Bitmap Create(ArchivePreview preview, int dimension)
    {
        var stride = dimension * 4;
        var pixels = new byte[stride * dimension];
        Fill(pixels, stride, 0x2D, 0x25, 0x20, 0xFF);
        FillCircle(pixels, stride, 96, 88, 70, 0x46, 0x35, 0x27, 0xFF);

        var waveform = AudioWaveformBuilder.Build(preview.EncodedAudio ?? Array.Empty<byte>(), 48);
        const int innerRadius = 38;
        const int minimumBarLength = 10;
        const int maximumBarLength = 27;
        const int barWidth = 3;
        for (var index = 0; index < waveform.Count; index++)
        {
            var angle = -Math.PI / 2 + index * (Math.PI * 2 / waveform.Count);
            var outerRadius = innerRadius + minimumBarLength + waveform[index] * maximumBarLength;
            var gradientPosition = (Math.Cos(angle) + 1) / 2;
            var (red, green, blue) = InterpolateColor(
                0x9B, 0x3D, 0xFF,
                0x38, 0xB8, 0xF4,
                gradientPosition);
            DrawRadialBar(
                pixels,
                stride,
                96,
                88,
                innerRadius,
                outerRadius,
                barWidth,
                angle,
                red,
                green,
                blue,
                0xFF);
        }

        FillCircle(pixels, stride, 96, 88, 34, 0x2D, 0x25, 0x20, 0xFF);

        var bitmap = new WriteableBitmap(
            new PixelSize(dimension, dimension),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
        using (var framebuffer = bitmap.Lock())
        {
            for (var row = 0; row < dimension; row++)
            {
                Marshal.Copy(
                    pixels,
                    row * stride,
                    IntPtr.Add(framebuffer.Address, row * framebuffer.RowBytes),
                    stride);
            }
        }

        return bitmap;
    }

    private static void Fill(byte[] pixels, int stride, byte r, byte g, byte b, byte a)
    {
        for (var row = 0; row < pixels.Length / stride; row++)
        {
            for (var column = 0; column < stride; column += 4)
            {
                SetPixel(pixels, row * stride + column, r, g, b, a);
            }
        }
    }

    private static void FillRect(byte[] pixels, int stride, int x, int y, int width, int height, byte r, byte g, byte b, byte a)
    {
        for (var row = Math.Max(0, y); row < Math.Min(y + height, pixels.Length / stride); row++)
        {
            for (var column = Math.Max(0, x); column < Math.Min(x + width, stride / 4); column++)
            {
                SetPixel(pixels, row * stride + column * 4, r, g, b, a);
            }
        }
    }

    private static void FillRoundedRect(byte[] pixels, int stride, int x, int y, int width, int height, byte r, byte g, byte b, byte a)
    {
        FillRect(pixels, stride, x, y, width, height, r, g, b, a);
    }

    private static void DrawRadialBar(
        byte[] pixels,
        int stride,
        int centerX,
        int centerY,
        int innerRadius,
        double outerRadius,
        int width,
        double angle,
        byte r,
        byte g,
        byte b,
        byte a)
    {
        var steps = Math.Max(1, (int)Math.Ceiling(outerRadius - innerRadius));
        for (var step = 0; step <= steps; step++)
        {
            var radius = innerRadius + (outerRadius - innerRadius) * step / steps;
            var x = centerX + (int)Math.Round(Math.Cos(angle) * radius);
            var y = centerY + (int)Math.Round(Math.Sin(angle) * radius);
            FillCircle(pixels, stride, x, y, Math.Max(1, width / 2), r, g, b, a);
        }
    }

    private static (byte Red, byte Green, byte Blue) InterpolateColor(
        byte firstRed,
        byte firstGreen,
        byte firstBlue,
        byte secondRed,
        byte secondGreen,
        byte secondBlue,
        double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return (
            (byte)(firstRed + (secondRed - firstRed) * amount),
            (byte)(firstGreen + (secondGreen - firstGreen) * amount),
            (byte)(firstBlue + (secondBlue - firstBlue) * amount));
    }

    private static void FillCircle(byte[] pixels, int stride, int centerX, int centerY, int radius, byte r, byte g, byte b, byte a)
    {
        var radiusSquared = radius * radius;
        for (var y = centerY - radius; y <= centerY + radius; y++)
        {
            for (var x = centerX - radius; x <= centerX + radius; x++)
            {
                var dx = x - centerX;
                var dy = y - centerY;
                if (dx * dx + dy * dy <= radiusSquared)
                {
                    SetPixel(pixels, y * stride + x * 4, r, g, b, a);
                }
            }
        }
    }

    private static void FillTriangle(
        byte[] pixels,
        int stride,
        int x1,
        int y1,
        int x2,
        int y2,
        int x3,
        int y3,
        byte r,
        byte g,
        byte b,
        byte a)
    {
        var minX = Math.Min(x1, Math.Min(x2, x3));
        var maxX = Math.Max(x1, Math.Max(x2, x3));
        var minY = Math.Min(y1, Math.Min(y2, y3));
        var maxY = Math.Max(y1, Math.Max(y2, y3));
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                if (Sign(x, y, x1, y1, x2, y2) < 0 ||
                    Sign(x, y, x2, y2, x3, y3) < 0 ||
                    Sign(x, y, x3, y3, x1, y1) < 0)
                {
                    continue;
                }
                SetPixel(pixels, y * stride + x * 4, r, g, b, a);
            }
        }
    }

    private static int Sign(int pointX, int pointY, int x1, int y1, int x2, int y2) =>
        (pointX - x2) * (y1 - y2) - (x1 - x2) * (pointY - y2);

    private static void SetPixel(byte[] pixels, int offset, byte r, byte g, byte b, byte a)
    {
        if (offset < 0 || offset + 3 >= pixels.Length)
        {
            return;
        }

        pixels[offset] = b;
        pixels[offset + 1] = g;
        pixels[offset + 2] = r;
        pixels[offset + 3] = a;
    }
}
