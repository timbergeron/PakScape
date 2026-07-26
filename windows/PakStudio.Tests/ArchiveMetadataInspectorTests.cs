using System.Buffers.Binary;
using System.Text;
using PakStudio.Core.Nodes;
using PakStudio.Core.Preview;
using Xunit;

namespace PakStudio.Tests;

public sealed class ArchiveMetadataInspectorTests
{
    [Fact]
    public void DetailsColumnOmitsPreviewMetadataPrefixes()
    {
        var metadata = new ArchiveMetadata(
            [],
            "Dimensions: 320 × 200 pixels  •  Color Depth: 8-bit  •  Duration: 0:12");

        Assert.Equal(
            "320 × 200 pixels  •  Color Depth: 8-bit  •  0:12",
            metadata.DetailsColumnText);
        Assert.Equal(
            "Dimensions: 320 × 200 pixels  •  Color Depth: 8-bit  •  Duration: 0:12",
            metadata.Summary);
        Assert.Equal(
            "The Slipgate Complex",
            new ArchiveMetadata([], "Description: The Slipgate Complex").DetailsColumnText);
    }

    [Fact]
    public void SavegameDetailsReadClassicAndRemasterHeaders()
    {
        var classic = ArchiveMetadataInspector.Inspect(
            new ArchiveFileNode("s0.sav", Savegame(version: 5, comment: "The_Slipgate_Complex_kills:__3/__9")));
        var classicDetails = classic.Details.ToDictionary(detail => detail.Label, detail => detail.Value);

        Assert.Equal("Quake savegame", classicDetails["Format"]);
        Assert.Equal("The Slipgate Complex kills:  3/  9", classicDetails["Description"]);
        Assert.Equal("e1m1", classicDetails["Map"]);
        Assert.Equal("Hard", classicDetails["Skill"]);
        Assert.Equal("1:36", classicDetails["Duration"]);
        Assert.Equal("Map: e1m1  •  Skill: Hard", classic.DetailsColumnText);

        var remaster = ArchiveMetadataInspector.Inspect(
            new ArchiveFileNode("s1.sav", Savegame(version: 6, comment: "Dimension_of_the_Machine", gameDirectory: "mg1")));
        var remasterDetails = remaster.Details.ToDictionary(detail => detail.Label, detail => detail.Value);

        Assert.Equal("Quake remaster savegame", remasterDetails["Format"]);
        Assert.Equal("mg1", remasterDetails["Mod"]);
    }

