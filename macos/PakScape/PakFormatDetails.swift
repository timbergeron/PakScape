import Foundation

struct PakFormatDetail: Identifiable, Equatable {
    let label: String
    let value: String

    var id: String { label }
}

private extension PakFormatInspector {
    static func md3Details(_ data: Data) -> [PakFormatDetail] {
        guard ascii(data, at: 0, length: 4) == "IDP3",
              int32LE(data, at: 4) == 15,
              let frames = nonnegativeInt32(data, at: 76),
              let tags = nonnegativeInt32(data, at: 80),
              let surfaces = nonnegativeInt32(data, at: 84),
              let surfaceOffset = int32LE(data, at: 100),
              frames <= 1_000_000,
              tags <= 1_000_000,
              surfaces <= 16_384 else { return [] }

        var details = [
            detail("Format", "Quake III alias model"),
            detail("Version", "15"),
            detail("Frames", formatted(frames)),
            detail("Tags", formatted(tags)),
            detail("Surfaces", formatted(surfaces)),
        ]
        appendText(&details, label: "Name", value: nullTerminatedText(data, at: 8, length: 64))

        var cursor = surfaceOffset
        var vertices = 0
        var triangles = 0
        var shaders = 0
        var complete = cursor >= 0
        for _ in 0 ..< surfaces where complete {
            guard ascii(data, at: cursor, length: 4) == "IDP3",
                  let surfaceShaders = nonnegativeInt32(data, at: cursor + 76),
                  let surfaceVertices = nonnegativeInt32(data, at: cursor + 80),
                  let surfaceTriangles = nonnegativeInt32(data, at: cursor + 84),
                  let surfaceSize = positiveInt32(data, at: cursor + 104),
                  cursor <= data.count - surfaceSize else {
                complete = false
                break
            }
            shaders += surfaceShaders
            vertices += surfaceVertices
            triangles += surfaceTriangles
            cursor += surfaceSize
        }
        if complete {
            details.append(detail("Vertices", formatted(vertices)))
            details.append(detail("Triangles", formatted(triangles)))
            details.append(detail("Shaders", formatted(shaders)))
        }
        return details
    }

    static func md5Details(_ data: Data) -> [PakFormatDetail] {
        guard let text = String(data: data, encoding: .utf8),
              md5Value(text, key: "MD5Version") == 10 else { return [] }
        let frames = md5Value(text, key: "numFrames")
        let meshes = md5Value(text, key: "numMeshes")
        guard frames != nil || meshes != nil else { return [] }

        var details = [
            detail("Format", frames != nil ? "Doom 3 model animation" : "Doom 3 model mesh"),
            detail("Version", "10"),
        ]
        if let joints = md5Value(text, key: "numJoints") {
            details.append(detail("Joints", formatted(joints)))
        }
        if let frames {
            details.append(detail("Frames", formatted(frames)))
            if let frameRate = md5Value(text, key: "frameRate"), frameRate > 0 {
                details.append(detail("Frame Rate", "\(formatted(frameRate)) fps"))
                details.append(detail("Duration", duration(Double(frames) / Double(frameRate))))
            }
            if let components = md5Value(text, key: "numAnimatedComponents") {
                details.append(detail("Animated Components", formatted(components)))
            }
        } else if let meshes {
            details.append(detail("Meshes", formatted(meshes)))
            if let vertices = md5Sum(text, key: "numverts") {
                details.append(detail("Vertices", formatted(vertices)))
            }
            if let triangles = md5Sum(text, key: "numtris") {
                details.append(detail("Triangles", formatted(triangles)))
            }
        }
        return details
    }

    static func md5Value(_ text: String, key: String) -> Int? {
        for rawLine in text.split(separator: "\n", omittingEmptySubsequences: false) {
            let parts = rawLine
                .trimmingCharacters(in: .whitespacesAndNewlines)
                .split(whereSeparator: { $0.isWhitespace || $0 == "/" })
            if parts.count >= 2, parts[0] == Substring(key), let value = Int(parts[1]), value >= 0 {
                return value
            }
        }
        return nil
    }

    static func md5Sum(_ text: String, key: String) -> Int? {
        var total = 0
        var found = false
        for rawLine in text.split(separator: "\n", omittingEmptySubsequences: false) {
            let parts = rawLine
                .trimmingCharacters(in: .whitespacesAndNewlines)
                .split(whereSeparator: { $0.isWhitespace || $0 == "/" })
            if parts.count >= 2, parts[0] == Substring(key), let value = Int(parts[1]), value >= 0 {
                total += value
                found = true
            }
        }
        return found ? total : nil
    }

    static func litDetails(_ data: Data, fileSize: Int) -> [PakFormatDetail] {
        guard ascii(data, at: 0, length: 4) == "QLIT",
              let version = positiveInt32(data, at: 4),
              fileSize >= 8 else { return [] }
        let payload = fileSize - 8
        var details = [
            detail("Format", "Quake coloured lighting"),
            detail("Version", String(version)),
            detail("Data Size", "\(formatted(payload)) bytes"),
        ]
        if version == 1, payload % 3 == 0 {
            details.append(detail("Samples", formatted(payload / 3)))
        }
        return details
    }

    static func visDetails(_ data: Data, fileSize: Int) -> [PakFormatDetail] {
        var cursor = 0
        var maps: [String] = []
        var payloadBytes = 0
        while cursor <= data.count - 36 {
            let name = nullTerminatedText(data, at: cursor, length: 32)
            guard !name.isEmpty,
                  let payload = positiveInt32(data, at: cursor + 32),
                  cursor <= fileSize - 36 - payload else { return [] }
            maps.append(name)
            payloadBytes += payload
            cursor += 36 + payload
            if cursor > data.count { break }
        }
        guard !maps.isEmpty, cursor == fileSize else { return [] }
        return [
            detail("Format", "Quake external visibility patch"),
            detail("Maps", maps.count == 1 ? maps[0] : "\(formatted(maps.count)) maps"),
            detail("Visibility Data", "\(formatted(payloadBytes)) bytes"),
        ]
    }

    static func navDetails(_ data: Data, fileSize: Int) -> [PakFormatDetail] {
        guard ascii(data, at: 0, length: 4) == "NAV2", fileSize >= 8 else { return [] }
        return [
            detail("Format", "Quake bot navigation"),
            detail("Version", "NAV2"),
            detail("Data Size", "\(formatted(fileSize - 4)) bytes"),
        ]
    }

    static func ddsDetails(_ data: Data) -> [PakFormatDetail] {
        guard ascii(data, at: 0, length: 4) == "DDS ",
              int32LE(data, at: 4) == 124,
              let height = positiveInt32(data, at: 12),
              let width = positiveInt32(data, at: 16),
              dimensionsAreSafe(width, height),
              int32LE(data, at: 76) == 32 else { return [] }
        var details = [
            detail("Format", "DirectDraw Surface image"),
            detail("Dimensions", dimensions(width, height)),
        ]
        if let mipmaps = positiveInt32(data, at: 28) {
            details.append(detail("Mipmaps", formatted(mipmaps)))
        }
        let fourCC = ascii(data, at: 84, length: 4)
            .trimmingCharacters(in: CharacterSet(charactersIn: "\0 "))
        if !fourCC.isEmpty {
            if fourCC == "DX10", let dxgi = int32LE(data, at: 128) {
                details.append(detail("Compression", "DX10 (DXGI format \(dxgi))"))
            } else {
                details.append(detail("Compression", fourCC))
            }
        } else if let bitDepth = positiveInt32(data, at: 88) {
            details.append(detail("Color Depth", "\(bitDepth)-bit"))
        }
        return details
    }

    static func flacDetails(_ data: Data) -> [PakFormatDetail] {
        guard ascii(data, at: 0, length: 4) == "fLaC",
              data.count >= 42,
              let blockType = byte(data, at: 4),
              blockType & 0x7f == 0,
              uint24BE(data, at: 5) == 34,
              let packed = uint64BE(data, at: 18) else { return [] }
        let sampleRate = Int(packed >> 44 & 0xfffff)
        let channels = Int(packed >> 41 & 0x7) + 1
        let bitDepth = Int(packed >> 36 & 0x1f) + 1
        let samples = packed & 0xfffffffff
        guard sampleRate > 0 else { return [] }
        var details = [
            detail("Format", "FLAC audio"),
            detail("Sample Rate", "\(formatted(sampleRate)) Hz"),
            detail("Channels", channelDescription(channels)),
            detail("Bit Depth", "\(bitDepth)-bit"),
        ]
        if samples > 0 {
            details.append(detail("Duration", duration(Double(samples) / Double(sampleRate))))
        }
        addFlacComments(&details, data: data)
        return details
    }

