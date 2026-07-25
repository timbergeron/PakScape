import Foundation

struct QuakeDemoPlayer: Equatable {
    let slot: Int
    let name: String
    let frags: Int
    let shirt: Int
    let pants: Int
}

struct QuakeDemoSegment: Equatable {
    let map: String
    let levelName: String
    let duration: Double
}

/// Everything the metadata inspector can learn by walking a recorded demo.
/// `messagesComplete` is false when the server message stream ran into something this
/// parser cannot decode, in which case the fields carry whatever was read before that
/// point and the frame walk supplied the timings.
struct QuakeDemoSummary: Equatable {
    let protocolName: String
    let segments: [QuakeDemoSegment]
    let gameDir: String
    let maxClients: Int
    let gameType: Int
    let duration: Double
    let frameCount: Int
    let players: [QuakeDemoPlayer]
    let messagesComplete: Bool
    let truncated: Bool

    var isDeathmatch: Bool { gameType != 0 }

    var isSinglePlayer: Bool { gameType == 0 && maxClients <= 1 }
}

/// Parses Quake demo (.dem) recordings far enough to describe them: protocol, levels,
/// players, scores, and length. The logic follows the q1tools demo parser, which in turn
/// follows the FTE and QuakeSpasm client message readers.
///
/// Demo frames are length prefixed, so timings survive even when a frame's payload uses an
/// extension this parser does not know. Message decoding stops at the first such frame and
/// the frame walk continues alone.
enum QuakeDemoInspector {
    private static let maximumMessageLength = 64_000
    private static let maximumModels = 4_096
    private static let maximumSounds = 2_048
    private static let maximumPlayers = 255

    private static let protocolNetQuake: Int32 = 15
    private static let protocolFitzQuake: Int32 = 666
    private static let protocolRMQ: Int32 = 999
    private static let protocolDP7: Int32 = 3_504
    private static let protocolBJP3: Int32 = 10_002
    private static let protocolFTEPext1: Int32 = 0x5845_5446
    private static let protocolFTEPext2: Int32 = 0x3245_5446

    private static let prflShortAngle: UInt32 = 1 << 1
    private static let prflFloatAngle: UInt32 = 1 << 2
    private static let prfl24BitCoord: UInt32 = 1 << 3
    private static let prflFloatCoord: UInt32 = 1 << 4
    private static let prflInt32Coord: UInt32 = 1 << 7

    private static let pext2ReplacementDeltas: UInt32 = 0x0000_0008
    private static let pext2PredInfo: UInt32 = 0x0000_0020
    private static let pext2NewSizeEncoding: UInt32 = 0x0000_0040

    private static let uMoreBits = 1 << 0
    private static let uOrigin1 = 1 << 1
    private static let uOrigin2 = 1 << 2
    private static let uOrigin3 = 1 << 3
    private static let uAngle2 = 1 << 4
    private static let uFrame = 1 << 6
    private static let uSignal = 1 << 7
    private static let uAngle1 = 1 << 8
    private static let uAngle3 = 1 << 9
    private static let uModel = 1 << 10
    private static let uColorMap = 1 << 11
    private static let uSkin = 1 << 12
    private static let uEffects = 1 << 13
    private static let uLongEntity = 1 << 14
    private static let uExtend1 = 1 << 15
    private static let uAlpha = 1 << 16
    private static let uFrame2 = 1 << 17
    private static let uModel2 = 1 << 18
    private static let uLerpFinish = 1 << 19
    private static let uScale = 1 << 20
    private static let uExtend2 = 1 << 23
    private static let uTrans = 1 << 15

    private static let ufFrame: UInt32 = 1 << 0
    private static let ufOriginXY: UInt32 = 1 << 1
    private static let ufOriginZ: UInt32 = 1 << 2
    private static let ufAnglesXZ: UInt32 = 1 << 3
    private static let ufAnglesY: UInt32 = 1 << 4
    private static let ufEffects: UInt32 = 1 << 5
    private static let ufPredInfo: UInt32 = 1 << 6
    private static let ufExtend1: UInt32 = 1 << 7
    private static let uf16Bit: UInt32 = 1 << 9
    private static let ufModel: UInt32 = 1 << 10
    private static let ufSkin: UInt32 = 1 << 11
    private static let ufColorMap: UInt32 = 1 << 12
    private static let ufSolid: UInt32 = 1 << 13
    private static let ufFlags: UInt32 = 1 << 14
    private static let ufExtend2: UInt32 = 1 << 15
    private static let ufAlpha: UInt32 = 1 << 16
    private static let ufScale: UInt32 = 1 << 17
    private static let ufBoneData: UInt32 = 1 << 18
    private static let ufDrawFlags: UInt32 = 1 << 19
    private static let ufTagInfo: UInt32 = 1 << 20
    private static let ufLight: UInt32 = 1 << 21
    private static let ufTrailEffect: UInt32 = 1 << 22
    private static let ufExtend3: UInt32 = 1 << 23
    private static let ufColorMod: UInt32 = 1 << 24
    private static let ufGlow: UInt32 = 1 << 25
    private static let ufFatness: UInt32 = 1 << 26
    private static let ufModelIndex2: UInt32 = 1 << 27
    private static let ufGravityDir: UInt32 = 1 << 28
    private static let ufEffects2: UInt32 = 1 << 29
    private static let ufUnused2: UInt32 = 1 << 30
    private static let ufUnused1: UInt32 = 1 << 31

    private static let ufpForward = 1 << 0
    private static let ufpSide = 1 << 1
    private static let ufpUp = 1 << 2
    private static let ufpMoveType = 1 << 3
    private static let ufpVelocityXY = 1 << 4
    private static let ufpVelocityZ = 1 << 5
    private static let ufpMsec = 1 << 6
    private static let ufpViewAngle = 1 << 7
    private static let ufpWeaponFrameOld = 1 << 7

