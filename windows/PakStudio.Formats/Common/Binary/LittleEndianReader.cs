using System.Buffers.Binary;

namespace PakStudio.Formats.Common.Binary;

internal static class LittleEndianReader
{
    public static int ReadInt32(ReadOnlySpan<byte> data, int offset)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, sizeof(int)));
    }
}
