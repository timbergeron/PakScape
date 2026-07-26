namespace PakStudio.Core.Preview;

public enum ArchivePreviewKind
{
    Metadata,
    Text,
    Audio,
    EncodedImage,
    Bitmap,
    Model,
}

public sealed record PreviewBitmap(int Width, int Height, byte[] BgraPixels)
{
    public int Stride => checked(Width * 4);
}

public readonly record struct BspLevelPreviewOptions(
    bool ShowArmors,
    bool ShowMegaHealth,
    bool ShowPowerups,
    bool ShowMajorWeapons,
    bool ShowFlags)
{
    public static BspLevelPreviewOptions GeometryOnly => default;

    public static BspLevelPreviewOptions All => new(
        ShowArmors: true,
        ShowMegaHealth: true,
        ShowPowerups: true,
        ShowMajorWeapons: true,
        ShowFlags: true);
}

/// <summary>
/// A model the viewer renders interactively. MD3 and MD5 name their skins, so the
/// resolver finds those entries in the same archive.
/// </summary>
public sealed record PreviewModel(
    byte[] Data,
    string Extension,
    ModelTextureResolver Textures);

public sealed record ArchivePreview(
    string Title,
    string TypeDescription,
    long Size,
    ArchivePreviewKind Kind,
    string? Text = null,
    byte[]? EncodedAudio = null,
    byte[]? EncodedImage = null,
    int ImageWidth = 0,
    int ImageHeight = 0,
    PreviewBitmap? Bitmap = null,
    string? Message = null,
    PreviewModel? Model = null);

public sealed class ArchivePreviewException : Exception
{
    public ArchivePreviewException(string message) : base(message)
    {
    }
}
