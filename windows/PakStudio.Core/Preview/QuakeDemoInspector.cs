using System.Buffers.Binary;
using System.Text;

namespace PakStudio.Core.Preview;

public sealed record QuakeDemoPlayer(int Slot, string Name, int Frags, int Shirt, int Pants);

public sealed record QuakeDemoSegment(string Map, string LevelName, double Duration);

/// <summary>
/// Everything the metadata inspector can learn by walking a recorded demo.
/// <see cref="MessagesComplete"/> is false when the server message stream ran into
/// something this parser cannot decode, in which case the fields carry whatever was
/// read before that point and the frame walk supplied the timings.
/// </summary>
public sealed record QuakeDemoSummary(
    string ProtocolName,
    IReadOnlyList<QuakeDemoSegment> Segments,
    string GameDir,
    int MaxClients,
    int GameType,
    double Duration,
    int FrameCount,
    IReadOnlyList<QuakeDemoPlayer> Players,
    bool MessagesComplete,
    bool Truncated)
{
    public bool IsDeathmatch => GameType != 0;

    public bool IsSinglePlayer => GameType == 0 && MaxClients <= 1;
}

/// <summary>
/// Parses Quake demo (.dem) recordings far enough to describe them: protocol, levels,
/// players, scores, and length. The logic follows the q1tools demo parser, which in turn
/// follows the FTE and QuakeSpasm client message readers.
/// </summary>
/// <remarks>
/// Demo frames are length prefixed, so timings survive even when a frame's payload uses
/// an extension this parser does not know. Message decoding stops at the first such frame
/// and the frame walk continues alone.
/// </remarks>
public static class QuakeDemoInspector
{
    private const int MaximumMessageLength = 64000;
    private const int MaximumModels = 4096;
    private const int MaximumSounds = 2048;
    private const int MaximumPlayers = 255;

    private const int ProtocolNetQuake = 15;
    private const int ProtocolFitzQuake = 666;
    private const int ProtocolRmq = 999;
    private const int ProtocolDp7 = 3504;
    private const int ProtocolBjp3 = 10002;
    private const int ProtocolFtePext1 = 0x58455446;
    private const int ProtocolFtePext2 = 0x32455446;

    private const uint PrflShortAngle = 1 << 1;
    private const uint PrflFloatAngle = 1 << 2;
    private const uint Prfl24BitCoord = 1 << 3;
    private const uint PrflFloatCoord = 1 << 4;
    private const uint PrflInt32Coord = 1 << 7;

    private const uint Pext2ReplacementDeltas = 0x00000008;
    private const uint Pext2PredInfo = 0x00000020;
    private const uint Pext2NewSizeEncoding = 0x00000040;

    private const int UMoreBits = 1 << 0;
    private const int UOrigin1 = 1 << 1;
    private const int UOrigin2 = 1 << 2;
    private const int UOrigin3 = 1 << 3;
    private const int UAngle2 = 1 << 4;
    private const int UFrame = 1 << 6;
    private const int USignal = 1 << 7;
    private const int UAngle1 = 1 << 8;
    private const int UAngle3 = 1 << 9;
    private const int UModel = 1 << 10;
    private const int UColorMap = 1 << 11;
    private const int USkin = 1 << 12;
    private const int UEffects = 1 << 13;
    private const int ULongEntity = 1 << 14;
    private const int UExtend1 = 1 << 15;
    private const int UAlpha = 1 << 16;
    private const int UFrame2 = 1 << 17;
    private const int UModel2 = 1 << 18;
    private const int ULerpFinish = 1 << 19;
    private const int UScale = 1 << 20;
    private const int UExtend2 = 1 << 23;
    private const int UTrans = 1 << 15;

    private const uint UfFrame = 1 << 0;
    private const uint UfOriginXy = 1 << 1;
    private const uint UfOriginZ = 1 << 2;
    private const uint UfAnglesXz = 1 << 3;
    private const uint UfAnglesY = 1 << 4;
    private const uint UfEffects = 1 << 5;
    private const uint UfPredInfo = 1 << 6;
    private const uint UfExtend1 = 1 << 7;
    private const uint Uf16Bit = 1 << 9;
    private const uint UfModel = 1 << 10;
    private const uint UfSkin = 1 << 11;
    private const uint UfColorMap = 1 << 12;
    private const uint UfSolid = 1 << 13;
    private const uint UfFlags = 1 << 14;
    private const uint UfExtend2 = 1 << 15;
    private const uint UfAlpha = 1 << 16;
    private const uint UfScale = 1 << 17;
    private const uint UfBoneData = 1 << 18;
    private const uint UfDrawFlags = 1 << 19;
    private const uint UfTagInfo = 1 << 20;
    private const uint UfLight = 1 << 21;
    private const uint UfTrailEffect = 1 << 22;
    private const uint UfExtend3 = 1 << 23;
    private const uint UfColorMod = 1 << 24;
    private const uint UfGlow = 1 << 25;
    private const uint UfFatness = 1 << 26;
    private const uint UfModelIndex2 = 1 << 27;
    private const uint UfGravityDir = 1 << 28;
    private const uint UfEffects2 = 1 << 29;
    private const uint UfUnused2 = 1u << 30;
    private const uint UfUnused1 = 1u << 31;

    private const int UfpForward = 1 << 0;
    private const int UfpSide = 1 << 1;
    private const int UfpUp = 1 << 2;
    private const int UfpMoveType = 1 << 3;
    private const int UfpVelocityXy = 1 << 4;
    private const int UfpVelocityZ = 1 << 5;
    private const int UfpMsec = 1 << 6;
    private const int UfpViewAngle = 1 << 7;
    private const int UfpWeaponFrameOld = 1 << 7;

    private const int SuViewHeight = 1 << 0;
    private const int SuIdealPitch = 1 << 1;
    private const int SuPunch1 = 1 << 2;
    private const int SuVelocity1 = 1 << 5;
    private const int SuItems = 1 << 9;
    private const int SuWeaponFrame = 1 << 12;
    private const int SuArmor = 1 << 13;
    private const int SuWeapon = 1 << 14;
    private const int SuExtend1 = 1 << 15;
    private const int SuWeapon2 = 1 << 16;
    private const int SuArmor2 = 1 << 17;
    private const int SuAmmo2 = 1 << 18;
    private const int SuShells2 = 1 << 19;
    private const int SuNails2 = 1 << 20;
    private const int SuRockets2 = 1 << 21;
    private const int SuCells2 = 1 << 22;
    private const int SuExtend2 = 1 << 23;
    private const int SuWeaponFrame2 = 1 << 24;
    private const int SuWeaponAlpha = 1 << 25;
    private const int DpSuPunchVec1 = 1 << 16;

