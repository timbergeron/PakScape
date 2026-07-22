using PakStudio.Core.Audio;
using Xunit;

namespace PakStudio.Tests;

public sealed class NativeAudioPlayerTests
{
    [Fact]
    public void BundledPlayerDecodesAndSeeksWaveAudio()
    {
        using var player = NativeAudioPlayer.Create(CreateSilentWave(), ".wav");

        Assert.InRange(player.DurationSeconds, 0.09, 0.11);
        player.Seek(0.05);
        Assert.InRange(player.PositionSeconds, 0.04, 0.06);
        Assert.False(player.IsPlaying);
    }

    private static byte[] CreateSilentWave()
    {
        const int sampleRate = 8_000;
        const int sampleCount = 800;
        const int dataSize = sampleCount * sizeof(short);

        using var stream = new MemoryStream(44 + dataSize);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);
        writer.Write(new byte[dataSize]);
        return stream.ToArray();
    }
}
