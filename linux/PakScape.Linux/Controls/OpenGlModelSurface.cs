using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;

namespace PakScape.Linux.Controls;

/// <summary>Hosts the native model renderer inside Avalonia's current GL context.</summary>
internal sealed class OpenGlModelSurface : OpenGlControlBase
{
    private readonly ModelPreviewControl _owner;

    public OpenGlModelSurface(ModelPreviewControl owner)
    {
        _owner = owner;
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);
        RequestNextFrameRendering();
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _owner.DeinitializeOpenGl();
        base.OnOpenGlDeinit(gl);
    }

    protected override void OnOpenGlRender(GlInterface gl, int framebuffer)
    {
        _owner.RenderOpenGl(gl, framebuffer);
    }
}
