using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PakStudio.Core.Models;

public enum ModelFormat
{
    Unknown = 0,
    Mdl = 1,
    Md3 = 2,
    Md5 = 3,
    Spr = 4,
    Bsp = 5,
}

public enum ModelNudge
{
    Left = 0,
    Right = 1,
    Up = 2,
    Down = 3,
    In = 4,
    Out = 5,
}

public sealed record ModelStatistics(
    ModelFormat Format,
    int SurfaceCount,
    int VertexCount,
    int TriangleCount,
    int FrameCount,
    int SkinCount,
    int TextureRequestCount,
    int TexturedSurfaceCount);

public sealed record ModelTextureRequest(int Index, string Surface, string Name);

public sealed record ModelSkin(byte[] RgbaPixels, int Width, int Height);

public sealed record EmbeddedModelTexture(string Name, byte[] RgbaPixels, int Width, int Height);

/// <summary>
/// Cross-platform managed owner for PakScape's private model viewer, which parses
/// MDL, MD3, and MD5 meshes plus SPR sprites and BSP brush models, and software
/// renders them with an orbit camera.
/// </summary>
public sealed class NativeModelViewer : IDisposable
{
    private const int ErrorBufferSize = 512;
    private const int NameBufferSize = 512;

    private readonly SafeModelHandle _model;
    private readonly SafeModelViewHandle _view;
    private bool _disposed;

    private NativeModelViewer(SafeModelHandle model, SafeModelViewHandle view)
    {
        _model = model;
        _view = view;
        Statistics = ReadStatistics(model);
        TextureRequests = ReadTextureRequests(model, Statistics.TextureRequestCount);
    }

    public ModelStatistics Statistics { get; private set; }

    public IReadOnlyList<ModelTextureRequest> TextureRequests { get; }