    static func addFlacComments(_ details: inout [PakFormatDetail], data: Data) {
        var cursor = 4
        while cursor <= data.count - 4 {
            guard let header = byte(data, at: cursor), let length = uint24BE(data, at: cursor + 1) else {
                return
            }
            cursor += 4
            guard cursor <= data.count - length else { return }
            if header & 0x7f == 4 {
                var commentCursor = cursor
                addVorbisComments(&details, data: data, cursor: &commentCursor, limit: cursor + length)
                return
            }
            cursor += length
            if header & 0x80 != 0 { return }
        }
    }

    static func oggDetails(_ data: Data, fileSize: Int) -> [PakFormatDetail] {
        guard let packet = firstOggPacket(data) else { return [] }
        var details: [PakFormatDetail]
        var sampleRate: Int
        var preSkip = 0
        if packet.count >= 16,
           byte(packet, at: 0) == 1,
           ascii(packet, at: 1, length: 6) == "vorbis",
           let channels = byte(packet, at: 11),
           let rate = uint32LE(packet, at: 12),
           channels > 0,
           rate > 0 {
            sampleRate = Int(rate)
            details = [
                detail("Format", "Ogg Vorbis audio"),
                detail("Sample Rate", "\(formatted(sampleRate)) Hz"),
                detail("Channels", channelDescription(Int(channels))),
            ]
            addOggComments(&details, data: data, marker: Data([3]) + Data("vorbis".utf8))
        } else if packet.count >= 19,
                  ascii(packet, at: 0, length: 8) == "OpusHead",
                  let version = byte(packet, at: 8),
                  let channels = byte(packet, at: 9),
                  let skip = uint16LE(packet, at: 10),
                  channels > 0 {
            sampleRate = 48_000
            preSkip = Int(skip)
            details = [
                detail("Format", "Ogg Opus audio"),
                detail("Version", String(version)),
                detail("Sample Rate", "48,000 Hz"),
                detail("Channels", channelDescription(Int(channels))),
            ]
            addOggComments(&details, data: data, marker: Data("OpusTags".utf8))
        } else {
            return []
        }
        if data.count == fileSize,
           let granule = lastOggGranule(data),
           granule > UInt64(preSkip) {
            details.append(detail(
                "Duration",
                duration(Double(granule - UInt64(preSkip)) / Double(sampleRate))
            ))
        }
        return details
    }

    static func firstOggPacket(_ data: Data) -> Data? {
        guard ascii(data, at: 0, length: 4) == "OggS",
              data.count >= 28,
              let segmentCount = byte(data, at: 26),
              data.count >= 27 + Int(segmentCount) else { return nil }
        var size = 0
        for index in 0 ..< Int(segmentCount) {
            guard let segment = byte(data, at: 27 + index) else { return nil }
            size += Int(segment)
            if segment < 255 { break }
        }
        let body = 27 + Int(segmentCount)
        guard size > 0, body <= data.count - size else { return nil }
        return data.subdata(in: body ..< body + size)
    }

    static func lastOggGranule(_ data: Data) -> UInt64? {
        guard data.count >= 27 else { return nil }
        for cursor in stride(from: data.count - 27, through: 0, by: -1) {
            if ascii(data, at: cursor, length: 4) == "OggS",
               let granule = uint64LE(data, at: cursor + 6),
               granule != UInt64.max {
                return granule
            }
        }
        return nil
    }

    static func addOggComments(
        _ details: inout [PakFormatDetail],
        data: Data,
        marker: Data
    ) {
        guard let range = data.range(of: marker) else { return }
        var cursor = range.upperBound
        addVorbisComments(&details, data: data, cursor: &cursor, limit: data.count)
    }

    static func addVorbisComments(
        _ details: inout [PakFormatDetail],
        data: Data,
        cursor: inout Int,
        limit: Int
    ) {
        guard readLengthPrefixedText(data, cursor: &cursor, limit: limit) != nil,
              let count = uint32LE(data, at: cursor),
              count <= 10_000 else { return }
        cursor += 4
        for _ in 0 ..< Int(count) {
            guard let comment = readLengthPrefixedText(data, cursor: &cursor, limit: limit),
                  let separator = comment.firstIndex(of: "=") else { return }
            let key = comment[..<separator].uppercased()
            let value = comment[comment.index(after: separator)...]
                .trimmingCharacters(in: .whitespacesAndNewlines)
            let label = ["TITLE": "Title", "ARTIST": "Artist", "ALBUM": "Album"][key]
            if let label, !value.isEmpty, !details.contains(where: { $0.label == label }) {
                details.append(detail(label, value))
            }
        }
    }

    static func readLengthPrefixedText(
        _ data: Data,
        cursor: inout Int,
        limit: Int
    ) -> String? {
        guard let rawLength = uint32LE(data, at: cursor),
              rawLength <= Int.max,
              Int(rawLength) <= limit - cursor - 4 else { return nil }
        cursor += 4
        let length = Int(rawLength)
        let value = String(data: data.subdata(in: cursor ..< cursor + length), encoding: .utf8)
        cursor += length
        return value
    }

    static func xmDetails(_ data: Data) -> [PakFormatDetail] {
        guard ascii(data, at: 0, length: 17) == "Extended Module: ",
              data.count >= 80,
              byte(data, at: 37) == 0x1a,
              let version = uint16LE(data, at: 58) else { return [] }
        return trackerDetails(
            format: "FastTracker II module",
            title: nullTerminatedText(data, at: 17, length: 20),
            channels: Int(uint16LE(data, at: 68) ?? 0),
            orders: Int(uint16LE(data, at: 64) ?? 0),
            patterns: Int(uint16LE(data, at: 70) ?? 0),
            instruments: Int(uint16LE(data, at: 72) ?? 0),
            tempo: Int(uint16LE(data, at: 78) ?? 0),
            version: "\(version >> 8).\(String(format: "%02d", version & 0xff))",
            tracker: nullTerminatedText(data, at: 38, length: 20)
        )
    }

    static func s3mDetails(_ data: Data) -> [PakFormatDetail] {
        guard ascii(data, at: 44, length: 4) == "SCRM", data.count >= 96 else { return [] }
        let channels = (64 ..< 96).reduce(into: 0) { count, index in
            if let setting = byte(data, at: index), setting < 16 { count += 1 }
        }
        return trackerDetails(
            format: "Scream Tracker 3 module",
            title: nullTerminatedText(data, at: 0, length: 28),
            channels: channels,
            orders: Int(uint16LE(data, at: 32) ?? 0),
            patterns: Int(uint16LE(data, at: 36) ?? 0),
            instruments: Int(uint16LE(data, at: 34) ?? 0),
            tempo: Int(byte(data, at: 50) ?? 0)
        )
    }

    static func itDetails(_ data: Data) -> [PakFormatDetail] {
        guard ascii(data, at: 0, length: 4) == "IMPM",
              data.count >= 128,
              let version = uint16LE(data, at: 40) else { return [] }
        let channels = (64 ..< 128).reduce(into: 0) { count, index in
            if let setting = byte(data, at: index), setting < 128 { count += 1 }
        }
        return trackerDetails(
            format: "Impulse Tracker module",
            title: nullTerminatedText(data, at: 4, length: 26),
            channels: channels,
            orders: Int(uint16LE(data, at: 32) ?? 0),
            patterns: Int(uint16LE(data, at: 38) ?? 0),
            instruments: Int(uint16LE(data, at: 34) ?? 0),
            samples: Int(uint16LE(data, at: 36) ?? 0),
            tempo: Int(byte(data, at: 51) ?? 0),
            version: "\(version >> 8).\(String(format: "%02d", version & 0xff))"
        )
    }

    static func modDetails(_ data: Data) -> [PakFormatDetail] {
        guard data.count >= 1_084 else { return [] }
        let signature = ascii(data, at: 1_080, length: 4)
        let channels = modChannels(signature)
        guard channels > 0, let rawOrders = byte(data, at: 950) else { return [] }
        let orders = Int(rawOrders)
        var patterns = 0
        if orders > 0 {
            for index in 952 ..< min(1_080, 952 + orders) {
                patterns = max(patterns, Int(byte(data, at: index) ?? 0) + 1)
            }
        }
        return trackerDetails(
            format: "ProTracker module",
            title: nullTerminatedText(data, at: 0, length: 20),
            channels: channels,
            orders: orders,
            patterns: patterns,
            instruments: 31,
            tracker: signature
        )
    }

