using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PakStudio.App.Services;
using PakStudio.Core.Preview;

namespace PakStudio.App.Controls;

/// <summary>
/// A dependency-free, interactive cube-map viewer for Quake skybox images.
/// </summary>
internal sealed class SkyboxPreviewControl : Image
{
    private const int MaximumRenderWidth = 960;
    private const int MaximumRenderHeight = 640;
    private const int MaximumFaceDimension = 2048;
    private readonly IReadOnlyDictionary<SkyboxFaceSet.Face, FaceTexture> _faces;
    private Point? _lastDragPoint;
    private double _yaw;
    private double _pitch;
    private double _fieldOfView = 70;
    private bool _renderQueued;

    public SkyboxPreviewControl(SkyboxFaceSet faceSet)
    {
        ArgumentNullException.ThrowIfNull(faceSet);
        _faces = DecodeFaces(faceSet);
        Stretch = Stretch.Fill;
        SnapsToDevicePixels = true;
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.SizeAll;
        SizeChanged += (_, _) => QueueRender();
        Loaded += (_, _) =>
        {
            Focus();
            QueueRender();
        };
    }

    public void ResetView()
    {
        _yaw = 0;
        _pitch = 0;
        _fieldOfView = 70;
        QueueRender();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        _lastDragPoint = e.GetPosition(this);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_lastDragPoint is not { } previous || e.LeftButton != MouseButtonState.Pressed)
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

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _lastDragPoint = null;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _lastDragPoint = null;
        base.OnLostMouseCapture(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        _fieldOfView = Math.Clamp(_fieldOfView - e.Delta / 120.0 * 4, 30, 100);
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
        Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(() =>
            {
                _renderQueued = false;
                Render();
            }));
    }

    private void Render()
    {
        if (ActualWidth < 1 || ActualHeight < 1)
        {
            return;
        }

        var width = Math.Clamp((int)Math.Ceiling(ActualWidth), 1, MaximumRenderWidth);
        var height = Math.Clamp((int)Math.Ceiling(ActualHeight), 1, MaximumRenderHeight);
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
            var rowOffset = y * stride;
            for (var x = 0; x < width; x++)
            {
                var screenX = (2 * ((x + 0.5) / width) - 1) * scale * aspect;
                var ray = forward + right * screenX + up * screenY;
                Sample(ray, pixels, rowOffset + x * 4);
            }
        });

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);
        bitmap.Freeze();
        Source = bitmap;
    }

    private void Sample(Vector3 ray, byte[] destination, int destinationOffset)
    {
        var ax = Math.Abs(ray.X);
        var ay = Math.Abs(ray.Y);
        var az = Math.Abs(ray.Z);
        SkyboxFaceSet.Face face;
        double u;
        double v;

        if (ax >= ay && ax >= az)
        {
            if (ray.X >= 0)
            {
                face = SkyboxFaceSet.Face.Front;
                u = -ray.Z / ax;
            }
            else
            {
                face = SkyboxFaceSet.Face.Back;
                u = ray.Z / ax;
            }
            v = -ray.Y / ax;
        }
        else if (az >= ax && az >= ay)
        {
            if (ray.Z >= 0)
            {
                face = SkyboxFaceSet.Face.Right;
                u = ray.X / az;
            }
            else
            {
                face = SkyboxFaceSet.Face.Left;
                u = -ray.X / az;
            }
            v = -ray.Y / az;
        }
        else if (ray.Y >= 0)
        {
            face = SkyboxFaceSet.Face.Up;
            u = ray.X / ay;
            v = ray.Z / ay;
        }
        else
        {
            face = SkyboxFaceSet.Face.Down;
            u = ray.X / ay;
            v = -ray.Z / ay;
        }

        var texture = _faces[face];
        var sourceX = Math.Clamp((int)((u + 1) * 0.5 * texture.Width), 0, texture.Width - 1);
        var sourceY = Math.Clamp((int)((v + 1) * 0.5 * texture.Height), 0, texture.Height - 1);
        var sourceOffset = (sourceY * texture.Width + sourceX) * 4;
        destination[destinationOffset] = texture.Pixels[sourceOffset];
        destination[destinationOffset + 1] = texture.Pixels[sourceOffset + 1];
        destination[destinationOffset + 2] = texture.Pixels[sourceOffset + 2];
        destination[destinationOffset + 3] = texture.Pixels[sourceOffset + 3];
    }

    private static IReadOnlyDictionary<SkyboxFaceSet.Face, FaceTexture> DecodeFaces(
        SkyboxFaceSet faceSet)
    {
        var result = new Dictionary<SkyboxFaceSet.Face, FaceTexture>();
        int? expectedSourceSize = null;
        foreach (var (face, file) in faceSet.Faces)
        {
            var preview = ArchivePreviewBuilder.Build(file, includeInteractiveModels: false);
            var sourceWidth = preview.Kind switch
            {
                ArchivePreviewKind.EncodedImage => preview.ImageWidth,
                ArchivePreviewKind.Bitmap => preview.Bitmap?.Width ?? 0,
                _ => 0,
            };
            var sourceHeight = preview.Kind switch
            {
                ArchivePreviewKind.EncodedImage => preview.ImageHeight,
                ArchivePreviewKind.Bitmap => preview.Bitmap?.Height ?? 0,
                _ => 0,
            };
            if (sourceWidth <= 0 ||
                sourceWidth != sourceHeight ||
                expectedSourceSize is { } expected && sourceWidth != expected)
            {
                throw new ArchivePreviewException(
                    "Skybox faces must be square images with matching dimensions.");
            }
            expectedSourceSize ??= sourceWidth;

            if (!PreviewImageFactory.TryCreate(
                    preview,
                    MaximumFaceDimension,
                    out var image) ||
                image is not BitmapSource source)
            {
                throw new ArchivePreviewException($"'{file.Name}' could not be decoded.");
            }

            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var stride = checked(converted.PixelWidth * 4);
            var pixels = new byte[checked(stride * converted.PixelHeight)];
            converted.CopyPixels(pixels, stride, 0);
            result[face] = new FaceTexture(converted.PixelWidth, converted.PixelHeight, pixels);
        }

        return result;
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
