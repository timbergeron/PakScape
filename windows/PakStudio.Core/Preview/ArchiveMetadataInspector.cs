using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using PakStudio.Core.Nodes;

namespace PakStudio.Core.Preview;

public sealed record ArchiveMetadataDetail(string Label, string Value);

public sealed record ArchiveMetadata(IReadOnlyList<ArchiveMetadataDetail> Details, string Summary)
{
    private static readonly string[] DetailsColumnPrefixesToHide =
        ["Duration:", "Description:", "Dimensions:"];

    public static ArchiveMetadata Empty { get; } = new([], string.Empty);

    public string SearchText => string.Join(" ", Details.Select(detail => detail.Value));

    public string DetailsColumnText => string.Join(
        "  •  ",
        Summary
            .Split("  •  ", StringSplitOptions.RemoveEmptyEntries)
            .Select(part =>
            {
                var prefix = DetailsColumnPrefixesToHide.FirstOrDefault(
                    candidate => part.StartsWith(candidate, StringComparison.Ordinal));
                return prefix is null ? part : part[prefix.Length..].TrimStart();
            }));

    public string DisplayText => string.Join(
        Environment.NewLine,
        Details.Select(detail => $"{detail.Label}: {detail.Value}"));
}

/// <summary>
/// Reads small, bounded headers from common Quake and desktop file formats.
/// Results are shared by details views, Quick Preview metadata, and search.
/// </summary>
public static partial class ArchiveMetadataInspector
{
    public const int MaximumInspectionBytes = 1024 * 1024;

    /// <summary>
    /// Large BSPs can place their entity lump, including the worldspawn title, after
    /// the first megabyte even though their fixed header is at the start.
    /// </summary>
    public const int MaximumBspInspectionBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Demos are read frame by frame rather than sampled, so the whole recording has to be
    /// available for the duration and the closing scores to be right. Longer recordings than
    /// this still describe themselves, but report their length as a lower bound.
    /// </summary>
    public const int MaximumDemoInspectionBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Ogg duration lives in the final page rather than the identification header. Most
    /// Quake music fits inside this bound; larger files still report their stream format.
    /// </summary>
    public const int MaximumAudioInspectionBytes = 16 * 1024 * 1024;

    /// <summary>
    /// MD3 surface headers are chained through each surface's complete payload, so the
    /// inspector needs the whole ordinary model to total vertices, triangles, and shaders.
    /// </summary>
    public const int MaximumModelInspectionBytes = 8 * 1024 * 1024;

    private const int MaximumListedPlayers = 8;

    /// <summary>The frag count Quake parks in a player slot once that player disconnects.</summary>
    private const int VacatedSlotFrags = -99;