    static func umxDetails(_ data: Data) -> [PakFormatDetail] {
        guard uint32LE(data, at: 0) == 0x9e2a83c1, data.count >= 36 else { return [] }
        var details = [
            detail("Format", "Unreal music package"),
            detail("Version", String(uint16LE(data, at: 4) ?? 0)),
        ]
        if let names = nonnegativeInt32(data, at: 12) {
            details.append(detail("Names", formatted(names)))
        }
        if let exports = nonnegativeInt32(data, at: 20) {
            details.append(detail("Exports", formatted(exports)))
        }
        return details
    }

    static func trackerDetails(
        format: String,
        title: String,
        channels: Int,
        orders: Int,
        patterns: Int,
        instruments: Int,
        samples: Int? = nil,
        tempo: Int? = nil,
        version: String? = nil,
        tracker: String? = nil
    ) -> [PakFormatDetail] {
        var details = [detail("Format", format)]
        appendText(&details, label: "Title", value: title)
        appendText(&details, label: "Tracker", value: tracker ?? "")
        appendText(&details, label: "Version", value: version ?? "")
        if channels > 0 { details.append(detail("Channels", formatted(channels))) }
        if orders > 0 { details.append(detail("Orders", formatted(orders))) }
        if patterns > 0 { details.append(detail("Patterns", formatted(patterns))) }
        if instruments > 0 { details.append(detail("Instruments", formatted(instruments))) }
        if let samples, samples > 0 { details.append(detail("Samples", formatted(samples))) }
        if let tempo, tempo > 0 { details.append(detail("Tempo", "\(formatted(tempo)) BPM")) }
        return details
    }

    static func modChannels(_ signature: String) -> Int {
        if ["M.K.", "M!K!", "M&K!", "FLT4"].contains(signature) { return 4 }
        if ["OCTA", "CD81", "FLT8"].contains(signature) { return 8 }
        if signature.count == 4, signature.hasSuffix("CHN"),
           let first = signature.first?.wholeNumberValue { return first }
        if signature.count == 4, signature.hasSuffix("CH"),
           let channels = Int(signature.prefix(2)) { return channels }
        return 0
    }

    static func appendText(
        _ details: inout [PakFormatDetail],
        label: String,
        value: String
    ) {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmed.isEmpty { details.append(detail(label, trimmed)) }
    }

    static func nullTerminatedText(_ data: Data, at offset: Int, length: Int) -> String {
        guard offset >= 0, length >= 0, offset <= data.count - length else { return "" }
        var bytes = Array(data[offset ..< offset + length])
        if let end = bytes.firstIndex(of: 0) { bytes.removeSubrange(end...) }
        return String(bytes: bytes, encoding: .isoLatin1)?
            .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
    }

    static func uint24BE(_ data: Data, at offset: Int) -> Int? {
        guard let a = byte(data, at: offset),
              let b = byte(data, at: offset + 1),
              let c = byte(data, at: offset + 2) else { return nil }
        return Int(a) << 16 | Int(b) << 8 | Int(c)
    }

    static func uint64LE(_ data: Data, at offset: Int) -> UInt64? {
        guard offset >= 0, offset <= data.count - 8 else { return nil }
        return (0 ..< 8).reduce(UInt64(0)) {
            $0 | UInt64(data[offset + $1]) << UInt64($1 * 8)
        }
    }

    static func uint64BE(_ data: Data, at offset: Int) -> UInt64? {
        guard offset >= 0, offset <= data.count - 8 else { return nil }
        return (0 ..< 8).reduce(UInt64(0)) {
            $0 << 8 | UInt64(data[offset + $1])
        }
    }
}

enum PakFormatInspector {
    static let maximumInspectionBytes = 1 * 1_024 * 1_024

    /// Large BSPs can place their entity lump, including the worldspawn title, after
    /// the first megabyte even though their fixed header is at the start.
    static let maximumBspInspectionBytes = 4 * 1_024 * 1_024

    /// Demos are read frame by frame rather than sampled, so the whole recording has to be
    /// available for the duration and the closing scores to be right. Longer recordings than
    /// this still describe themselves, but report their length as a lower bound.
    static let maximumDemoInspectionBytes = 16 * 1_024 * 1_024

    /// Ogg duration is stored in the final page rather than the identification header.
    static let maximumAudioInspectionBytes = 16 * 1_024 * 1_024

    /// MD3 surface headers are chained through each surface's full payload.
    static let maximumModelInspectionBytes = 8 * 1_024 * 1_024

    private static let maximumListedPlayers = 8

    /// The frag count Quake parks in a player slot once that player disconnects.
    private static let vacatedSlotFrags = -99

    /// Demos earn a larger budget than the fixed headers every other format is read from.
    static func inspectionByteLimit(for fileName: String) -> Int {
        switch (fileName as NSString).pathExtension.lowercased() {
        case "bsp": return maximumBspInspectionBytes
        case "dem": return maximumDemoInspectionBytes
        case "md3": return maximumModelInspectionBytes
        case "ogg", "opus": return maximumAudioInspectionBytes
        default: return maximumInspectionBytes
        }
    }

    private static let textExtensions: Set<String> = [
        "arena", "cfg", "csv", "def", "ent", "fgd", "ini", "json", "loc", "log",
        "lst", "map", "md", "menu", "pts", "qc", "rc", "rtlights", "scr", "shader", "skin",
        "src", "txt", "xml", "yaml", "yml",
    ]

    static func details(fileName: String, data: Data?, fileSize: Int) -> [PakFormatDetail] {
        guard let data, !data.isEmpty else { return [] }

        let lowerName = fileName.lowercased()
        let ext = (lowerName as NSString).pathExtension
        var details = formatDetails(lowerName: lowerName, ext: ext, data: data, fileSize: fileSize)

        /* What the file is for, which its header often cannot say on its own. */
        if let purpose = purpose(lowerName: lowerName, ext: ext) {
            details.append(detail("Purpose", purpose))
        }
        return details
    }

    private static func formatDetails(
        lowerName: String,
        ext: String,
        data: Data,
        fileSize: Int
    ) -> [PakFormatDetail] {
        let leaf = (lowerName as NSString).lastPathComponent
        if leaf == "servers.json.bad" {
            return textDetails(extension: "json", data: data, fileSize: fileSize)
        }
        if leaf == "qw_maps.tmp" {
            return textDetails(extension: "txt", data: data, fileSize: fileSize)
        }
        switch ext {
        case "bsp":
            return bspDetails(data)
        case "dem":
            return demoDetails(data)
        case "mdl":
            let details = mdlDetails(data)
            return details.isEmpty ? detailsFromMagic(data) : details
        case "md3":
            return md3Details(data)
        case "md5", "md5mesh", "md5anim":
            return md5Details(data)
        case "spr":
            return spriteDetails(data)
        case "wad":
            return wadDetails(data)
        case "lit":
            return litDetails(data, fileSize: fileSize)
        case "vis":
            return visDetails(data, fileSize: fileSize)
        case "nav":
            return navDetails(data, fileSize: fileSize)
        case "lmp":
            return lmpDetails(fileName: lowerName, data: data, fileSize: fileSize)
        case "dds":
            return ddsDetails(data)
        case "pcx":
            return pcxDetails(data)
        case "tga":
            return tgaDetails(data)
        case "png":
            return pngDetails(data)
        case "jpg", "jpeg":
            return jpegDetails(data)
        case "gif":
            return gifDetails(data)
        case "bmp":
            return bitmapDetails(data)
        case "wav":
            return waveDetails(data)
        case "mp3":
            return mp3Details(data, fileSize: fileSize)
        case "flac":
            return flacDetails(data)
        case "ogg", "opus":
            return oggDetails(data, fileSize: fileSize)
        case "it":
            return itDetails(data)
        case "s3m":
            return s3mDetails(data)
        case "xm":
            return xmDetails(data)
        case "mod":
            return modDetails(data)
        case "umx":
            return umxDetails(data)
        case "sav":
            return savegameDetails(data)
        case "dat", "bin":
            /* Neither extension is exclusively Quake's, so both fall back to the magic. */
            let details = ext == "dat"
                ? (leaf == "iplog.dat"
                    ? ipLogDetails(data, fileSize: fileSize)
                    : quakeCProgramDetails(data))
                : dosTextScreenDetails(data)
            return details.isEmpty ? detailsFromMagic(data) : details
        default:
            if textExtensions.contains(ext) {
                return textDetails(extension: ext, data: data, fileSize: fileSize)
            }
            return detailsFromMagic(data)
        }
    }

