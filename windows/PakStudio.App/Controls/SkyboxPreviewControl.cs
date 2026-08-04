using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenTK.Graphics.OpenGL;
using OpenTK.Wpf;
using PakStudio.App.Services;
using PakStudio.Core.Preview;
using GlPixelFormat = OpenTK.Graphics.OpenGL.PixelFormat;

namespace PakStudio.App.Controls;

/// <summary>
/// An interactive cube-map viewer for Quake skyboxes. The six faces become textures
/// on a cube drawn around the camera, matching the SceneKit viewer on macOS. A
/// software raycaster stands in when OpenGL interop is unavailable.
/// </summary>
internal sealed class SkyboxPreviewControl : UserControl, IDisposable
{
    private const int MaximumRenderWidth = 960;
    private const int MaximumRenderHeight = 640;
    private const int MaximumFaceDimension = 2048;
    private const double DefaultFieldOfView = 70;

    /// <summary>
    /// Textures outlive the control that owns them: deleting one needs the GL context
    /// current, which only holds inside a render callback. Closed viewers park their
    /// textures here and the next render pass reclaims them.
    /// </summary>
    private static readonly List<int> AbandonedTextures = new();

    private readonly FaceTexture[] _faces;
    private readonly Image _softwareSurface;
    private readonly GLWpfControl? _glControl;
    private readonly int[] _textures = new int[6];

    private Point? _lastDragPoint;
    private double _yaw;
    private double _pitch;
    private double _fieldOfView = DefaultFieldOfView;
    private bool _usingOpenGl;
    private bool _texturesUploaded;
    private bool _renderQueued;
    private bool _disposed;

    public SkyboxPreviewControl(SkyboxFaceSet faceSet)
    {
        ArgumentNullException.ThrowIfNull(faceSet);
        _faces = DecodeFaces(faceSet);

        _softwareSurface = new Image
        {
            Stretch = Stretch.Fill,
            SnapsToDevicePixels = true,
        };

        try
        {
            var glControl = new GLWpfControl
            {
                IsHitTestVisible = false,
            };
            glControl.Render += GlControl_OnRender;
            glControl.Start(new GLWpfControlSettings
            {
                MajorVersion = 2,
                MinorVersion = 1,
            });
            _glControl = glControl;
            _usingOpenGl = true;
            _softwareSurface.Visibility = Visibility.Collapsed;
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or InvalidOperationException)
        {
            /* Keep the portable raycaster when OpenGL interop is unavailable. */
            _glControl = null;
        }

        var surface = new Grid
        {
            Background = Brushes.Black,
        };
        if (_glControl is not null)
        {
            surface.Children.Add(_glControl);
        }
        surface.Children.Add(_softwareSurface);
        Content = surface;

        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.SizeAll;
        SizeChanged += (_, _) => Invalidate();
        Loaded += (_, _) =>
        {
            Focus();
            Invalidate();
        };
    }

    public void ResetView()
    {
        _yaw = 0;
        _pitch = 0;
        _fieldOfView = DefaultFieldOfView;
        Invalidate();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_glControl is not null)
        {
            _glControl.Render -= GlControl_OnRender;
        }

