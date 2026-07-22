using System.Buffers.Binary;
using PakStudio.Core.Nodes;
using PakStudio.Core.Preview;
using Xunit;

namespace PakStudio.Tests;

public sealed class ArchivePreviewBuilderTests
{
    [Fact]
    public void SupportedImageHeadersReportSafeDimensions()
    {
        byte[][] headers =
        [
            [
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
                0, 0, 0, 3, 0, 0, 0, 2,
            ],
            [
                0xFF, 0xD8, 0xFF, 0xC0, 0, 17, 8, 0, 2, 0, 3,
                3, 1, 0x11, 0, 2, 0x11, 0, 3, 0x11, 0,
            ],
            [(byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 3, 0, 2, 0],
            [
                (byte)'B', (byte)'M', 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                40, 0, 0, 0, 3, 0, 0, 0, 2, 0, 0, 0,
            ],
            [
                (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0,
                2, 0,
                0, 1, 3, 0, 1, 0, 0, 0, 3, 0, 0, 0,
                1, 1, 3, 0, 1, 0, 0, 0, 2, 0, 0, 0,
            ],
        ];

        foreach (var header in headers)
        {
            Assert.True(EncodedImageInspector.TryGetSafeDimensions(header, out var width, out var height));
            Assert.Equal(3, width);
            Assert.Equal(2, height);
        }
    }

    [Fact]
    public void CfgFilesArePreviewedAsText()
    {
        var file = new ArchiveFileNode("autoexec.cfg", "echo hello\n"u8.ToArray());

        var preview = ArchivePreviewBuilder.Build(file);

        Assert.Equal(ArchivePreviewKind.Text, preview.Kind);
        Assert.Equal("echo hello\n", preview.Text);
    }

    [Theory]
    [InlineData("SOUND.WAV")]
    [InlineData("music.mp3")]
    [InlineData("music.flac")]
    [InlineData("music.ogg")]
    [InlineData("music.opus")]
    [InlineData("music.it")]
    [InlineData("music.s3m")]
    [InlineData("music.xm")]
    [InlineData("music.mod")]
    [InlineData("music.umx")]
    public void QssMAudioFilesRemainEncodedForPlayback(string fileName)
    {
        byte[] data = [1, 2, 3];
        var file = new ArchiveFileNode(fileName, data);

        var preview = ArchivePreviewBuilder.Build(file);

        Assert.Equal(ArchivePreviewKind.Audio, preview.Kind);
        Assert.Same(data, preview.EncodedAudio);
    }

    [Fact]
    public void CommonImagesRemainEncodedForNativePresentation()
    {
        byte[] data =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            0, 0, 0, 1, 0, 0, 0, 1,
        ];
        var file = new ArchiveFileNode("image.png", data);

        var preview = ArchivePreviewBuilder.Build(file);

        Assert.Equal(ArchivePreviewKind.EncodedImage, preview.Kind);
        Assert.Same(data, preview.EncodedImage);
        Assert.Equal(1, preview.ImageWidth);
        Assert.Equal(1, preview.ImageHeight);
    }

    [Fact]
    public void OversizedEncodedImagesReceiveMetadataFallback()
    {
        byte[] data =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            0, 0, 0x20, 1, 0, 0, 0, 1,
        ];

        var preview = ArchivePreviewBuilder.Build(new ArchiveFileNode("huge.png", data));

        Assert.Equal(ArchivePreviewKind.Metadata, preview.Kind);
        Assert.Contains("safe preview dimensions", preview.Message);
    }