    private static let suViewHeight = 1 << 0
    private static let suIdealPitch = 1 << 1
    private static let suPunch1 = 1 << 2
    private static let suVelocity1 = 1 << 5
    private static let suItems = 1 << 9
    private static let suWeaponFrame = 1 << 12
    private static let suArmor = 1 << 13
    private static let suWeapon = 1 << 14
    private static let suExtend1 = 1 << 15
    private static let suWeapon2 = 1 << 16
    private static let suArmor2 = 1 << 17
    private static let suAmmo2 = 1 << 18
    private static let suShells2 = 1 << 19
    private static let suNails2 = 1 << 20
    private static let suRockets2 = 1 << 21
    private static let suCells2 = 1 << 22
    private static let suExtend2 = 1 << 23
    private static let suWeaponFrame2 = 1 << 24
    private static let suWeaponAlpha = 1 << 25
    private static let dpSuPunchVec1 = 1 << 16

    private static let bLargeModel = 1 << 0
    private static let bLargeFrame = 1 << 1
    private static let bAlpha = 1 << 2
    private static let bScale = 1 << 3

    private static let sndVolume: UInt64 = 1 << 0
    private static let sndAttenuation: UInt64 = 1 << 1
    private static let sndFTEMoreFlags: UInt64 = 1 << 2
    private static let sndLargeEntity: UInt64 = 1 << 3
    private static let sndLargeSound: UInt64 = 1 << 4
    private static let sndDPPitch: UInt64 = 1 << 5
    private static let sndFTETimeOfs: UInt64 = 1 << 6
    private static let sndFTEPitchAdj: UInt64 = 1 << 7
    private static let sndFTEVelocity: UInt64 = 1 << 8

    private enum SVC: UInt8 {
        case nop = 1
        case disconnect = 2
        case updateStat = 3
        case version = 4
        case setView = 5
        case sound = 6
        case time = 7
        case print = 8
        case stuffText = 9
        case setAngle = 10
        case serverInfo = 11
        case lightStyle = 12
        case updateName = 13
        case updateFrags = 14
        case clientData = 15
        case stopSound = 16
        case updateColors = 17
        case particle = 18
        case damage = 19
        case spawnStatic = 20
        case fteSpawnStatic2Alias = 21
        case spawnBaseline = 22
        case tempEntity = 23
        case setPause = 24
        case signOnNum = 25
        case centerPrint = 26
        case killedMonster = 27
        case foundSecret = 28
        case spawnStaticSound = 29
        case intermission = 30
        case finale = 31
        case cdTrack = 32
        case sellScreen = 33
        case cutScene = 34
        case dpShowPic = 35
        case dpHidePic = 36
        case skybox = 37
        case bf = 40
        case fog = 41
        case spawnBaseline2 = 42
        case spawnStatic2 = 43
        case spawnStaticSound2 = 44
        case dpDownloadData = 50
        case dpUpdateStatByte = 51
        case dpEffect = 52
        case dpEffect2 = 53
        case dpPrecache = 54
        case dpSpawnBaseline2 = 55
        case dpSpawnStatic2 = 56
        case dpSpawnStaticSound2 = 59
        case dpTrailParticles = 60
        case dpPointParticles = 61
        case dpPointParticles1 = 62
        case fteSpawnBaseline2 = 66
        case fteUpdateStatString = 78
        case fteUpdateStatFloat = 79
        case fteVoiceChat = 84
        case fteSetAngleDelta = 85
        case fteUpdateEntities = 86
    }

    private struct DemoParseError: Error {}

    /// Returns nil when the buffer does not open like a demo, leaving callers free to fall
    /// back to a plain text scan.
    static func inspect(_ data: Data) -> QuakeDemoSummary? {
        let bytes = [UInt8](data)
        guard var offset = trackLineEnd(bytes) else { return nil }

        let state = DemoState()
        var frameCount = 0
        var truncated = false

        while offset < bytes.count {
            guard bytes.count - offset >= 16 else {
                truncated = bytes.count > offset
                break
            }

            let messageLength = Int(int32(bytes, at: offset))
            guard messageLength >= 0, messageLength <= maximumMessageLength else {
                truncated = true
                break
            }

            let payloadOffset = offset + 16
            guard bytes.count - payloadOffset >= messageLength else {
                truncated = true
                break
            }

            frameCount += 1
            let payloadRange = payloadOffset ..< payloadOffset + messageLength
            if state.messagesComplete {
                parseFrameGuarded(bytes, payloadRange, state)
            } else {
                readFrameTime(bytes, payloadRange, state)
            }

            offset = payloadOffset + messageLength
        }

        guard frameCount > 0 else { return nil }

        state.closeSegment()
        if state.segments.isEmpty, state.playerCount == 0, state.protocolVersion == 0 {
            return nil
        }

        return QuakeDemoSummary(
            protocolName: protocolName(state.protocolVersion, state.pext2),
            segments: state.segments,
            gameDir: state.gameDir,
            maxClients: state.maxClients,
            gameType: state.gameType,
            duration: state.segments.reduce(0) { $0 + $1.duration },
            frameCount: frameCount,
            players: state.orderedPlayers(),
            messagesComplete: state.messagesComplete,
            truncated: truncated
        )
    }

    /// The first line names the CD track, so a demo starts with a small integer.
    private static func trackLineEnd(_ bytes: [UInt8]) -> Int? {
        guard let newline = bytes.firstIndex(of: 0x0a), newline > 0, newline <= 16 else { return nil }

        var end = newline
        if bytes[end - 1] == 0x0d {
            end -= 1
        }
        guard end > 0 else { return nil }

        let start = bytes[0] == UInt8(ascii: "-") ? 1 : 0
        guard start < end else { return nil }
        let digits = (0x30 as UInt8) ... (0x39 as UInt8)
        guard bytes[start ..< end].allSatisfy({ digits.contains($0) }) else { return nil }
        return newline + 1
    }

