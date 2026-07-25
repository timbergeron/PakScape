using System.Buffers.Binary;
using PakStudio.Core.Nodes;
using PakStudio.Core.Preview;
using Xunit;

namespace PakStudio.Tests;

public sealed class ArchiveMetadataInspectorTests
{
    [Fact]
    public void DetailsColumnOmitsPreviewMetadata()
    {
        var metadata = new ArchiveMetadata(
            [],
            "Dimensions: 320 × 200 pixels  •  Color Depth: 8-bit  •  Duration: 0:12");

        Assert.Equal("Color Depth: 8-bit", metadata.DetailsColumnText);
        Assert.Equal(
            "Dimensions: 320 × 200 pixels  •  Color Depth: 8-bit  •  Duration: 0:12",
            metadata.Summary);
        Assert.Empty(new ArchiveMetadata([], "Description: The Slipgate Complex").DetailsColumnText);
    }

    [Fact]
    public void DemoSummaryFindsMapsAndIgnoresBrushModels()
    {
        var file = new ArchiveFileNode(
            "run.dem",
            "noise maps/b_shell0.bsp more maps/e1m3.bsp duplicate maps/E1M3.bsp"u8.ToArray());

        var metadata = ArchiveMetadataInspector.Inspect(file);

        Assert.Equal("Map: e1m3", metadata.Summary);
        Assert.Contains("e1m3", metadata.SearchText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DemoDetailsReadServerInfoAndScores()
    {
        var signon = new List<byte>();
        AppendServerInfo(
            signon,
            protocolVersion: 15,
            maxClients: 4,
            gameType: 1,
            levelName: "the Slipgate Complex",
            models: ["maps/e1m3.bsp", "progs/player.mdl"],
            sounds: ["weapons/r_exp3.wav"]);
        AppendPlayer(signon, slot: 0, name: "alice", frags: 12, colors: 0x44);
        AppendPlayer(signon, slot: 1, name: "bob", frags: 7, colors: 0x33);
        AppendTime(signon, 0);

        var closing = new List<byte>();
        AppendTime(closing, 95.5f);
        closing.Add(14); // svc_updatefrags
        closing.Add(1);
        AppendUInt16(closing, 20);

        var file = new ArchiveFileNode("duel.dem", Demo([signon, closing]));
        var metadata = ArchiveMetadataInspector.Inspect(file);
        var details = metadata.Details.ToDictionary(detail => detail.Label, detail => detail.Value);

        Assert.Equal("Quake demo", details["Format"]);
        Assert.Equal("e1m3", details["Map"]);
        Assert.Equal("the Slipgate Complex", details["Level"]);
        Assert.Equal("1:36", details["Duration"]);
        Assert.Equal("Deathmatch", details["Mode"]);
        Assert.Equal("alice, bob", details["Players"]);
        Assert.Equal("bob 20, alice 12", details["Scores"]);
        Assert.Equal("15", details["Protocol"]);
        Assert.Equal("Map: e1m3  •  Duration: 1:36", metadata.Summary);
    }

    [Fact]
    public void DemoDetailsReportSinglePlayerMode()
    {
        var signon = new List<byte>();
        AppendServerInfo(
            signon,
            protocolVersion: 666,
            maxClients: 1,
            gameType: 0,
            levelName: "the Slipgate Complex",
            models: ["maps/e1m1.bsp"],
            sounds: []);
        AppendPlayer(signon, slot: 0, name: "player", frags: 0, colors: 0x00);
        AppendTime(signon, 1.25f);

        var file = new ArchiveFileNode("run.dem", Demo([signon]));
        var details = ArchiveMetadataInspector.Inspect(file).Details
            .ToDictionary(detail => detail.Label, detail => detail.Value);

        Assert.Equal("e1m1", details["Map"]);
        Assert.Equal("Single player", details["Mode"]);
        Assert.Equal("player", details["Player"]);
        Assert.Equal("666", details["Protocol"]);
        Assert.False(details.ContainsKey("Scores"));
    }

    /// <summary>
    /// A frame this parser cannot decode must not cost the timings the frame walk already has.
    /// </summary>
    [Fact]
    public void DemoDetailsKeepTimingAcrossUnreadableFrames()
    {
        var signon = new List<byte>();
        AppendServerInfo(
            signon,
            protocolVersion: 15,
            maxClients: 2,
            gameType: 1,
            levelName: "",
            models: ["maps/dm4.bsp"],
            sounds: []);
        AppendTime(signon, 0);

        var unreadable = new List<byte>();
        AppendTime(unreadable, 30);
        unreadable.Add(58); // svc_csqcentities, deliberately unsupported

        var later = new List<byte>();
        AppendTime(later, 61);

        var file = new ArchiveFileNode("dm.dem", Demo([signon, unreadable, later]));
        var details = ArchiveMetadataInspector.Inspect(file).Details
            .ToDictionary(detail => detail.Label, detail => detail.Value);

        Assert.Equal("dm4", details["Map"]);
        Assert.Equal("At least 1:01", details["Duration"]);
    }

    /// <summary>Quake parks -99 in a slot whose player left, so those names are not scores.</summary>
    [Fact]
    public void DemoScoresExcludeVacatedSlots()
    {
        var signon = new List<byte>();
        AppendServerInfo(
            signon,
            protocolVersion: 15,
            maxClients: 16,
            gameType: 1,
            levelName: "",
            models: ["maps/ctf2m8.bsp"],
            sounds: []);
        AppendPlayer(signon, slot: 0, name: "sa", frags: 3, colors: 0x44);
        AppendPlayer(signon, slot: 1, name: "lilbro", frags: 1, colors: 0x33);
        AppendPlayer(signon, slot: 2, name: "departed", frags: -99, colors: 0x00);
        AppendTime(signon, 0);

        var file = new ArchiveFileNode("ctf.dem", Demo([signon]));
        var details = ArchiveMetadataInspector.Inspect(file).Details
            .ToDictionary(detail => detail.Label, detail => detail.Value);

        Assert.Equal("sa, lilbro, departed", details["Players"]);
        Assert.Equal("sa 3, lilbro 1", details["Scores"]);
    }

    [Fact]
    public void DemoFallsBackToTextScanWhenFramesCannotBeWalked()
    {
        var data = new List<byte>();
        data.AddRange("-1\n"u8.ToArray());
        data.AddRange("noise maps/e1m5.bsp"u8.ToArray());

        var metadata = ArchiveMetadataInspector.Inspect(new ArchiveFileNode("broken.dem", data.ToArray()));

        Assert.Equal("Map: e1m5", metadata.Summary);
    }

    /// <summary>Wraps message payloads in the length-prefixed frames a recording is made of.</summary>
    private static byte[] Demo(IReadOnlyList<List<byte>> frames)
    {
        var data = new List<byte>("-1\n"u8.ToArray());
        foreach (var frame in frames)
        {
            AppendInt32(data, frame.Count);
            for (var axis = 0; axis < 3; axis++)
            {
                AppendFloat(data, 0);
            }
            data.AddRange(frame);
        }
        return data.ToArray();
    }

    private static void AppendServerInfo(
        List<byte> data,
        int protocolVersion,
        byte maxClients,
        byte gameType,
        string levelName,
        string[] models,
        string[] sounds)
    {
        data.Add(11); // svc_serverinfo
        AppendInt32(data, protocolVersion);
        data.Add(maxClients);
        data.Add(gameType);
        AppendCString(data, levelName);
        foreach (var model in models)
        {
            AppendCString(data, model);
        }
        data.Add(0);
        foreach (var sound in sounds)
        {
            AppendCString(data, sound);
        }
        data.Add(0);
    }

    private static void AppendPlayer(List<byte> data, byte slot, string name, short frags, byte colors)
    {
        data.Add(13); // svc_updatename
        data.Add(slot);
        AppendCString(data, name);
        data.Add(14); // svc_updatefrags
        data.Add(slot);
        AppendUInt16(data, unchecked((ushort)frags));
        data.Add(17); // svc_updatecolors
        data.Add(slot);
        data.Add(colors);
    }

    private static void AppendTime(List<byte> data, float seconds)
    {
        data.Add(7); // svc_time
        AppendFloat(data, seconds);
    }

    private static void AppendCString(List<byte> data, string value)
    {
        foreach (var character in value)
        {
            data.Add((byte)character);
        }
        data.Add(0);
    }

    private static void AppendInt32(List<byte> data, int value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        data.AddRange(buffer);
    }

    private static void AppendUInt16(List<byte> data, ushort value)
    {
        var buffer = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        data.AddRange(buffer);
    }

    private static void AppendFloat(List<byte> data, float value)
    {
        var buffer = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(buffer, value);
        data.AddRange(buffer);
    }

    [Fact]
    public void BspSummaryReadsWorldspawnDescription()
    {
        var entities = "{\"classname\" \"worldspawn\" \"message\" \"The Slipgate Complex\"}"u8.ToArray();
        var data = new byte[124 + entities.Length];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0, 4), 29);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), 124);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8, 4), entities.Length);
        entities.CopyTo(data, 124);
        var file = new ArchiveFileNode("e1m1.bsp", data);

        var metadata = ArchiveMetadataInspector.Inspect(file);

        Assert.Equal("Description: The Slipgate Complex", metadata.Summary);
        Assert.Contains(
            metadata.Details,
            detail => detail.Label == "Description" && detail.Value == "The Slipgate Complex");
    }

    [Fact]
    public void QuakeCProgramDetailsReadTheProgsHeader()
    {
        var data = new byte[60];
        WriteHeader(data, [6, 5927, 60, 20940, 0, 4287, 0, 218, 0, 2091, 60, 88336, 0, 11471, 195]);

        var metadata = ArchiveMetadataInspector.Inspect(new ArchiveFileNode("progs.dat", data));

        Assert.Contains(
            metadata.Details,
            detail => detail.Label == "Format" && detail.Value == "Compiled QuakeC program");
        Assert.Contains(
            metadata.Details,
            detail => detail.Label == "Progdefs CRC" && detail.Value == "5927");
        Assert.Contains(
            metadata.Details,
            detail => detail.Label == "Functions" && detail.Value == "2,091");
        Assert.Contains(
            metadata.Details,
            detail => detail.Label == "Entity Fields" && detail.Value == "195");
        Assert.Contains("compiled QuakeC program", Find(metadata, "Purpose"));

        /* The column stays a stat rather than the sentence. */
        Assert.Equal("Functions: 2,091  •  Entity Fields: 195", metadata.Summary);
    }

    [Fact]
    public void DosTextScreenDetailsReadTheirHeadline()
    {
        const string headline = "QUAKE: The Doomed Dimension by id Software";
        var data = new byte[80 * 25 * 2];
        for (var index = 0; index < headline.Length; index++)
        {
            data[index * 2] = (byte)headline[index];
            data[index * 2 + 1] = 0x4f; // colour attribute
        }

        var metadata = ArchiveMetadataInspector.Inspect(new ArchiveFileNode("end1.bin", data));

        Assert.Contains(
            metadata.Details,
            detail => detail.Label == "Format" && detail.Value == "DOS text-mode screen");
        Assert.Contains(
            metadata.Details,
            detail => detail.Label == "Screen Size" && detail.Value == "80 × 25 characters");
        Assert.Equal($"Description: {headline}", metadata.Summary);
        Assert.Contains("shareware", Find(metadata, "Purpose"));
    }

    [Fact]
    public void WellKnownQuakeFilesExplainThemselves()
    {
        var startup = ArchiveMetadataInspector.Inspect(
            new ArchiveFileNode("quake.rc", "exec default.cfg\nexec config.cfg\n"u8.ToArray()));
        Assert.Contains(
            startup.Details,
            detail => detail.Label == "Format" && detail.Value == "Quake console script");
        Assert.Contains("startup script", Find(startup, "Purpose"));
        /* A sentence never crowds out the stats in the column. */
        Assert.DoesNotContain("Purpose", startup.Summary);

        /* A name PakScape does not know still gets its extension's description. */
        var source = ArchiveMetadataInspector.Inspect(
            new ArchiveFileNode("sv_main.qc", "void() main = {};\n"u8.ToArray()));
        Assert.Contains("QuakeC source", Find(source, "Purpose"));

        /* A file with nothing known about it keeps saying nothing. */
        var unknown = ArchiveMetadataInspector.Inspect(new ArchiveFileNode("unknown.xyz", [1, 2, 3, 4]));
        Assert.Empty(unknown.Details);
    }

    [Fact]
    public void DataFilesThatAreNotQuakeCFallBackToTheirMagic()
    {
        byte[] wad = [(byte)'W', (byte)'A', (byte)'D', (byte)'2', 3, 0, 0, 0, 12, 0, 0, 0];

        var metadata = ArchiveMetadataInspector.Inspect(new ArchiveFileNode("textures.dat", wad));

        Assert.Contains(
            metadata.Details,
            detail => detail.Label == "Entries" && detail.Value == "3");
    }

    private static void WriteHeader(byte[] data, int[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(index * 4, 4), values[index]);
        }
    }

    private static string Find(ArchiveMetadata metadata, string label) =>
        metadata.Details.FirstOrDefault(detail => detail.Label == label)?.Value ?? string.Empty;

    [Fact]
    public void CommonImageMetadataProducesConciseDetails()
    {
        byte[] png =
        [
            137, 80, 78, 71, 13, 10, 26, 10,
            0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
            0, 0, 1, 64, 0, 0, 0, 200, 8, 6, 0, 0, 0,
        ];

        var metadata = ArchiveMetadataInspector.Inspect(new ArchiveFileNode("shot.png", png));

        Assert.Contains("Dimensions: 320 × 200 pixels", metadata.Summary);
        Assert.Contains("8-bit", metadata.SearchText);
    }
}
