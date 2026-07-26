using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PakStudio.Core.Preview;

public static partial class ArchiveMetadataInspector
{
    private static List<ArchiveMetadataDetail> InspectMd3(ReadOnlySpan<byte> data)
    {
        if (!HasAscii(data, 0, "IDP3") ||
            !TryInt32(data, 4, out var version) || version != 15 ||
            !TryNonnegativeInt32(data, 76, out var frames) ||
            !TryNonnegativeInt32(data, 80, out var tags) ||
            !TryNonnegativeInt32(data, 84, out var surfaces) ||
            !TryInt32(data, 100, out var surfaceOffset) ||
            frames > 1_000_000 || tags > 1_000_000 || surfaces > 16_384)
        {
            return [];
        }

        var details = new List<ArchiveMetadataDetail>
        {
            Detail("Format", "Quake III alias model"),
            Detail("Version", version.ToString(CultureInfo.InvariantCulture)),
            Detail("Frames", Formatted(frames)),
            Detail("Tags", Formatted(tags)),
            Detail("Surfaces", Formatted(surfaces)),
        };
        AddNonemptyText(details, "Name", NullTerminatedText(data, 8, 64));

        long vertices = 0;
        long triangles = 0;
        long shaders = 0;
        var cursor = surfaceOffset;
        var complete = surfaceOffset >= 0;
        for (var index = 0; complete && index < surfaces; index++)
        {
            if (!HasAscii(data, cursor, "IDP3") ||
                !TryNonnegativeInt32(data, cursor + 76, out var surfaceShaders) ||
                !TryNonnegativeInt32(data, cursor + 80, out var surfaceVertices) ||
                !TryNonnegativeInt32(data, cursor + 84, out var surfaceTriangles) ||
                !TryPositiveInt32(data, cursor + 104, out var surfaceSize) ||
                cursor > data.Length - surfaceSize)
            {
                complete = false;
                break;
            }
            shaders += surfaceShaders;
            vertices += surfaceVertices;
            triangles += surfaceTriangles;
            cursor += surfaceSize;
        }
        if (complete)
        {
            details.Add(Detail("Vertices", Formatted(vertices)));
            details.Add(Detail("Triangles", Formatted(triangles)));
            details.Add(Detail("Shaders", Formatted(shaders)));
        }
        return details;
    }

    private static List<ArchiveMetadataDetail> InspectMd5(ReadOnlySpan<byte> data)
    {
        var text = Encoding.UTF8.GetString(data);
        if (!TryMd5Value(text, "MD5Version", out var version) || version != 10)
        {
            return [];
        }

        var isAnimation = TryMd5Value(text, "numFrames", out var frames);
        var isMesh = TryMd5Value(text, "numMeshes", out var meshes);
        if (!isAnimation && !isMesh)
        {
            return [];
        }

        List<ArchiveMetadataDetail> details =
        [
            Detail("Format", isAnimation ? "Doom 3 model animation" : "Doom 3 model mesh"),
            Detail("Version", version.ToString(CultureInfo.InvariantCulture)),
        ];
        if (TryMd5Value(text, "numJoints", out var joints))
        {
            details.Add(Detail("Joints", Formatted(joints)));
        }
        if (isAnimation)
        {
            details.Add(Detail("Frames", Formatted(frames)));
            if (TryMd5Value(text, "frameRate", out var frameRate) && frameRate > 0)
            {
                details.Add(Detail("Frame Rate", $"{frameRate:N0} fps"));
                details.Add(Detail("Duration", FormatDuration((double)frames / frameRate)));
            }
            if (TryMd5Value(text, "numAnimatedComponents", out var components))
            {
                details.Add(Detail("Animated Components", Formatted(components)));
            }
        }
        else
        {
            details.Add(Detail("Meshes", Formatted(meshes)));
            AddMd5Sum(details, text, "numverts", "Vertices");
            AddMd5Sum(details, text, "numtris", "Triangles");
        }
        return details;
    }