    /// Keeps the timeline moving after message decoding gave up. Timed frames lead with
    /// svc_time, so the timestamp is readable without understanding the rest of the frame.
    private static func readFrameTime(_ bytes: [UInt8], _ range: Range<Int>, _ state: DemoState) {
        guard range.count >= 5, bytes[range.lowerBound] == SVC.time.rawValue else { return }
        state.registerTime(float32(bytes, at: range.lowerBound + 1))
    }

    private static func parseFrameGuarded(_ bytes: [UInt8], _ range: Range<Int>, _ state: DemoState) {
        var reader = MessageReader(bytes, range)
        do {
            try parseFrame(&reader, state)
        } catch {
            state.messagesComplete = false
            readFrameTime(bytes, range, state)
        }
    }

    private static func parseFrame(_ reader: inout MessageReader, _ state: DemoState) throws {
        while reader.remaining > 0 {
            let command = Int(try reader.readByte())
            if command & uSignal != 0 {
                try skipClassicUpdate(&reader, state, command & 127)
                continue
            }

            guard let message = SVC(rawValue: UInt8(command)) else { throw DemoParseError() }

            switch message {
            case .nop, .disconnect, .intermission, .sellScreen, .bf, .killedMonster, .foundSecret:
                break

            case .setPause, .signOnNum:
                _ = try reader.readByte()

            case .updateStat:
                _ = try reader.readByte()
                _ = try reader.readInt32()

            case .version:
                state.protocolVersion = try reader.readInt32()

            case .setView, .stopSound:
                _ = try reader.readUInt16()

            case .sound:
                try skipStartSound(&reader, state)

            case .time:
                state.registerTime(try reader.readFloat())
                if state.pext2 & pext2PredInfo != 0 {
                    _ = try reader.readUInt16()
                }

            case .print, .centerPrint, .finale, .cutScene, .dpHidePic, .skybox:
                _ = try reader.readString()

            case .stuffText:
                state.applyStuffText(try reader.readString())

            case .setAngle:
                for _ in 0 ..< 3 {
                    try skipAngle(&reader, state)
                }

            case .serverInfo:
                try parseServerInfo(&reader, state)

            case .lightStyle:
                _ = try reader.readByte()
                _ = try reader.readString()

            case .updateName:
                let slot = Int(try reader.readByte())
                state.setPlayerName(slot, dequake(try reader.readString()))

            case .updateFrags:
                let slot = Int(try reader.readByte())
                state.setPlayerFrags(slot, Int(try reader.readInt16()))

            case .clientData:
                try skipClientData(&reader, state)

            case .updateColors:
                let slot = Int(try reader.readByte())
                let colors = Int(try reader.readByte())
                state.setPlayerColors(slot, shirt: (colors >> 4) & 0x0f, pants: colors & 0x0f)

            case .particle:
                try skipCoords(&reader, state, 3)
                try reader.skip(5)

            case .damage:
                try reader.skip(2)
                try skipCoords(&reader, state, 3)

            case .spawnStatic:
                try skipBaseline(&reader, state, version: 1)

            case .fteSpawnStatic2Alias:
                try skipBaseline(&reader, state, version: 6)

            case .spawnBaseline:
                _ = try reader.readUInt16()
                try skipBaseline(&reader, state, version: 1)

            case .tempEntity:
                try skipTempEntity(&reader, state)

            case .spawnStaticSound:
                try skipStaticSound(&reader, state, version: 1)

            case .cdTrack:
                try reader.skip(2)

            case .dpShowPic:
                _ = try reader.readString()
                _ = try reader.readString()
                try reader.skip(2)

            case .fog:
                try reader.skip(6)

            case .spawnBaseline2:
                _ = try reader.readUInt16()
                try skipBaseline(&reader, state, version: 2)

            case .spawnStatic2:
                try skipBaseline(&reader, state, version: 2)

            case .spawnStaticSound2, .dpSpawnStaticSound2:
                try skipStaticSound(&reader, state, version: 2)

            case .dpDownloadData:
                let start = try reader.readInt32()
                let size = Int(try reader.readUInt16())
                guard start >= 0 else { throw DemoParseError() }
                try reader.skip(size)

            case .dpUpdateStatByte:
                try reader.skip(2)

            case .dpEffect:
                if state.protocolVersion == protocolDP7 {
                    try skipEffect(&reader, state, big: false)
                } else {
                    _ = try reader.readString()
                }

            case .dpEffect2:
                guard state.protocolVersion == protocolDP7 else { throw DemoParseError() }
                try skipEffect(&reader, state, big: true)

            case .dpPrecache:
                _ = try reader.readUInt16()
                _ = try reader.readString()

            case .dpSpawnBaseline2:
                _ = try reader.readUInt16()
                try skipBaseline(&reader, state, version: 7)

            case .dpSpawnStatic2:
                try skipBaseline(&reader, state, version: 7)

            case .dpTrailParticles:
                try skipParticles(&reader, state, type: -1)

            case .dpPointParticles:
                try skipParticles(&reader, state, type: 0)

            case .dpPointParticles1:
                try skipParticles(&reader, state, type: 1)

            case .fteSpawnBaseline2:
                try readEntityIndex(&reader, state)
                try skipBaseline(&reader, state, version: 6)

            case .fteUpdateStatString:
                _ = try reader.readByte()
                _ = try reader.readString()

            case .fteUpdateStatFloat:
                _ = try reader.readByte()
                _ = try reader.readFloat()

            case .fteVoiceChat:
                try reader.skip(3)
                try reader.skip(Int(try reader.readUInt16()))

            case .fteSetAngleDelta:
                for _ in 0 ..< 3 {
                    try skipAngle16(&reader, state)
                }

            case .fteUpdateEntities:
                try skipFTEUpdateEntities(&reader, state)
            }
        }
    }

