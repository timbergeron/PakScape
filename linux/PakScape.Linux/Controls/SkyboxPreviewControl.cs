using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using PakScape.Linux.Services;
using PakStudio.Core.Preview;

namespace PakScape.Linux.Controls;

internal sealed class SkyboxPreviewControl : Image, IDisposable
{
    private const int MaximumRenderWidth = 960;
    private const int MaximumRenderHeight = 640;
    private const int MaximumFaceDimension = 2048;
    private readonly Dictionary<SkyboxFaceSet.Face, FaceTexture> _faces;
    private Point? _lastDragPoint;
    private WriteableBitmap? _rendered;
    private double _yaw;
    private double _pitch;
    private double _fieldOfView = 70;
    private bool _renderQueued;
    private bool _isDisposed;

    public SkyboxPreviewControl(SkyboxFaceSet faceSet)
    {
        _faces = DecodeFaces(faceSet);
        Stretch = Stretch.Fill;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.SizeAll);
        SizeChanged += (_, _) => QueueRender();
        Loaded += (_, _) =>
        {
            Focus();
            QueueRender();
        };
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerWheelChanged += OnPointerWheelChanged;
    }

    public void ResetView()
    {
        _yaw = 0;
        _pitch = 0;
        _fieldOfView = 70;
        QueueRender();
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        Focus();
        _lastDragPoint = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_lastDragPoint is not { } previous ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        var current = e.GetPosition(this);
        _yaw += (current.X - previous.X) * 0.005;
        _pitch = Math.Clamp(
            _pitch - (current.Y - previous.Y) * 0.005,
            -Math.PI / 2 + 0.01,
            Math.PI / 2 - 0.01);
        _lastDragPoint = current;
        QueueRender();
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _lastDragPoint = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        _fieldOfView = Math.Clamp(_fieldOfView - e.Delta.Y * 4, 30, 100);
        QueueRender();
        e.Handled = true;
    }

    private void QueueRender()
    {
        if (_renderQueued)
        {
            return;
        }
        _renderQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _renderQueued = false;
            RenderSkybox();
        }, DispatcherPriority.Render);
    }

    private void RenderSkybox()
    {
        var width = Math.Clamp((int)Math.Ceiling(Bounds.Width), 1, MaximumRenderWidth);
        var height = Math.Clamp((int)Math.Ceiling(Bounds.Height), 1, MaximumRenderHeight);
        if (Bounds.Width < 1 || Bounds.Height < 1)
        {
            return;
        }
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];
        var cosYaw = Math.Cos(_yaw);
        var sinYaw = Math.Sin(_yaw);
        var cosPitch = Math.Cos(_pitch);
        var sinPitch = Math.Sin(_pitch);
        var forward = new Vector3(cosPitch * cosYaw, sinPitch, cosPitch * sinYaw);
        var right = new Vector3(-sinYaw, 0, cosYaw);
        var up = Cross(right, forward);
        var scale = Math.Tan(_fieldOfView * Math.PI / 360);
        var aspect = (double)width / height;

        Parallel.For(0, height, y =>
        {
            var screenY = (1 - 2 * ((y + 0.5) / height)) * scale;
            for (var x = 0; x < width; x++)
            {
                var screenX = (2 * ((x + 0.5) / width) - 1) * scale * aspect;
                Sample(forward + right * screenX + up * screenY, pixels, y * stride + x * 4);
            }
        });

        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormats.Bgra8888,
            AlphaFormat.Unpremul);
        using (var framebuffer = bitmap.Lock())
        {
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(
                    pixels,
                    y * stride,
                    IntPtr.Add(framebuffer.Address, y * framebuffer.RowBytes),
                    stride);
            }
        }
        var previous = _rendered;
        _rendered = bitmap;
        Source = bitmap;
        previous?.Dispose();
    }

    private void Sample(Vector3 ray, byte[] output, int offset)
    {
        var ax = Math.Abs(ray.X);
        var ay = Math.Abs(ray.Y);
        var az = Math.Abs(ray.Z);
        SkyboxFaceSet.Face face;
        double u;
        double v;
        if (ax >= ay && ax >= az)
        {
            face = ray.X >= 0 ? SkyboxFaceSet.Face.Front : SkyboxFaceSet.Face.Back;
            u = ray.X >= 0 ? -ray.Z / ax : ray.Z / ax;
            v = -ray.Y / ax;
        }
        else if (az >= ax && az >= ay)
        {
            face = ray.Z >= 0 ? SkyboxFaceSet.Face.Right : SkyboxFaceSet.Face.Left;
            u = ray.Z >= 0 ? ray.X / az : -ray.X / az;
            v = -ray.Y / az;
        }
        else
        {
            face = ray.Y >= 0 ? SkyboxFaceSet.Face.Up : SkyboxFaceSet.Face.Down;
            u = ray.X / ay;
            v = ray.Y >= 0 ? ray.Z / ay : -ray.Z / ay;
        }
        var texture = _faces[face];
        var x = Math.Clamp((int)((u + 1) * 0.5 * texture.Width), 0, texture.Width - 1);
        var y = Math.Clamp((int)((v + 1) * 0.5 * texture.Height), 0, texture.Height - 1);
        var source = (y * texture.Width + x) * 4;
        Array.Copy(texture.Pixels, source, output, offset, 4);
    }

    private static Dictionary<SkyboxFaceSet.Face, FaceTexture> DecodeFaces(
        SkyboxFaceSet faceSet)
    {
        var result = new Dictionary<SkyboxFaceSet.Face, FaceTexture>();
        int? expectedSize = null;
        foreach (var (face, file) in faceSet.Faces)
        {
            var preview = ArchivePreviewBuilder.Build(file, includeInteractiveModels: false);
            FaceTexture texture;
            if (preview.Kind == ArchivePreviewKind.Bitmap && preview.Bitmap is { } bitmap)
            {
                if (bitmap.Width > MaximumFaceDimension || bitmap.Height > MaximumFaceDimension)
                {
                    throw new ArchivePreviewException("Skybox faces exceed the preview dimension limit.");
                }
                texture = new FaceTexture(bitmap.Width, bitmap.Height, bitmap.BgraPixels);
            }
            else if (preview.Kind == ArchivePreviewKind.EncodedImage &&
                     preview.EncodedImage is { } encoded &&
                     preview.ImageWidth <= MaximumFaceDimension &&
                     preview.ImageHeight <= MaximumFaceDimension &&
                     ModelTextureDecoder.Decode(encoded) is { } decoded)
            {
                var bgra = decoded.RgbaPixels.ToArray();
                for (var i = 0; i < bgra.Length; i += 4)
                {
                    (bgra[i], bgra[i + 2]) = (bgra[i + 2], bgra[i]);
                }
                texture = new FaceTexture(decoded.Width, decoded.Height, bgra);
            }
            else
            {
                throw new ArchivePreviewException($"'{file.Name}' could not be decoded.");
            }
            if (texture.Width <= 0 ||
                texture.Width != texture.Height ||
                expectedSize is { } size && texture.Width != size)
            {
                throw new ArchivePreviewException(
                    "Skybox faces must be square images with matching dimensions.");
            }
            expectedSize ??= texture.Width;
            result[face] = texture;
        }
        return result;
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }
        Source = null;
        _rendered?.Dispose();
        _rendered = null;
        _isDisposed = true;
        GC.SuppressFinalize(this);
    }

    private static Vector3 Cross(Vector3 left, Vector3 right) => new(
        left.Y * right.Z - left.Z * right.Y,
        left.Z * right.X - left.X * right.Z,
        left.X * right.Y - left.Y * right.X);

    private readonly record struct Vector3(double X, double Y, double Z)
    {
        public static Vector3 operator +(Vector3 left, Vector3 right) =>
            new(left.X + right.X, left.Y + right.Y, left.Z + right.Z);
        public static Vector3 operator *(Vector3 value, double scale) =>
            new(value.X * scale, value.Y * scale, value.Z * scale);
    }

    private sealed record FaceTexture(int Width, int Height, byte[] Pixels);
}