    private static func savegameDetails(_ data: Data) -> [PakFormatDetail] {
        guard var text = String(data: data, encoding: .isoLatin1) else { return [] }
        text = text.replacingOccurrences(of: "\u{FEFF}", with: "")
            .replacingOccurrences(of: "\r", with: "")
        let lines = text.components(separatedBy: "\n")
        guard let version = lines.first.flatMap({ Int($0.trimmingCharacters(in: .whitespaces)) }),
              version == 5 || version == 6 else { return [] }

        var cursor = 1
        var gameDirectory: String?
        if version == 6 {
            guard cursor < lines.count else { return [] }
            gameDirectory = lines[cursor].trimmingCharacters(in: .whitespaces)
            cursor += 1
        }

        /* comment + 16 spawn parms + skill + map + elapsed time */
        guard lines.count - cursor >= 20 else { return [] }
        let comment = lines[cursor]
            .replacingOccurrences(of: "_", with: " ")
            .trimmingCharacters(in: .whitespaces)
        cursor += 17
        guard let skill = Int(lines[cursor].trimmingCharacters(in: .whitespaces)) else { return [] }
        cursor += 1
        let map = lines[cursor].trimmingCharacters(in: .whitespaces)
        cursor += 1
        guard !map.isEmpty,
              let elapsedTime = Double(lines[cursor].trimmingCharacters(in: .whitespaces)),
              elapsedTime.isFinite else { return [] }

        var details = [
            detail("Format", version == 6 ? "Quake remaster savegame" : "Quake savegame"),
            detail("Version", String(version)),
        ]
        if !comment.isEmpty {
            details.append(detail("Description", comment))
        }
        details.append(detail("Map", map))
        details.append(detail("Skill", skillName(skill)))
        details.append(detail("Duration", duration(elapsedTime)))
        if let gameDirectory, !gameDirectory.isEmpty {
            details.append(detail("Mod", gameDirectory))
        }
        return details
    }

    private static func skillName(_ skill: Int) -> String {
        switch skill {
        case 0: return "Easy"
        case 1: return "Normal"
        case 2: return "Hard"
        case 3: return "Nightmare"
        default: return "Skill \(skill)"
        }
    }

    private static func detailsFromMagic(_ data: Data) -> [PakFormatDetail] {
        if ascii(data, at: 0, length: 4) == "IDPO" { return mdlDetails(data) }
        if ascii(data, at: 0, length: 4) == "IDP3" { return md3Details(data) }
        if ascii(data, at: 0, length: 4) == "IDSP" { return spriteDetails(data) }
        if ["WAD2", "WAD3"].contains(ascii(data, at: 0, length: 4)) { return wadDetails(data) }
        if ascii(data, at: 0, length: 4) == "QLIT" { return litDetails(data, fileSize: data.count) }
        if ascii(data, at: 0, length: 4) == "NAV2" { return navDetails(data, fileSize: data.count) }
        if ascii(data, at: 0, length: 4) == "DDS " { return ddsDetails(data) }
        if ascii(data, at: 0, length: 4) == "fLaC" { return flacDetails(data) }
        if ascii(data, at: 0, length: 4) == "OggS" { return oggDetails(data, fileSize: data.count) }
        if data.starts(with: [137, 80, 78, 71, 13, 10, 26, 10]) { return pngDetails(data) }
        if data.starts(with: [0xff, 0xd8]) { return jpegDetails(data) }
        if ascii(data, at: 0, length: 3) == "GIF" { return gifDetails(data) }
        if ascii(data, at: 0, length: 2) == "BM" { return bitmapDetails(data) }
        if ascii(data, at: 0, length: 4) == "RIFF", ascii(data, at: 8, length: 4) == "WAVE" {
            return waveDetails(data)
        }
        return []
    }

    private static func bspDetails(_ data: Data) -> [PakFormatDetail] {
        guard let version = int32LE(data, at: 0) else { return [] }
        let magic = ascii(data, at: 0, length: 4)
        let format: String
        switch version {
        case 29: format = "Quake BSP level"
        case 30: format = "GoldSrc BSP level"
        case 23: format = "Quake 64 BSP level"
        default:
            if magic == "BSP2" {
                format = "Quake BSP2 level"
            } else if magic == "2PSB" {
                format = "Quake BSP2-RMQ level"
            } else {
                return []
            }
        }

        var details = [
            detail("Format", format),
            detail("Version", magic == "BSP2" || magic == "2PSB" ? magic : String(version)),
        ]

        if let description = bspWorldspawnMessage(data) {
            details.append(detail("Description", description))
        }
        if let vertices = bspLumpCount(data, index: 3, recordSize: 12) {
            details.append(detail("Vertices", formatted(vertices)))
        }
        if let faces = bspLumpCount(
            data,
            index: 7,
            recordSize: magic == "BSP2" || magic == "2PSB" ? 28 : 20
        ) {
            details.append(detail("Faces", formatted(faces)))
        }
        if let models = bspLumpCount(data, index: 14, recordSize: 64) {
            details.append(detail("Models", formatted(models)))
        }
        if let textureLump = bspLump(data, index: 2),
           let textures = int32LE(data, at: textureLump.offset),
           textures >= 0 {
            details.append(detail("Textures", formatted(textures)))
        }
        return details
    }