    private static func parseServerInfo(_ reader: inout MessageReader, _ state: DemoState) throws {
        state.closeSegment()
        state.pext1 = 0
        state.pext2 = 0
        state.protocolFlags = 0
        state.gameDir = ""

        var version: Int32
        while true {
            let value = try reader.readInt32()
            if value == protocolFTEPext1 {
                state.pext1 = try reader.readUInt32()
                continue
            }
            if value == protocolFTEPext2 {
                state.pext2 = try reader.readUInt32()
                continue
            }
            version = value
            break
        }

        state.protocolVersion = version
        switch version {
        case protocolRMQ:
            state.protocolFlags = try reader.readUInt32()
        case protocolDP7:
            state.protocolFlags = prflShortAngle | prflFloatCoord
        default:
            state.protocolFlags = 0
        }

        if state.pext2 & pext2PredInfo != 0 {
            state.gameDir = dequake(try reader.readString())
        }

        state.maxClients = Int(try reader.readByte())
        state.gameType = Int(try reader.readByte())
        let levelName = dequake(try reader.readString()).trimmingCharacters(in: .whitespacesAndNewlines)

        var map = ""
        var modelCount = 0
        while true {
            let modelName = try reader.readString()
            if modelName.isEmpty { break }
            modelCount += 1
            if modelCount == 1 {
                map = stripPathExtension(modelName)
            }
            guard modelCount <= maximumModels else { throw DemoParseError() }
        }

        var soundCount = 0
        while true {
            let soundName = try reader.readString()
            if soundName.isEmpty { break }
            soundCount += 1
            guard soundCount <= maximumSounds else { throw DemoParseError() }
        }

        state.beginSegment(map: map, levelName: levelName)
    }

    private static func skipClassicUpdate(
        _ reader: inout MessageReader,
        _ state: DemoState,
        _ firstBits: Int
    ) throws {
        var bits = firstBits
        if bits & uMoreBits != 0 {
            bits |= Int(try reader.readByte()) << 8
        }

        let fitzLike = state.protocolVersion == protocolFitzQuake || state.protocolVersion == protocolRMQ
        if fitzLike {
            if bits & uExtend1 != 0 {
                bits |= Int(try reader.readByte()) << 16
            }
            if bits & uExtend2 != 0 {
                bits |= Int(try reader.readByte()) << 24
            }
        }

        try reader.skip(bits & uLongEntity != 0 ? 2 : 1)

        if bits & uModel != 0 {
            try reader.skip(state.protocolVersion == protocolBJP3 ? 2 : 1)
        }
        for flag in [uFrame, uColorMap, uSkin, uEffects] where bits & flag != 0 {
            try reader.skip(1)
        }

        if bits & uOrigin1 != 0 {
            try skipCoord(&reader, state)
        }
        if bits & uAngle1 != 0 {
            try skipAngle(&reader, state)
        }
        if bits & uOrigin2 != 0 {
            try skipCoord(&reader, state)
        }
        if bits & uAngle2 != 0 {
            try skipAngle(&reader, state)
        }
        if bits & uOrigin3 != 0 {
            try skipCoord(&reader, state)
        }
        if bits & uAngle3 != 0 {
            try skipAngle(&reader, state)
        }

        if fitzLike {
            for flag in [uAlpha, uScale, uFrame2, uModel2, uLerpFinish] where bits & flag != 0 {
                try reader.skip(1)
            }
        } else if state.protocolVersion == protocolNetQuake || state.protocolVersion == protocolBJP3,
                  bits & uTrans != 0 {
            // Nehahra transparency: mode, alpha, and a fullbright float only for mode 2.
            let transparencyMode = try reader.readFloat()
            _ = try reader.readFloat()
            if transparencyMode == 2 {
                _ = try reader.readFloat()
            }
        }
    }

    private static func skipBaseline(
        _ reader: inout MessageReader,
        _ state: DemoState,
        version: Int
    ) throws {
        if version == 6 {
            try skipFTEDelta(&reader, state)
            return
        }

        var bits = 0
        switch version {
        case 1 where state.protocolVersion == protocolBJP3:
            bits = bLargeModel
        case 7:
            bits = bLargeModel | bLargeFrame
        case 2:
            bits = Int(try reader.readByte())
        default:
            bits = 0
        }

        try reader.skip(bits & bLargeModel != 0 ? 2 : 1)
        try reader.skip(bits & bLargeFrame != 0 ? 2 : 1)
        try reader.skip(2)
        for _ in 0 ..< 3 {
            try skipCoord(&reader, state)
            try skipAngle(&reader, state)
        }
        if bits & bAlpha != 0 {
            try reader.skip(1)
        }
        if bits & bScale != 0 {
            try reader.skip(1)
        }
    }