    private const int BLargeModel = 1 << 0;
    private const int BLargeFrame = 1 << 1;
    private const int BAlpha = 1 << 2;
    private const int BScale = 1 << 3;

    private const ulong SndVolume = 1UL << 0;
    private const ulong SndAttenuation = 1UL << 1;
    private const ulong SndFteMoreFlags = 1UL << 2;
    private const ulong SndLargeEntity = 1UL << 3;
    private const ulong SndLargeSound = 1UL << 4;
    private const ulong SndDpPitch = 1UL << 5;
    private const ulong SndFteTimeOfs = 1UL << 6;
    private const ulong SndFtePitchAdj = 1UL << 7;
    private const ulong SndFteVelocity = 1UL << 8;

    private static readonly char[] DequakeMap = BuildDequakeMap();

    /// <summary>
    /// Returns null when the buffer does not open like a demo, leaving callers free to
    /// fall back to a plain text scan.
    /// </summary>
    public static QuakeDemoSummary? Inspect(ReadOnlySpan<byte> data)
    {
        var offset = 0;
        if (!TryReadTrackLine(data, ref offset))
        {
            return null;
        }

        var state = new DemoState();
        var frameCount = 0;
        var truncated = false;

        while (offset < data.Length)
        {
            if (data.Length - offset < 16)
            {
                truncated = data.Length > offset;
                break;
            }

            var messageLength = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
            if (messageLength < 0 || messageLength > MaximumMessageLength)
            {
                truncated = true;
                break;
            }

            var payloadOffset = offset + 16;
            if (data.Length - payloadOffset < messageLength)
            {
                truncated = true;
                break;
            }

            frameCount++;
            var payload = data.Slice(payloadOffset, messageLength);
            if (state.MessagesComplete)
            {
                ParseFrameGuarded(payload, state);
            }
            else
            {
                ReadFrameTime(payload, state);
            }

            offset = payloadOffset + messageLength;
        }

        if (frameCount == 0)
        {
            return null;
        }

        state.CloseSegment();
        if (state.Segments.Count == 0 && state.PlayerCount == 0 && state.Protocol == 0)
        {
            return null;
        }

        return new QuakeDemoSummary(
            ProtocolName(state.Protocol, state.Pext2),
            state.Segments,
            state.GameDir,
            state.MaxClients,
            state.GameType,
            state.Segments.Sum(segment => segment.Duration),
            frameCount,
            state.OrderedPlayers(),
            state.MessagesComplete,
            truncated);
    }

    /// <summary>The first line names the CD track, so a demo starts with a small integer.</summary>
    private static bool TryReadTrackLine(ReadOnlySpan<byte> data, ref int offset)
    {
        var end = data.IndexOf((byte)'\n');
        if (end < 0 || end > 16 || end == 0)
        {
            return false;
        }

        var line = data[..end];
        if (line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }
        if (line.Length == 0)
        {
            return false;
        }

        var start = line[0] == (byte)'-' ? 1 : 0;
        if (start >= line.Length)
        {
            return false;
        }
        for (var index = start; index < line.Length; index++)
        {
            if (line[index] is < (byte)'0' or > (byte)'9')
            {
                return false;
            }
        }

        offset = end + 1;
        return true;
    }