    private static func bspWorldspawnMessage(_ data: Data) -> String? {
        guard let entities = bspLump(data, index: 0),
              entities.offset <= data.count,
              entities.length <= data.count - entities.offset else { return nil }

        let bytes = data[entities.offset ..< entities.offset + entities.length]
        guard let text = String(bytes: bytes.map { $0 & 0x7f }, encoding: .isoLatin1),
              let worldspawnEnd = text.firstIndex(of: "}") else { return nil }

        let worldspawn = text[..<worldspawnEnd]
        let pattern = #""message"\s+"((?:\\.|[^"])*)""#
        guard let regex = try? NSRegularExpression(pattern: pattern),
              let match = regex.firstMatch(
                in: String(worldspawn),
                range: NSRange(worldspawn.startIndex..., in: worldspawn)
              ),
              let valueRange = Range(match.range(at: 1), in: worldspawn) else { return nil }

        let value = worldspawn[valueRange]
            .replacingOccurrences(of: #"\""#, with: #"""#)
            .replacingOccurrences(of: #"\\"#, with: #"\"#)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        return value.isEmpty ? nil : value
    }

    private static func demoDetails(_ data: Data) -> [PakFormatDetail] {
        if let demo = QuakeDemoInspector.inspect(data) {
            let described = describeDemo(demo)
            if described.count > 1 {
                return described
            }
        }
        return demoMapNames(data)
    }

    private static func describeDemo(_ demo: QuakeDemoSummary) -> [PakFormatDetail] {
        var details = [detail("Format", "Quake demo")]

        var maps: [String] = []
        var seenMaps = Set<String>()
        for segment in demo.segments where !segment.map.isEmpty {
            if seenMaps.insert(segment.map.lowercased()).inserted {
                maps.append(segment.map)
            }
        }
        if !maps.isEmpty {
            details.append(detail(maps.count == 1 ? "Map" : "Maps", maps.joined(separator: ", ")))
        }

        if let levelName = demo.segments.first(where: { !$0.levelName.isEmpty })?.levelName,
           levelName.lowercased() != maps.first?.lowercased() {
            details.append(detail("Level", levelName))
        }

        if demo.duration > 0 {
            let prefix = demo.truncated || !demo.messagesComplete ? "At least " : ""
            details.append(detail("Duration", prefix + duration(demo.duration)))
        }

        if demo.maxClients > 0 {
            let mode = demo.isSinglePlayer ? "Single player" : demo.isDeathmatch ? "Deathmatch" : "Cooperative"
            details.append(detail("Mode", mode))
        }

        if !demo.gameDir.isEmpty, demo.gameDir.lowercased() != "id1" {
            details.append(detail("Mod", demo.gameDir))
        }

        let players = demo.players.filter { !$0.name.trimmingCharacters(in: .whitespaces).isEmpty }
        if !players.isEmpty {
            let names = players.prefix(maximumListedPlayers).map {
                $0.name.trimmingCharacters(in: .whitespaces)
            }
            let overflow = players.count - names.count
            details.append(detail(
                players.count == 1 ? "Player" : "Players",
                names.joined(separator: ", ") + (overflow > 0 ? ", +\(overflow) more" : "")
            ))
        }

        // Frag counts only mean something once someone can score against someone else, and
        // slots left by players who disconnected keep their name but score -99.
        let scoring = players.filter { $0.frags != vacatedSlotFrags }
        if scoring.count > 1, scoring.contains(where: { $0.frags != 0 }) {
            let scores = scoring
                .sorted { $0.frags > $1.frags }
                .prefix(maximumListedPlayers)
                .map { "\($0.name.trimmingCharacters(in: .whitespaces)) \($0.frags)" }
            details.append(detail("Scores", scores.joined(separator: ", ")))
        }

        if !demo.protocolName.isEmpty, demo.protocolName != "unknown" {
            details.append(detail("Protocol", demo.protocolName))
        }

        return details
    }

    /// Falls back to spotting map paths in the raw bytes, which still describes demos this
    /// parser bails on and recordings wrapped in another container.
    private static func demoMapNames(_ data: Data) -> [PakFormatDetail] {
        // The scan builds a string of the whole buffer, so it keeps the ordinary header
        // budget rather than the larger one the frame walk gets.
        let scanned = data.prefix(maximumInspectionBytes)
        let text = String(bytes: scanned.map { $0 & 0x7f }, encoding: .isoLatin1) ?? ""
        let pattern = #"(?i)maps/([a-z0-9_+\-.]+)\.bsp"#
        guard let regex = try? NSRegularExpression(pattern: pattern) else { return [] }

        let excludedBrushModels: Set<String> = [
            "b_batt0", "b_batt1", "b_bh10", "b_bh100", "b_bh25",
            "b_lnail0", "b_lnail1", "b_mrock0", "b_mrock1", "b_nail0",
            "b_nail1", "b_plas0", "b_plas1", "b_rock0", "b_rock1",
            "b_shell0", "b_shell1",
        ]
        var maps: [String] = []
        var seen = Set<String>()

        for match in regex.matches(in: text, range: NSRange(text.startIndex..., in: text)) {
            guard let range = Range(match.range(at: 1), in: text) else { continue }
            let map = String(text[range])
            let key = map.lowercased()
            guard !excludedBrushModels.contains(key), seen.insert(key).inserted else { continue }
            maps.append(map)
        }

        guard !maps.isEmpty else { return [detail("Format", "Quake demo")] }
        return [
            detail("Format", "Quake demo"),
            detail(maps.count == 1 ? "Map" : "Maps", maps.joined(separator: ", ")),
        ]
    }

    static func summary(fileName: String, data: Data?, fileSize: Int) -> String {
        let details = details(fileName: fileName, data: data, fileSize: fileSize)
        guard !details.isEmpty else { return "" }

        let ext = (fileName.lowercased() as NSString).pathExtension
        if ext == "bsp" || ext == "bin",
           let description = details.first(where: { $0.label == "Description" }) {
            return "Description: \(description.value)"
        }
        let preferredLabels: [String]
        switch ext {
        case "bsp":
            preferredLabels = ["Vertices", "Faces"]
        case "dat":
            preferredLabels = ["Functions", "Entity Fields"]
        case "bin":
            preferredLabels = ["Screen Size"]
        case "dem":
            preferredLabels = ["Map", "Maps", "Duration"]
        case "sav":
            preferredLabels = ["Map", "Skill", "Duration"]
        case "mdl", "md3", "md5", "md5mesh", "md5anim", "spr":
            preferredLabels = [
                "Skin Size", "Canvas Size", "Frames", "Meshes", "Surfaces", "Triangles",
            ]
        case "wav", "mp3", "flac", "ogg", "opus", "it", "s3m", "xm", "mod", "umx":
            preferredLabels = ["Duration", "Channels", "Sample Rate", "Bit Rate", "Patterns"]
        case "lit":
            preferredLabels = ["Samples", "Version"]
        case "vis":
            preferredLabels = ["Maps", "Visibility Data"]
        case "nav":
            preferredLabels = ["Format", "Data Size"]
        case "dds":
            preferredLabels = ["Dimensions", "Compression", "Mipmaps"]
        case "wad":
            preferredLabels = ["Entries"]
        case "cfg", "csv", "def", "ent", "fgd", "ini", "json", "loc", "log", "lst", "map",
             "md", "menu", "pts", "qc", "rc", "rtlights", "scr", "shader", "skin",
             "src", "txt", "xml", "yaml", "yml":
            preferredLabels = ["Lines", "Encoding"]
        default:
            preferredLabels = ["Dimensions", "Canvas Size", "Color Depth", "Bit Depth", "Frames"]
        }

        let selected = preferredLabels.compactMap { label in
            details.first(where: { $0.label == label })
        }
        /* Purpose is a sentence, so it belongs in Get Info rather than in a column. */
        let visible = selected.isEmpty
            ? Array(
                details
                    .filter { $0.label != "Format" && $0.label != "Version" && $0.label != "Purpose" }
                    .prefix(2)
            )
            : Array(selected.prefix(2))

        if visible.isEmpty {
            return details.first(where: { $0.label == "Format" })?.value ?? ""
        }
        return visible.map { "\($0.label): \($0.value)" }.joined(separator: "  •  ")
    }

    static func detailsColumnSummary(fileName: String, data: Data?, fileSize: Int) -> String {
        let hiddenPrefixes = ["Duration:", "Description:", "Dimensions:"]
        return summary(fileName: fileName, data: data, fileSize: fileSize)
            .components(separatedBy: "  •  ")
            .map { part in
                guard let prefix = hiddenPrefixes.first(where: { part.hasPrefix($0) }) else {
                    return part
                }
                return part.dropFirst(prefix.count)
                    .trimmingCharacters(in: .whitespaces)
            }
            .joined(separator: "  •  ")
    }

    static func searchableText(fileName: String, data: Data?, fileSize: Int) -> String {
        details(fileName: fileName, data: data, fileSize: fileSize)
            .map(\.value)
            .joined(separator: " ")
    }

    private static func bspLump(_ data: Data, index: Int) -> (offset: Int, length: Int)? {
        let base = 4 + index * 8
        guard let offset = int32LE(data, at: base),
              let length = int32LE(data, at: base + 4),
              offset >= 0,
              length >= 0 else { return nil }
        return (offset, length)
    }

    private static func bspLumpCount(_ data: Data, index: Int, recordSize: Int) -> Int? {
        guard let lump = bspLump(data, index: index), lump.length % recordSize == 0 else { return nil }
        return lump.length / recordSize
    }

    private static func mdlDetails(_ data: Data) -> [PakFormatDetail] {
        guard ascii(data, at: 0, length: 4) == "IDPO",
              let version = int32LE(data, at: 4),
              let skins = nonnegativeInt32(data, at: 48),
              let width = positiveInt32(data, at: 52),
              let height = positiveInt32(data, at: 56),
              let vertices = nonnegativeInt32(data, at: 60),
              let triangles = nonnegativeInt32(data, at: 64),
              let frames = nonnegativeInt32(data, at: 68) else { return [] }

        return [
            detail("Format", "Quake alias model"),
            detail("Version", String(version)),
            detail("Skin Size", dimensions(width, height)),
            detail("Skins", formatted(skins)),
            detail("Vertices", formatted(vertices)),
            detail("Triangles", formatted(triangles)),
            detail("Frames", formatted(frames)),
        ]
    }

    /*
     * The header qcc writes at the front of a compiled QuakeC program. The CRC is of
     * the progdefs the code was built against, which is what an engine checks before
     * it agrees to run a mod.
     */
    private static func quakeCProgramDetails(_ data: Data) -> [PakFormatDetail] {
        guard let version = int32LE(data, at: 0), version == 6 || version == 7,
              let crc = nonnegativeInt32(data, at: 4),
              let statements = positiveInt32(data, at: 12),
              let functions = positiveInt32(data, at: 36),
              let stringBytes = nonnegativeInt32(data, at: 44),
              let entityFields = nonnegativeInt32(data, at: 56),
              let stringOffset = int32LE(data, at: 40), stringOffset >= 60 else { return [] }

        return [
            detail("Format", "Compiled QuakeC program"),
            detail("Version", version == 7 ? "7 (extended)" : String(version)),
            detail("Progdefs CRC", String(crc)),
            detail("Functions", formatted(functions)),
            detail("Statements", formatted(statements)),
            detail("Entity Fields", formatted(entityFields)),
            detail("String Data", "\(formatted(stringBytes)) bytes"),
        ]
    }

    /// ProQuake and QSS-M store one masked IPv4 prefix and a 16-byte player name per record.
    private static func ipLogDetails(_ data: Data, fileSize: Int) -> [PakFormatDetail] {
        let recordSize = 20
        guard fileSize >= recordSize, fileSize % recordSize == 0, data.count >= recordSize else {
            return []
        }
        return [
            detail("Format", "ProQuake IP log"),
            detail("Entries", formatted(fileSize / recordSize)),
        ]
    }

    /*
     * The 80 by 25 text-mode screens the DOS release printed as it exited, stored as a
     * character and a colour attribute per cell.
     */
    private static func dosTextScreenDetails(_ data: Data) -> [PakFormatDetail] {
        let columns = 80
        let rows = 25
        guard data.count == columns * rows * 2 else { return [] }

        var characters: [UInt8] = []
        characters.reserveCapacity(columns * rows)
        for index in stride(from: 0, to: data.count, by: 2) {
            characters.append(data[data.startIndex + index])
        }
        /* Junk that happens to be this long is ruled out by its character cells. */
        let plausible = characters.filter { $0 == 0 || $0 >= 0x20 }.count
        guard plausible * 100 >= characters.count * 95 else { return [] }

        var details = [
            detail("Format", "DOS text-mode screen"),
            detail("Screen Size", "\(columns) × \(rows) characters"),
        ]
        if let headline = dosTextScreenHeadline(characters, columns: columns, rows: rows) {
            details.append(detail("Description", headline))
        }
        return details
    }

    /// The first line with words on it, which on these screens is the title.
    private static func dosTextScreenHeadline(
        _ characters: [UInt8],
        columns: Int,
        rows: Int
    ) -> String? {
        for row in 0 ..< rows {
            let cells = characters[(row * columns) ..< ((row + 1) * columns)]
            let line = String(cells.map { $0 >= 0x20 && $0 < 0x7f ? Character(UnicodeScalar($0)) : " " })
                .trimmingCharacters(in: .whitespaces)
            let letters = line.filter { $0.isLetter }.count
            if letters >= 4, line.count >= 8 {
                return line
            }
        }
        return nil
    }

    private static func spriteDetails(_ data: Data) -> [PakFormatDetail] {
        guard ascii(data, at: 0, length: 4) == "IDSP",
              let version = int32LE(data, at: 4),
              let orientation = int32LE(data, at: 8),
              let width = positiveInt32(data, at: 16),
              let height = positiveInt32(data, at: 20),
              let frames = nonnegativeInt32(data, at: 24) else { return [] }

        let orientations = [
            0: "View parallel upright",
            1: "Facing upright",
            2: "View parallel",
            3: "Oriented",
            4: "View parallel oriented",
        ]
        return [
            detail("Format", "Quake sprite"),
            detail("Version", String(version)),
            detail("Canvas Size", dimensions(width, height)),
            detail("Frames", formatted(frames)),
            detail("Orientation", orientations[orientation] ?? "Type \(orientation)"),
        ]
    }

    private static func wadDetails(_ data: Data) -> [PakFormatDetail] {
        let magic = ascii(data, at: 0, length: 4)
        guard magic == "WAD2" || magic == "WAD3",
              let entries = nonnegativeInt32(data, at: 4) else { return [] }
        return [
            detail("Format", magic == "WAD2" ? "Quake WAD archive" : "GoldSrc WAD archive"),
            detail("Version", magic),
            detail("Entries", formatted(entries)),
        ]
    }

    private static func lmpDetails(fileName: String, data: Data, fileSize: Int) -> [PakFormatDetail] {
        let baseName = (fileName as NSString).lastPathComponent
        switch baseName {
        case "palette.lmp" where fileSize == 768:
            return [
                detail("Format", "Quake color palette"),
                detail("Colors", "256"),
                detail("Color Depth", "24-bit RGB"),
            ]
        case "colormap.lmp" where fileSize >= 16_384:
            return [
                detail("Format", "Quake color map"),
                detail("Dimensions", "256 × 64"),
                detail("Color Levels", "64"),
            ]
        case "conchars.lmp" where fileSize >= 16_384:
            return [
                detail("Format", "Quake console character sheet"),
                detail("Dimensions", "128 × 128"),
                detail("Color Depth", "8-bit indexed"),
            ]
        case "pop.lmp" where fileSize >= 256:
            return [
                detail("Format", "Quake indexed image"),
                detail("Dimensions", "16 × 16"),
                detail("Color Depth", "8-bit indexed"),
            ]
        default:
            guard let width = positiveInt32(data, at: 0),
                  let height = positiveInt32(data, at: 4),
                  dimensionsAreSafe(width, height),
                  width <= max(0, fileSize - 8) / height else {
                return [detail("Format", "Quake binary lump")]
            }
            return [
                detail("Format", "Quake indexed image"),
                detail("Dimensions", dimensions(width, height)),
                detail("Color Depth", "8-bit indexed"),
            ]
        }
    }

    private static func pcxDetails(_ data: Data) -> [PakFormatDetail] {
        guard byte(data, at: 0) == 0x0a,
              let xMin = uint16LE(data, at: 4),
              let yMin = uint16LE(data, at: 6),
              let xMax = uint16LE(data, at: 8),
              let yMax = uint16LE(data, at: 10),
              xMax >= xMin,
              yMax >= yMin else { return [] }

        let width = xMax - xMin + 1
        let height = yMax - yMin + 1
        let version = byte(data, at: 1) ?? 0
        let bitsPerPlane = byte(data, at: 3) ?? 0
        let planes = byte(data, at: 65) ?? 1
        let versionNames: [UInt8: String] = [0: "2.5", 2: "2.8", 3: "2.8", 5: "3.0"]

        return [
            detail("Format", "ZSoft PCX image"),
            detail("Version", versionNames[version] ?? String(version)),
            detail("Dimensions", dimensions(Int(width), Int(height))),
            detail("Color Depth", "\(Int(bitsPerPlane) * Int(planes))-bit (\(planes) plane\(planes == 1 ? "" : "s"))"),
            detail("Encoding", byte(data, at: 2) == 1 ? "Run-length encoded" : "Uncompressed"),
        ]
    }

    private static func tgaDetails(_ data: Data) -> [PakFormatDetail] {
        guard let imageType = byte(data, at: 2),
              let width = uint16LE(data, at: 12),
              let height = uint16LE(data, at: 14),
              width > 0,
              height > 0 else { return [] }

        let imageTypes: [UInt8: String] = [
            1: "Color-mapped", 2: "True-color", 3: "Grayscale",
            9: "RLE color-mapped", 10: "RLE true-color", 11: "RLE grayscale",
        ]
        return [
            detail("Format", "Truevision TGA image"),
            detail("Dimensions", dimensions(Int(width), Int(height))),
            detail("Color Depth", "\(byte(data, at: 16) ?? 0)-bit"),
            detail("Image Type", imageTypes[imageType] ?? "Type \(imageType)"),
        ]
    }

    private static func pngDetails(_ data: Data) -> [PakFormatDetail] {
        guard data.starts(with: [137, 80, 78, 71, 13, 10, 26, 10]),
              ascii(data, at: 12, length: 4) == "IHDR",
              let width = uint32BE(data, at: 16),
              let height = uint32BE(data, at: 20),
              width > 0,
              height > 0 else { return [] }

        let colorTypes: [UInt8: String] = [
            0: "Grayscale", 2: "RGB", 3: "Indexed color", 4: "Grayscale with alpha", 6: "RGBA",
        ]
        let colorType = byte(data, at: 25) ?? 255
        return [
            detail("Format", "PNG image"),
            detail("Dimensions", dimensions(Int(width), Int(height))),
            detail("Bit Depth", "\(byte(data, at: 24) ?? 0)-bit"),
            detail("Color Model", colorTypes[colorType] ?? "Type \(colorType)"),
            detail("Interlaced", byte(data, at: 28) == 1 ? "Yes" : "No"),
        ]
    }

    private static func jpegDetails(_ data: Data) -> [PakFormatDetail] {
        guard data.starts(with: [0xff, 0xd8]) else { return [] }
        var cursor = 2

        while cursor + 3 < data.count {
            guard byte(data, at: cursor) == 0xff else {
                cursor += 1
                continue
            }
            while cursor < data.count, byte(data, at: cursor) == 0xff { cursor += 1 }
            guard let marker = byte(data, at: cursor) else { break }
            cursor += 1

            let startOfFrameMarkers: Set<UInt8> = [
                0xc0, 0xc1, 0xc2, 0xc3, 0xc5, 0xc6, 0xc7,
                0xc9, 0xca, 0xcb, 0xcd, 0xce, 0xcf,
            ]
            if startOfFrameMarkers.contains(marker),
               let height = uint16BE(data, at: cursor + 3),
               let width = uint16BE(data, at: cursor + 5) {
                let encoding = marker == 0xc2 ? "Progressive" : "Sequential"
                return [
                    detail("Format", "JPEG image"),
                    detail("Dimensions", dimensions(Int(width), Int(height))),
                    detail("Components", String(byte(data, at: cursor + 7) ?? 0)),
                    detail("Precision", "\(byte(data, at: cursor + 2) ?? 0) bits per component"),
                    detail("Encoding", encoding),
                ]
            }

            if marker == 0xd8 || marker == 0xd9 || marker == 0x01 || (0xd0 ... 0xd7).contains(marker) {
                continue
            }
            guard let segmentLength = uint16BE(data, at: cursor), segmentLength >= 2 else { break }
            cursor += Int(segmentLength)
        }
        return [detail("Format", "JPEG image")]
    }

    private static func gifDetails(_ data: Data) -> [PakFormatDetail] {
        let signature = ascii(data, at: 0, length: 6)
        guard signature == "GIF87a" || signature == "GIF89a",
              let width = uint16LE(data, at: 6),
              let height = uint16LE(data, at: 8) else { return [] }
        let packed = byte(data, at: 10) ?? 0
        return [
            detail("Format", "GIF image"),
            detail("Version", String(signature.suffix(3))),
            detail("Canvas Size", dimensions(Int(width), Int(height))),
            detail("Color Depth", "\(Int((packed >> 4) & 0x07) + 1)-bit"),
            detail("Global Color Table", packed & 0x80 == 0 ? "No" : "Yes"),
        ]
    }

    private static func bitmapDetails(_ data: Data) -> [PakFormatDetail] {
        guard ascii(data, at: 0, length: 2) == "BM",
              let width = int32LE(data, at: 18),
              let rawHeight = int32LE(data, at: 22),
              width > 0,
              rawHeight != 0 else { return [] }
        let height = rawHeight == Int.min ? Int.max : abs(rawHeight)
        let compressionNames = [0: "Uncompressed", 1: "RLE 8-bit", 2: "RLE 4-bit", 3: "Bitfields"]
        let compression = int32LE(data, at: 30) ?? 0
        return [
            detail("Format", "Windows bitmap image"),
            detail("Dimensions", dimensions(width, height)),
            detail("Color Depth", "\(uint16LE(data, at: 28) ?? 0)-bit"),
            detail("Compression", compressionNames[compression] ?? "Type \(compression)"),
            detail("Row Order", rawHeight < 0 ? "Top to bottom" : "Bottom to top"),
        ]
    }

    private static func waveDetails(_ data: Data) -> [PakFormatDetail] {
        guard ascii(data, at: 0, length: 4) == "RIFF",
              ascii(data, at: 8, length: 4) == "WAVE" else { return [] }

        var cursor = 12
        var codec: Int?
        var channels: Int?
        var sampleRate: Int?
        var byteRate: Int?
        var bitsPerSample: Int?
        var audioDataSize: Int?

        while cursor + 8 <= data.count {
            let chunkID = ascii(data, at: cursor, length: 4)
            guard let chunkSizeValue = uint32LE(data, at: cursor + 4) else { break }
            let chunkSize = Int(chunkSizeValue)
            let payload = cursor + 8

            if chunkID == "fmt ", chunkSize >= 16 {
                codec = uint16LE(data, at: payload).map(Int.init)
                channels = uint16LE(data, at: payload + 2).map(Int.init)
                sampleRate = uint32LE(data, at: payload + 4).map(Int.init)
                byteRate = uint32LE(data, at: payload + 8).map(Int.init)
                bitsPerSample = uint16LE(data, at: payload + 14).map(Int.init)
            } else if chunkID == "data" {
                audioDataSize = chunkSize
            }

            let advance = chunkSize + (chunkSize % 2)
            guard advance <= data.count - payload else { break }
            cursor = payload + advance
        }

        let codecNames = [1: "Linear PCM", 3: "IEEE float", 6: "A-law", 7: "µ-law", 65_534: "Extensible"]
        var details = [detail("Format", "WAVE audio")]
        if let codec { details.append(detail("Encoding", codecNames[codec] ?? "Codec \(codec)")) }
        if let channels { details.append(detail("Channels", channelDescription(channels))) }
        if let sampleRate { details.append(detail("Sample Rate", "\(formatted(sampleRate)) Hz")) }
        if let bitsPerSample, bitsPerSample > 0 { details.append(detail("Bit Depth", "\(bitsPerSample)-bit")) }
        if let byteRate, byteRate > 0, let audioDataSize {
            details.append(detail("Duration", duration(Double(audioDataSize) / Double(byteRate))))
        }
        return details
    }

    private static func mp3Details(_ data: Data, fileSize: Int) -> [PakFormatDetail] {
        var details = [detail("Format", "MPEG audio layer III")]
        var cursor = 0

        if ascii(data, at: 0, length: 3) == "ID3", data.count >= 10 {
            let major = byte(data, at: 3) ?? 0
            let revision = byte(data, at: 4) ?? 0
            details.append(detail("ID3 Metadata", "Version 2.\(major).\(revision)"))
            if let tagSize = synchsafeInt32(data, at: 6) {
                cursor = min(data.count, 10 + tagSize)
            }
        }

        let searchEnd = min(data.count - 4, cursor + 256 * 1_024)
        guard searchEnd >= cursor else { return details }

        for offset in cursor ... searchEnd {
            guard let header = uint32BE(data, at: offset), header & 0xffe0_0000 == 0xffe0_0000 else { continue }
            let versionBits = Int((header >> 19) & 0x3)
            let layerBits = Int((header >> 17) & 0x3)
            let bitrateIndex = Int((header >> 12) & 0xf)
            let sampleRateIndex = Int((header >> 10) & 0x3)
            guard versionBits != 1,
                  layerBits == 1,
                  bitrateIndex > 0,
                  bitrateIndex < 15,
                  sampleRateIndex < 3 else { continue }

            let mpeg1Bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320]
            let mpeg2Bitrates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160]
            let baseSampleRates = [44_100, 48_000, 32_000]
            let bitrate = versionBits == 3 ? mpeg1Bitrates[bitrateIndex] : mpeg2Bitrates[bitrateIndex]
            let divisor = versionBits == 3 ? 1 : (versionBits == 2 ? 2 : 4)
            let sampleRate = baseSampleRates[sampleRateIndex] / divisor
            let channelMode = Int((header >> 6) & 0x3)

            details.append(detail("MPEG Version", versionBits == 3 ? "1" : (versionBits == 2 ? "2" : "2.5")))
            details.append(detail("Bit Rate", "\(bitrate) kbps"))
            details.append(detail("Sample Rate", "\(formatted(sampleRate)) Hz"))
            details.append(detail("Channels", channelMode == 3 ? "Mono" : "Stereo"))
            if bitrate > 0 {
                details.append(detail("Duration", duration(Double(fileSize * 8) / Double(bitrate * 1_000))))
            }
            break
        }
        return details
    }