    private static func skipFTEDelta(_ reader: inout MessageReader, _ state: DemoState) throws {
        var bits = UInt32(try reader.readByte())
        if bits & ufExtend1 != 0 {
            bits |= UInt32(try reader.readByte()) << 8
        }
        if bits & ufExtend2 != 0 {
            bits |= UInt32(try reader.readByte()) << 16
        }
        if bits & ufExtend3 != 0 {
            bits |= UInt32(try reader.readByte()) << 24
        }

        let wide = bits & uf16Bit != 0
        if bits & ufFrame != 0 {
            try reader.skip(wide ? 2 : 1)
        }
        if bits & ufOriginXY != 0 {
            try skipCoord(&reader, state)
            try skipCoord(&reader, state)
        }
        if bits & ufOriginZ != 0 {
            try skipCoord(&reader, state)
        }

        let shortAngles = bits & ufPredInfo != 0 && state.pext2 & pext2PredInfo == 0
        if bits & ufAnglesXZ != 0 {
            if shortAngles {
                try skipAngle16(&reader, state)
                try skipAngle16(&reader, state)
            } else {
                try skipAngle(&reader, state)
                try skipAngle(&reader, state)
            }
        }
        if bits & ufAnglesY != 0 {
            if shortAngles {
                try skipAngle16(&reader, state)
            } else {
                try skipAngle(&reader, state)
            }
        }

        if bits & (ufEffects | ufEffects2) == (ufEffects | ufEffects2) {
            try reader.skip(4)
        } else if bits & ufEffects2 != 0 {
            try reader.skip(2)
        } else if bits & ufEffects != 0 {
            try reader.skip(1)
        }

        if bits & ufPredInfo != 0 {
            let predBits = Int(try reader.readByte())
            for (flag, size) in [
                (ufpForward, 2), (ufpSide, 2), (ufpUp, 2), (ufpMoveType, 1),
                (ufpVelocityXY, 4), (ufpVelocityZ, 2), (ufpMsec, 1),
            ] where predBits & flag != 0 {
                try reader.skip(size)
            }

            if state.pext2 & pext2PredInfo != 0 {
                if predBits & ufpViewAngle != 0 {
                    if bits & ufAnglesXZ != 0 {
                        try reader.skip(4)
                    }
                    if bits & ufAnglesY != 0 {
                        try reader.skip(2)
                    }
                }
            } else if predBits & ufpWeaponFrameOld != 0 {
                let weaponFrame = try reader.readByte()
                if weaponFrame & 0x80 != 0 {
                    try reader.skip(1)
                }
            }
        }

        if bits & ufModel != 0 {
            try reader.skip(wide ? 2 : 1)
        }
        if bits & ufSkin != 0 {
            try reader.skip(wide ? 2 : 1)
        }
        if bits & ufColorMap != 0 {
            try reader.skip(1)
        }
        if bits & ufSolid != 0 {
            if state.pext2 & pext2NewSizeEncoding != 0 {
                switch Int(try reader.readByte()) {
                case 0, 1, 2, 3:
                    break
                case 16:
                    try reader.skip(2)
                case 32:
                    try reader.skip(4)
                default:
                    throw DemoParseError()
                }
            } else {
                try reader.skip(2)
            }
        }
        for flag in [ufFlags, ufAlpha, ufScale] where bits & flag != 0 {
            try reader.skip(1)
        }
        if bits & ufBoneData != 0 {
            let flags = Int(try reader.readByte())
            if flags & 0x80 != 0 {
                let boneCount = Int(try reader.readByte())
                try reader.skip(boneCount * 7 * 2)
            }
            if flags & 0x40 != 0 {
                try reader.skip(3)
            }
            guard flags & 0x3f == 0 else { throw DemoParseError() }
        }
        if bits & ufDrawFlags != 0 {
            if Int(try reader.readByte()) & 7 == 7 {
                try reader.skip(1)
            }
        }
        if bits & ufTagInfo != 0 {
            try readEntityIndex(&reader, state)
            try reader.skip(1)
        }
        if bits & ufLight != 0 {
            try reader.skip(10)
        }
        if bits & ufTrailEffect != 0 {
            if try reader.readUInt16() & 0x8000 != 0 {
                try reader.skip(2)
            }
        }
        if bits & ufColorMod != 0 {
            try reader.skip(3)
        }
        if bits & ufGlow != 0 {
            try reader.skip(5)
        }
        if bits & ufFatness != 0 {
            try reader.skip(1)
        }
        if bits & ufModelIndex2 != 0 {
            try reader.skip(wide ? 2 : 1)
        }
        if bits & ufGravityDir != 0 {
            try reader.skip(2)
        }
        guard bits & (ufUnused1 | ufUnused2) == 0 else { throw DemoParseError() }
    }

    private static func skipFTEUpdateEntities(_ reader: inout MessageReader, _ state: DemoState) throws {
        if state.pext2 & pext2PredInfo != 0 {
            _ = try reader.readUInt16()
        }
        state.registerTime(try reader.readFloat())

        while reader.remaining > 0 {
            var entityValue = Int(try reader.readUInt16())
            let removeFlag = entityValue & 0x8000 != 0
            if entityValue & 0x4000 != 0 {
                entityValue = (entityValue & 0x3fff) | (Int(try reader.readByte()) << 14)
            } else {
                entityValue &= ~0x8000
            }
            if entityValue == 0, !removeFlag {
                break
            }
            if removeFlag {
                continue
            }
            try skipFTEDelta(&reader, state)
        }
    }

    private static func skipClientData(_ reader: inout MessageReader, _ state: DemoState) throws {
        var bits = Int(try reader.readUInt16())
        if bits & suExtend1 != 0 {
            bits |= Int(try reader.readByte()) << 16
        }
        if bits & suExtend2 != 0 {
            bits |= Int(try reader.readByte()) << 24
        }

        let isDP7 = state.protocolVersion == protocolDP7
        if !isDP7 {
            bits |= suItems
        }

        if bits & suViewHeight != 0 {
            try reader.skip(1)
        }
        if bits & suIdealPitch != 0 {
            try reader.skip(1)
        }

        for axis in 0 ..< 3 {
            if bits & (suPunch1 << axis) != 0 {
                if isDP7 {
                    try skipAngle(&reader, flags: prflShortAngle)
                } else {
                    try reader.skip(1)
                }
            }
            if isDP7, bits & (dpSuPunchVec1 << axis) != 0 {
                try skipCoord(&reader, state)
            }
            if bits & (suVelocity1 << axis) != 0 {
                try reader.skip(isDP7 ? 4 : 1)
            }
        }

        if bits & suItems != 0 {
            try reader.skip(4)
        }

        if isDP7 {
            return
        }

        if bits & suWeaponFrame != 0 {
            try reader.skip(1)
        }
        if bits & suArmor != 0 {
            try reader.skip(1)
        }
        if bits & suWeapon != 0 {
            try reader.skip(state.protocolVersion == protocolBJP3 ? 2 : 1)
        }

        // health, ammo, four ammo counts, active weapon
        try reader.skip(2 + 1 + 4 + 1)

        let trailing = [
            suWeapon2, suArmor2, suAmmo2, suShells2, suNails2,
            suRockets2, suCells2, suWeaponFrame2, suWeaponAlpha,
        ]
        for flag in trailing where bits & flag != 0 {
            try reader.skip(1)
        }
    }