    /// <summary>
    /// Keeps the timeline moving after message decoding gave up. Timed frames lead with
    /// svc_time, so the timestamp is readable without understanding the rest of the frame.
    /// </summary>
    private static void ReadFrameTime(ReadOnlySpan<byte> payload, DemoState state)
    {
        if (payload.Length >= 5 && payload[0] == (byte)Svc.Time)
        {
            state.RegisterTime(BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(1, 4)));
        }
    }

    private static void ParseFrameGuarded(ReadOnlySpan<byte> payload, DemoState state)
    {
        try
        {
            var reader = new MessageReader(payload);
            ParseFrame(ref reader, state);
        }
        catch (DemoParseException)
        {
            state.MessagesComplete = false;
            ReadFrameTime(payload, state);
        }
    }

    private static void ParseFrame(ref MessageReader reader, DemoState state)
    {
        while (reader.Remaining > 0)
        {
            int command = reader.ReadByte();
            if ((command & USignal) != 0)
            {
                SkipClassicUpdate(ref reader, state, command & 127);
                continue;
            }

            switch ((Svc)command)
            {
                case Svc.Nop:
                case Svc.Disconnect:
                case Svc.Intermission:
                case Svc.SellScreen:
                case Svc.Bf:
                case Svc.KilledMonster:
                case Svc.FoundSecret:
                    break;

                case Svc.SetPause:
                    reader.ReadByte();
                    break;

                case Svc.UpdateStat:
                    reader.ReadByte();
                    reader.ReadInt32();
                    break;

                case Svc.Version:
                    state.Protocol = reader.ReadInt32();
                    break;

                case Svc.SetView:
                    reader.ReadUInt16();
                    break;

                case Svc.Sound:
                    SkipStartSound(ref reader, state);
                    break;

                case Svc.Time:
                    state.RegisterTime(reader.ReadSingle());
                    if ((state.Pext2 & Pext2PredInfo) != 0)
                    {
                        reader.ReadUInt16();
                    }
                    break;

                case Svc.Print:
                case Svc.CenterPrint:
                case Svc.Finale:
                case Svc.CutScene:
                    reader.ReadString();
                    break;

                case Svc.StuffText:
                    state.ApplyStuffText(reader.ReadString());
                    break;

                case Svc.SetAngle:
                    SkipAngle(ref reader, state);
                    SkipAngle(ref reader, state);
                    SkipAngle(ref reader, state);
                    break;

                case Svc.ServerInfo:
                    ParseServerInfo(ref reader, state);
                    break;

                case Svc.LightStyle:
                    reader.ReadByte();
                    reader.ReadString();
                    break;

                case Svc.UpdateName:
                {
                    int slot = reader.ReadByte();
                    var name = Dequake(reader.ReadString());
                    state.SetPlayerName(slot, name);
                    break;
                }

                case Svc.UpdateFrags:
                {
                    int slot = reader.ReadByte();
                    int frags = reader.ReadInt16();
                    state.SetPlayerFrags(slot, frags);
                    break;
                }

                case Svc.ClientData:
                    SkipClientData(ref reader, state);
                    break;

                case Svc.StopSound:
                    reader.ReadUInt16();
                    break;

                case Svc.UpdateColors:
                {
                    int slot = reader.ReadByte();
                    int colors = reader.ReadByte();
                    state.SetPlayerColors(slot, (colors >> 4) & 0x0f, colors & 0x0f);
                    break;
                }

                case Svc.Particle:
                    SkipCoords(ref reader, state, 3);
                    reader.Skip(5);
                    break;

                case Svc.Damage:
                    reader.Skip(2);
                    SkipCoords(ref reader, state, 3);
                    break;

                case Svc.SpawnStatic:
                    SkipBaseline(ref reader, state, 1);
                    break;

                case Svc.FteSpawnStatic2Alias:
                    SkipBaseline(ref reader, state, 6);
                    break;

                case Svc.SpawnBaseline:
                    reader.ReadUInt16();
                    SkipBaseline(ref reader, state, 1);
                    break;

                case Svc.TempEntity:
                    SkipTempEntity(ref reader, state);
                    break;

                case Svc.SignOnNum:
                    reader.ReadByte();
                    break;

                case Svc.SpawnStaticSound:
                    SkipStaticSound(ref reader, state, 1);
                    break;

                case Svc.CdTrack:
                    reader.Skip(2);
                    break;

                case Svc.DpShowPic:
                    reader.ReadString();
                    reader.ReadString();
                    reader.Skip(2);
                    break;

                case Svc.DpHidePic:
                    reader.ReadString();
                    break;

                case Svc.Skybox:
                    reader.ReadString();
                    break;

                case Svc.Fog:
                    reader.Skip(6);
                    break;

                case Svc.SpawnBaseline2:
                    reader.ReadUInt16();
                    SkipBaseline(ref reader, state, 2);
                    break;

                case Svc.SpawnStatic2:
                    SkipBaseline(ref reader, state, 2);
                    break;

                case Svc.SpawnStaticSound2:
                case Svc.DpSpawnStaticSound2:
                    SkipStaticSound(ref reader, state, 2);
                    break;

                case Svc.DpDownloadData:
                {
                    var start = reader.ReadInt32();
                    var size = reader.ReadUInt16();
                    if (start < 0)
                    {
                        throw new DemoParseException();
                    }
                    reader.Skip(size);
                    break;
                }

                case Svc.DpUpdateStatByte:
                    reader.Skip(2);
                    break;

                case Svc.DpEffect:
                    if (state.Protocol == ProtocolDp7)
                    {
                        SkipEffect(ref reader, state, big: false);
                    }
                    else
                    {
                        reader.ReadString();
                    }
                    break;

                case Svc.DpEffect2:
                    if (state.Protocol != ProtocolDp7)
                    {
                        throw new DemoParseException();
                    }
                    SkipEffect(ref reader, state, big: true);
                    break;

                case Svc.DpPrecache:
                    reader.ReadUInt16();
                    reader.ReadString();
                    break;

                case Svc.DpSpawnBaseline2:
                    reader.ReadUInt16();
                    SkipBaseline(ref reader, state, 7);
                    break;

                case Svc.DpSpawnStatic2:
                    SkipBaseline(ref reader, state, 7);
                    break;

                case Svc.DpTrailParticles:
                    SkipParticles(ref reader, state, -1);
                    break;

                case Svc.DpPointParticles:
                    SkipParticles(ref reader, state, 0);
                    break;

                case Svc.DpPointParticles1:
                    SkipParticles(ref reader, state, 1);
                    break;

                case Svc.FteSpawnBaseline2:
                    ReadEntityIndex(ref reader, state);
                    SkipBaseline(ref reader, state, 6);
                    break;

                case Svc.FteUpdateStatString:
                    reader.ReadByte();
                    reader.ReadString();
                    break;

                case Svc.FteUpdateStatFloat:
                    reader.ReadByte();
                    reader.ReadSingle();
                    break;

                case Svc.FteVoiceChat:
                    reader.Skip(3);
                    reader.Skip(reader.ReadUInt16());
                    break;

                case Svc.FteSetAngleDelta:
                    SkipAngle16(ref reader, state);
                    SkipAngle16(ref reader, state);
                    SkipAngle16(ref reader, state);
                    break;

                case Svc.FteUpdateEntities:
                    SkipFteUpdateEntities(ref reader, state);
                    break;

                default:
                    throw new DemoParseException();
            }
        }
    }

    private static void ParseServerInfo(ref MessageReader reader, DemoState state)
    {
        state.CloseSegment();
        state.Pext1 = 0;
        state.Pext2 = 0;
        state.ProtocolFlags = 0;
        state.GameDir = string.Empty;

        int protocol;
        while (true)
        {
            var value = reader.ReadInt32();
            if (value == ProtocolFtePext1)
            {
                state.Pext1 = reader.ReadUInt32();
                continue;
            }
            if (value == ProtocolFtePext2)
            {
                state.Pext2 = reader.ReadUInt32();
                continue;
            }
            protocol = value;
            break;
        }

        state.Protocol = protocol;
        state.ProtocolFlags = protocol switch
        {
            ProtocolRmq => reader.ReadUInt32(),
            ProtocolDp7 => PrflShortAngle | PrflFloatCoord,
            _ => 0,
        };

        if ((state.Pext2 & Pext2PredInfo) != 0)
        {
            state.GameDir = Dequake(reader.ReadString());
        }

        state.MaxClients = reader.ReadByte();
        state.GameType = reader.ReadByte();
        var levelName = Dequake(reader.ReadString()).Trim();

        var map = string.Empty;
        var modelCount = 0;
        while (true)
        {
            var modelName = reader.ReadString();
            if (modelName.Length == 0)
            {
                break;
            }
            if (++modelCount == 1)
            {
                map = StripPathExtension(modelName);
            }
            if (modelCount > MaximumModels)
            {
                throw new DemoParseException();
            }
        }

        var soundCount = 0;
        while (true)
        {
            var soundName = reader.ReadString();
            if (soundName.Length == 0)
            {
                break;
            }
            if (++soundCount > MaximumSounds)
            {
                throw new DemoParseException();
            }
        }

        state.BeginSegment(map, levelName);
    }

    private static void SkipClassicUpdate(ref MessageReader reader, DemoState state, int firstBits)
    {
        var bits = firstBits;
        if ((bits & UMoreBits) != 0)
        {
            bits |= reader.ReadByte() << 8;
        }

        var fitzLike = state.Protocol is ProtocolFitzQuake or ProtocolRmq;
        if (fitzLike)
        {
            if ((bits & UExtend1) != 0)
            {
                bits |= reader.ReadByte() << 16;
            }
            if ((bits & UExtend2) != 0)
            {
                bits |= reader.ReadByte() << 24;
            }
        }

        if ((bits & ULongEntity) != 0)
        {
            reader.ReadUInt16();
        }
        else
        {
            reader.ReadByte();
        }

        if ((bits & UModel) != 0)
        {
            if (state.Protocol == ProtocolBjp3)
            {
                reader.ReadUInt16();
            }
            else
            {
                reader.ReadByte();
            }
        }

        if ((bits & UFrame) != 0)
        {
            reader.ReadByte();
        }
        if ((bits & UColorMap) != 0)
        {
            reader.ReadByte();
        }
        if ((bits & USkin) != 0)
        {
            reader.ReadByte();
        }
        if ((bits & UEffects) != 0)
        {
            reader.ReadByte();
        }

        if ((bits & UOrigin1) != 0)
        {
            SkipCoord(ref reader, state);
        }
        if ((bits & UAngle1) != 0)
        {
            SkipAngle(ref reader, state);
        }
        if ((bits & UOrigin2) != 0)
        {
            SkipCoord(ref reader, state);
        }
        if ((bits & UAngle2) != 0)
        {
            SkipAngle(ref reader, state);
        }
        if ((bits & UOrigin3) != 0)
        {
            SkipCoord(ref reader, state);
        }
        if ((bits & UAngle3) != 0)
        {
            SkipAngle(ref reader, state);
        }

        if (fitzLike)
        {
            if ((bits & UAlpha) != 0)
            {
                reader.ReadByte();
            }
            if ((bits & UScale) != 0)
            {
                reader.ReadByte();
            }
            if ((bits & UFrame2) != 0)
            {
                reader.ReadByte();
            }
            if ((bits & UModel2) != 0)
            {
                reader.ReadByte();
            }
            if ((bits & ULerpFinish) != 0)
            {
                reader.ReadByte();
            }
        }
        else if ((state.Protocol is ProtocolNetQuake or ProtocolBjp3) && (bits & UTrans) != 0)
        {
            // Nehahra transparency: mode, alpha, and a fullbright float only for mode 2.
            var transparencyMode = reader.ReadSingle();
            reader.ReadSingle();
            if (transparencyMode == 2)
            {
                reader.ReadSingle();
            }
        }
    }

    private static void SkipBaseline(ref MessageReader reader, DemoState state, int version)
    {
        if (version == 6)
        {
            SkipFteDelta(ref reader, state);
            return;
        }

        var bits = version switch
        {
            1 when state.Protocol == ProtocolBjp3 => BLargeModel,
            7 => BLargeModel | BLargeFrame,
            2 => reader.ReadByte(),
            _ => 0,
        };

        if ((bits & BLargeModel) != 0)
        {
            reader.ReadUInt16();
        }
        else
        {
            reader.ReadByte();
        }
        if ((bits & BLargeFrame) != 0)
        {
            reader.ReadUInt16();
        }
        else
        {
            reader.ReadByte();
        }
        reader.Skip(2);
        for (var axis = 0; axis < 3; axis++)
        {
            SkipCoord(ref reader, state);
            SkipAngle(ref reader, state);
        }
        if ((bits & BAlpha) != 0)
        {
            reader.ReadByte();
        }
        if ((bits & BScale) != 0)
        {
            reader.ReadByte();
        }
    }

    private static void SkipFteDelta(ref MessageReader reader, DemoState state)
    {
        uint bits = reader.ReadByte();
        if ((bits & UfExtend1) != 0)
        {
            bits |= (uint)reader.ReadByte() << 8;
        }
        if ((bits & UfExtend2) != 0)
        {
            bits |= (uint)reader.ReadByte() << 16;
        }
        if ((bits & UfExtend3) != 0)
        {
            bits |= (uint)reader.ReadByte() << 24;
        }

        var wide = (bits & Uf16Bit) != 0;
        if ((bits & UfFrame) != 0)
        {
            reader.Skip(wide ? 2 : 1);
        }
        if ((bits & UfOriginXy) != 0)
        {
            SkipCoord(ref reader, state);
            SkipCoord(ref reader, state);
        }
        if ((bits & UfOriginZ) != 0)
        {
            SkipCoord(ref reader, state);
        }

        var shortAngles = (bits & UfPredInfo) != 0 && (state.Pext2 & Pext2PredInfo) == 0;
        if ((bits & UfAnglesXz) != 0)
        {
            if (shortAngles)
            {
                SkipAngle16(ref reader, state);
                SkipAngle16(ref reader, state);
            }
            else
            {
                SkipAngle(ref reader, state);
                SkipAngle(ref reader, state);
            }
        }
        if ((bits & UfAnglesY) != 0)
        {
            if (shortAngles)
            {
                SkipAngle16(ref reader, state);
            }
            else
            {
                SkipAngle(ref reader, state);
            }
        }

        if ((bits & (UfEffects | UfEffects2)) == (UfEffects | UfEffects2))
        {
            reader.Skip(4);
        }
        else if ((bits & UfEffects2) != 0)
        {
            reader.Skip(2);
        }
        else if ((bits & UfEffects) != 0)
        {
            reader.Skip(1);
        }

        if ((bits & UfPredInfo) != 0)
        {
            int predBits = reader.ReadByte();
            if ((predBits & UfpForward) != 0)
            {
                reader.Skip(2);
            }
            if ((predBits & UfpSide) != 0)
            {
                reader.Skip(2);
            }
            if ((predBits & UfpUp) != 0)
            {
                reader.Skip(2);
            }
            if ((predBits & UfpMoveType) != 0)
            {
                reader.Skip(1);
            }
            if ((predBits & UfpVelocityXy) != 0)
            {
                reader.Skip(4);
            }
            if ((predBits & UfpVelocityZ) != 0)
            {
                reader.Skip(2);
            }
            if ((predBits & UfpMsec) != 0)
            {
                reader.Skip(1);
            }

            if ((state.Pext2 & Pext2PredInfo) != 0)
            {
                if ((predBits & UfpViewAngle) != 0)
                {
                    if ((bits & UfAnglesXz) != 0)
                    {
                        reader.Skip(4);
                    }
                    if ((bits & UfAnglesY) != 0)
                    {
                        reader.Skip(2);
                    }
                }
            }
            else if ((predBits & UfpWeaponFrameOld) != 0)
            {
                var weaponFrame = reader.ReadByte();
                if ((weaponFrame & 0x80) != 0)
                {
                    reader.Skip(1);
                }
            }
        }

        if ((bits & UfModel) != 0)
        {
            reader.Skip(wide ? 2 : 1);
        }
        if ((bits & UfSkin) != 0)
        {
            reader.Skip(wide ? 2 : 1);
        }
        if ((bits & UfColorMap) != 0)
        {
            reader.Skip(1);
        }
        if ((bits & UfSolid) != 0)
        {
            if ((state.Pext2 & Pext2NewSizeEncoding) != 0)
            {
                int encoding = reader.ReadByte();
                switch (encoding)
                {
                    case 0:
                    case 1:
                    case 2:
                    case 3:
                        break;
                    case 16:
                        reader.Skip(2);
                        break;
                    case 32:
                        reader.Skip(4);
                        break;
                    default:
                        throw new DemoParseException();
                }
            }
            else
            {
                reader.Skip(2);
            }
        }
        if ((bits & UfFlags) != 0)
        {
            reader.Skip(1);
        }
        if ((bits & UfAlpha) != 0)
        {
            reader.Skip(1);
        }
        if ((bits & UfScale) != 0)
        {
            reader.Skip(1);
        }
        if ((bits & UfBoneData) != 0)
        {
            int flags = reader.ReadByte();
            if ((flags & 0x80) != 0)
            {
                int boneCount = reader.ReadByte();
                reader.Skip(boneCount * 7 * 2);
            }
            if ((flags & 0x40) != 0)
            {
                reader.Skip(3);
            }
            if ((flags & 0x3f) != 0)
            {
                throw new DemoParseException();
            }
        }
        if ((bits & UfDrawFlags) != 0)
        {
            int drawFlags = reader.ReadByte();
            if ((drawFlags & 7) == 7)
            {
                reader.Skip(1);
            }
        }
        if ((bits & UfTagInfo) != 0)
        {
            ReadEntityIndex(ref reader, state);
            reader.Skip(1);
        }
        if ((bits & UfLight) != 0)
        {
            reader.Skip(10);
        }
        if ((bits & UfTrailEffect) != 0)
        {
            var effectValue = reader.ReadUInt16();
            if ((effectValue & 0x8000) != 0)
            {
                reader.Skip(2);
            }
        }
        if ((bits & UfColorMod) != 0)
        {
            reader.Skip(3);
        }
        if ((bits & UfGlow) != 0)
        {
            reader.Skip(5);
        }
        if ((bits & UfFatness) != 0)
        {
            reader.Skip(1);
        }
        if ((bits & UfModelIndex2) != 0)
        {
            reader.Skip(wide ? 2 : 1);
        }
        if ((bits & UfGravityDir) != 0)
        {
            reader.Skip(2);
        }
        if ((bits & (UfUnused1 | UfUnused2)) != 0)
        {
            throw new DemoParseException();
        }
    }

    private static void SkipFteUpdateEntities(ref MessageReader reader, DemoState state)
    {
        if ((state.Pext2 & Pext2PredInfo) != 0)
        {
            reader.ReadUInt16();
        }
        state.RegisterTime(reader.ReadSingle());

        while (reader.Remaining > 0)
        {
            int entityValue = reader.ReadUInt16();
            var removeFlag = (entityValue & 0x8000) != 0;
            if ((entityValue & 0x4000) != 0)
            {
                entityValue = (entityValue & 0x3fff) | (reader.ReadByte() << 14);
            }
            else
            {
                entityValue &= ~0x8000;
            }
            if (entityValue == 0 && !removeFlag)
            {
                break;
            }
            if (removeFlag)
            {
                continue;
            }
            SkipFteDelta(ref reader, state);
        }
    }

    private static void SkipClientData(ref MessageReader reader, DemoState state)
    {
        int bits = reader.ReadUInt16();
        if ((bits & SuExtend1) != 0)
        {
            bits |= reader.ReadByte() << 16;
        }
        if ((bits & SuExtend2) != 0)
        {
            bits |= reader.ReadByte() << 24;
        }

        var dp7 = state.Protocol == ProtocolDp7;
        if (!dp7)
        {
            bits |= SuItems;
        }

        if ((bits & SuViewHeight) != 0)
        {
            reader.Skip(1);
        }
        if ((bits & SuIdealPitch) != 0)
        {
            reader.Skip(1);
        }

        for (var axis = 0; axis < 3; axis++)
        {
            if ((bits & (SuPunch1 << axis)) != 0)
            {
                if (dp7)
                {
                    SkipAngleWithFlags(ref reader, PrflShortAngle);
                }
                else
                {
                    reader.Skip(1);
                }
            }
            if (dp7 && (bits & (DpSuPunchVec1 << axis)) != 0)
            {
                SkipCoord(ref reader, state);
            }
            if ((bits & (SuVelocity1 << axis)) != 0)
            {
                reader.Skip(dp7 ? 4 : 1);
            }
        }

        if ((bits & SuItems) != 0)
        {
            reader.Skip(4);
        }

        if (dp7)
        {
            return;
        }

        if ((bits & SuWeaponFrame) != 0)
        {
            reader.Skip(1);
        }
        if ((bits & SuArmor) != 0)
        {
            reader.Skip(1);
        }
        if ((bits & SuWeapon) != 0)
        {
            reader.Skip(state.Protocol == ProtocolBjp3 ? 2 : 1);
        }

        // health, ammo, four ammo counts, active weapon
        reader.Skip(2 + 1 + 4 + 1);

        if ((bits & SuWeapon2) != 0)
        {
            reader.Skip(1);
        }
        if ((bits & SuArmor2) != 0)
        {
            reader.Skip(1);
        }
        if ((bits & SuAmmo2) != 0)
        {
            reader.Skip(1);
        }
        if ((bits & SuShells2) != 0)
        {
            reader.Skip(1);
        }
        if ((bits & SuNails2) != 0)
        {
            reader.Skip(1);
        }
        if ((bits & SuRockets2) != 0)
        {
            reader.Skip(1);
        }
        if ((bits & SuCells2) != 0)
        {
            reader.Skip(1);
        }
        if ((bits & SuWeaponFrame2) != 0)
        {
            reader.Skip(1);
        }
        if ((bits & SuWeaponAlpha) != 0)
        {
            reader.Skip(1);
        }
    }

    private static void SkipStartSound(ref MessageReader reader, DemoState state)
    {
        ulong fieldMask = reader.ReadByte();
        if (state.Protocol == ProtocolBjp3)
        {
            fieldMask |= SndLargeSound;
        }
        if ((fieldMask & SndFteMoreFlags) != 0)
        {
            fieldMask |= reader.ReadVarUInt64() << 8;
        }
        if ((fieldMask & SndVolume) != 0)
        {
            reader.Skip(1);
        }
        if ((fieldMask & SndAttenuation) != 0)
        {
            reader.Skip(1);
        }

        var replacementDeltas = (state.Pext2 & Pext2ReplacementDeltas) != 0;
        if (replacementDeltas)
        {
            if ((fieldMask & SndFtePitchAdj) != 0)
            {
                reader.Skip(1);
            }
            if ((fieldMask & SndFteTimeOfs) != 0)
            {
                reader.Skip(2);
            }
            if ((fieldMask & SndFteVelocity) != 0)
            {
                reader.Skip(6);
            }
        }
        if ((state.Protocol == ProtocolDp7 || replacementDeltas) && (fieldMask & SndDpPitch) != 0)
        {
            reader.Skip(2);
        }

        reader.Skip((fieldMask & SndLargeEntity) != 0 ? 3 : 2);
        reader.Skip((fieldMask & SndLargeSound) != 0 ? 2 : 1);
        SkipCoords(ref reader, state, 3);
    }

    private static void SkipStaticSound(ref MessageReader reader, DemoState state, int version)
    {
        SkipCoords(ref reader, state, 3);
        reader.Skip(version == 2 ? 2 : 1);
        reader.Skip(2);
    }

    private static void SkipEffect(ref MessageReader reader, DemoState state, bool big)
    {
        SkipCoords(ref reader, state, 3);
        reader.Skip(big ? 4 : 2);
        reader.Skip(2);
    }

    private static void SkipParticles(ref MessageReader reader, DemoState state, int type)
    {
        if (type < 0)
        {
            reader.Skip(4);
            SkipCoords(ref reader, state, 6);
            return;
        }

        reader.Skip(2);
        SkipCoords(ref reader, state, 3);
        if (type == 0)
        {
            SkipCoords(ref reader, state, 3);
            reader.Skip(2);
        }
    }

    private static void SkipTempEntity(ref MessageReader reader, DemoState state)
    {
        int type = reader.ReadByte();
        switch (type)
        {
            case 0: // spike
            case 1: // super spike
            case 2: // gunshot
            case 3: // explosion
            case 4: // tar explosion
            case 7: // wizard spike
            case 8: // knight spike
            case 10: // lava splash
            case 11: // teleport
            case 20: // FTE explosion sprite
            case 57: // DP gunshot quad
            case 58: // DP spike quad
            case 59: // DP super spike quad
            case 70: // DP explosion quad
            case 72: // DP small flash
            case 75: // DP plasma burn
                SkipCoords(ref reader, state, 3);
                break;

            case 21: // FTE gunshot with a count
                reader.Skip(1);
                SkipCoords(ref reader, state, 3);
                break;

            case 12: // explosion 2
                SkipCoords(ref reader, state, 3);
                reader.Skip(2);
                break;

            case 16: // Nehahra explosion 3
                SkipCoords(ref reader, state, 6);
                break;

            case 5: // lightning 1
            case 6: // lightning 2
            case 9: // lightning 3
            case 13: // beam
                ReadEntityIndex(ref reader, state);
                SkipCoords(ref reader, state, 6);
                break;

            case 17: // Nehahra lightning 4
                reader.ReadString();
                ReadEntityIndex(ref reader, state);
                SkipCoords(ref reader, state, 6);
                break;

            case 73: // DP custom flash
                SkipCoords(ref reader, state, 3);
                reader.Skip(5);
                break;

            case 55: // DP particle rain
            case 56: // DP particle snow
                SkipCoords(ref reader, state, 9);
                reader.Skip(3);
                break;

            case 50: // DP blood
            case 51: // DP spark
                SkipCoords(ref reader, state, 3);
                reader.Skip(4);
                break;

            case 52: // DP blood shower
                SkipCoords(ref reader, state, 7);
                reader.Skip(2);
                break;

            case 53: // DP explosion RGB
                SkipCoords(ref reader, state, 3);
                reader.Skip(3);
                break;

            case 54: // DP particle cube
                SkipCoords(ref reader, state, 10);
                reader.Skip(4);
                break;

            case 74: // DP flame jet
                SkipCoords(ref reader, state, 6);
                reader.Skip(1);
                break;

            default:
                throw new DemoParseException();
        }
    }

    private static void ReadEntityIndex(ref MessageReader reader, DemoState state)
    {
        var value = reader.ReadUInt16();
        if ((state.Pext2 & Pext2ReplacementDeltas) != 0 && (value & 0x8000) != 0)
        {
            reader.ReadByte();
        }
    }

    private static void SkipCoords(ref MessageReader reader, DemoState state, int count)
    {
        for (var index = 0; index < count; index++)
        {
            SkipCoord(ref reader, state);
        }
    }

    private static void SkipCoord(ref MessageReader reader, DemoState state)
    {
        var flags = state.ProtocolFlags;
        if ((flags & PrflFloatCoord) != 0)
        {
            reader.Skip(4);
        }
        else if ((flags & PrflInt32Coord) != 0)
        {
            reader.Skip(4);
        }
        else if ((flags & Prfl24BitCoord) != 0)
        {
            reader.Skip(3);
        }
        else
        {
            reader.Skip(2);
        }
    }

    private static void SkipAngle(ref MessageReader reader, DemoState state) =>
        SkipAngleWithFlags(ref reader, state.ProtocolFlags);

    private static void SkipAngleWithFlags(ref MessageReader reader, uint flags)
    {
        if ((flags & PrflFloatAngle) != 0)
        {
            reader.Skip(4);
        }
        else if ((flags & PrflShortAngle) != 0)
        {
            reader.Skip(2);
        }
        else
        {
            reader.Skip(1);
        }
    }

    private static void SkipAngle16(ref MessageReader reader, DemoState state) =>
        reader.Skip((state.ProtocolFlags & PrflFloatAngle) != 0 ? 4 : 2);

    private static string ProtocolName(int protocol, uint pext2)
    {
        var name = protocol switch
        {
            0 => "unknown",
            ProtocolNetQuake => "15",
            ProtocolFitzQuake => "666",
            ProtocolRmq => "999",
            ProtocolDp7 => "3504",
            ProtocolBjp3 => "10002",
            _ => protocol.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        return pext2 != 0 ? name + "+fte" : name;
    }

    private static string StripPathExtension(string path)
    {
        var slash = path.LastIndexOf('/');
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    /// <summary>Maps the Quake character set onto readable ASCII, as the console does.</summary>
    private static string Dequake(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            var index = character & 0xff;
            var mapped = DequakeMap[index];
            if (mapped != '\0')
            {
                builder.Append(mapped);
            }
        }
        return builder.ToString();
    }

    private static char[] BuildDequakeMap()
    {
        var map = new char[256];
        for (var index = 1; index < 12; index++)
        {
            map[index] = '#';
        }
        map[9] = '\t';
        map[10] = '\n';
        map[12] = ' ';
        map[13] = '\r';
        map[1] = '.';
        map[5] = '.';
        map[14] = '.';
        map[15] = '.';
        map[16] = '[';
        map[17] = ']';
        map[28] = '.';
        map[29] = '<';
        map[30] = '-';
        map[31] = '>';
        for (var index = 0; index < 10; index++)
        {
            map[18 + index] = (char)('0' + index);
        }
        for (var index = 32; index < 128; index++)
        {
            map[index] = (char)index;
        }
        for (var index = 0; index < 128; index++)
        {
            map[index + 128] = map[index];
        }
        map[128] = '(';
        map[129] = '=';
        map[130] = ')';
        map[131] = '*';
        map[141] = '>';
        return map;
    }

    private sealed class DemoState
    {
        private readonly Dictionary<int, PlayerRecord> _players = [];
        private readonly List<QuakeDemoSegment> _segments = [];
        private string _segmentMap = string.Empty;
        private string _segmentLevelName = string.Empty;
        private bool _segmentOpen;
        private double _segmentStart;
        private double _segmentEnd;

        public int Protocol { get; set; }

        public uint ProtocolFlags { get; set; }

        public uint Pext1 { get; set; }

        public uint Pext2 { get; set; }

        public int MaxClients { get; set; }

        public int GameType { get; set; }

        public string GameDir { get; set; } = string.Empty;

        public bool MessagesComplete { get; set; } = true;

        public IReadOnlyList<QuakeDemoSegment> Segments => _segments;

        public int PlayerCount => _players.Count;

        public void RegisterTime(float time)
        {
            if (float.IsNaN(time) || float.IsInfinity(time))
            {
                return;
            }

            if (!_segmentOpen)
            {
                return;
            }
            if (double.IsNaN(_segmentStart))
            {
                _segmentStart = time;
            }
            _segmentEnd = time;
        }

        public void BeginSegment(string map, string levelName)
        {
            _segmentMap = map;
            _segmentLevelName = levelName;
            _segmentOpen = true;
            _segmentStart = double.NaN;
            _segmentEnd = double.NaN;
        }

        public void CloseSegment()
        {
            if (!_segmentOpen)
            {
                return;
            }

            var duration = double.IsNaN(_segmentStart) || double.IsNaN(_segmentEnd)
                ? 0
                : Math.Max(0, _segmentEnd - _segmentStart);
            _segments.Add(new QuakeDemoSegment(_segmentMap, _segmentLevelName, duration));
            _segmentOpen = false;
        }

        public void SetPlayerName(int slot, string name)
        {
            if (Ensure(slot) is { } record)
            {
                record.Name = name;
            }
        }

        public void SetPlayerFrags(int slot, int frags)
        {
            if (Ensure(slot) is { } record)
            {
                record.Frags = frags;
            }
        }

        public void SetPlayerColors(int slot, int shirt, int pants)
        {
            if (Ensure(slot) is { } record)
            {
                record.Shirt = shirt;
                record.Pants = pants;
            }
        }

        /// <summary>
        /// QuakeWorld-style servers publish names and colours through stuffed
        /// <c>//fullserverinfo</c> and <c>//ui</c> commands rather than svc_updatename.
        /// </summary>
        public void ApplyStuffText(string text)
        {
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim('\r', ' ', '\t');
                if (line.StartsWith("gamedir ", StringComparison.OrdinalIgnoreCase))
                {
                    var value = line[8..].Trim().Trim('"');
                    if (value.Length > 0)
                    {
                        GameDir = value;
                    }
                    continue;
                }
                if (!line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                var tokens = Tokenize(line);
                if (tokens.Count == 0)
                {
                    continue;
                }

                var command = tokens[0][2..].ToLowerInvariant();
                if (command == "fullserverinfo" && tokens.Count >= 2)
                {
                    var info = ParseInfoString(tokens[1]);
                    if (info.TryGetValue("*gamedir", out var gamedir) && gamedir.Length > 0)
                    {
                        GameDir = gamedir;
                    }
                    else if (info.TryGetValue("gamedir", out gamedir) && gamedir.Length > 0)
                    {
                        GameDir = gamedir;
                    }
                }
                else if (command == "svi" && tokens.Count >= 3)
                {
                    if ((tokens[1] is "*gamedir" or "gamedir") && tokens[2].Length > 0)
                    {
                        GameDir = tokens[2];
                    }
                }
                else if ((command is "fui" or "ui") && tokens.Count >= 3 &&
                         int.TryParse(tokens[1], out var slot) && slot >= 0)
                {
                    if (command == "fui")
                    {
                        var info = ParseInfoString(tokens[2]);
                        if (info.TryGetValue("name", out var name) && name.Length > 0)
                        {
                            SetPlayerName(slot, Dequake(name));
                        }
                    }
                    else if (tokens.Count >= 4 &&
                             string.Equals(tokens[2], "name", StringComparison.OrdinalIgnoreCase))
                    {
                        SetPlayerName(slot, Dequake(tokens[3]));
                    }
                }
            }
        }

        public IReadOnlyList<QuakeDemoPlayer> OrderedPlayers() =>
        [
            .. _players
                .Where(pair => pair.Value.Name.Length > 0)
                .OrderBy(pair => pair.Key)
                .Select(pair => new QuakeDemoPlayer(
                    pair.Key,
                    pair.Value.Name,
                    pair.Value.Frags,
                    pair.Value.Shirt,
                    pair.Value.Pants)),
        ];

        private PlayerRecord? Ensure(int slot)
        {
            if (slot < 0 || slot >= MaximumPlayers)
            {
                return null;
            }
            if (!_players.TryGetValue(slot, out var record))
            {
                record = new PlayerRecord();
                _players[slot] = record;
            }
            return record;
        }

        private static List<string> Tokenize(string line)
        {
            var tokens = new List<string>();
            var index = 0;
            while (index < line.Length)
            {
                while (index < line.Length && char.IsWhiteSpace(line[index]))
                {
                    index++;
                }
                if (index >= line.Length)
                {
                    break;
                }

                if (line[index] == '"')
                {
                    index++;
                    var start = index;
                    while (index < line.Length && line[index] != '"')
                    {
                        index++;
                    }
                    tokens.Add(line[start..index]);
                    if (index < line.Length)
                    {
                        index++;
                    }
                }
                else
                {
                    var start = index;
                    while (index < line.Length && !char.IsWhiteSpace(line[index]))
                    {
                        index++;
                    }
                    tokens.Add(line[start..index]);
                }
            }
            return tokens;
        }

        private static Dictionary<string, string> ParseInfoString(string value)
        {
            var info = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var parts = value.Split('\\', StringSplitOptions.None);
            for (var index = 1; index + 1 < parts.Length; index += 2)
            {
                if (parts[index].Length > 0)
                {
                    info[parts[index]] = parts[index + 1];
                }
            }
            return info;
        }

        private sealed class PlayerRecord
        {
            public string Name { get; set; } = string.Empty;

            public int Frags { get; set; }

            public int Shirt { get; set; } = -1;

            public int Pants { get; set; } = -1;
        }
    }

    private sealed class DemoParseException : Exception
    {
    }

    private ref struct MessageReader
    {
        private readonly ReadOnlySpan<byte> _bytes;
        private int _offset;

        public MessageReader(ReadOnlySpan<byte> bytes)
        {
            _bytes = bytes;
            _offset = 0;
        }

        public readonly int Remaining => _bytes.Length - _offset;

        public byte ReadByte()
        {
            Require(1);
            return _bytes[_offset++];
        }

        public ushort ReadUInt16()
        {
            Require(2);
            var value = BinaryPrimitives.ReadUInt16LittleEndian(_bytes.Slice(_offset, 2));
            _offset += 2;
            return value;
        }

        public short ReadInt16()
        {
            Require(2);
            var value = BinaryPrimitives.ReadInt16LittleEndian(_bytes.Slice(_offset, 2));
            _offset += 2;
            return value;
        }

        public int ReadInt32()
        {
            Require(4);
            var value = BinaryPrimitives.ReadInt32LittleEndian(_bytes.Slice(_offset, 4));
            _offset += 4;
            return value;
        }

        public uint ReadUInt32()
        {
            Require(4);
            var value = BinaryPrimitives.ReadUInt32LittleEndian(_bytes.Slice(_offset, 4));
            _offset += 4;
            return value;
        }

        public float ReadSingle()
        {
            Require(4);
            var value = BinaryPrimitives.ReadSingleLittleEndian(_bytes.Slice(_offset, 4));
            _offset += 4;
            return value;
        }

        public ulong ReadVarUInt64()
        {
            ulong value = ReadByte();
            var mask = 0x80UL;
            var extraBytes = 0;
            while ((value & mask) != 0 && mask != 0)
            {
                value -= mask;
                extraBytes++;
                mask >>= 1;
            }

            var result = value << (extraBytes * 8);
            while (extraBytes > 0)
            {
                extraBytes--;
                result |= (ulong)ReadByte() << (extraBytes * 8);
            }
            return result;
        }

        public string ReadString()
        {
            var start = _offset;
            while (_offset < _bytes.Length && _bytes[_offset] != 0)
            {
                _offset++;
            }
            if (_offset >= _bytes.Length)
            {
                throw new DemoParseException();
            }

            var span = _bytes[start.._offset];
            _offset++;

            var characters = new char[span.Length];
            for (var index = 0; index < span.Length; index++)
            {
                characters[index] = (char)span[index];
            }
            return new string(characters);
        }

        public void Skip(int count)
        {
            if (count < 0)
            {
                throw new DemoParseException();
            }
            Require(count);
            _offset += count;
        }

        private readonly void Require(int count)
        {
            if (count > Remaining)
            {
                throw new DemoParseException();
            }
        }
    }

    private enum Svc
    {
        Nop = 1,
        Disconnect = 2,
        UpdateStat = 3,
        Version = 4,
        SetView = 5,
        Sound = 6,
        Time = 7,
        Print = 8,
        StuffText = 9,
        SetAngle = 10,
        ServerInfo = 11,
        LightStyle = 12,
        UpdateName = 13,
        UpdateFrags = 14,
        ClientData = 15,
        StopSound = 16,
        UpdateColors = 17,
        Particle = 18,
        Damage = 19,
        SpawnStatic = 20,
        FteSpawnStatic2Alias = 21,
        SpawnBaseline = 22,
        TempEntity = 23,
        SetPause = 24,
        SignOnNum = 25,
        CenterPrint = 26,
        KilledMonster = 27,
        FoundSecret = 28,
        SpawnStaticSound = 29,
        Intermission = 30,
        Finale = 31,
        CdTrack = 32,
        SellScreen = 33,
        CutScene = 34,
        DpShowPic = 35,
        DpHidePic = 36,
        Skybox = 37,
        Bf = 40,
        Fog = 41,
        SpawnBaseline2 = 42,
        SpawnStatic2 = 43,
        SpawnStaticSound2 = 44,
        DpDownloadData = 50,
        DpUpdateStatByte = 51,
        DpEffect = 52,
        DpEffect2 = 53,
        DpPrecache = 54,
        DpSpawnBaseline2 = 55,
        DpSpawnStatic2 = 56,
        DpSpawnStaticSound2 = 59,
        DpTrailParticles = 60,
        DpPointParticles = 61,
        DpPointParticles1 = 62,
        FteSpawnBaseline2 = 66,
        FteUpdateStatString = 78,
        FteUpdateStatFloat = 79,
        FteVoiceChat = 84,
        FteSetAngleDelta = 85,
        FteUpdateEntities = 86,
    }
}