    private static func textDetails(extension ext: String, data: Data, fileSize: Int) -> [PakFormatDetail] {
        let languages: [String: String] = [
            "cfg": "Quake configuration", "rc": "Quake console script",
            "src": "qcc source list", "loc": "QuakeWorld locations",
            "ent": "Quake entity definitions", "fgd": "Game definition",
            "lst": "Package load-order list", "map": "Quake map source",
            "pts": "Quake leak trail", "qc": "QuakeC source",
            "rtlights": "Quake real-time lights", "scr": "Quake console script",
            "shader": "Shader script", "skin": "Quake III skin mapping", "json": "JSON", "xml": "XML",
            "yaml": "YAML", "yml": "YAML", "csv": "CSV", "md": "Markdown",
        ]

        let encoding: String
        let text: String?
        if data.starts(with: [0xff, 0xfe]) {
            encoding = "UTF-16 little-endian"
            text = String(data: data, encoding: .utf16LittleEndian)
        } else if data.starts(with: [0xfe, 0xff]) {
            encoding = "UTF-16 big-endian"
            text = String(data: data, encoding: .utf16BigEndian)
        } else if let decoded = String(data: data, encoding: .utf8) {
            encoding = "UTF-8"
            text = decoded
        } else {
            encoding = "Legacy or binary text"
            text = String(data: data, encoding: .isoLatin1)
        }

        var details = [
            detail("Format", languages[ext] ?? "Plain text"),
            detail("Encoding", encoding),
        ]
        if let text {
            let newlineCount = text.reduce(into: 0) { count, character in
                if character == "\n" { count += 1 }
            }
            let lineCount = text.isEmpty ? 0 : newlineCount + (text.last == "\n" ? 0 : 1)
            let prefix = data.count < fileSize ? "At least " : ""
            details.append(detail("Lines", prefix + formatted(lineCount)))
        }
        return details
    }

