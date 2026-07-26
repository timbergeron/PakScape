using System.Buffers.Binary;
using PakStudio.Core.Preview;
using Xunit;

namespace PakStudio.Tests;

public sealed class WadTextureExtractorTests
{
    [Fact]
    public void ExtractsNamedOpaqueWad2MipTextures()
    {
        var wad = CreateWad2Texture();

        var texture = Assert.Single(WadTextureExtractor.Extract(wad));

        Assert.Equal("crate_top", texture.Name);
        Assert.Equal(8, texture.Width);
        Assert.Equal(8, texture.Height);
        Assert.Equal(8 * 8 * 4, texture.RgbaPixels.Length);
        Assert.Equal(255, texture.RgbaPixels[3]);
    }

    [Fact]
    public void RejectsInvalidWadDirectories()
    {
        byte[] wad = [(byte)'W', (byte)'A', (byte)'D', (byte)'2', 1, 0, 0, 0];

        Assert.Throws<InvalidDataException>(() => WadTextureExtractor.Extract(wad));
    }

    private static byte[] CreateWad2Texture()
    {
        const int directoryOffset = 12;
        const int lumpOffset = 44;
        const int width = 8;
        const int height = 8;
        const int lumpSize = 40 + width * height;
        var data = new byte[lumpOffset + lumpSize];
        "WAD2"u8.CopyTo(data);
        WriteInt32(data, 4, 1);
        WriteInt32(data, 8, directoryOffset);
        WriteInt32(data, directoryOffset, lumpOffset);
        WriteInt32(data, directoryOffset + 4, lumpSize);
        WriteInt32(data, directoryOffset + 8, lumpSize);
        data[directoryOffset + 12] = (byte)'D';
        "crate_top"u8.CopyTo(data.AsSpan(directoryOffset + 16));
        "crate_top"u8.CopyTo(data.AsSpan(lumpOffset));
        WriteInt32(data, lumpOffset + 16, width);
        WriteInt32(data, lumpOffset + 20, height);
        WriteInt32(data, lumpOffset + 24, 40);
        data[lumpOffset + 40] = 255;
        data.AsSpan(lumpOffset + 41, width * height - 1).Fill(15);
        return data;
    }

    private static void WriteInt32(byte[] data, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset, 4), value);
}