    private static func skipStartSound(_ reader: inout MessageReader, _ state: DemoState) throws {
        var fieldMask = UInt64(try reader.readByte())
        if state.protocolVersion == protocolBJP3 {
            fieldMask |= sndLargeSound
        }
        if fieldMask & sndFTEMoreFlags != 0 {
            fieldMask |= try reader.readVarUInt64() << 8
        }
        if fieldMask & sndVolume != 0 {
            try reader.skip(1)
        }
        if fieldMask & sndAttenuation != 0 {
            try reader.skip(1)
        }

        let replacementDeltas = state.pext2 & pext2ReplacementDeltas != 0
        if replacementDeltas {
            if fieldMask & sndFTEPitchAdj != 0 {
                try reader.skip(1)
            }
            if fieldMask & sndFTETimeOfs != 0 {
                try reader.skip(2)
            }
            if fieldMask & sndFTEVelocity != 0 {
                try reader.skip(6)
            }
        }
        if state.protocolVersion == protocolDP7 || replacementDeltas, fieldMask & sndDPPitch != 0 {
            try reader.skip(2)
        }

        try reader.skip(fieldMask & sndLargeEntity != 0 ? 3 : 2)
        try reader.skip(fieldMask & sndLargeSound != 0 ? 2 : 1)
        try skipCoords(&reader, state, 3)
    }

    private static func skipStaticSound(
        _ reader: inout MessageReader,
        _ state: DemoState,
        version: Int
    ) throws {
        try skipCoords(&reader, state, 3)
        try reader.skip(version == 2 ? 2 : 1)
        try reader.skip(2)
    }

    private static func skipEffect(
        _ reader: inout MessageReader,
        _ state: DemoState,
        big: Bool
    ) throws {
        try skipCoords(&reader, state, 3)
        try reader.skip(big ? 4 : 2)
        try reader.skip(2)
    }

    private static func skipParticles(
        _ reader: inout MessageReader,
        _ state: DemoState,
        type: Int
    ) throws {
        if type < 0 {
            try reader.skip(4)
            try skipCoords(&reader, state, 6)
            return
        }

        try reader.skip(2)
        try skipCoords(&reader, state, 3)
        if type == 0 {
            try skipCoords(&reader, state, 3)
            try reader.skip(2)
        }
    }

    private static func skipTempEntity(_ reader: inout MessageReader, _ state: DemoState) throws {
        switch Int(try reader.readByte()) {
        case 0, 1, 2, 3, 4, 7, 8, 10, 11, 20, 57, 58, 59, 70, 72, 75:
            try skipCoords(&reader, state, 3)

        case 21: // FTE gunshot with a count
            try reader.skip(1)
            try skipCoords(&reader, state, 3)

        case 12: // explosion 2
            try skipCoords(&reader, state, 3)
            try reader.skip(2)

        case 16: // Nehahra explosion 3
            try skipCoords(&reader, state, 6)

        case 5, 6, 9, 13: // lightning 1-3 and beam
            try readEntityIndex(&reader, state)
            try skipCoords(&reader, state, 6)

        case 17: // Nehahra lightning 4
            _ = try reader.readString()
            try readEntityIndex(&reader, state)
            try skipCoords(&reader, state, 6)

        case 73: // DP custom flash
            try skipCoords(&reader, state, 3)
            try reader.skip(5)

        case 55, 56: // DP particle rain and snow
            try skipCoords(&reader, state, 9)
            try reader.skip(3)

        case 50, 51: // DP blood and spark
            try skipCoords(&reader, state, 3)
            try reader.skip(4)

        case 52: // DP blood shower
            try skipCoords(&reader, state, 7)
            try reader.skip(2)

        case 53: // DP explosion RGB
            try skipCoords(&reader, state, 3)
            try reader.skip(3)

        case 54: // DP particle cube
            try skipCoords(&reader, state, 10)
            try reader.skip(4)

        case 74: // DP flame jet
            try skipCoords(&reader, state, 6)
            try reader.skip(1)

        default:
            throw DemoParseError()
        }
    }

    private static func readEntityIndex(_ reader: inout MessageReader, _ state: DemoState) throws {
        let value = try reader.readUInt16()
        if state.pext2 & pext2ReplacementDeltas != 0, value & 0x8000 != 0 {
            _ = try reader.readByte()
        }
    }

    private static func skipCoords(
        _ reader: inout MessageReader,
        _ state: DemoState,
        _ count: Int
    ) throws {
        for _ in 0 ..< count {
            try skipCoord(&reader, state)
        }
    }

    private static func skipCoord(_ reader: inout MessageReader, _ state: DemoState) throws {
        let flags = state.protocolFlags
        if flags & prflFloatCoord != 0 || flags & prflInt32Coord != 0 {
            try reader.skip(4)
        } else if flags & prfl24BitCoord != 0 {
            try reader.skip(3)
        } else {
            try reader.skip(2)
        }
    }