    public static bool SupportsExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        try
        {
            return NativeMethods.SupportsExtension(extension) != 0;
        }
        catch (Exception exception) when (IsMissingBackend(exception))
        {
            return false;
        }
    }

    /// <summary>
    /// True when BSP data is a brush model — an ammo box or a health kit — rather than
    /// a playable level. A level has its own flat overview preview, so only brush models
    /// are handed to the viewer. The file's content decides, not its name.
    /// </summary>
    public static bool IsBspBrushModel(byte[] bspData)
    {
        ArgumentNullException.ThrowIfNull(bspData);
        if (bspData.Length == 0)
        {
            return false;
        }

        try
        {
            return NativeMethods.BspIsBrushModel(bspData, (nuint)bspData.Length) != 0;
        }
        catch (Exception exception) when (IsMissingBackend(exception))
        {
            return false;
        }
    }

    public static NativeModelViewer Create(byte[] modelData, string extension)
    {
        ArgumentNullException.ThrowIfNull(modelData);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        if (modelData.Length == 0)
        {
            throw new ArgumentException("The model file is empty.", nameof(modelData));
        }

        var errorBuffer = new byte[ErrorBufferSize];
        SafeModelHandle? model = null;
        try
        {
            model = NativeMethods.ModelCreate(
                modelData,
                (nuint)modelData.Length,
                extension,
                errorBuffer,
                (nuint)errorBuffer.Length);
            if (model.IsInvalid)
            {
                var message = DecodeError(errorBuffer);
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(message)
                        ? "The model could not be read."
                        : message);
            }

            var view = NativeMethods.ViewCreate(model);
            if (view.IsInvalid)
            {
                view.Dispose();
                throw new InvalidOperationException("The model viewer could not be started.");
            }

            return new NativeModelViewer(model, view);
        }
        catch (Exception exception) when (IsMissingBackend(exception))
        {
            model?.Dispose();
            throw MissingBackend(exception);
        }
        catch
        {
            model?.Dispose();
            throw;
        }
    }

    /// <summary>Uploads straight RGBA8 pixels for one of the model's texture requests.</summary>
    public void SetTexture(int index, byte[] rgbaPixels, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rgbaPixels);
        ThrowIfDisposed();
        if (width <= 0 || height <= 0 || (long)width * height * 4 > rgbaPixels.LongLength)
        {
            throw new ArgumentException("The texture dimensions do not match the pixel buffer.");
        }

        if (NativeMethods.ModelSetTexture(_model, index, rgbaPixels, width, height) == 0)
        {
            Statistics = ReadStatistics(_model);
        }
    }

    public bool TrySetSkin(int skinIndex)
    {
        ThrowIfDisposed();
        return NativeMethods.ModelSetSkin(_model, skinIndex) == 0;
    }

    public ModelSkin? GetSkin(int skinIndex)
    {
        ThrowIfDisposed();
        if (NativeMethods.ModelGetSkinSize(_model, skinIndex, out var width, out var height) != 0 ||
            width <= 0 || height <= 0)
        {
            return null;
        }

        var byteCount = checked(width * height * 4);
        var pixels = new byte[byteCount];
        return NativeMethods.ModelCopySkinRgba(
            _model, skinIndex, pixels, (nuint)pixels.Length) == 0
            ? new ModelSkin(pixels, width, height)
            : null;
    }

    public int EmbeddedTextureCount
    {
        get
        {
            ThrowIfDisposed();
            return NativeMethods.ModelTextureCount(_model);
        }
    }

    public EmbeddedModelTexture? GetEmbeddedTexture(int textureIndex)
    {
        ThrowIfDisposed();
        var nameBytes = new byte[256];
        if (NativeMethods.ModelTextureName(
                _model, textureIndex, nameBytes, (nuint)nameBytes.Length) != 0 ||
            NativeMethods.ModelGetTextureSize(
                _model, textureIndex, out var width, out var height) != 0 ||
            width <= 0 || height <= 0)
        {
            return null;
        }

        var pixels = new byte[checked(width * height * 4)];
        if (NativeMethods.ModelCopyTextureRgba(
                _model, textureIndex, pixels, (nuint)pixels.Length) != 0)
        {
            return null;
        }

        return new EmbeddedModelTexture(DecodeError(nameBytes), pixels, width, height);
    }

    public bool ShowInteractionPrompt
    {
        get
        {
            ThrowIfDisposed();
            return NativeMethods.ViewShowInteractionPrompt(_view) != 0;
        }
    }

    public bool DarkBackground
    {
        set
        {
            ThrowIfDisposed();
            NativeMethods.ViewSetDarkBackground(_view, value ? 1 : 0);
        }
    }

    public bool AutoRotate
    {
        set
        {
            ThrowIfDisposed();
            NativeMethods.ViewSetAutoRotate(_view, value ? 1 : 0);
        }
    }

    public bool AnimationEnabled
    {
        set
        {
            ThrowIfDisposed();
            NativeMethods.ViewSetAnimationEnabled(_view, value ? 1 : 0);
        }
    }

    public double AnimationSpeed
    {
        set
        {
            ThrowIfDisposed();
            NativeMethods.ViewSetAnimationSpeed(_view, (float)value);
        }
    }

    public void BeginInteraction()
    {
        ThrowIfDisposed();
        NativeMethods.ViewBeginInteraction(_view);
    }

    /// <summary>Pointer deltas are in rendered device pixels.</summary>
    public void Orbit(double dx, double dy)
    {
        ThrowIfDisposed();
        NativeMethods.ViewOrbit(_view, (float)dx, (float)dy);
    }

    public void Pan(double dx, double dy)
    {
        ThrowIfDisposed();
        NativeMethods.ViewPan(_view, (float)dx, (float)dy);
    }

    public void Zoom(double steps)
    {
        ThrowIfDisposed();
        NativeMethods.ViewZoom(_view, (float)steps);
    }

    public void EndInteraction()
    {
        ThrowIfDisposed();
        NativeMethods.ViewEndInteraction(_view);
    }

    public void Nudge(ModelNudge nudge)
    {
        ThrowIfDisposed();
        NativeMethods.ViewNudge(_view, (int)nudge);
    }

    public void Reset()
    {
        ThrowIfDisposed();
        NativeMethods.ViewReset(_view);
    }

    /// <summary>Returns true when the next frame differs from the one already shown.</summary>
    public bool Advance(double elapsedSeconds)
    {
        ThrowIfDisposed();
        return NativeMethods.ViewAdvance(_view, elapsedSeconds) != 0;
    }

    /// <summary>Renders BGRA8 rows into a caller-owned buffer.</summary>
    public bool Render(nint bgraPixels, int width, int height, int stride)
    {
        ThrowIfDisposed();
        return NativeMethods.ViewRender(_view, bgraPixels, width, height, stride) == 0;
    }

    /// <summary>Initializes textures in the current OpenGL context.</summary>
    public bool InitializeOpenGl()
    {
        ThrowIfDisposed();
        return NativeMethods.ViewGlInit(_view) == 0;
    }

    /// <summary>Draws directly into the current OpenGL context.</summary>
    public bool RenderOpenGl(int width, int height)
    {
        ThrowIfDisposed();
        return NativeMethods.ViewRenderGl(_view, width, height) == 0;
    }

    /// <summary>Releases OpenGL resources while its context is current.</summary>
    public void DeinitializeOpenGl()
    {
        if (!_disposed)
        {
            NativeMethods.ViewGlDeinit(_view);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _view.Dispose();
        _model.Dispose();
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static ModelStatistics ReadStatistics(SafeModelHandle model)
    {
        NativeModelStats stats = default;
        NativeMethods.ModelGetStats(model, ref stats);
        return new ModelStatistics(
            (ModelFormat)stats.Format,
            stats.SurfaceCount,
            stats.VertexCount,
            stats.TriangleCount,
            stats.FrameCount,
            stats.SkinCount,
            stats.TextureRequestCount,
            stats.TexturedSurfaceCount);
    }

    private static IReadOnlyList<ModelTextureRequest> ReadTextureRequests(
        SafeModelHandle model,
        int count)
    {
        if (count <= 0)
        {
            return [];
        }

        var requests = new List<ModelTextureRequest>(count);
        var surface = new byte[NameBufferSize];
        var name = new byte[NameBufferSize];
        for (var index = 0; index < count; index++)
        {
            Array.Clear(surface);
            Array.Clear(name);
            NativeMethods.ModelTextureRequestSurface(model, index, surface, (nuint)surface.Length);
            NativeMethods.ModelTextureRequestName(model, index, name, (nuint)name.Length);
            requests.Add(new ModelTextureRequest(index, DecodeError(surface), DecodeError(name)));
        }
        return requests;
    }

    private static string DecodeError(byte[] buffer)
    {
        var length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
        {
            length = buffer.Length;
        }
        return Encoding.UTF8.GetString(buffer, 0, length);
    }

    private static bool IsMissingBackend(Exception exception) =>
        exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    private static InvalidOperationException MissingBackend(Exception innerException) =>
        new(
            "PakScape's native model viewer is missing or incompatible. Reinstall the application.",
            innerException);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeModelStats
    {
        public int Format;
        public int SurfaceCount;
        public int VertexCount;
        public int TriangleCount;
        public int FrameCount;
        public int SkinCount;
        public int TextureRequestCount;
        public int TexturedSurfaceCount;
    }

    private sealed class SafeModelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeModelHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle()
        {
            NativeMethods.ModelDestroy(handle);
            return true;
        }
    }

    private sealed class SafeModelViewHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeModelViewHandle()
            : base(ownsHandle: true)
        {
        }

        protected override bool ReleaseHandle()
        {
            NativeMethods.ViewDestroy(handle);
            return true;
        }
    }

    private static class NativeMethods
    {
        private const string LibraryName = "pakscape_model";

        [DllImport(LibraryName, EntryPoint = "pkm_supports_extension", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SupportsExtension(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string extension);

        [DllImport(LibraryName, EntryPoint = "pkm_bsp_is_brush_model", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int BspIsBrushModel([In] byte[] bspData, nuint bspSize);

        [DllImport(LibraryName, EntryPoint = "pkm_model_create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern SafeModelHandle ModelCreate(
            [In] byte[] modelData,
            nuint modelSize,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string extension,
            [Out] byte[] errorMessage,
            nuint errorMessageSize);

        [DllImport(LibraryName, EntryPoint = "pkm_model_destroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ModelDestroy(nint model);

        [DllImport(LibraryName, EntryPoint = "pkm_model_get_stats", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ModelGetStats(SafeModelHandle model, ref NativeModelStats stats);

        [DllImport(LibraryName, EntryPoint = "pkm_model_texture_request_surface", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ModelTextureRequestSurface(
            SafeModelHandle model,
            int index,
            [Out] byte[] name,
            nuint nameSize);

        [DllImport(LibraryName, EntryPoint = "pkm_model_texture_request_name", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ModelTextureRequestName(
            SafeModelHandle model,
            int index,
            [Out] byte[] name,
            nuint nameSize);

        [DllImport(LibraryName, EntryPoint = "pkm_model_set_texture", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ModelSetTexture(
            SafeModelHandle model,
            int index,
            [In] byte[] rgbaPixels,
            int width,
            int height);

        [DllImport(LibraryName, EntryPoint = "pkm_model_set_skin", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ModelSetSkin(SafeModelHandle model, int skinIndex);

        [DllImport(LibraryName, EntryPoint = "pkm_model_get_skin_size", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ModelGetSkinSize(
            SafeModelHandle model,
            int skinIndex,
            out int width,
            out int height);

        [DllImport(LibraryName, EntryPoint = "pkm_model_copy_skin_rgba", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ModelCopySkinRgba(
            SafeModelHandle model,
            int skinIndex,
            [Out] byte[] rgbaPixels,
            nuint rgbaSize);

        [DllImport(LibraryName, EntryPoint = "pkm_model_texture_count", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ModelTextureCount(SafeModelHandle model);

        [DllImport(LibraryName, EntryPoint = "pkm_model_texture_name", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ModelTextureName(
            SafeModelHandle model,
            int textureIndex,
            [Out] byte[] name,
            nuint nameSize);

        [DllImport(LibraryName, EntryPoint = "pkm_model_get_texture_size", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ModelGetTextureSize(
            SafeModelHandle model,
            int textureIndex,
            out int width,
            out int height);

        [DllImport(LibraryName, EntryPoint = "pkm_model_copy_texture_rgba", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ModelCopyTextureRgba(
            SafeModelHandle model,
            int textureIndex,
            [Out] byte[] rgbaPixels,
            nuint rgbaSize);

        [DllImport(LibraryName, EntryPoint = "pkm_view_create", CallingConvention = CallingConvention.Cdecl)]
        internal static extern SafeModelViewHandle ViewCreate(SafeModelHandle model);

        [DllImport(LibraryName, EntryPoint = "pkm_view_destroy", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ViewDestroy(nint view);

        [DllImport(LibraryName, EntryPoint = "pkm_view_set_dark_background", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ViewSetDarkBackground(SafeModelViewHandle view, int dark);

        [DllImport(LibraryName, EntryPoint = "pkm_view_set_auto_rotate", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ViewSetAutoRotate(SafeModelViewHandle view, int enabled);

        [DllImport(LibraryName, EntryPoint = "pkm_view_set_animation_enabled", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ViewSetAnimationEnabled(SafeModelViewHandle view, int enabled);

        [DllImport(LibraryName, EntryPoint = "pkm_view_set_animation_speed", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ViewSetAnimationSpeed(SafeModelViewHandle view, float speed);

        [DllImport(LibraryName, EntryPoint = "pkm_view_begin_interaction", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ViewBeginInteraction(SafeModelViewHandle view);

        [DllImport(LibraryName, EntryPoint = "pkm_view_orbit", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ViewOrbit(SafeModelViewHandle view, float dx, float dy);

        [DllImport(LibraryName, EntryPoint = "pkm_view_pan", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ViewPan(SafeModelViewHandle view, float dx, float dy);

        [DllImport(LibraryName, EntryPoint = "pkm_view_zoom", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ViewZoom(SafeModelViewHandle view, float zoomSteps);

        [DllImport(LibraryName, EntryPoint = "pkm_view_end_interaction", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ViewEndInteraction(SafeModelViewHandle view);

        [DllImport(LibraryName, EntryPoint = "pkm_view_nudge", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ViewNudge(SafeModelViewHandle view, int nudge);

        [DllImport(LibraryName, EntryPoint = "pkm_view_reset", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ViewReset(SafeModelViewHandle view);

        [DllImport(LibraryName, EntryPoint = "pkm_view_advance", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ViewAdvance(SafeModelViewHandle view, double elapsedSeconds);

        [DllImport(LibraryName, EntryPoint = "pkm_view_show_interaction_prompt", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ViewShowInteractionPrompt(SafeModelViewHandle view);

        [DllImport(LibraryName, EntryPoint = "pkm_view_render", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ViewRender(
            SafeModelViewHandle view,
            nint bgraPixels,
            int width,
            int height,
            int stride);

        [DllImport(LibraryName, EntryPoint = "pkm_view_gl_init", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ViewGlInit(SafeModelViewHandle view);

        [DllImport(LibraryName, EntryPoint = "pkm_view_gl_deinit", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void ViewGlDeinit(SafeModelViewHandle view);

        [DllImport(LibraryName, EntryPoint = "pkm_view_render_gl", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ViewRenderGl(SafeModelViewHandle view, int width, int height);
    }
}
