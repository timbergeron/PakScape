namespace PakStudio.Core.Audio;

/// <summary>
/// Produces a small, safe-to-render amplitude profile for audio artwork.
/// It intentionally works for both PCM and compressed formats without decoding audio.
/// </summary>
public static class AudioWaveformBuilder
{
    public static IReadOnlyList<float> Build(ReadOnlySpan<byte> data, int barCount = 48)
    {
        if (barCount <= 0)
        {
            return Array.Empty<float>();
        }

        var result = new float[barCount];
        if (data.IsEmpty)
        {
            Array.Fill(result, 0.2f);
            return result;
        }

        var offset = IsWaveFile(data) ? Math.Min(44, data.Length) : 0;
        var sampleLength = Math.Max(1, data.Length - offset);
        for (var bar = 0; bar < barCount; bar++)
        {
            var start = offset + (bar * sampleLength / barCount);
            var end = offset + ((bar + 1) * sampleLength / barCount);
            end = Math.Max(start + 1, end);

            double total = 0;
            var count = 0;
            for (var index = start; index < Math.Min(end, data.Length); index++)
            {
                total += Math.Abs(data[index] - 128) / 128.0;
                count++;
            }

            var amplitude = count == 0 ? 0.2 : total / count;
            result[bar] = Math.Clamp((float)(0.16 + amplitude * 0.94), 0.16f, 1f);
        }

        return result;
    }

    private static bool IsWaveFile(ReadOnlySpan<byte> data) =>
        data.Length >= 12 &&
        data[..4].SequenceEqual("RIFF"u8) &&
        data[8..12].SequenceEqual("WAVE"u8);
}