    private static readonly ConditionalWeakTable<ArchiveFileNode, CacheEntry> Cache = new();
    private static readonly object CacheLock = new();

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".arena", ".cfg", ".csv", ".def", ".ent", ".fgd", ".ini", ".json", ".loc",
        ".log", ".lst", ".map", ".md", ".menu", ".pts", ".qc", ".rc", ".rtlights", ".scr",
        ".shader", ".skin", ".src", ".txt", ".xml", ".yaml", ".yml",
    };

    private static readonly HashSet<string> ExcludedBrushModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "b_batt0", "b_batt1", "b_bh10", "b_bh100", "b_bh25",
        "b_lnail0", "b_lnail1", "b_mrock0", "b_mrock1", "b_nail0", "b_nail1",
        "b_plas0", "b_plas1", "b_rock0", "b_rock1", "b_shell0", "b_shell1",
    };

    public static ArchiveMetadata Inspect(ArchiveNode node)
    {
        if (node is not ArchiveFileNode file || file.Data.Length == 0)
        {
            return ArchiveMetadata.Empty;
        }

        lock (CacheLock)
        {
            if (Cache.TryGetValue(file, out var cached) &&
                ReferenceEquals(cached.Data, file.Data) &&
                string.Equals(cached.Name, file.Name, StringComparison.Ordinal))
            {
                return cached.Metadata;
            }

            var metadata = InspectCore(file);
            Cache.Remove(file);
            Cache.Add(file, new CacheEntry(file.Data, file.Name, metadata));
            return metadata;
        }
    }

    private static ArchiveMetadata InspectCore(ArchiveFileNode file)
    {
        var extension = file.Extension.ToLowerInvariant();
        var leaf = Path.GetFileName(file.Name);
        var budget = extension switch
        {
            ".bsp" => MaximumBspInspectionBytes,
            ".dem" => MaximumDemoInspectionBytes,
            ".md3" => MaximumModelInspectionBytes,
            ".ogg" or ".opus" => MaximumAudioInspectionBytes,
            _ => MaximumInspectionBytes,
        };
        var data = file.Data.AsSpan(0, Math.Min(file.Data.Length, budget));
        List<ArchiveMetadataDetail> details = extension switch
        {
            ".bsp" => InspectBsp(data),
            ".dem" => InspectDemo(data),
            ".mdl" => OrMagic(InspectMdl(data), data),
            ".md3" => InspectMd3(data),
            ".md5" or ".md5mesh" or ".md5anim" => InspectMd5(data),
            ".spr" => InspectSprite(data),
            ".wad" => InspectWad(data),
            ".lit" => InspectLit(data, file.Data.Length),
            ".vis" => InspectVis(data, file.Data.Length),
            ".nav" => InspectNav(data, file.Data.Length),
            ".lmp" => InspectLmp(file.Name, data, file.Data.Length),
            ".dds" => InspectDds(data),
            ".pcx" => InspectPcx(data),
            ".tga" => InspectTga(data),
            ".png" => InspectPng(data),
            ".jpg" or ".jpeg" => InspectJpeg(data),
            ".gif" => InspectGif(data),
            ".bmp" => InspectBitmap(data),
            ".wav" => InspectWave(data),
            ".mp3" => InspectMp3(data, file.Data.Length),
            ".flac" => InspectFlac(data),
            ".ogg" or ".opus" => InspectOgg(data, file.Data.Length),
            ".it" => InspectIt(data),
            ".s3m" => InspectS3m(data),
            ".xm" => InspectXm(data),
            ".mod" => InspectMod(data),
            ".umx" => InspectUmx(data),
            ".sav" => InspectSavegame(data),
            /* Neither extension is exclusively Quake's, so both fall back to the magic. */
            ".dat" when leaf.Equals("iplog.dat", StringComparison.OrdinalIgnoreCase) =>
                OrMagic(InspectIpLog(data, file.Data.Length), data),
            ".dat" => OrMagic(InspectQuakeCProgram(data), data),
            ".bin" => OrMagic(InspectDosTextScreen(data, file.Data.Length), data),
            _ when leaf.Equals("servers.json.bad", StringComparison.OrdinalIgnoreCase) =>
                InspectText(".json", data, file.Data.Length),
            _ when leaf.Equals("qw_maps.tmp", StringComparison.OrdinalIgnoreCase) =>
                InspectText(".txt", data, file.Data.Length),
            _ when TextExtensions.Contains(extension) =>
                InspectText(extension, data, file.Data.Length),
            _ => InspectMagic(data),
        };

        /* What the file is for, which its header often cannot say on its own. */
        if (DescribePurpose(file.Name, extension) is { } purpose)
        {
            details.Add(Detail("Purpose", purpose));
        }

        return new ArchiveMetadata(details, BuildSummary(extension, details));
    }

    private static List<ArchiveMetadataDetail> InspectSavegame(ReadOnlySpan<byte> data)
    {
        var text = Encoding.Latin1.GetString(data).TrimStart('\uFEFF').Replace("\r", string.Empty);
        var lines = text.Split('\n');
        if (lines.Length == 0 ||
            !int.TryParse(lines[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) ||
            version is not (5 or 6))
        {
            return [];
        }

        var cursor = 1;
        string? gameDirectory = null;
        if (version == 6)
        {
            if (cursor >= lines.Length)
            {
                return [];
            }
            gameDirectory = lines[cursor++].Trim();
        }

        /* comment + 16 spawn parms + skill + map + elapsed time */
        if (lines.Length - cursor < 20)
        {
            return [];
        }

        var comment = lines[cursor++].Replace('_', ' ').Trim();
        cursor += 16;
        if (!int.TryParse(lines[cursor++].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var skill))
        {
            return [];
        }
        var map = lines[cursor++].Trim();
        if (map.Length == 0 ||
            !double.TryParse(
                lines[cursor].Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var elapsedTime) ||
            !double.IsFinite(elapsedTime))
        {
            return [];
        }

        List<ArchiveMetadataDetail> details =
        [
            Detail("Format", version == 6 ? "Quake remaster savegame" : "Quake savegame"),
            Detail("Version", version.ToString(CultureInfo.InvariantCulture)),
        ];
        if (comment.Length > 0)
        {
            details.Add(Detail("Description", comment));
        }
        details.Add(Detail("Map", map));
        details.Add(Detail("Skill", SkillName(skill)));
        details.Add(Detail("Duration", FormatDuration(elapsedTime)));
        if (!string.IsNullOrEmpty(gameDirectory))
        {
            details.Add(Detail("Mod", gameDirectory));
        }
        return details;
    }

    private static string SkillName(int skill) => skill switch
    {
        0 => "Easy",
        1 => "Normal",
        2 => "Hard",
        3 => "Nightmare",
        _ => $"Skill {skill}",
    };

    /// <summary>
    /// Reads the header qcc writes at the front of a compiled QuakeC program. The CRC is
    /// of the progdefs the code was built against, which is what an engine checks before
    /// it agrees to run a mod.
    /// </summary>
    private static List<ArchiveMetadataDetail> InspectQuakeCProgram(ReadOnlySpan<byte> data)
    {
        if (!TryInt32(data, 0, out var version) || version is not (6 or 7) ||
            !TryInt32(data, 4, out var crc) || crc < 0 ||
            !TryInt32(data, 12, out var statements) || statements <= 0 ||
            !TryInt32(data, 36, out var functions) || functions <= 0 ||
            !TryInt32(data, 40, out var stringOffset) || stringOffset < 60 ||
            !TryInt32(data, 44, out var stringBytes) || stringBytes < 0 ||
            !TryInt32(data, 56, out var entityFields) || entityFields < 0)
        {
            return [];
        }

        return
        [
            Detail("Format", "Compiled QuakeC program"),
            Detail("Version", version == 7 ? "7 (extended)" : version.ToString(CultureInfo.InvariantCulture)),
            Detail("Progdefs CRC", crc.ToString(CultureInfo.InvariantCulture)),
            Detail("Functions", functions.ToString("N0", CultureInfo.CurrentCulture)),
            Detail("Statements", statements.ToString("N0", CultureInfo.CurrentCulture)),
            Detail("Entity Fields", entityFields.ToString("N0", CultureInfo.CurrentCulture)),
            Detail("String Data", $"{stringBytes:N0} bytes"),
        ];
    }

    /// <summary>
    /// ProQuake and QSS-M store one masked IPv4 prefix and a 16-byte player name per record.
    /// </summary>
    private static List<ArchiveMetadataDetail> InspectIpLog(ReadOnlySpan<byte> data, int fileLength)
    {
        const int recordSize = 20;
        if (fileLength < recordSize || fileLength % recordSize != 0 || data.Length < recordSize)
        {
            return [];
        }
        return
        [
            Detail("Format", "ProQuake IP log"),
            Detail("Entries", (fileLength / recordSize).ToString("N0", CultureInfo.CurrentCulture)),
        ];
    }

    /// <summary>
    /// The 80 by 25 text-mode screens the DOS release printed as it exited, stored as a
    /// character and a colour attribute per cell.
    /// </summary>
    private static List<ArchiveMetadataDetail> InspectDosTextScreen(
        ReadOnlySpan<byte> data,
        int fileLength)
    {
        const int columns = 80;
        const int rows = 25;
        if (fileLength != columns * rows * 2 || data.Length < fileLength)
        {
            return [];
        }

        var characters = new byte[columns * rows];
        var plausible = 0;
        for (var cell = 0; cell < characters.Length; cell++)
        {
            characters[cell] = data[cell * 2];
            if (characters[cell] == 0 || characters[cell] >= 0x20)
            {
                plausible++;
            }
        }
        /* Junk that happens to be this long is ruled out by its character cells. */
        if (plausible * 100 < characters.Length * 95)
        {
            return [];
        }

        List<ArchiveMetadataDetail> details =
        [
            Detail("Format", "DOS text-mode screen"),
            Detail("Screen Size", $"{columns} × {rows} characters"),
        ];
        if (DosTextScreenHeadline(characters, columns, rows) is { } headline)
        {
            details.Add(Detail("Description", headline));
        }
        return details;
    }

    /// <summary>The first line with words on it, which on these screens is the title.</summary>
    private static string? DosTextScreenHeadline(byte[] characters, int columns, int rows)
    {
        var builder = new StringBuilder(columns);
        for (var row = 0; row < rows; row++)
        {
            builder.Clear();
            for (var column = 0; column < columns; column++)
            {
                var character = characters[row * columns + column];
                builder.Append(character is >= 0x20 and < 0x7f ? (char)character : ' ');
            }

            var line = builder.ToString().Trim();
            if (line.Length >= 8 && line.Count(char.IsLetter) >= 4)
            {
                return line;
            }
        }
        return null;
    }

    /*
     * What a file is for, in one line. Names come first, because a name can mean
     * something an extension cannot: palette.lmp is a palette, not just an image.
     * Formats that already describe themselves are left alone.
     */
    private static readonly Dictionary<string, string> PurposesByName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["progs.dat"] = "The compiled QuakeC program the engine runs: the rules, weapons, and monsters of the game or mod.",
            ["qwprogs.dat"] = "The compiled QuakeC program a QuakeWorld server runs.",
            ["spprogs.dat"] = "The compiled QuakeC program for single-player, where a mod ships a separate build.",
            ["csprogs.dat"] = "Client-side QuakeC, run by the client for effects and HUD work the server cannot draw.",
            ["menu.dat"] = "A QuakeC menu program, run by engines that replace the built-in menus.",
            ["pak.lst"] = "The package load order QSS-M applies after the base game PAKs, with one PAK or PK3 name per entry.",
            ["progs.src"] = "The list qcc compiles: the program to write first, then every QuakeC source file in order.",
            ["quake.rc"] = "The startup script the engine runs at launch: it execs default.cfg, config.cfg, and autoexec.cfg, then starts the demo loop.",
            ["default.cfg"] = "The bindings and settings the game ships with, exec'd before any saved configuration.",
            ["config.cfg"] = "The bindings and settings the engine writes back when it quits.",
            ["autoexec.cfg"] = "Commands run after the saved configuration, where a player keeps their own overrides.",
            ["end1.bin"] = "The text screen the DOS release printed on exit from the shareware episode.",
            ["end2.bin"] = "The text screen the DOS release printed on exit from the registered game.",
            ["palette.lmp"] = "The 256 colours every paletted Quake image is drawn from.",
            ["colormap.lmp"] = "The shading table the software renderer used to darken palette colours.",
            ["pop.lmp"] = "The pattern QuakeWorld servers checked to tell a registered install from shareware.",
            ["gfx.wad"] = "The 2D interface art: console font, status bar, and menu graphics.",
            ["conchars.lmp"] = "The console character set, one 16 by 16 grid of glyphs.",
            ["servers.json"] = "QSS-M's dated multiplayer server history, used by history menus and address completion.",
            ["servers.json.bad"] = "An unreadable QSS-M server history preserved before a fresh servers.json was started.",
            ["servers.txt"] = "The legacy QSS-M multiplayer server history, imported into servers.json.",
            ["lastserver.txt"] = "The legacy record of the last multiplayer server used, imported into servers.json.",
            ["server_hostnames.json"] = "QSS-M's cache of successfully resolved server hostnames and endpoints.",
            ["bookmarks.json"] = "QSS-M's multiplayer server bookmarks, including their pinned order.",
            ["bookmarks.txt"] = "The legacy QSS-M server bookmark list, imported into bookmarks.json.",
            ["names.json"] = "QSS-M's dated player-name history.",
            ["names.txt"] = "The legacy QSS-M player-name history, imported into names.json.",
            ["demomarks.json"] = "QSS-M's saved timeline markers for recorded demos.",
            ["mapdesc.json"] = "QSS-M's cache of map names and descriptions.",
            ["shistory.json"] = "QSS-M's most recently used multiplayer host-game settings.",
            ["demos_metadata_cache.json"] = "QSS-M's cache of metadata parsed for the demo browser.",
            ["optional_download_cache.json"] = "QSS-M's retry cache for optional location-file downloads.",
            ["skybox_download_cache.json"] = "QSS-M's retry cache for downloaded skybox faces.",
            ["qw_maps.txt"] = "QSS-M's downloaded QuakeWorld map-name list for console completion.",
            ["qw_maps.tmp"] = "A temporary QSS-M QuakeWorld map-list download awaiting validation.",
            ["lastdemo.txt"] = "The name of the most recently recorded QSS-M demo.",
            ["ghost.txt"] = "QSS-M's temporary multiplayer ghost code, retained across a restart or crash.",
            ["name.txt"] = "QSS-M's temporary player-name backup, retained while the AFK name is active.",
            ["iplog.dat"] = "The binary player IP-prefix and name history used by ProQuake-compatible commands.",
            ["iplog.txt"] = "A readable export of the player IP-prefix and name history.",
        };

    private static readonly Dictionary<string, string> PurposesByExtension =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".rc"] = "A console script the engine execs at startup.",
            [".cfg"] = "A console script of settings and bindings, exec'd by the engine.",
            [".src"] = "A qcc source list naming the QuakeC files to compile, in order.",
            [".qc"] = "QuakeC source, compiled into a progs program by qcc.",
            [".lit"] = "External coloured lighting for the level of the same name.",
            [".ent"] = "An external entity list, loaded in place of the one inside the level.",
            [".loc"] = "Location names a QuakeWorld client reports with %l.",
            [".sav"] = "A Quake savegame.",
            [".skin"] = "A Quake III skin file, mapping each surface of a model to a texture.",
            [".shader"] = "A Quake III shader script describing how a texture is drawn.",
            [".fgd"] = "Game and entity definitions used by level editors.",
            [".pts"] = "A point-by-point leak trail written by a map compiler.",
            [".rtlights"] = "External real-time lights for the level of the same name.",
            [".scr"] = "A console command script.",
            [".vis"] = "External visibility and leaf data for one or more Quake levels.",
            [".nav"] = "Bot navigation data for the level of the same name.",
        };

    private static string? DescribePurpose(string fileName, string extension)
    {
        var leaf = Path.GetFileName(fileName);
        if (leaf.Length > 0 && PurposesByName.TryGetValue(leaf, out var named))
        {
            return named;
        }
        if (Regex.IsMatch(leaf, @"^config-\d{2}-\d{2}-\d{4}\.cfg$", RegexOptions.IgnoreCase))
        {
            return "A dated backup of the effective QSS-M configuration.";
        }
        return PurposesByExtension.TryGetValue(extension, out var byExtension) ? byExtension : null;
    }

    private static List<ArchiveMetadataDetail> InspectBsp(ReadOnlySpan<byte> data)
    {
        if (!TryInt32(data, 0, out var version))
        {
            return [];
        }

        var magic = Ascii(data, 0, 4);
        var format = version switch
        {
            29 => "Quake BSP level",
            30 => "GoldSrc BSP level",
            23 => "Quake 64 BSP level",
            _ when magic == "BSP2" => "Quake BSP2 level",
            _ when magic == "2PSB" => "Quake BSP2-RMQ level",
            _ => null,
        };
        if (format is null)
        {
            return [];
        }

        List<ArchiveMetadataDetail> details =
        [
            Detail("Format", format),
            Detail("Version", magic is "BSP2" or "2PSB"
                ? magic
                : version.ToString(CultureInfo.InvariantCulture)),
        ];
        if (TryBspLump(data, 0, out var entityOffset, out var entityLength))
        {
            var entityText = QuakeText(data.Slice(entityOffset, entityLength));
            var worldspawnEnd = entityText.IndexOf('}');
            if (worldspawnEnd >= 0)
            {
                var match = WorldspawnMessageRegex().Match(entityText[..worldspawnEnd]);
                if (match.Success)
                {
                    var description = match.Groups[1].Value
                        .Replace("\\\"", "\"", StringComparison.Ordinal)
                        .Replace("\\\\", "\\", StringComparison.Ordinal)
                        .Trim();
                    if (description.Length > 0)
                    {
                        details.Add(Detail("Description", description));
                    }
                }
            }
        }
        AddBspCount(details, data, 3, 12, "Vertices");
        AddBspCount(details, data, 7, magic is "BSP2" or "2PSB" ? 28 : 20, "Faces");
        AddBspCount(details, data, 14, 64, "Models");
        if (TryBspLump(data, 2, out var textureOffset, out _) &&
            TryInt32(data, textureOffset, out var textures) &&
            textures >= 0)
        {
            details.Add(Detail("Textures", textures.ToString("N0", CultureInfo.CurrentCulture)));
        }
        return details;
    }

    private static List<ArchiveMetadataDetail> InspectDemo(ReadOnlySpan<byte> data)
    {
        if (QuakeDemoInspector.Inspect(data) is { } demo && DescribeDemo(demo) is { Count: > 1 } parsed)
        {
            return parsed;
        }
        return ScanDemoMapNames(data);
    }

    private static List<ArchiveMetadataDetail> DescribeDemo(QuakeDemoSummary demo)
    {
        List<ArchiveMetadataDetail> details = [Detail("Format", "Quake demo")];

        var maps = demo.Segments
            .Select(segment => segment.Map)
            .Where(map => map.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (maps.Count > 0)
        {
            details.Add(Detail(maps.Count == 1 ? "Map" : "Maps", string.Join(", ", maps)));
        }

        var levelName = demo.Segments.FirstOrDefault(segment => segment.LevelName.Length > 0)?.LevelName;
        if (levelName is not null && !string.Equals(levelName, maps.FirstOrDefault(), StringComparison.OrdinalIgnoreCase))
        {
            details.Add(Detail("Level", levelName));
        }

        if (demo.Duration > 0)
        {
            var prefix = demo.Truncated || !demo.MessagesComplete ? "At least " : string.Empty;
            details.Add(Detail("Duration", prefix + FormatDuration(demo.Duration)));
        }

        if (demo.MaxClients > 0)
        {
            details.Add(Detail(
                "Mode",
                demo.IsSinglePlayer ? "Single player" : demo.IsDeathmatch ? "Deathmatch" : "Cooperative"));
        }

        if (demo.GameDir.Length > 0 && !string.Equals(demo.GameDir, "id1", StringComparison.OrdinalIgnoreCase))
        {
            details.Add(Detail("Mod", demo.GameDir));
        }

        var players = demo.Players.Where(player => player.Name.Trim().Length > 0).ToList();
        if (players.Count > 0)
        {
            details.Add(Detail(
                players.Count == 1 ? "Player" : "Players",
                string.Join(", ", players.Take(MaximumListedPlayers).Select(player => player.Name.Trim())) +
                (players.Count > MaximumListedPlayers ? $", +{players.Count - MaximumListedPlayers} more" : string.Empty)));
        }

        // Frag counts only mean something once someone can score against someone else, and
        // slots left by players who disconnected keep their name but score -99.
        var scoring = players.Where(player => player.Frags != VacatedSlotFrags).ToList();
        if (scoring.Count > 1 && scoring.Any(player => player.Frags != 0))
        {
            details.Add(Detail(
                "Scores",
                string.Join(
                    ", ",
                    scoring
                        .OrderByDescending(player => player.Frags)
                        .Take(MaximumListedPlayers)
                        .Select(player => $"{player.Name.Trim()} {player.Frags.ToString(CultureInfo.CurrentCulture)}"))));
        }

        if (demo.ProtocolName.Length > 0 && demo.ProtocolName != "unknown")
        {
            details.Add(Detail("Protocol", demo.ProtocolName));
        }

        return details;
    }

    /// <summary>
    /// Falls back to spotting map paths in the raw bytes, which still describes demos this
    /// parser bails on and recordings wrapped in another container.
    /// </summary>
    private static List<ArchiveMetadataDetail> ScanDemoMapNames(ReadOnlySpan<byte> data)
    {
        // The scan builds a string of the whole span, so it keeps the ordinary header budget
        // rather than the larger one the frame walk gets.
        var text = QuakeText(data[..Math.Min(data.Length, MaximumInspectionBytes)]);
        var maps = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in DemoMapRegex().Matches(text))
        {
            var map = match.Groups[1].Value;
            if (!ExcludedBrushModels.Contains(map) && seen.Add(map))
            {
                maps.Add(map);
            }
        }

        List<ArchiveMetadataDetail> details = [Detail("Format", "Quake demo")];
        if (maps.Count > 0)
        {
            details.Add(Detail(maps.Count == 1 ? "Map" : "Maps", string.Join(", ", maps)));
        }
        return details;
    }

    private static List<ArchiveMetadataDetail> InspectMdl(ReadOnlySpan<byte> data)
    {
        if (!HasAscii(data, 0, "IDPO") ||
            !TryInt32(data, 4, out var version) ||
            !TryPositiveInt32(data, 52, out var width) ||
            !TryPositiveInt32(data, 56, out var height))
        {
            return [];
        }
        List<ArchiveMetadataDetail> details =
        [
            Detail("Format", "Quake alias model"),
            Detail("Version", version.ToString(CultureInfo.InvariantCulture)),
            Detail("Skin Size", Dimensions(width, height)),
            CountDetail(data, 48, "Skins"),
            CountDetail(data, 60, "Vertices"),
            CountDetail(data, 64, "Triangles"),
            CountDetail(data, 68, "Frames"),
        ];
        return details.Where(detail => detail.Value.Length > 0).ToList();
    }

    private static List<ArchiveMetadataDetail> InspectSprite(ReadOnlySpan<byte> data)
    {
        if (!HasAscii(data, 0, "IDSP") ||
            !TryInt32(data, 4, out var version) ||
            !TryPositiveInt32(data, 16, out var width) ||
            !TryPositiveInt32(data, 20, out var height))
        {
            return [];
        }
        List<ArchiveMetadataDetail> details =
        [
            Detail("Format", "Quake sprite"),
            Detail("Version", version.ToString(CultureInfo.InvariantCulture)),
            Detail("Canvas Size", Dimensions(width, height)),
            CountDetail(data, 24, "Frames"),
        ];
        return details.Where(detail => detail.Value.Length > 0).ToList();
    }

    private static List<ArchiveMetadataDetail> InspectWad(ReadOnlySpan<byte> data)
    {
        var magic = Ascii(data, 0, 4);
        if (magic is not ("WAD2" or "WAD3") || !TryInt32(data, 4, out var entries) || entries < 0)
        {
            return [];
        }
        return
        [
            Detail("Format", magic == "WAD2" ? "Quake WAD archive" : "GoldSrc WAD archive"),
            Detail("Entries", entries.ToString("N0", CultureInfo.CurrentCulture)),
        ];
    }

    private static List<ArchiveMetadataDetail> InspectLmp(
        string fileName,
        ReadOnlySpan<byte> data,
        int fileSize)
    {
        var baseName = Path.GetFileName(fileName);
        if (baseName.Equals("palette.lmp", StringComparison.OrdinalIgnoreCase) && fileSize == 768)
        {
            return [Detail("Format", "Quake color palette"), Detail("Colors", "256"), Detail("Color Depth", "24-bit RGB")];
        }
        if (baseName.Equals("colormap.lmp", StringComparison.OrdinalIgnoreCase) && fileSize >= 16_384)
        {
            return [Detail("Format", "Quake color map"), Detail("Dimensions", Dimensions(256, 64))];
        }
        if (baseName.Equals("conchars.lmp", StringComparison.OrdinalIgnoreCase) && fileSize >= 16_384)
        {
            return [Detail("Format", "Quake console character sheet"), Detail("Dimensions", Dimensions(128, 128))];
        }
        if (TryPositiveInt32(data, 0, out var width) &&
            TryPositiveInt32(data, 4, out var height) &&
            SafeDimensions(width, height) &&
            (long)width * height <= fileSize - 8)
        {
            return
            [
                Detail("Format", "Quake indexed image"),
                Detail("Dimensions", Dimensions(width, height)),
                Detail("Color Depth", "8-bit indexed"),
            ];
        }
        return [Detail("Format", "Quake binary lump")];
    }

    private static List<ArchiveMetadataDetail> InspectPcx(ReadOnlySpan<byte> data)
    {
        if (data.Length < 66 || data[0] != 0x0a)
        {
            return [];
        }
        var xMin = U16(data, 4);
        var yMin = U16(data, 6);
        var xMax = U16(data, 8);
        var yMax = U16(data, 10);
        if (xMax < xMin || yMax < yMin)
        {
            return [];
        }
        var bits = data[3] * data[65];
        return
        [
            Detail("Format", "ZSoft PCX image"),
            Detail("Dimensions", Dimensions(xMax - xMin + 1, yMax - yMin + 1)),
            Detail("Color Depth", $"{bits}-bit"),
        ];
    }

    private static List<ArchiveMetadataDetail> InspectTga(ReadOnlySpan<byte> data)
    {
        if (data.Length < 18)
        {
            return [];
        }
        var width = U16(data, 12);
        var height = U16(data, 14);
        if (!SafeDimensions(width, height))
        {
            return [];
        }
        return
        [
            Detail("Format", "Truevision TGA image"),
            Detail("Dimensions", Dimensions(width, height)),
            Detail("Color Depth", $"{data[16]}-bit"),
        ];
    }

    private static List<ArchiveMetadataDetail> InspectPng(ReadOnlySpan<byte> data)
    {
        if (data.Length < 29 ||
            !data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            return [];
        }
        var rawWidth = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(16, 4));
        var rawHeight = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(20, 4));
        if (rawWidth > int.MaxValue || rawHeight > int.MaxValue)
        {
            return [];
        }
        var width = (int)rawWidth;
        var height = (int)rawHeight;
        return SafeDimensions(width, height)
            ?
            [
                Detail("Format", "PNG image"),
                Detail("Dimensions", Dimensions(width, height)),
                Detail("Bit Depth", $"{data[24]}-bit"),
            ]
            : [];
    }

    private static List<ArchiveMetadataDetail> InspectJpeg(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xff || data[1] != 0xd8)
        {
            return [];
        }
        var cursor = 2;
        while (cursor + 8 < data.Length)
        {
            if (data[cursor] != 0xff)
            {
                cursor++;
                continue;
            }
            var marker = data[cursor + 1];
            if (marker is 0xc0 or 0xc1 or 0xc2 or 0xc3 or 0xc5 or 0xc6 or 0xc7 or 0xc9 or 0xca or 0xcb or 0xcd or 0xce or 0xcf)
            {
                var height = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(cursor + 5, 2));
                var width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(cursor + 7, 2));
                return
                [
                    Detail("Format", "JPEG image"),
                    Detail("Dimensions", Dimensions(width, height)),
                    Detail("Precision", $"{data[cursor + 4]} bits per component"),
                ];
            }
            if (cursor + 4 > data.Length)
            {
                break;
            }
            var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(cursor + 2, 2));
            if (length < 2 || cursor + 2 + length > data.Length)
            {
                break;
            }
            cursor += 2 + length;
        }
        return [Detail("Format", "JPEG image")];
    }

    private static List<ArchiveMetadataDetail> InspectGif(ReadOnlySpan<byte> data) =>
        data.Length >= 10 && (HasAscii(data, 0, "GIF87a") || HasAscii(data, 0, "GIF89a"))
            ?
            [
                Detail("Format", "GIF image"),
                Detail("Canvas Size", Dimensions(U16(data, 6), U16(data, 8))),
            ]
            : [];

    private static List<ArchiveMetadataDetail> InspectBitmap(ReadOnlySpan<byte> data)
    {
        if (data.Length < 30 || !HasAscii(data, 0, "BM") ||
            !TryInt32(data, 18, out var width) ||
            !TryInt32(data, 22, out var rawHeight))
        {
            return [];
        }
        return
        [
            Detail("Format", "Windows bitmap image"),
            Detail("Dimensions", Dimensions(Math.Abs(width), Math.Abs(rawHeight))),
            Detail("Color Depth", $"{U16(data, 28)}-bit"),
        ];
    }

    private static List<ArchiveMetadataDetail> InspectWave(ReadOnlySpan<byte> data)
    {
        if (!HasAscii(data, 0, "RIFF") || !HasAscii(data, 8, "WAVE"))
        {
            return [];
        }
        List<ArchiveMetadataDetail> details = [Detail("Format", "WAVE audio")];
        var cursor = 12;
        uint? byteRate = null;
        uint? audioBytes = null;
        while (cursor + 8 <= data.Length)
        {
            var id = Ascii(data, cursor, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(cursor + 4, 4));
            if (size > int.MaxValue || cursor + 8L + size > data.Length)
            {
                break;
            }
            if (id == "fmt " && size >= 16)
            {
                var channels = U16(data, cursor + 10);
                var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(cursor + 12, 4));
                byteRate = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(cursor + 16, 4));
                details.Add(Detail("Channels", channels == 1 ? "Mono" : channels == 2 ? "Stereo" : $"{channels} channels"));
                details.Add(Detail("Sample Rate", $"{sampleRate:N0} Hz"));
            }
            else if (id == "data")
            {
                audioBytes = size;
            }
            cursor += 8 + (int)size + ((int)size & 1);
        }
        if (byteRate > 0 && audioBytes is { } bytes)
        {
            details.Add(Detail("Duration", FormatDuration((double)bytes / byteRate.Value)));
        }
        return details;
    }

    private static List<ArchiveMetadataDetail> InspectMp3(ReadOnlySpan<byte> data, int fileSize)
    {
        List<ArchiveMetadataDetail> details = [Detail("Format", "MPEG audio layer III")];
        var cursor = 0;
        if (data.Length >= 10 && HasAscii(data, 0, "ID3"))
        {
            details.Add(Detail("ID3 Metadata", $"Version 2.{data[3]}.{data[4]}"));
            if (data[6] < 128 && data[7] < 128 && data[8] < 128 && data[9] < 128)
            {
                var tagSize = data[6] << 21 | data[7] << 14 | data[8] << 7 | data[9];
                cursor = Math.Min(data.Length, 10 + tagSize);
            }
        }

        var searchEnd = Math.Min(data.Length - 4, cursor + 256 * 1024);
        for (var offset = cursor; offset <= searchEnd; offset++)
        {
            var header = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            if ((header & 0xffe0_0000) != 0xffe0_0000)
            {
                continue;
            }
            var versionBits = (int)(header >> 19 & 0x3);
            var layerBits = (int)(header >> 17 & 0x3);
            var bitrateIndex = (int)(header >> 12 & 0xf);
            var sampleRateIndex = (int)(header >> 10 & 0x3);
            if (versionBits == 1 || layerBits != 1 ||
                bitrateIndex is <= 0 or >= 15 || sampleRateIndex >= 3)
            {
                continue;
            }

            int[] mpeg1Bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
            int[] mpeg2Bitrates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
            int[] baseSampleRates = [44_100, 48_000, 32_000];
            var bitrate = versionBits == 3 ? mpeg1Bitrates[bitrateIndex] : mpeg2Bitrates[bitrateIndex];
            var divisor = versionBits == 3 ? 1 : versionBits == 2 ? 2 : 4;
            var sampleRate = baseSampleRates[sampleRateIndex] / divisor;
            var channelMode = (int)(header >> 6 & 0x3);
            details.Add(Detail("Bit Rate", $"{bitrate} kbps"));
            details.Add(Detail("Sample Rate", $"{sampleRate:N0} Hz"));
            details.Add(Detail("Channels", channelMode == 3 ? "Mono" : "Stereo"));
            details.Add(Detail("Duration", FormatDuration((double)fileSize * 8 / (bitrate * 1000))));
            break;
        }
        return details;
    }

    private static List<ArchiveMetadataDetail> InspectText(
        string extension,
        ReadOnlySpan<byte> data,
        int fileSize)
    {
        string encoding;
        string text;
        if (data.StartsWith(new byte[] { 0xff, 0xfe }))
        {
            encoding = "UTF-16 little-endian";
            text = Encoding.Unicode.GetString(data[2..]);
        }
        else if (data.StartsWith(new byte[] { 0xfe, 0xff }))
        {
            encoding = "UTF-16 big-endian";
            text = Encoding.BigEndianUnicode.GetString(data[2..]);
        }
        else
        {
            encoding = "UTF-8";
            text = Encoding.UTF8.GetString(data);
        }
        var lines = text.Length == 0 ? 0 : text.Count(character => character == '\n') + (text.EndsWith('\n') ? 0 : 1);
        var prefix = data.Length < fileSize ? "At least " : string.Empty;
        return
        [
            Detail("Format", TextFormat(extension)),
            Detail("Encoding", encoding),
            Detail("Lines", prefix + lines.ToString("N0", CultureInfo.CurrentCulture)),
        ];
    }

    private static List<ArchiveMetadataDetail> OrMagic(
        List<ArchiveMetadataDetail> details,
        ReadOnlySpan<byte> data) =>
        details.Count > 0 ? details : InspectMagic(data);

    private static List<ArchiveMetadataDetail> InspectMagic(ReadOnlySpan<byte> data)
    {
        if (HasAscii(data, 0, "IDPO")) return InspectMdl(data);
        if (HasAscii(data, 0, "IDP3")) return InspectMd3(data);
        if (HasAscii(data, 0, "IDSP")) return InspectSprite(data);
        if (HasAscii(data, 0, "WAD2") || HasAscii(data, 0, "WAD3")) return InspectWad(data);
        if (HasAscii(data, 0, "QLIT")) return InspectLit(data, data.Length);
        if (HasAscii(data, 0, "NAV2")) return InspectNav(data, data.Length);
        if (HasAscii(data, 0, "DDS ")) return InspectDds(data);
        if (HasAscii(data, 0, "fLaC")) return InspectFlac(data);
        if (HasAscii(data, 0, "OggS")) return InspectOgg(data, data.Length);
        if (HasAscii(data, 0, "RIFF") && HasAscii(data, 8, "WAVE")) return InspectWave(data);
        return [];
    }

    private static string BuildSummary(string extension, IReadOnlyList<ArchiveMetadataDetail> details)
    {
        if (details.Count == 0)
        {
            return string.Empty;
        }
        if (extension is ".bsp" or ".bin" && Find(details, "Description") is { } description)
        {
            return $"Description: {description}";
        }
        string[] preferred = extension switch
        {
            ".bsp" => ["Vertices", "Faces"],
            ".dat" => ["Functions", "Entity Fields"],
            ".bin" => ["Screen Size"],
            ".dem" => ["Map", "Maps", "Duration"],
            ".sav" => ["Map", "Skill", "Duration"],
            ".mdl" or ".md3" or ".md5" or ".md5mesh" or ".md5anim" or ".spr" =>
                ["Skin Size", "Canvas Size", "Frames", "Meshes", "Surfaces", "Triangles"],
            ".wav" or ".mp3" or ".flac" or ".ogg" or ".opus" or
                ".it" or ".s3m" or ".xm" or ".mod" or ".umx" =>
                ["Duration", "Channels", "Sample Rate", "Bit Rate", "Patterns"],
            ".lit" => ["Samples", "Version"],
            ".vis" => ["Maps", "Visibility Data"],
            ".nav" => ["Format", "Data Size"],
            ".dds" => ["Dimensions", "Compression", "Mipmaps"],
            ".wad" => ["Entries"],
            _ when TextExtensions.Contains(extension) => ["Lines", "Encoding"],
            _ => ["Dimensions", "Canvas Size", "Color Depth", "Bit Depth", "Frames"],
        };
        var selected = preferred
            .Select(label => details.FirstOrDefault(detail => detail.Label == label))
            .Where(detail => detail is not null)
            .Take(2)
            .Cast<ArchiveMetadataDetail>()
            .ToList();
        if (selected.Count == 0)
        {
            /* Purpose is a sentence, so it belongs in details rather than in a column. */
            selected = details
                .Where(detail => detail.Label is not ("Format" or "Version" or "Purpose"))
                .Take(2)
                .ToList();
        }
        return selected.Count == 0
            ? Find(details, "Format") ?? string.Empty
            : string.Join("  •  ", selected.Select(detail => $"{detail.Label}: {detail.Value}"));
    }

    private static void AddBspCount(
        ICollection<ArchiveMetadataDetail> details,
        ReadOnlySpan<byte> data,
        int lump,
        int recordSize,
        string label)
    {
        if (TryBspLump(data, lump, out _, out var length) && length % recordSize == 0)
        {
            details.Add(Detail(label, (length / recordSize).ToString("N0", CultureInfo.CurrentCulture)));
        }
    }

    private static bool TryBspLump(
        ReadOnlySpan<byte> data,
        int index,
        out int offset,
        out int length)
    {
        offset = 0;
        length = 0;
        var baseOffset = 4 + index * 8;
        return TryInt32(data, baseOffset, out offset) &&
               TryInt32(data, baseOffset + 4, out length) &&
               offset >= 0 &&
               length >= 0 &&
               offset <= data.Length &&
               length <= data.Length - offset;
    }

    private static ArchiveMetadataDetail CountDetail(ReadOnlySpan<byte> data, int offset, string label) =>
        TryInt32(data, offset, out var value) && value >= 0
            ? Detail(label, value.ToString("N0", CultureInfo.CurrentCulture))
            : Detail(label, string.Empty);

    private static ArchiveMetadataDetail Detail(string label, string value) => new(label, value);

    private static string? Find(IEnumerable<ArchiveMetadataDetail> details, string label) =>
        details.FirstOrDefault(detail => detail.Label == label)?.Value;

    private static string Dimensions(int width, int height) => $"{width:N0} × {height:N0} pixels";

    private static bool SafeDimensions(int width, int height) =>
        width > 0 && height > 0 && width <= 8192 && height <= 8192 && (long)width * height <= 16_777_216;

    private static string FormatDuration(double seconds)
    {
        var duration = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private static string TextFormat(string extension) => extension switch
    {
        ".cfg" => "Quake configuration",
        ".rc" => "Quake console script",
        ".src" => "qcc source list",
        ".lst" => "Package load-order list",
        ".loc" => "QuakeWorld locations",
        ".ent" => "Quake entity definitions",
        ".fgd" => "Game definition",
        ".map" => "Quake map source",
        ".pts" => "Quake leak trail",
        ".qc" => "QuakeC source",
        ".rtlights" => "Quake real-time lights",
        ".scr" => "Quake console script",
        ".shader" => "Shader script",
        ".skin" => "Quake III skin mapping",
        ".json" => "JSON",
        ".xml" => "XML",
        ".yaml" or ".yml" => "YAML",
        ".csv" => "CSV",
        ".md" => "Markdown",
        _ => "Plain text",
    };

    private static string QuakeText(ReadOnlySpan<byte> data)
    {
        var characters = new char[data.Length];
        for (var index = 0; index < data.Length; index++)
        {
            characters[index] = (char)(data[index] & 0x7f);
        }
        return new string(characters);
    }

    private static bool TryPositiveInt32(ReadOnlySpan<byte> data, int offset, out int value) =>
        TryInt32(data, offset, out value) && value > 0;

    private static bool TryInt32(ReadOnlySpan<byte> data, int offset, out int value)
    {
        value = 0;
        if (offset < 0 || offset > data.Length - 4)
        {
            return false;
        }
        value = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
        return true;
    }

    private static ushort U16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));

    private static string Ascii(ReadOnlySpan<byte> data, int offset, int length) =>
        offset >= 0 && length >= 0 && offset <= data.Length - length
            ? Encoding.ASCII.GetString(data.Slice(offset, length))
            : string.Empty;

    private static bool HasAscii(ReadOnlySpan<byte> data, int offset, string value) =>
        Ascii(data, offset, value.Length) == value;

    private sealed record CacheEntry(byte[] Data, string Name, ArchiveMetadata Metadata);

    [GeneratedRegex("\"message\"\\s+\"((?:\\\\.|[^\"])*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex WorldspawnMessageRegex();

    [GeneratedRegex(@"maps/([a-z0-9_+\-.]+)\.bsp", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DemoMapRegex();
}
