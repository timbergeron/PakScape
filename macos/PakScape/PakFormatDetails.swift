import Foundation

struct PakFormatDetail: Identifiable, Equatable {
    let label: String
    let value: String

    var id: String { label }
}

enum PakFormatInspector {
    static let maximumInspectionBytes = 1 * 1_024 * 1_024

    /// Demos are read frame by frame rather than sampled, so the whole recording has to be
    /// available for the duration and the closing scores to be right. Longer recordings than
    /// this still describe themselves, but report their length as a lower bound.
    static let maximumDemoInspectionBytes = 16 * 1_024 * 1_024

    private static let maximumListedPlayers = 8

    /// The frag count Quake parks in a player slot once that player disconnects.
    private static let vacatedSlotFrags = -99

    /// Demos earn a larger budget than the fixed headers every other format is read from.
    static func inspectionByteLimit(for fileName: String) -> Int {
        (fileName as NSString).pathExtension.lowercased() == "dem"
            ? maximumDemoInspectionBytes
            : maximumInspectionBytes
    }

    private static let textExtensions: Set<String> = [
        "arena", "cfg", "csv", "def", "ent", "ini", "json", "loc", "log", "map",
        "md", "menu", "qc", "rc", "shader", "src", "txt", "xml", "yaml", "yml",
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
        switch ext {
        case "bsp":
            return bspDetails(data)
        case "dem":
            return demoDetails(data)
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
        case "dat", "bin":
            /* Neither extension is exclusively Quake's, so both fall back to the magic. */
            let details = ext == "dat" ? quakeCProgramDetails(data) : dosTextScreenDetails(data)
            return details.isEmpty ? detailsFromMagic(data) : details
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

        if let description = bspWorldspawnMessage(data) {
            details.append(detail("Description", description))
        }
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
        case "mdl", "spr":
            preferredLabels = ["Skin Size", "Canvas Size", "Frames"]
        case "wav", "mp3":
            preferredLabels = ["Duration", "Channels", "Sample Rate", "Bit Rate"]
        case "wad":
            preferredLabels = ["Entries"]
        case "cfg", "csv", "def", "ent", "ini", "json", "loc", "log", "map", "md",
             "menu", "qc", "rc", "shader", "src", "txt", "xml", "yaml", "yml":
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
            .filter { part in
                !hiddenPrefixes.contains(where: { part.hasPrefix($0) })
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
            "ent": "Quake entity definitions", "map": "Quake map source",
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
    ]

    private static func purpose(lowerName: String, ext: String) -> String? {
        let leaf = (lowerName as NSString).lastPathComponent
        if let named = purposesByName[leaf] {
            return named
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