    /*
     * What a file is for, in one line. Names come first, because a name can mean
     * something an extension cannot: palette.lmp is a palette, not just an image.
     * Formats that already describe themselves are left alone.
     */
    private static let purposesByName: [String: String] = [
        "progs.dat": "The compiled QuakeC program the engine runs: the rules, weapons, and monsters of the game or mod.",
        "qwprogs.dat": "The compiled QuakeC program a QuakeWorld server runs.",
        "spprogs.dat": "The compiled QuakeC program for single-player, where a mod ships a separate build.",
        "csprogs.dat": "Client-side QuakeC, run by the client for effects and HUD work the server cannot draw.",
        "menu.dat": "A QuakeC menu program, run by engines that replace the built-in menus.",
        "pak.lst": "The package load order QSS-M applies after the base game PAKs, with one PAK or PK3 name per entry.",
        "progs.src": "The list qcc compiles: the program to write first, then every QuakeC source file in order.",
        "quake.rc": "The startup script the engine runs at launch: it execs default.cfg, config.cfg, and autoexec.cfg, then starts the demo loop.",
        "default.cfg": "The bindings and settings the game ships with, exec'd before any saved configuration.",
        "config.cfg": "The bindings and settings the engine writes back when it quits.",
        "autoexec.cfg": "Commands run after the saved configuration, where a player keeps their own overrides.",
        "end1.bin": "The text screen the DOS release printed on exit from the shareware episode.",
        "end2.bin": "The text screen the DOS release printed on exit from the registered game.",
        "palette.lmp": "The 256 colours every paletted Quake image is drawn from.",
        "colormap.lmp": "The shading table the software renderer used to darken palette colours.",
        "pop.lmp": "The pattern QuakeWorld servers checked to tell a registered install from shareware.",
        "gfx.wad": "The 2D interface art: console font, status bar, and menu graphics.",
        "conchars.lmp": "The console character set, one 16 by 16 grid of glyphs.",
        "servers.json": "QSS-M's dated multiplayer server history, used by history menus and address completion.",
        "servers.json.bad": "An unreadable QSS-M server history preserved before a fresh servers.json was started.",
        "servers.txt": "The legacy QSS-M multiplayer server history, imported into servers.json.",
        "lastserver.txt": "The legacy record of the last multiplayer server used, imported into servers.json.",
        "server_hostnames.json": "QSS-M's cache of successfully resolved server hostnames and endpoints.",
        "bookmarks.json": "QSS-M's multiplayer server bookmarks, including their pinned order.",
        "bookmarks.txt": "The legacy QSS-M server bookmark list, imported into bookmarks.json.",
        "names.json": "QSS-M's dated player-name history.",
        "names.txt": "The legacy QSS-M player-name history, imported into names.json.",
        "demomarks.json": "QSS-M's saved timeline markers for recorded demos.",
        "mapdesc.json": "QSS-M's cache of map names and descriptions.",
        "shistory.json": "QSS-M's most recently used multiplayer host-game settings.",
        "demos_metadata_cache.json": "QSS-M's cache of metadata parsed for the demo browser.",
        "optional_download_cache.json": "QSS-M's retry cache for optional location-file downloads.",
        "skybox_download_cache.json": "QSS-M's retry cache for downloaded skybox faces.",
        "qw_maps.txt": "QSS-M's downloaded QuakeWorld map-name list for console completion.",
        "qw_maps.tmp": "A temporary QSS-M QuakeWorld map-list download awaiting validation.",
        "lastdemo.txt": "The name of the most recently recorded QSS-M demo.",
        "ghost.txt": "QSS-M's temporary multiplayer ghost code, retained across a restart or crash.",
        "name.txt": "QSS-M's temporary player-name backup, retained while the AFK name is active.",
        "iplog.dat": "The binary player IP-prefix and name history used by ProQuake-compatible commands.",
        "iplog.txt": "A readable export of the player IP-prefix and name history.",
    ]