    private static func skipAngle(_ reader: inout MessageReader, _ state: DemoState) throws {
        try skipAngle(&reader, flags: state.protocolFlags)
    }

    private static func skipAngle(_ reader: inout MessageReader, flags: UInt32) throws {
        if flags & prflFloatAngle != 0 {
            try reader.skip(4)
        } else if flags & prflShortAngle != 0 {
            try reader.skip(2)
        } else {
            try reader.skip(1)
        }
    }

    private static func skipAngle16(_ reader: inout MessageReader, _ state: DemoState) throws {
        try reader.skip(state.protocolFlags & prflFloatAngle != 0 ? 4 : 2)
    }

    private static func protocolName(_ version: Int32, _ pext2: UInt32) -> String {
        let name: String
        switch version {
        case 0: name = "unknown"
        case protocolNetQuake: name = "15"
        case protocolFitzQuake: name = "666"
        case protocolRMQ: name = "999"
        case protocolDP7: name = "3504"
        case protocolBJP3: name = "10002"
        default: name = String(version)
        }
        return pext2 != 0 ? name + "+fte" : name
    }

    private static func stripPathExtension(_ path: String) -> String {
        let name = path.split(separator: "/").last.map(String.init) ?? path
        guard let dot = name.lastIndex(of: "."), dot != name.startIndex else { return name }
        return String(name[name.startIndex ..< dot])
    }

    /// Maps the Quake character set onto readable ASCII, as the console does.
    static func dequake(_ value: String) -> String {
        guard !value.isEmpty else { return value }

        var result = ""
        result.reserveCapacity(value.count)
        for scalar in value.unicodeScalars {
            let mapped = dequakeMap[Int(scalar.value) & 0xff]
            if mapped != 0 {
                result.unicodeScalars.append(Unicode.Scalar(mapped))
            }
        }
        return result
    }

    private static let dequakeMap: [UInt8] = {
        var map = [UInt8](repeating: 0, count: 256)
        for index in 1 ..< 12 {
            map[index] = UInt8(ascii: "#")
        }
        map[9] = 9
        map[10] = 10
        map[12] = UInt8(ascii: " ")
        map[13] = 13
        map[1] = UInt8(ascii: ".")
        map[5] = UInt8(ascii: ".")
        map[14] = UInt8(ascii: ".")
        map[15] = UInt8(ascii: ".")
        map[16] = UInt8(ascii: "[")
        map[17] = UInt8(ascii: "]")
        map[28] = UInt8(ascii: ".")
        map[29] = UInt8(ascii: "<")
        map[30] = UInt8(ascii: "-")
        map[31] = UInt8(ascii: ">")
        for index in 0 ..< 10 {
            map[18 + index] = UInt8(ascii: "0") + UInt8(index)
        }
        for index in 32 ..< 128 {
            map[index] = UInt8(index)
        }
        for index in 0 ..< 128 {
            map[index + 128] = map[index]
        }
        map[128] = UInt8(ascii: "(")
        map[129] = UInt8(ascii: "=")
        map[130] = UInt8(ascii: ")")
        map[131] = UInt8(ascii: "*")
        map[141] = UInt8(ascii: ">")
        return map
    }()

    private static func int32(_ bytes: [UInt8], at offset: Int) -> Int32 {
        Int32(bitPattern: uint32(bytes, at: offset))
    }

    private static func uint32(_ bytes: [UInt8], at offset: Int) -> UInt32 {
        UInt32(bytes[offset])
            | UInt32(bytes[offset + 1]) << 8
            | UInt32(bytes[offset + 2]) << 16
            | UInt32(bytes[offset + 3]) << 24
    }

    private static func float32(_ bytes: [UInt8], at offset: Int) -> Float {
        Float(bitPattern: uint32(bytes, at: offset))
    }

    private final class DemoState {
        private var players: [Int: PlayerRecord] = [:]
        private(set) var segments: [QuakeDemoSegment] = []
        private var segmentMap = ""
        private var segmentLevelName = ""
        private var segmentOpen = false
        private var segmentStart: Double?
        private var segmentEnd: Double?

        var protocolVersion: Int32 = 0
        var protocolFlags: UInt32 = 0
        var pext1: UInt32 = 0
        var pext2: UInt32 = 0
        var maxClients = 0
        var gameType = 0
        var gameDir = ""
        var messagesComplete = true

        var playerCount: Int { players.count }

        func registerTime(_ time: Float) {
            guard time.isFinite, segmentOpen else { return }
            let value = Double(time)
            if segmentStart == nil {
                segmentStart = value
            }
            segmentEnd = value
        }

        func beginSegment(map: String, levelName: String) {
            segmentMap = map
            segmentLevelName = levelName
            segmentOpen = true
            segmentStart = nil
            segmentEnd = nil
        }

        func closeSegment() {
            guard segmentOpen else { return }

            var duration = 0.0
            if let start = segmentStart, let end = segmentEnd {
                duration = max(0, end - start)
            }
            segments.append(
                QuakeDemoSegment(map: segmentMap, levelName: segmentLevelName, duration: duration)
            )
            segmentOpen = false
        }

        func setPlayerName(_ slot: Int, _ name: String) {
            ensure(slot)?.name = name
        }

        func setPlayerFrags(_ slot: Int, _ frags: Int) {
            ensure(slot)?.frags = frags
        }

        func setPlayerColors(_ slot: Int, shirt: Int, pants: Int) {
            guard let record = ensure(slot) else { return }
            record.shirt = shirt
            record.pants = pants
        }

