import Foundation

struct PakFormatDetail: Identifiable, Equatable {
    let label: String
    let value: String

    var id: String { label }
}

enum PakFormatInspector {
    static let maximumInspectionBytes = 1 * 1_024 * 1_024

    private static let textExtensions: Set<String> = [
        "arena", "cfg", "csv", "def", "ent", "ini", "json", "log", "map", "md",
        "menu", "qc", "rc", "shader", "txt", "xml", "yaml", "yml",
    ]

    static func details(fileName: String, data: Data?, fileSize: Int) -> [PakFormatDetail] {
        guard let data, !data.isEmpty else { return [] }

        let lowerName = fileName.lowercased()
        let ext = (lowerName as NSString).pathExtension

        switch ext {
        case "bsp":
            return bspDetails(data)
        case "mdl":
            return mdlDetails(data)
        case "spr":
            return spriteDetails(data)
        case "wad":
            return wadDetails(data)
        case "lmp":
            return lmpDetails(fileName: lowerName, data: data, fileSize: fileSize)
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
        default:
            if textExtensions.contains(ext) {
                return textDetails(extension: ext, data: data, fileSize: fileSize)
            }
            return detailsFromMagic(data)
        }
    }

    private static func detailsFromMagic(_ data: Data) -> [PakFormatDetail] {
        if ascii(data, at: 0, length: 4) == "IDPO" { return mdlDetails(data) }
        if ascii(data, at: 0, length: 4) == "IDSP" { return spriteDetails(data) }
        if ["WAD2", "WAD3"].contains(ascii(data, at: 0, length: 4)) { return wadDetails(data) }
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
        guard let version = int32LE(data, at: 0), version == 29 || version == 30 else { return [] }

        var details = [
            detail("Format", version == 29 ? "Quake BSP level" : "GoldSrc BSP level"),
            detail("Version", String(version)),
        ]

        if let vertices = bspLumpCount(data, index: 3, recordSize: 12) {
            details.append(detail("Vertices", formatted(vertices)))
        }
        if let faces = bspLumpCount(data, index: 7, recordSize: 20) {
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
            "cfg": "Quake configuration", "ent": "Quake entity definitions", "map": "Quake map source",
            "qc": "QuakeC source", "shader": "Shader script", "json": "JSON", "xml": "XML",
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