    private static let purposesByExtension: [String: String] = [
        "rc": "A console script the engine execs at startup.",
        "cfg": "A console script of settings and bindings, exec'd by the engine.",
        "src": "A qcc source list naming the QuakeC files to compile, in order.",
        "qc": "QuakeC source, compiled into a progs program by qcc.",
        "lit": "External coloured lighting for the level of the same name.",
        "ent": "An external entity list, loaded in place of the one inside the level.",
        "loc": "Location names a QuakeWorld client reports with %l.",
        "sav": "A Quake savegame.",
        "skin": "A Quake III skin file, mapping each surface of a model to a texture.",
        "shader": "A Quake III shader script describing how a texture is drawn.",
        "fgd": "Game and entity definitions used by level editors.",
        "pts": "A point-by-point leak trail written by a map compiler.",
        "rtlights": "External real-time lights for the level of the same name.",
        "scr": "A console command script.",
        "vis": "External visibility and leaf data for one or more Quake levels.",
        "nav": "Bot navigation data for the level of the same name.",
    ]

    private static func purpose(lowerName: String, ext: String) -> String? {
        let leaf = (lowerName as NSString).lastPathComponent
        if let named = purposesByName[leaf] {
            return named
        }
        if leaf.range(
            of: #"^config-\d{2}-\d{2}-\d{4}\.cfg$"#,
            options: .regularExpression
        ) != nil {
            return "A dated backup of the effective QSS-M configuration."
        }
        return purposesByExtension[ext]
    }

    private static func detail(_ label: String, _ value: String) -> PakFormatDetail {
        PakFormatDetail(label: label, value: value)
    }

    private static func dimensions(_ width: Int, _ height: Int) -> String {
        "\(formatted(width)) × \(formatted(height)) pixels"
    }

    private static func dimensionsAreSafe(_ width: Int, _ height: Int) -> Bool {
        guard width > 0, height > 0, width <= 8_192, height <= 8_192 else { return false }
        let product = width.multipliedReportingOverflow(by: height)
        return !product.overflow && product.partialValue <= 16_777_216
    }

    private static func formatted(_ value: Int) -> String {
        NumberFormatter.localizedString(from: NSNumber(value: value), number: .decimal)
    }

    private static func channelDescription(_ channels: Int) -> String {
        switch channels {
        case 1: return "Mono"
        case 2: return "Stereo"
        default: return "\(channels) channels"
        }
    }

    private static func duration(_ seconds: Double) -> String {
        guard seconds.isFinite, seconds >= 0 else { return "Unknown" }
        let totalSeconds = Int(seconds.rounded())
        let hours = totalSeconds / 3_600
        let minutes = totalSeconds % 3_600 / 60
        let remainder = totalSeconds % 60
        return hours > 0
            ? String(format: "%d:%02d:%02d", hours, minutes, remainder)
            : String(format: "%d:%02d", minutes, remainder)
    }

    private static func ascii(_ data: Data, at offset: Int, length: Int) -> String {
        guard offset >= 0, length >= 0, offset <= data.count, length <= data.count - offset else { return "" }
        return String(bytes: data[offset ..< offset + length], encoding: .ascii) ?? ""
    }

    private static func byte(_ data: Data, at offset: Int) -> UInt8? {
        guard offset >= 0, offset < data.count else { return nil }
        return data[offset]
    }

    private static func uint16LE(_ data: Data, at offset: Int) -> UInt16? {
        guard let a = byte(data, at: offset), let b = byte(data, at: offset + 1) else { return nil }
        return UInt16(a) | UInt16(b) << 8
    }

    private static func uint16BE(_ data: Data, at offset: Int) -> UInt16? {
        guard let a = byte(data, at: offset), let b = byte(data, at: offset + 1) else { return nil }
        return UInt16(a) << 8 | UInt16(b)
    }

    private static func uint32LE(_ data: Data, at offset: Int) -> UInt32? {
        guard let a = byte(data, at: offset),
              let b = byte(data, at: offset + 1),
              let c = byte(data, at: offset + 2),
              let d = byte(data, at: offset + 3) else { return nil }
        return UInt32(a) | UInt32(b) << 8 | UInt32(c) << 16 | UInt32(d) << 24
    }

    private static func uint32BE(_ data: Data, at offset: Int) -> UInt32? {
        guard let a = byte(data, at: offset),
              let b = byte(data, at: offset + 1),
              let c = byte(data, at: offset + 2),
              let d = byte(data, at: offset + 3) else { return nil }
        return UInt32(a) << 24 | UInt32(b) << 16 | UInt32(c) << 8 | UInt32(d)
    }

    private static func int32LE(_ data: Data, at offset: Int) -> Int? {
        uint32LE(data, at: offset).map { Int(Int32(bitPattern: $0)) }
    }

    private static func positiveInt32(_ data: Data, at offset: Int) -> Int? {
        guard let value = int32LE(data, at: offset), value > 0 else { return nil }
        return value
    }

    private static func nonnegativeInt32(_ data: Data, at offset: Int) -> Int? {
        guard let value = int32LE(data, at: offset), value >= 0 else { return nil }
        return value
    }

    private static func synchsafeInt32(_ data: Data, at offset: Int) -> Int? {
        guard let a = byte(data, at: offset),
              let b = byte(data, at: offset + 1),
              let c = byte(data, at: offset + 2),
              let d = byte(data, at: offset + 3),
              a < 128, b < 128, c < 128, d < 128 else { return nil }
        return Int(a) << 21 | Int(b) << 14 | Int(c) << 7 | Int(d)
    }
}
