using System.Buffers.Binary;
using PakStudio.Core.Preview;
using Xunit;

namespace PakStudio.Tests;

public sealed class ImageFormatConverterTests
{
    private static readonly byte[] TwoPixelLmp =
    [
        2, 0, 0, 0,
        1, 0, 0, 0,
        15, 255,
    ];

    [Theory]
    [InlineData(ImageSaveFormat.Png)]
    [InlineData(ImageSaveFormat.Jpeg)]
    [InlineData(ImageSaveFormat.Tga)]
    public void ConvertsLmpToStandardFormatAndBack(ImageSaveFormat format)
    {
        var encoded = ImageFormatConverter.Convert("pixel.lmp", TwoPixelLmp, format);

        Assert.NotEmpty(encoded);
        var extension = ImageFormatConverter.ExtensionFor(format);
        var roundTrip = ImageFormatConverter.Convert("pixel" + extension, encoded, ImageSaveFormat.Lmp);
        Assert.Equal(10, roundTrip.Length);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(roundTrip.AsSpan(0, 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(roundTrip.AsSpan(4, 4)));
    }

    [Fact]
    public void PreservesTransparentPixelsWhenRoundTrippingThroughPng()
    {
        var png = ImageFormatConverter.Convert("pixel.lmp", TwoPixelLmp, ImageSaveFormat.Png);
        var roundTrip = ImageFormatConverter.Convert("pixel.png", png, ImageSaveFormat.Lmp);

        Assert.Equal(255, roundTrip[9]);
    }

    [Theory]
    [InlineData(ImageSaveFormat.Lmp)]
    [InlineData(ImageSaveFormat.Png)]
    [InlineData(ImageSaveFormat.Jpeg)]
    [InlineData(ImageSaveFormat.Tga)]
    public void EncodesDecodedRgbaSkins(ImageSaveFormat format)
    {
        byte[] pixels = [255, 0, 0, 255, 0, 0, 0, 0];

        var encoded = ImageFormatConverter.EncodeRgba(2, 1, pixels, format);

        Assert.NotEmpty(encoded);
        var extension = ImageFormatConverter.ExtensionFor(format);
        var roundTrip = ImageFormatConverter.Convert("skin" + extension, encoded, ImageSaveFormat.Lmp);
        Assert.Equal(10, roundTrip.Length);
        if (format != ImageSaveFormat.Jpeg)
        {
            Assert.Equal(255, roundTrip[9]);
        }
    }

    [Theory]
    [InlineData("picture.lmp")]
    [InlineData("picture.jpg")]
    [InlineData("picture.jpeg")]
    [InlineData("picture.png")]
    [InlineData("picture.tga")]
    public void RecognizesSupportedSources(string fileName)
    {
        Assert.True(ImageFormatConverter.IsSupportedSource(fileName));
    }

    [Theory]
    [InlineData("*water", "_water")]
    [InlineData("maps/wall", "maps_wall")]
    [InlineData("CON", "_CON")]
    [InlineData("", "texture3")]
    public void MakesTextureNamesSafeForExport(string name, string expected)
    {
        Assert.Equal(expected, ImageFormatConverter.SafeTextureFileStem(name, 2));
    }
}
