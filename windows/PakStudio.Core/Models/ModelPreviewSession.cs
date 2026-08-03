using System.Runtime.InteropServices;
using PakStudio.Core.Preview;

namespace PakStudio.Core.Models;

/// <summary>
/// Everything the desktop viewers share: it opens a model, resolves its skins from
/// the archive, and forwards input to the native orbit camera. Each toolkit only
/// has to own a pixel buffer and an event loop.
/// </summary>
public sealed class ModelPreviewSession : IDisposable
{
    /* The bitmap fallback is capped and stretched to fill larger panes. */
    private const int MaximumRenderPixels = 1_400_000;
    private const int MinimumRenderDimension = 16;

    private readonly NativeModelViewer _viewer;
    private readonly int _missingTextureCount;
    private int _skinIndex;

    private ModelPreviewSession(NativeModelViewer viewer, int missingTextureCount)
    {
        _viewer = viewer;
        _missingTextureCount = missingTextureCount;
    }

    /// <summary>
    /// Opens <paramref name="model"/>. Skins stored in formats the host toolkit
    /// decodes (PNG and JPEG) are passed to <paramref name="decodeEncodedImage"/>.
    /// </summary>
    public static ModelPreviewSession Create(
        PreviewModel model,
        Func<byte[], ModelTextureData?>? decodeEncodedImage)
    {
        ArgumentNullException.ThrowIfNull(model);

        var viewer = NativeModelViewer.Create(model.Data, model.Extension);
        try
        {
            var missing = 0;
            var overrides = viewer.TextureRequests.Count > 0
                ? model.Textures.ReadSkinOverrides()
                : new Dictionary<string, string>();

            foreach (var request in viewer.TextureRequests)
            {
                var name = overrides.TryGetValue(request.Surface, out var replacement)
                    ? replacement
                    : request.Name;

                if (!TryUpload(viewer, model, request.Index, name, decodeEncodedImage))
                {
                    missing++;
                }
            }

            return new ModelPreviewSession(viewer, missing);
        }
        catch
        {
            viewer.Dispose();
            throw;
        }
    }

    private static bool TryUpload(
        NativeModelViewer viewer,
        PreviewModel model,
        int index,
        string name,
        Func<byte[], ModelTextureData?>? decodeEncodedImage)
    {
        if (!model.Textures.TryResolve(name, out var resolved))
        {
            return false;
        }

        var data = resolved.Decoded;
        if (data is null && resolved.EncodedImage is { } encoded && decodeEncodedImage is not null)
        {
            data = decodeEncodedImage(encoded);
        }
        if (data is null)
        {
            return false;
        }

        viewer.SetTexture(index, data.RgbaPixels, data.Width, data.Height);
        return true;
    }

    public ModelStatistics Statistics => _viewer.Statistics;

    public int SkinCount => _viewer.Statistics.SkinCount;

    public int SkinIndex => _skinIndex;

    public bool ShowInteractionPrompt => _viewer.ShowInteractionPrompt;

    /// <summary>A short description of what is on screen, for the preview subtitle.</summary>
    public string StatusLine
    {
        get
        {
            var stats = _viewer.Statistics;
            var format = stats.Format switch
            {
                ModelFormat.Mdl => "Quake MDL",
                ModelFormat.Md3 => "Quake III MD3",
                ModelFormat.Md5 => "Doom 3 MD5",
                ModelFormat.Spr => "Quake sprite",
                ModelFormat.Bsp => "Quake brush model",
                _ => "Model",
            };

            /* A sprite is one quad, so its frame count is the only count worth showing. */
            if (stats.Format == ModelFormat.Spr)
            {
                var frameLabel = stats.FrameCount == 1 ? "frame" : "frames";
                return $"{format} • {stats.FrameCount:N0} {frameLabel}";
            }

            var parts = new List<string>
            {
                format,
                $"{stats.TriangleCount:N0} {(stats.TriangleCount == 1 ? "triangle" : "triangles")}",
            };
            if (stats.SurfaceCount > 1)
            {
                parts.Add($"{stats.SurfaceCount:N0} surfaces");
            }
            if (stats.FrameCount > 1)
            {
                parts.Add($"{stats.FrameCount:N0} frames");
            }
            if (SkinCount > 1)
            {
                parts.Add($"skin {_skinIndex + 1} of {SkinCount}");
            }
            if (_missingTextureCount > 0)
            {
                parts.Add(_missingTextureCount == 1
                    ? "1 skin not in this archive"
                    : $"{_missingTextureCount:N0} skins not in this archive");
            }
            return string.Join(" • ", parts);
        }
    }

    public bool TrySelectSkin(int skinIndex)
    {
        if (skinIndex == _skinIndex || !_viewer.TrySetSkin(skinIndex))
        {
            return false;
        }
        _skinIndex = skinIndex;
        return true;
    }

    public ModelSkin? GetSelectedSkin() => _viewer.GetSkin(_skinIndex);

    public bool DarkBackground
    {
        set => _viewer.DarkBackground = value;
    }

    public bool AutoRotate
    {
        set => _viewer.AutoRotate = value;
    }

    public bool AnimationEnabled
    {
        set => _viewer.AnimationEnabled = value;
    }

    public double AnimationSpeed
    {
        set => _viewer.AnimationSpeed = value;
    }

    public void BeginInteraction() => _viewer.BeginInteraction();

    public void Orbit(double dx, double dy) => _viewer.Orbit(dx, dy);

    public void Pan(double dx, double dy) => _viewer.Pan(dx, dy);

    public void Zoom(double steps) => _viewer.Zoom(steps);

    public void EndInteraction() => _viewer.EndInteraction();

    public void Nudge(ModelNudge nudge) => _viewer.Nudge(nudge);

    public void Reset() => _viewer.Reset();

    public bool Advance(double elapsedSeconds) => _viewer.Advance(elapsedSeconds);

    public bool Render(nint bgraPixels, int width, int height, int stride) =>
        _viewer.Render(bgraPixels, width, height, stride);

    /// <summary>Renders one deterministic software frame for an archive thumbnail.</summary>
    public PreviewBitmap? RenderBitmap(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var pixels = new byte[checked(width * height * 4)];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            return _viewer.Render(handle.AddrOfPinnedObject(), width, height, width * 4)
                ? new PreviewBitmap(width, height, pixels)
                : null;
        }
        finally
        {
            handle.Free();
        }
    }

    public bool RenderOpenGl(int width, int height) => _viewer.RenderOpenGl(width, height);

    public void DeinitializeOpenGl() => _viewer.DeinitializeOpenGl();

    /// <summary>Fits a pane size to the bitmap renderer's budget.</summary>
    public static (int Width, int Height) ClampRenderSize(double width, double height)
    {
        var pixelWidth = Math.Max(MinimumRenderDimension, (int)Math.Round(width));
        var pixelHeight = Math.Max(MinimumRenderDimension, (int)Math.Round(height));

        var total = (long)pixelWidth * pixelHeight;
        if (total <= MaximumRenderPixels)
        {
            return (pixelWidth, pixelHeight);
        }

        var scale = Math.Sqrt(MaximumRenderPixels / (double)total);
        return (
            Math.Max(MinimumRenderDimension, (int)(pixelWidth * scale)),
            Math.Max(MinimumRenderDimension, (int)(pixelHeight * scale)));
    }

    public void Dispose() => _viewer.Dispose();
}
