using System.Buffers.Binary;
using PakStudio.Core.Preview;
using Xunit;

namespace PakStudio.Tests;

public sealed class WadPreviewCatalogTests
{
    [Fact]
    public void ListsEveryEntryWithNamesAndThumbnails()
    {
        var wad = CreateWad2(
            ("crate_top", (byte)'D', MipTexture(8, 8)),
            ("palette", (byte)'@', new byte[768]),
            ("mystery", (byte)'Z', new byte[4]));

        Assert.True(WadPreviewCatalog.TryRead(wad, out var items));

        Assert.Collection(
            items,
            texture =>
            {
                Assert.Equal("crate_top", texture.Name);
                Assert.Equal("Mip texture", texture.TypeDescription);
                Assert.Equal(8, texture.Width);
                Assert.Equal(8, texture.Height);
                Assert.Equal(8, texture.Thumbnail?.Width);
            },
            palette =>
            {
                Assert.Equal("palette", palette.Name);
                Assert.Equal("Palette", palette.TypeDescription);
                Assert.NotNull(palette.Thumbnail);
            },
            unknown =>
            {
                Assert.Equal("mystery", unknown.Name);
                Assert.Equal("Type 0x5A", unknown.TypeDescription);
                Assert.Null(unknown.Thumbnail);
            });
    }

    [Fact]
    public void DownscalesLargeTexturesToTheThumbnailCap()
    {
        var wad = CreateWad2(("big", (byte)'D', MipTexture(512, 256)));

        Assert.True(WadPreviewCatalog.TryRead(wad, out var items));

        var item = Assert.Single(items);
        Assert.Equal(512, item.Width);
        Assert.Equal(WadPreviewCatalog.MaximumThumbnailDimension, item.Thumbnail?.Width);
        Assert.Equal(WadPreviewCatalog.MaximumThumbnailDimension / 2, item.Thumbnail?.Height);
    }

    [Fact]
    public void RejectsNonWadData()
    {
        Assert.False(WadPreviewCatalog.TryRead("PACK\0\0\0\0\0\0\0\0"u8.ToArray(), out _));
    }

    private static byte[] MipTexture(int width, int height)
    {
        var lump = new byte[40 + width * height];
        BinaryPrimitives.WriteInt32LittleEndian(lump.AsSpan(16), width);
        BinaryPrimitives.WriteInt32LittleEndian(lump.AsSpan(20), height);
        BinaryPrimitives.WriteInt32LittleEndian(lump.AsSpan(24), 40);
        lump.AsSpan(40).Fill(15);
        return lump;
    }

    private static byte[] CreateWad2(params (string Name, byte Type, byte[] Lump)[] entries)
    {
        var lumpBytes = entries.Sum(entry => entry.Lump.Length);
        var directoryOffset = 12 + lumpBytes;
        var data = new byte[directoryOffset + entries.Length * 32];
        "WAD2"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), entries.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), directoryOffset);

        var lumpOffset = 12;
        for (var index = 0; index < entries.Length; index++)
        {
            var (name, type, lump) = entries[index];
            lump.CopyTo(data, lumpOffset);
            var entry = directoryOffset + index * 32;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(entry), lumpOffset);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(entry + 4), lump.Length);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(entry + 8), lump.Length);
            data[entry + 12] = type;
            System.Text.Encoding.ASCII.GetBytes(name).CopyTo(data, entry + 16);
            lumpOffset += lump.Length;
        }
        return data;
    }
}