        /// QuakeWorld-style servers publish names and colours through stuffed
        /// `//fullserverinfo` and `//ui` commands rather than svc_updatename.
        func applyStuffText(_ text: String) {
            for rawLine in text.split(whereSeparator: \.isNewline) {
                let line = rawLine.trimmingCharacters(in: .whitespaces)
                if line.lowercased().hasPrefix("gamedir ") {
                    let value = line.dropFirst(8)
                        .trimmingCharacters(in: .whitespaces)
                        .trimmingCharacters(in: CharacterSet(charactersIn: "\""))
                    if !value.isEmpty {
                        gameDir = value
                    }
                    continue
                }
                guard line.hasPrefix("//") else { continue }

                let tokens = Self.tokenize(line)
                guard let first = tokens.first else { continue }
                let command = String(first.dropFirst(2)).lowercased()

                switch command {
                case "fullserverinfo" where tokens.count >= 2:
                    let info = Self.parseInfoString(tokens[1])
                    if let value = info["*gamedir"] ?? info["gamedir"], !value.isEmpty {
                        gameDir = value
                    }
                case "svi" where tokens.count >= 3:
                    if tokens[1] == "*gamedir" || tokens[1] == "gamedir", !tokens[2].isEmpty {
                        gameDir = tokens[2]
                    }
                case "fui" where tokens.count >= 3:
                    if let slot = Int(tokens[1]), slot >= 0,
                       let name = Self.parseInfoString(tokens[2])["name"], !name.isEmpty {
                        setPlayerName(slot, QuakeDemoInspector.dequake(name))
                    }
                case "ui" where tokens.count >= 4:
                    if let slot = Int(tokens[1]), slot >= 0, tokens[2].lowercased() == "name" {
                        setPlayerName(slot, QuakeDemoInspector.dequake(tokens[3]))
                    }
                default:
                    break
                }
            }
        }

        func orderedPlayers() -> [QuakeDemoPlayer] {
            players
                .filter { !$0.value.name.isEmpty }
                .sorted { $0.key < $1.key }
                .map {
                    QuakeDemoPlayer(
                        slot: $0.key,
                        name: $0.value.name,
                        frags: $0.value.frags,
                        shirt: $0.value.shirt,
                        pants: $0.value.pants
                    )
                }
        }

        @discardableResult
        private func ensure(_ slot: Int) -> PlayerRecord? {
            guard slot >= 0, slot < QuakeDemoInspector.maximumPlayers else { return nil }
            if let existing = players[slot] { return existing }
            let record = PlayerRecord()
            players[slot] = record
            return record
        }

        private static func tokenize(_ line: String) -> [String] {
            var tokens: [String] = []
            let characters = Array(line)
            var index = 0
            while index < characters.count {
                while index < characters.count, characters[index].isWhitespace {
                    index += 1
                }
                guard index < characters.count else { break }

                if characters[index] == "\"" {
                    index += 1
                    let start = index
                    while index < characters.count, characters[index] != "\"" {
                        index += 1
                    }
                    tokens.append(String(characters[start ..< index]))
                    if index < characters.count {
                        index += 1
                    }
                } else {
                    let start = index
                    while index < characters.count, !characters[index].isWhitespace {
                        index += 1
                    }
                    tokens.append(String(characters[start ..< index]))
                }
            }
            return tokens
        }

        private static func parseInfoString(_ value: String) -> [String: String] {
            var info: [String: String] = [:]
            let parts = value.components(separatedBy: "\\")
            var index = 1
            while index + 1 < parts.count {
                if !parts[index].isEmpty {
                    info[parts[index]] = parts[index + 1]
                }
                index += 2
            }
            return info
        }

        private final class PlayerRecord {
            var name = ""
            var frags = 0
            var shirt = -1
            var pants = -1
        }
    }

    private struct MessageReader {
        private let bytes: [UInt8]
        private let end: Int
        private var offset: Int

        init(_ bytes: [UInt8], _ range: Range<Int>) {
            self.bytes = bytes
            end = range.upperBound
            offset = range.lowerBound
        }

        var remaining: Int { end - offset }

        mutating func readByte() throws -> UInt8 {
            try require(1)
            defer { offset += 1 }
            return bytes[offset]
        }

        mutating func readUInt16() throws -> UInt16 {
            try require(2)
            defer { offset += 2 }
            return UInt16(bytes[offset]) | UInt16(bytes[offset + 1]) << 8
        }

        mutating func readInt16() throws -> Int16 {
            Int16(bitPattern: try readUInt16())
        }

        mutating func readUInt32() throws -> UInt32 {
            try require(4)
            defer { offset += 4 }
            return QuakeDemoInspector.uint32(bytes, at: offset)
        }

        mutating func readInt32() throws -> Int32 {
            Int32(bitPattern: try readUInt32())
        }

        mutating func readFloat() throws -> Float {
            Float(bitPattern: try readUInt32())
        }

        mutating func readVarUInt64() throws -> UInt64 {
            var value = UInt64(try readByte())
            var mask: UInt64 = 0x80
            var extraBytes = 0
            while value & mask != 0, mask != 0 {
                value -= mask
                extraBytes += 1
                mask >>= 1
            }

            var result = value << (extraBytes * 8)
            while extraBytes > 0 {
                extraBytes -= 1
                result |= UInt64(try readByte()) << (extraBytes * 8)
            }
            return result
        }

        mutating func readString() throws -> String {
            let stringStart = offset
            while offset < end, bytes[offset] != 0 {
                offset += 1
            }
            guard offset < end else { throw DemoParseError() }

            let scalars = bytes[stringStart ..< offset]
            offset += 1
            return String(String.UnicodeScalarView(scalars.map { Unicode.Scalar($0) }))
        }

        mutating func skip(_ count: Int) throws {
            guard count >= 0 else { throw DemoParseError() }
            try require(count)
            offset += count
        }

        private func require(_ count: Int) throws {
            guard count <= remaining else { throw DemoParseError() }
        }
    }
}