    [Fact]
    public void HeaderedLmpProducesBgraBitmap()
    {
        byte[] data = [1, 0, 0, 0, 1, 0, 0, 0, 0];
        var file = new ArchiveFileNode("pixel.lmp", data);

        var preview = ArchivePreviewBuilder.Build(file);

        Assert.Equal(ArchivePreviewKind.Bitmap, preview.Kind);
        var bitmap = Assert.IsType<PreviewBitmap>(preview.Bitmap);
        Assert.Equal(1, bitmap.Width);
        Assert.Equal(1, bitmap.Height);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, bitmap.BgraPixels);
    }

    [Fact]
    public void GfxWadDoesNotReadPixelsPastTheDeclaredLump()
    {
        var preview = ArchivePreviewBuilder.Build(
            new ArchiveFileNode("gfx.wad", CreateSinglePictureWad(diskSize: 8)));

        Assert.Equal(ArchivePreviewKind.Metadata, preview.Kind);
    }

    [Fact]
    public void GfxWadDecodesAnUncompressedPictureWithinItsLump()
    {
        var preview = ArchivePreviewBuilder.Build(
            new ArchiveFileNode("gfx.wad", CreateSinglePictureWad(diskSize: 9)));

        Assert.Equal(ArchivePreviewKind.Bitmap, preview.Kind);
        Assert.Equal(1, preview.Bitmap?.Width);
        Assert.Equal(1, preview.Bitmap?.Height);
    }

    [Fact]
    public void GfxWadRejectsCompressedLumps()
    {
        var preview = ArchivePreviewBuilder.Build(
            new ArchiveFileNode("gfx.wad", CreateSinglePictureWad(diskSize: 9, compression: 1)));

        Assert.Equal(ArchivePreviewKind.Metadata, preview.Kind);
    }

    [Fact]
    public void ExtremeFiniteBspCoordinatesRemainBounded()
    {
        var preview = ArchivePreviewBuilder.Build(
            new ArchiveFileNode("extreme.bsp", CreateExtremeCoordinateBsp()));

        Assert.Equal(ArchivePreviewKind.Bitmap, preview.Kind);
        Assert.Equal(768, preview.Bitmap?.Width);
        Assert.Equal(768, preview.Bitmap?.Height);
    }

    [Theory]
    [InlineData("random.lmp")]
    [InlineData("random.mdl")]
    [InlineData("random.spr")]
    [InlineData("random.pcx")]
    [InlineData("random.tga")]
    [InlineData("random.bsp")]
    [InlineData("gfx.wad")]
    public void MalformedQuakeAssetsDoNotThrow(string fileName)
    {
        var random = new Random(12_345);
        for (var length = 0; length < 256; length++)
        {
            var data = new byte[length];
            random.NextBytes(data);

            var exception = Record.Exception(
                () => ArchivePreviewBuilder.Build(new ArchiveFileNode(fileName, data)));

            Assert.Null(exception);
        }
    }

    [Fact]
    public void UnknownFilesReceiveMetadataFallback()
    {
        var file = new ArchiveFileNode("progs.dat", [1, 2, 3]);

        var preview = ArchivePreviewBuilder.Build(file);

        Assert.Equal(ArchivePreviewKind.Metadata, preview.Kind);
        Assert.Contains("No rich preview", preview.Message);
    }

    [Fact]
    public void SelectionItemLimitIsEnforced()
    {
        var nodes = Enumerable.Range(0, ArchivePreviewBuilder.MaximumItemCount + 1)
            .Select(index => (ArchiveNode)new ArchiveFolderNode($"folder-{index}"))
            .ToList();

        var exception = Assert.Throws<ArchivePreviewException>(
            () => ArchivePreviewBuilder.ValidateSelection(nodes));

        Assert.Contains("1,000", exception.Message);
    }

    private static byte[] CreateSinglePictureWad(int diskSize, byte compression = 0)
    {
        const int directoryOffset = 12;
        const int lumpOffset = 44;
        var data = new byte[53];
        "WAD2"u8.CopyTo(data);
        WriteInt32(data, 4, 1);
        WriteInt32(data, 8, directoryOffset);
        WriteInt32(data, directoryOffset, lumpOffset);
        WriteInt32(data, directoryOffset + 4, diskSize);
        WriteInt32(data, directoryOffset + 8, 9);
        data[directoryOffset + 12] = (byte)'B';
        data[directoryOffset + 13] = compression;
        WriteInt32(data, lumpOffset, 1);
        WriteInt32(data, lumpOffset + 4, 1);
        data[lumpOffset + 8] = 0;
        return data;
    }

    private static byte[] CreateExtremeCoordinateBsp()
    {
        const int headerSize = 124;
        const int vertexOffset = headerSize;
        const int faceOffset = vertexOffset + 24;
        const int edgeOffset = faceOffset + 20;
        const int surfEdgeOffset = edgeOffset + 4;
        var data = new byte[surfEdgeOffset + 8];
        WriteInt32(data, 0, 29);
        WriteLump(data, 3, vertexOffset, 24);
        WriteLump(data, 7, faceOffset, 20);
        WriteLump(data, 12, edgeOffset, 4);
        WriteLump(data, 13, surfEdgeOffset, 8);

        WriteSingle(data, vertexOffset, -float.MaxValue);
        WriteSingle(data, vertexOffset + 4, -float.MaxValue);
        WriteSingle(data, vertexOffset + 12, float.MaxValue);
        WriteSingle(data, vertexOffset + 16, float.MaxValue);
        WriteInt32(data, faceOffset + 4, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(faceOffset + 8, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(edgeOffset, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(edgeOffset + 2, 2), 1);
        WriteInt32(data, surfEdgeOffset, 0);
        WriteInt32(data, surfEdgeOffset + 4, 0);
        return data;
    }

    private static void WriteLump(byte[] data, int index, int offset, int length)
    {
        var headerOffset = 4 + index * 8;
        WriteInt32(data, headerOffset, offset);
        WriteInt32(data, headerOffset + 4, length);
    }

    private static void WriteInt32(byte[] data, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), value);

    private static void WriteSingle(byte[] data, int offset, float value) =>
        WriteInt32(data, offset, BitConverter.SingleToInt32Bits(value));
}