    private static bool TryMd5Value(string text, string key, out int value)
    {
        value = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.AsSpan().Trim();
            if (!line.StartsWith(key, StringComparison.Ordinal) ||
                line.Length == key.Length ||
                !char.IsWhiteSpace(line[key.Length]))
            {
                continue;
            }
            var number = line[(key.Length + 1)..].TrimStart();
            var end = number.IndexOfAny(" \t\r/");
            if (end >= 0)
            {
                number = number[..end];
            }
            return int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
                   value >= 0;
        }
        return false;
    }

    private static void AddMd5Sum(
        ICollection<ArchiveMetadataDetail> details,
        string text,
        string key,
        string label)
    {
        long total = 0;
        var found = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.AsSpan().Trim();
            if (!line.StartsWith(key, StringComparison.Ordinal) ||
                line.Length == key.Length ||
                !char.IsWhiteSpace(line[key.Length]))
            {
                continue;
            }
            var number = line[(key.Length + 1)..].TrimStart();
            var end = number.IndexOfAny(" \t\r/");
            if (end >= 0)
            {
                number = number[..end];
            }
            if (int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
                value >= 0)
            {
                total += value;
                found = true;
            }
        }
        if (found)
        {
            details.Add(Detail(label, Formatted(total)));
        }
    }

    private static List<ArchiveMetadataDetail> InspectLit(ReadOnlySpan<byte> data, int fileSize)
    {
        if (!HasAscii(data, 0, "QLIT") ||
            !TryPositiveInt32(data, 4, out var version) ||
            fileSize < 8)
        {
            return [];
        }
        var payload = fileSize - 8;
        List<ArchiveMetadataDetail> details =
        [
            Detail("Format", "Quake coloured lighting"),
            Detail("Version", version.ToString(CultureInfo.InvariantCulture)),
            Detail("Data Size", $"{payload:N0} bytes"),
        ];
        if (version == 1 && payload % 3 == 0)
        {
            details.Add(Detail("Samples", Formatted(payload / 3)));
        }
        return details;
    }

    private static List<ArchiveMetadataDetail> InspectVis(ReadOnlySpan<byte> data, int fileSize)
    {
        var cursor = 0;
        var maps = new List<string>();
        long payloadBytes = 0;
        while (cursor <= data.Length - 36)
        {
            var name = NullTerminatedText(data, cursor, 32);
            if (name.Length == 0 ||
                !TryPositiveInt32(data, cursor + 32, out var payload))
            {
                return [];
            }
            var next = (long)cursor + 36 + payload;
            if (next > fileSize)
            {
                return [];
            }
            maps.Add(name);
            payloadBytes += payload;
            cursor = (int)next;
            if (cursor > data.Length)
            {
                break;
            }
        }
        if (maps.Count == 0 || cursor != fileSize)
        {
            return [];
        }
        List<ArchiveMetadataDetail> details =
        [
            Detail("Format", "Quake external visibility patch"),
            Detail("Maps", maps.Count == 1 ? maps[0] : $"{maps.Count:N0} maps"),
            Detail("Visibility Data", $"{payloadBytes:N0} bytes"),
        ];
        return details;
    }

    private static List<ArchiveMetadataDetail> InspectNav(ReadOnlySpan<byte> data, int fileSize)
    {
        if (!HasAscii(data, 0, "NAV2") || fileSize < 8)
        {
            return [];
        }
        return
        [
            Detail("Format", "Quake bot navigation"),
            Detail("Version", "NAV2"),
            Detail("Data Size", $"{fileSize - 4:N0} bytes"),
        ];
    }

    private static List<ArchiveMetadataDetail> InspectDds(ReadOnlySpan<byte> data)
    {
        if (!HasAscii(data, 0, "DDS ") ||
            !TryInt32(data, 4, out var headerSize) || headerSize != 124 ||
            !TryPositiveInt32(data, 12, out var height) ||
            !TryPositiveInt32(data, 16, out var width) ||
            !SafeDimensions(width, height) ||
            !TryInt32(data, 76, out var pixelHeaderSize) || pixelHeaderSize != 32)
        {
            return [];
        }
        List<ArchiveMetadataDetail> details =
        [
            Detail("Format", "DirectDraw Surface image"),
            Detail("Dimensions", Dimensions(width, height)),
        ];
        if (TryInt32(data, 28, out var mipmaps) && mipmaps > 0)
        {
            details.Add(Detail("Mipmaps", Formatted(mipmaps)));
        }
        var fourCc = Ascii(data, 84, 4).TrimEnd('\0', ' ');
        if (fourCc.Length > 0)
        {
            details.Add(Detail("Compression", fourCc == "DX10" && TryInt32(data, 128, out var dxgi)
                ? $"DX10 (DXGI format {dxgi})"
                : fourCc));
        }
        else if (TryPositiveInt32(data, 88, out var bitDepth))
        {
            details.Add(Detail("Color Depth", $"{bitDepth}-bit"));
        }
        return details;
    }

    private static List<ArchiveMetadataDetail> InspectFlac(ReadOnlySpan<byte> data)
    {
        if (!HasAscii(data, 0, "fLaC") || data.Length < 42 ||
            (data[4] & 0x7f) != 0 || ReadUInt24BigEndian(data, 5) != 34)
        {
            return [];
        }
        var packed = BinaryPrimitives.ReadUInt64BigEndian(data.Slice(18, 8));
        var sampleRate = (int)(packed >> 44 & 0xfffff);
        var channels = (int)(packed >> 41 & 0x7) + 1;
        var bitDepth = (int)(packed >> 36 & 0x1f) + 1;
        var samples = packed & 0xfffffffffUL;
        if (sampleRate <= 0)
        {
            return [];
        }
        List<ArchiveMetadataDetail> details =
        [
            Detail("Format", "FLAC audio"),
            Detail("Sample Rate", $"{sampleRate:N0} Hz"),
            Detail("Channels", ChannelDescription(channels)),
            Detail("Bit Depth", $"{bitDepth}-bit"),
        ];
        if (samples > 0)
        {
            details.Add(Detail("Duration", FormatDuration((double)samples / sampleRate)));
        }
        AddFlacComments(details, data);
        return details;
    }

    private static void AddFlacComments(ICollection<ArchiveMetadataDetail> details, ReadOnlySpan<byte> data)
    {
        var cursor = 4;
        while (cursor <= data.Length - 4)
        {
            var last = (data[cursor] & 0x80) != 0;
            var type = data[cursor] & 0x7f;
            var length = ReadUInt24BigEndian(data, cursor + 1);
            cursor += 4;
            if (length < 0 || cursor > data.Length - length)
            {
                return;
            }
            if (type == 4)
            {
                AddVorbisComments(details, data.Slice(cursor, length));
                return;
            }
            cursor += length;
            if (last)
            {
                return;
            }
        }
    }

    private static List<ArchiveMetadataDetail> InspectOgg(ReadOnlySpan<byte> data, int fileSize)
    {
        if (!TryOggFirstPacket(data, out var packet))
        {
            return [];
        }
        List<ArchiveMetadataDetail> details;
        var sampleRate = 0;
        var preSkip = 0;
        if (packet.Length >= 16 && packet[0] == 1 && HasAscii(packet, 1, "vorbis"))
        {
            var channels = packet[11];
            sampleRate = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(12, 4));
            if (channels == 0 || sampleRate <= 0)
            {
                return [];
            }
            details =
            [
                Detail("Format", "Ogg Vorbis audio"),
                Detail("Sample Rate", $"{sampleRate:N0} Hz"),
                Detail("Channels", ChannelDescription(channels)),
            ];
            AddOggComments(details, data, "\u0003vorbis"u8);
        }
        else if (packet.Length >= 19 && HasAscii(packet, 0, "OpusHead"))
        {
            var channels = packet[9];
            preSkip = U16(packet, 10);
            sampleRate = 48_000;
            if (channels == 0)
            {
                return [];
            }
            details =
            [
                Detail("Format", "Ogg Opus audio"),
                Detail("Version", packet[8].ToString(CultureInfo.InvariantCulture)),
                Detail("Sample Rate", "48,000 Hz"),
                Detail("Channels", ChannelDescription(channels)),
            ];
            AddOggComments(details, data, "OpusTags"u8);
        }
        else
        {
            return [];
        }

        if (data.Length == fileSize && TryLastOggGranule(data, out var granule) && granule > (ulong)preSkip)
        {
            details.Add(Detail("Duration", FormatDuration((double)(granule - (ulong)preSkip) / sampleRate)));
        }
        return details;
    }

    private static bool TryOggFirstPacket(ReadOnlySpan<byte> data, out ReadOnlySpan<byte> packet)
    {
        packet = default;
        if (!HasAscii(data, 0, "OggS") || data.Length < 28)
        {
            return false;
        }
        var segments = data[26];
        if (data.Length < 27 + segments)
        {
            return false;
        }
        var size = 0;
        for (var index = 0; index < segments; index++)
        {
            size += data[27 + index];
            if (data[27 + index] < 255)
            {
                break;
            }
        }
        var body = 27 + segments;
        if (size <= 0 || body > data.Length - size)
        {
            return false;
        }
        packet = data.Slice(body, size);
        return true;
    }

    private static bool TryLastOggGranule(ReadOnlySpan<byte> data, out ulong granule)
    {
        granule = 0;
        for (var cursor = data.Length - 27; cursor >= 0; cursor--)
        {
            if (!HasAscii(data, cursor, "OggS") || cursor > data.Length - 27)
            {
                continue;
            }
            granule = BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(cursor + 6, 8));
            return granule != ulong.MaxValue;
        }
        return false;
    }

    private static void AddOggComments(
        ICollection<ArchiveMetadataDetail> details,
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> marker)
    {
        var offset = data.IndexOf(marker);
        if (offset < 0)
        {
            return;
        }
        AddVorbisComments(details, data[(offset + marker.Length)..]);
    }

    private static void AddVorbisComments(
        ICollection<ArchiveMetadataDetail> details,
        ReadOnlySpan<byte> data)
    {
        var cursor = 0;
        if (!TryReadLengthPrefixedText(data, ref cursor, out _))
        {
            return;
        }
        if (!TryUInt32(data, cursor, out var count) || count > 10_000)
        {
            return;
        }
        cursor += 4;
        for (var index = 0; index < count; index++)
        {
            if (!TryReadLengthPrefixedText(data, ref cursor, out var comment))
            {
                return;
            }
            var separator = comment.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }
            var key = comment[..separator];
            var value = comment[(separator + 1)..].Trim();
            var label = key.ToUpperInvariant() switch
            {
                "TITLE" => "Title",
                "ARTIST" => "Artist",
                "ALBUM" => "Album",
                _ => null,
            };
            if (label is not null && value.Length > 0 &&
                !details.Any(detail => detail.Label == label))
            {
                details.Add(Detail(label, value));
            }
        }
    }

    private static bool TryReadLengthPrefixedText(
        ReadOnlySpan<byte> data,
        ref int cursor,
        out string text)
    {
        text = string.Empty;
        if (!TryUInt32(data, cursor, out var length) || length > int.MaxValue)
        {
            return false;
        }
        cursor += 4;
        var size = (int)length;
        if (cursor > data.Length - size)
        {
            return false;
        }
        text = Encoding.UTF8.GetString(data.Slice(cursor, size));
        cursor += size;
        return true;
    }

    private static List<ArchiveMetadataDetail> InspectXm(ReadOnlySpan<byte> data)
    {
        if (!HasAscii(data, 0, "Extended Module: ") || data.Length < 80 || data[37] != 0x1a)
        {
            return [];
        }
        var version = U16(data, 58);
        return TrackerDetails(
            "FastTracker II module",
            NullTerminatedText(data, 17, 20),
            U16(data, 68),
            U16(data, 64),
            U16(data, 70),
            U16(data, 72),
            null,
            U16(data, 78),
            $"{version >> 8}.{version & 0xff:D2}",
            NullTerminatedText(data, 38, 20));
    }

    private static List<ArchiveMetadataDetail> InspectS3m(ReadOnlySpan<byte> data)
    {
        if (!HasAscii(data, 44, "SCRM") || data.Length < 96)
        {
            return [];
        }
        var channels = 0;
        for (var index = 64; index < 96; index++)
        {
            if (data[index] < 16)
            {
                channels++;
            }
        }
        return TrackerDetails(
            "Scream Tracker 3 module",
            NullTerminatedText(data, 0, 28),
            channels,
            U16(data, 32),
            U16(data, 36),
            U16(data, 34),
            null,
            data[50]);
    }

    private static List<ArchiveMetadataDetail> InspectIt(ReadOnlySpan<byte> data)
    {
        if (!HasAscii(data, 0, "IMPM") || data.Length < 128)
        {
            return [];
        }
        var channels = 0;
        for (var index = 64; index < 128; index++)
        {
            if (data[index] < 128)
            {
                channels++;
            }
        }
        var version = U16(data, 40);
        return TrackerDetails(
            "Impulse Tracker module",
            NullTerminatedText(data, 4, 26),
            channels,
            U16(data, 32),
            U16(data, 38),
            U16(data, 34),
            U16(data, 36),
            data[51],
            $"{version >> 8}.{version & 0xff:D2}");
    }

    private static List<ArchiveMetadataDetail> InspectMod(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1084)
        {
            return [];
        }
        var signature = Ascii(data, 1080, 4);
        var channels = ModChannels(signature);
        if (channels <= 0)
        {
            return [];
        }
        var orders = data[950];
        var patterns = 0;
        for (var index = 952; index < 1080 && index < 952 + orders; index++)
        {
            patterns = Math.Max(patterns, data[index] + 1);
        }
        return TrackerDetails(
            "ProTracker module",
            NullTerminatedText(data, 0, 20),
            channels,
            orders,
            patterns,
            31,
            null,
            null,
            signature);
    }

    private static List<ArchiveMetadataDetail> InspectUmx(ReadOnlySpan<byte> data)
    {
        if (data.Length < 36 || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x9e2a83c1)
        {
            return [];
        }
        List<ArchiveMetadataDetail> details =
        [
            Detail("Format", "Unreal music package"),
            Detail("Version", U16(data, 4).ToString(CultureInfo.InvariantCulture)),
        ];
        if (TryNonnegativeInt32(data, 12, out var names))
        {
            details.Add(Detail("Names", Formatted(names)));
        }
        if (TryNonnegativeInt32(data, 20, out var exports))
        {
            details.Add(Detail("Exports", Formatted(exports)));
        }
        return details;
    }

    private static List<ArchiveMetadataDetail> TrackerDetails(
        string format,
        string title,
        int channels,
        int orders,
        int patterns,
        int instruments,
        int? samples,
        int? tempo,
        string? version = null,
        string? tracker = null)
    {
        List<ArchiveMetadataDetail> details = [Detail("Format", format)];
        AddNonemptyText(details, "Title", title);
        AddNonemptyText(details, "Tracker", tracker ?? string.Empty);
        AddNonemptyText(details, "Version", version ?? string.Empty);
        if (channels > 0) details.Add(Detail("Channels", Formatted(channels)));
        if (orders > 0) details.Add(Detail("Orders", Formatted(orders)));
        if (patterns > 0) details.Add(Detail("Patterns", Formatted(patterns)));
        if (instruments > 0) details.Add(Detail("Instruments", Formatted(instruments)));
        if (samples > 0) details.Add(Detail("Samples", Formatted(samples.Value)));
        if (tempo > 0) details.Add(Detail("Tempo", $"{tempo.Value:N0} BPM"));
        return details;
    }

    private static int ModChannels(string signature)
    {
        if (signature is "M.K." or "M!K!" or "M&K!" or "FLT4") return 4;
        if (signature is "OCTA" or "CD81" or "FLT8") return 8;
        if (signature.Length == 4 && signature.EndsWith("CHN", StringComparison.Ordinal) &&
            char.IsDigit(signature[0])) return signature[0] - '0';
        if (signature.Length == 4 && signature.EndsWith("CH", StringComparison.Ordinal) &&
            int.TryParse(signature[..2], out var channels)) return channels;
        return 0;
    }

    private static void AddNonemptyText(
        ICollection<ArchiveMetadataDetail> details,
        string label,
        string value)
    {
        value = value.Trim();
        if (value.Length > 0)
        {
            details.Add(Detail(label, value));
        }
    }

    private static string NullTerminatedText(ReadOnlySpan<byte> data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
        {
            return string.Empty;
        }
        var bytes = data.Slice(offset, length);
        var end = bytes.IndexOf((byte)0);
        if (end >= 0)
        {
            bytes = bytes[..end];
        }
        return Encoding.Latin1.GetString(bytes).Trim();
    }

    private static bool TryNonnegativeInt32(ReadOnlySpan<byte> data, int offset, out int value) =>
        TryInt32(data, offset, out value) && value >= 0;

    private static bool TryUInt32(ReadOnlySpan<byte> data, int offset, out uint value)
    {
        value = 0;
        if (offset < 0 || offset > data.Length - 4)
        {
            return false;
        }
        value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
        return true;
    }

    private static int ReadUInt24BigEndian(ReadOnlySpan<byte> data, int offset) =>
        offset >= 0 && offset <= data.Length - 3
            ? data[offset] << 16 | data[offset + 1] << 8 | data[offset + 2]
            : -1;

    private static string ChannelDescription(int channels) => channels switch
    {
        1 => "Mono",
        2 => "Stereo",
        _ => channels.ToString("N0", CultureInfo.CurrentCulture),
    };

    private static string Formatted(long value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