    [Fact]
    public void TruncatedSavegameDoesNotClaimMetadata()
    {
        var metadata = ArchiveMetadataInspector.Inspect(
            new ArchiveFileNode("broken.sav", "5\nnot enough lines\n"u8.ToArray()));

        Assert.DoesNotContain(metadata.Details, detail => detail.Label == "Format");
        Assert.Empty(metadata.DetailsColumnText);
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

    private static byte[] Savegame(int version, string comment, string? gameDirectory = null)
    {
        var lines = new List<string> { version.ToString() };
        if (version == 6)
        {
            lines.Add(gameDirectory ?? "id1");
        }
        lines.Add(comment);
        lines.AddRange(Enumerable.Repeat("0", 16));
        lines.Add("2");
        lines.Add("e1m1");
        lines.Add("95.5");
        lines.Add("{}");
        return Encoding.ASCII.GetBytes(string.Join("\r\n", lines));
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
    public void BspSummaryReadsDescriptionPastOrdinaryInspectionLimit()
    {
        var entities = "{\"classname\" \"worldspawn\" \"message\" \"The Wind Tunnels\"}"u8.ToArray();
        var entityOffset = ArchiveMetadataInspector.MaximumInspectionBytes + 128;
        var data = new byte[entityOffset + entities.Length];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0, 4), 29);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), entityOffset);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8, 4), entities.Length);
        entities.CopyTo(data.AsSpan(entityOffset));

        var metadata = ArchiveMetadataInspector.Inspect(new ArchiveFileNode("e3m5.bsp", data));

        Assert.Equal("Description: The Wind Tunnels", metadata.Summary);
        Assert.Equal("The Wind Tunnels", metadata.DetailsColumnText);
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
    public void AlternateQuakeCProgramsHaveSpecificPurposes()
    {
        var data = new byte[60];
        WriteHeader(data, [6, 5927, 60, 20, 0, 4, 0, 2, 0, 3, 60, 32, 0, 8, 5]);

        var client = ArchiveMetadataInspector.Inspect(new ArchiveFileNode("csprogs.dat", data));
        Assert.Equal("Compiled QuakeC program", Find(client, "Format"));
        Assert.Contains("Client-side QuakeC", Find(client, "Purpose"));

        var menu = ArchiveMetadataInspector.Inspect(new ArchiveFileNode("menu.dat", data));
        Assert.Equal("3", Find(menu, "Functions"));
        Assert.Contains("menu program", Find(menu, "Purpose"));
    }

    [Fact]
    public void QssMListsAndBackupFilesExplainThemselves()
    {
        var packageList = ArchiveMetadataInspector.Inspect(new ArchiveFileNode(
            "pak.lst",
            "// Generated by PAK Loading Menu\nad_v1_80p1.pk3\npatch.pak\n"u8.ToArray()));
        Assert.Equal("Package load-order list", Find(packageList, "Format"));
        Assert.Equal("3", Find(packageList, "Lines"));
        Assert.Contains("package load order", Find(packageList, "Purpose"));
        Assert.Equal("Lines: 3  •  Encoding: UTF-8", packageList.Summary);

        string[] namedBackups =
        [
            "servers.json", "servers.json.bad", "servers.txt", "lastserver.txt",
            "server_hostnames.json",
            "bookmarks.json", "bookmarks.txt", "names.json", "names.txt",
            "demomarks.json", "mapdesc.json", "shistory.json", "demos_metadata_cache.json",
            "optional_download_cache.json", "skybox_download_cache.json",
            "qw_maps.txt", "qw_maps.tmp", "lastdemo.txt", "ghost.txt", "name.txt", "iplog.txt",
        ];
        foreach (var name in namedBackups)
        {
            var metadata = ArchiveMetadataInspector.Inspect(
                new ArchiveFileNode(name, "{}\n"u8.ToArray()));
            Assert.False(string.IsNullOrEmpty(Find(metadata, "Purpose")));
        }

        var config = ArchiveMetadataInspector.Inspect(
            new ArchiveFileNode("config-07-26-2026.cfg", "bind w +forward\n"u8.ToArray()));
        Assert.Contains("dated backup", Find(config, "Purpose"));

        var ipLog = ArchiveMetadataInspector.Inspect(
            new ArchiveFileNode("iplog.dat", new byte[60]));
        Assert.Equal("ProQuake IP log", Find(ipLog, "Format"));
        Assert.Equal("3", Find(ipLog, "Entries"));
        Assert.Contains("IP-prefix", Find(ipLog, "Purpose"));
        Assert.Equal("Entries: 3", ipLog.Summary);
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
    public void ModernBspHeadersReportBsp2AndQuake64Metadata()
    {
        var bsp2 = new byte[320];
        "BSP2"u8.CopyTo(bsp2);
        var entities = "{\"classname\" \"worldspawn\" \"message\" \"Modern Map\"}"u8.ToArray();
        WriteInt32At(bsp2, 4, 124);
        WriteInt32At(bsp2, 8, entities.Length);
        entities.CopyTo(bsp2.AsSpan(124));
        WriteInt32At(bsp2, 4 + 3 * 8, 200);
        WriteInt32At(bsp2, 4 + 3 * 8 + 4, 24);
        WriteInt32At(bsp2, 4 + 7 * 8, 224);
        WriteInt32At(bsp2, 4 + 7 * 8 + 4, 56);

        var details = Inspected("modern.bsp", bsp2);
        Assert.Equal("Quake BSP2 level", details["Format"]);
        Assert.Equal("Modern Map", details["Description"]);
        Assert.Equal("2", details["Vertices"]);
        Assert.Equal("2", details["Faces"]);

        var quake64 = new byte[124];
        WriteInt32At(quake64, 0, 23);
        Assert.Equal("Quake 64 BSP level", Inspected("q64.bsp", quake64)["Format"]);
    }

    [Fact]
    public void ModernModelHeadersReportMd3AndMd5Metadata()
    {
        var md3 = new byte[216];
        "IDP3"u8.CopyTo(md3);
        WriteInt32At(md3, 4, 15);
        "ogre"u8.CopyTo(md3.AsSpan(8));
        WriteInt32At(md3, 76, 3);
        WriteInt32At(md3, 80, 2);
        WriteInt32At(md3, 84, 1);
        WriteInt32At(md3, 100, 108);
        "IDP3"u8.CopyTo(md3.AsSpan(108));
        WriteInt32At(md3, 108 + 76, 2);
        WriteInt32At(md3, 108 + 80, 24);
        WriteInt32At(md3, 108 + 84, 12);
        WriteInt32At(md3, 108 + 104, 108);

        var md3Details = Inspected("ogre.md3", md3);
        Assert.Equal("3", md3Details["Frames"]);
        Assert.Equal("1", md3Details["Surfaces"]);
        Assert.Equal("12", md3Details["Triangles"]);

        var mesh = """
            MD5Version 10
            numJoints 4
            numMeshes 2
            numverts 12
            numtris 6
            numverts 8
            numtris 4
            """u8.ToArray();
        var meshDetails = Inspected("ogre.md5mesh", mesh);
        Assert.Equal("2", meshDetails["Meshes"]);
        Assert.Equal("20", meshDetails["Vertices"]);
        Assert.Equal("10", meshDetails["Triangles"]);

        var animation = """
            MD5Version 10
            numFrames 48
            numJoints 4
            frameRate 24
            numAnimatedComponents 16
            """u8.ToArray();
        var animationDetails = Inspected("ogre.md5anim", animation);
        Assert.Equal("0:02", animationDetails["Duration"]);
        Assert.Equal("24 fps", animationDetails["Frame Rate"]);
    }

    [Fact]
    public void QuakeSidecarsAndAddedTextFormatsReportMetadata()
    {
        var lit = new byte[38];
        "QLIT"u8.CopyTo(lit);
        WriteInt32At(lit, 4, 1);
        Assert.Equal("10", Inspected("e1m1.lit", lit)["Samples"]);

        var vis = new byte[44];
        "e1m1.bsp"u8.CopyTo(vis);
        WriteInt32At(vis, 32, 8);
        vis.AsSpan(36).Fill(1);
        var visDetails = Inspected("e1m1.vis", vis);
        Assert.Equal("e1m1.bsp", visDetails["Maps"]);
        Assert.Equal("8 bytes", visDetails["Visibility Data"]);

        var nav = new byte[16];
        "NAV2"u8.CopyTo(nav);
        Assert.Equal("NAV2", Inspected("e1m1.nav", nav)["Version"]);

        var fgd = "// entity definitions\n@PointClass\n"u8.ToArray();
        var fgdDetails = Inspected("quake.fgd", fgd);
        Assert.Equal("Game definition", fgdDetails["Format"]);
        Assert.Equal("2", fgdDetails["Lines"]);
        Assert.True(fgdDetails.ContainsKey("Purpose"));
    }

    [Fact]
    public void DdsAndModernAudioHeadersReportMetadata()
    {
        var dds = new byte[148];
        "DDS "u8.CopyTo(dds);
        WriteInt32At(dds, 4, 124);
        WriteInt32At(dds, 12, 128);
        WriteInt32At(dds, 16, 256);
        WriteInt32At(dds, 28, 8);
        WriteInt32At(dds, 76, 32);
        "DX10"u8.CopyTo(dds.AsSpan(84));
        WriteInt32At(dds, 128, 98);
        var ddsDetails = Inspected("wall.dds", dds);
        Assert.Equal("256 × 128 pixels", ddsDetails["Dimensions"]);
        Assert.Equal("8", ddsDetails["Mipmaps"]);
        Assert.Equal("DX10 (DXGI format 98)", ddsDetails["Compression"]);

        var flac = new byte[42];
        "fLaC"u8.CopyTo(flac);
        flac[7] = 34;
        var streamInfo = (ulong)44_100 << 44 |
                         (ulong)1 << 41 |
                         (ulong)15 << 36 |
                         441_000UL;
        BinaryPrimitives.WriteUInt64BigEndian(flac.AsSpan(18), streamInfo);
        var flacDetails = Inspected("track.flac", flac);
        Assert.Equal("Stereo", flacDetails["Channels"]);
        Assert.Equal("16-bit", flacDetails["Bit Depth"]);
        Assert.Equal("0:10", flacDetails["Duration"]);

        var oggDetails = Inspected("track.ogg", MakeVorbis(48_000, 2, 480_000));
        Assert.Equal("Ogg Vorbis audio", oggDetails["Format"]);
        Assert.Equal("48,000 Hz", oggDetails["Sample Rate"]);
        Assert.Equal("0:10", oggDetails["Duration"]);

        var opusDetails = Inspected("track.opus", MakeOpus(480_312));
        Assert.Equal("Ogg Opus audio", opusDetails["Format"]);
        Assert.Equal("48,000 Hz", opusDetails["Sample Rate"]);
        Assert.Equal("0:10", opusDetails["Duration"]);
    }

    [Fact]
    public void TrackerAndUmxHeadersReportMetadata()
    {
        var xm = new byte[80];
        "Extended Module: "u8.CopyTo(xm);
        "Song"u8.CopyTo(xm.AsSpan(17));
        xm[37] = 0x1a;
        WriteUInt16At(xm, 58, 0x0104);
        WriteUInt16At(xm, 64, 4);
        WriteUInt16At(xm, 68, 8);
        WriteUInt16At(xm, 70, 3);
        WriteUInt16At(xm, 72, 5);
        WriteUInt16At(xm, 78, 125);
        Assert.Equal("8", Inspected("song.xm", xm)["Channels"]);

        var s3m = Enumerable.Repeat((byte)255, 96).ToArray();
        "Song"u8.CopyTo(s3m);
        "SCRM"u8.CopyTo(s3m.AsSpan(44));
        WriteUInt16At(s3m, 32, 4);
        WriteUInt16At(s3m, 34, 2);
        WriteUInt16At(s3m, 36, 3);
        s3m[50] = 125;
        s3m[64] = 0;
        s3m[65] = 1;
        Assert.Equal("2", Inspected("song.s3m", s3m)["Channels"]);

        var it = Enumerable.Repeat((byte)255, 128).ToArray();
        "IMPM"u8.CopyTo(it);
        "Song"u8.CopyTo(it.AsSpan(4));
        WriteUInt16At(it, 32, 4);
        WriteUInt16At(it, 34, 2);
        WriteUInt16At(it, 36, 6);
        WriteUInt16At(it, 38, 3);
        WriteUInt16At(it, 40, 0x0214);
        it[51] = 125;
        it[64] = 32;
        Assert.Equal("6", Inspected("song.it", it)["Samples"]);

        var mod = new byte[1_084];
        "Song"u8.CopyTo(mod);
        mod[950] = 2;
        mod[952] = 0;
        mod[953] = 3;
        "M.K."u8.CopyTo(mod.AsSpan(1_080));
        Assert.Equal("4", Inspected("song.mod", mod)["Patterns"]);

        var umx = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(umx, 0x9e2a83c1);
        WriteUInt16At(umx, 4, 69);
        WriteInt32At(umx, 12, 12);
        WriteInt32At(umx, 20, 1);
        Assert.Equal("Unreal music package", Inspected("song.umx", umx)["Format"]);
    }

    private static Dictionary<string, string> Inspected(string name, byte[] data) =>
        ArchiveMetadataInspector.Inspect(new ArchiveFileNode(name, data)).Details
            .ToDictionary(detail => detail.Label, detail => detail.Value);

    private static byte[] MakeVorbis(int sampleRate, byte channels, ulong samples)
    {
        var packet = new List<byte> { 1 };
        packet.AddRange("vorbis"u8.ToArray());
        packet.AddRange(new byte[4]);
        packet.Add(channels);
        var sampleRateBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(sampleRateBytes, sampleRate);
        packet.AddRange(sampleRateBytes);
        packet.AddRange(new byte[14]);

        var firstPage = new byte[28];
        "OggS"u8.CopyTo(firstPage);
        firstPage[5] = 2;
        firstPage[26] = 1;
        firstPage[27] = (byte)packet.Count;
        var data = new List<byte>(firstPage);
        data.AddRange(packet);

        var finalPage = new byte[28];
        "OggS"u8.CopyTo(finalPage);
        BinaryPrimitives.WriteUInt64LittleEndian(finalPage.AsSpan(6), samples);
        finalPage[26] = 1;
        data.AddRange(finalPage);
        return data.ToArray();
    }

    private static byte[] MakeOpus(ulong samples)
    {
        var packet = new List<byte>("OpusHead"u8.ToArray())
        {
            1,
            2,
            0x38,
            0x01,
        };
        var inputRate = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(inputRate, 44_100);
        packet.AddRange(inputRate);
        packet.AddRange([0, 0, 0]);

        var firstPage = new byte[28];
        "OggS"u8.CopyTo(firstPage);
        firstPage[5] = 2;
        firstPage[26] = 1;
        firstPage[27] = (byte)packet.Count;
        var data = new List<byte>(firstPage);
        data.AddRange(packet);

        var finalPage = new byte[28];
        "OggS"u8.CopyTo(finalPage);
        BinaryPrimitives.WriteUInt64LittleEndian(finalPage.AsSpan(6), samples);
        finalPage[26] = 1;
        data.AddRange(finalPage);
        return data.ToArray();
    }

    private static void WriteInt32At(byte[] data, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), value);

    private static void WriteUInt16At(byte[] data, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);

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
