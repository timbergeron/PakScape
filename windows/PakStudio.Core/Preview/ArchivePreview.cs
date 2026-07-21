namespace PakStudio.Core.Preview;

public enum ArchivePreviewKind
{
    Metadata,
    Text,
    Audio,
    EncodedImage,
    Bitmap,
}

public sealed record PreviewBitmap(int Width, int Height, byte[] BgraPixels)
{
    public int Stride => checked(Width * 4);
}

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
    string? Message = null);

public sealed class ArchivePreviewException : Exception
{
    public ArchivePreviewException(string message) : base(message)
    {
    }
}