        if (_texturesUploaded)
        {
            _texturesUploaded = false;
            lock (AbandonedTextures)
            {
                foreach (var texture in _textures)
                {
                    if (texture != 0)
                    {
                        AbandonedTextures.Add(texture);
                    }
                }
            }
        }
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
        Invalidate();
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
        Invalidate();
        e.Handled = true;
    }

    private void Invalidate()
    {
        if (_disposed)
        {
            return;
        }

        if (_usingOpenGl)
        {
            _glControl?.InvalidateVisual();
            return;
        }

        QueueSoftwareRender();
    }

    private void GlControl_OnRender(TimeSpan delta)
    {
        if (_disposed || !_usingOpenGl || _glControl is null)
        {
            return;
        }

        try
        {
            ReclaimAbandonedTextures();
            EnsureTextures();
            var scale = VisualTreeHelper.GetDpi(_glControl).DpiScaleX;
            if (double.IsNaN(scale) || scale <= 0)
            {
                scale = 1.0;
            }

            RenderOpenGl(
                Math.Max(1, (int)Math.Round(_glControl.ActualWidth * scale)),
                Math.Max(1, (int)Math.Round(_glControl.ActualHeight * scale)));
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException or
            PlatformNotSupportedException or InvalidOperationException)
        {
            FallBackToSoftwareRendering();
        }
    }

    private void FallBackToSoftwareRendering()
    {
        _usingOpenGl = false;
        _texturesUploaded = false;
        if (_glControl is not null)
        {
            _glControl.Render -= GlControl_OnRender;
            _glControl.Visibility = Visibility.Collapsed;
        }
        _softwareSurface.Visibility = Visibility.Visible;
        QueueSoftwareRender();
    }

    private static void ReclaimAbandonedTextures()
    {
        int[] textures;
        lock (AbandonedTextures)
        {
            if (AbandonedTextures.Count == 0)
            {
                return;
            }

            textures = AbandonedTextures.ToArray();
            AbandonedTextures.Clear();
        }

        GL.DeleteTextures(textures.Length, textures);
    }

    private void EnsureTextures()
    {
        if (_texturesUploaded)
        {
            return;
        }

        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 4);
        for (var index = 0; index < _faces.Length; index++)
        {
            var face = _faces[index];
            var texture = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, texture);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Linear);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear);
            /* Clamping keeps the bilinear filter from bleeding one face into the next. */
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureWrapS,
                (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureWrapT,
                (int)TextureWrapMode.ClampToEdge);
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.Rgba,
                face.Width,
                face.Height,
                0,
                GlPixelFormat.Bgra,
                PixelType.UnsignedByte,
                face.Pixels);
            _textures[index] = texture;
        }

        _texturesUploaded = true;
    }

    private void RenderOpenGl(int width, int height)
    {
        GL.Viewport(0, 0, width, height);
        GL.ClearColor(0f, 0f, 0f, 1f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        GL.Disable(EnableCap.DepthTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.Lighting);
        GL.Disable(EnableCap.Blend);
        GL.Enable(EnableCap.Texture2D);
        GL.Color4(1f, 1f, 1f, 1f);

        GL.MatrixMode(MatrixMode.Projection);
        var projection = CreateProjection(_fieldOfView, (double)width / height);
        GL.LoadMatrix(projection);

        GL.MatrixMode(MatrixMode.Modelview);
        var (forward, right, up) = CreateBasis(_yaw, _pitch);
        GL.LoadMatrix(CreateView(forward, right, up));

        for (var index = 0; index < _faces.Length; index++)
        {
            GL.BindTexture(TextureTarget.Texture2D, _textures[index]);
            GL.Begin(PrimitiveType.Quads);
            foreach (var (u, v) in FaceCorners)
            {
                var (x, y, z) = ProjectFace((SkyboxFaceSet.Face)index, u, v);
                GL.TexCoord2((u + 1) * 0.5f, (v + 1) * 0.5f);
                GL.Vertex3(x, y, z);
            }
            GL.End();
        }

        GL.BindTexture(TextureTarget.Texture2D, 0);
    }

    private static readonly (float U, float V)[] FaceCorners =
    {
        (-1f, -1f),
        (1f, -1f),
        (1f, 1f),
        (-1f, 1f),
    };

    /// <summary>
    /// Places a face's (u, v) corner in world space. This is the inverse of the
    /// direction-to-face mapping in <see cref="Sample"/>, so both renderers agree on
    /// which image lands where.
    /// </summary>
    private static (float X, float Y, float Z) ProjectFace(SkyboxFaceSet.Face face, float u, float v) =>
        face switch
        {
            SkyboxFaceSet.Face.Front => (1f, -v, -u),
            SkyboxFaceSet.Face.Back => (-1f, -v, u),
            SkyboxFaceSet.Face.Right => (u, -v, 1f),
            SkyboxFaceSet.Face.Left => (-u, -v, -1f),
            SkyboxFaceSet.Face.Up => (u, 1f, v),
            _ => (u, -1f, -v),
        };

    /// <summary>Column-major perspective projection, in the layout glLoadMatrix wants.</summary>
    private static float[] CreateProjection(double verticalFieldOfView, double aspect)
    {
        const double near = 0.05;
        const double far = 10.0;
        var focal = 1.0 / Math.Tan(verticalFieldOfView * Math.PI / 360);
        var matrix = new float[16];
        matrix[0] = (float)(focal / aspect);
        matrix[5] = (float)focal;
        matrix[10] = (float)((far + near) / (near - far));
        matrix[11] = -1f;
        matrix[14] = (float)(2 * far * near / (near - far));
        return matrix;
    }

    /// <summary>Column-major view matrix for a camera parked at the origin.</summary>
    private static float[] CreateView(Vector3 forward, Vector3 right, Vector3 up)
    {
        var matrix = new float[16];
        matrix[0] = (float)right.X;
        matrix[4] = (float)right.Y;
        matrix[8] = (float)right.Z;
        matrix[1] = (float)up.X;
        matrix[5] = (float)up.Y;
        matrix[9] = (float)up.Z;
        matrix[2] = (float)-forward.X;
        matrix[6] = (float)-forward.Y;
        matrix[10] = (float)-forward.Z;
        matrix[15] = 1f;
        return matrix;
    }

    private static (Vector3 Forward, Vector3 Right, Vector3 Up) CreateBasis(double yaw, double pitch)
    {
        var cosYaw = Math.Cos(yaw);
        var sinYaw = Math.Sin(yaw);
        var cosPitch = Math.Cos(pitch);
        var sinPitch = Math.Sin(pitch);
        var forward = new Vector3(cosPitch * cosYaw, sinPitch, cosPitch * sinYaw);
        var right = new Vector3(-sinYaw, 0, cosYaw);
        return (forward, right, Cross(right, forward));
    }

    private void QueueSoftwareRender()
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
                RenderSoftware();
            }));
    }

    private void RenderSoftware()
    {
        if (_disposed || _usingOpenGl || ActualWidth < 1 || ActualHeight < 1)
        {
            return;
        }

        /* Stretch.Fill scales the bitmap onto the control, so the render target has
           to keep the control's aspect ratio while it is trimmed for cost. */
        var budget = Math.Min(
            1.0,
            Math.Min(MaximumRenderWidth / ActualWidth, MaximumRenderHeight / ActualHeight));
        var width = Math.Max(1, (int)Math.Round(ActualWidth * budget));
        var height = Math.Max(1, (int)Math.Round(ActualHeight * budget));
        var stride = checked(width * 4);
        var pixels = new byte[checked(stride * height)];

        var (forward, right, up) = CreateBasis(_yaw, _pitch);
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
        _softwareSurface.Source = bitmap;
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

        var texture = _faces[(int)face];
        var sourceX = Math.Clamp((int)((u + 1) * 0.5 * texture.Width), 0, texture.Width - 1);
        var sourceY = Math.Clamp((int)((v + 1) * 0.5 * texture.Height), 0, texture.Height - 1);
        var sourceOffset = (sourceY * texture.Width + sourceX) * 4;
        destination[destinationOffset] = texture.Pixels[sourceOffset];
        destination[destinationOffset + 1] = texture.Pixels[sourceOffset + 1];
        destination[destinationOffset + 2] = texture.Pixels[sourceOffset + 2];
        destination[destinationOffset + 3] = texture.Pixels[sourceOffset + 3];
    }

    private static FaceTexture[] DecodeFaces(SkyboxFaceSet faceSet)
    {
        var result = new FaceTexture[6];
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
            result[(int)face] = new FaceTexture(converted.PixelWidth, converted.PixelHeight, pixels);
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
