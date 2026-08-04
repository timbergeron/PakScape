import AppKit
import CoreGraphics
import Foundation

struct BspLevelPreviewOptions: Equatable {
    var showArmors = true
    var showMegaHealth = true
    var showPowerups = true
    var showMajorWeapons = true
    var showFlags = true

    static let all = BspLevelPreviewOptions()
    static let geometryOnly = BspLevelPreviewOptions(
        showArmors: false,
        showMegaHealth: false,
        showPowerups: false,
        showMajorWeapons: false,
        showFlags: false
    )

    /// The marker groups in the order they are offered in the preview controls.
    static let markerCategories: [WritableKeyPath<BspLevelPreviewOptions, Bool>] = [
        \.showArmors,
        \.showMegaHealth,
        \.showPowerups,
        \.showMajorWeapons,
        \.showFlags,
    ]

    static func showing(only category: WritableKeyPath<BspLevelPreviewOptions, Bool>) -> BspLevelPreviewOptions {
        var options = BspLevelPreviewOptions.geometryOnly
        options[keyPath: category] = true
        return options
    }
}

enum BspLevelPreviewRenderer {
    private static let maximumGeometryElementCount = 1_000_000
    private static let maximumTextureCount = 4_096
    private static let maximumEntityBytes = 4 * 1_024 * 1_024
    private static let maximumEntityCount = 100_000

    private struct Lump {
        let offset: Int
        let size: Int
    }

    private struct Header {
        let version: Int
        let lumps: [Lump]
    }

    private struct Vertex {
        let x: Double
        let y: Double
        let z: Double
    }

    private struct Plane {
        let normalX: Double
        let normalY: Double
        let normalZ: Double
    }

    private struct TexInfo {
        let miptexIndex: Int
        let flags: Int
    }

    private struct MipTexture {
        let name: String
        let averageColor: RGBColor
    }

    private struct Face {
        let planenum: Int
        let side: Int
        let firstEdge: Int
        let numEdges: Int
        let texinfo: Int
        var textureName: String?
        var textureColor: RGBColor?
    }

    private struct Bounds {
        let minX: Double
        let minY: Double
        let maxX: Double
        let maxY: Double
    }

    private struct RenderableFace {
        let vertices: [Vertex]
        let maxZ: Double
        let color: RGBColor
    }

    private struct MapMarker {
        let position: Vertex
        let label: String
        let color: RGBColor
    }

    private struct RGBColor {
        let r: Int
        let g: Int
        let b: Int

        func shaded(by factor: Double) -> RGBColor {
            RGBColor(
                r: Int((Double(r) * factor).rounded(.down)),
                g: Int((Double(g) * factor).rounded(.down)),
                b: Int((Double(b) * factor).rounded(.down))
            )
        }

        func nsColor(alpha: CGFloat = 1) -> NSColor {
            NSColor(
                calibratedRed: CGFloat(r) / 255,
                green: CGFloat(g) / 255,
                blue: CGFloat(b) / 255,
                alpha: alpha
            )
        }
    }

    private static let lumpCount = 15
    private static let lumpEntities = 0
    private static let lumpPlanes = 1
    private static let lumpTextures = 2
    private static let lumpVertices = 3
    private static let lumpTexInfo = 6
    private static let lumpFaces = 7
    private static let lumpEdges = 12
    private static let lumpSurfEdges = 13
    private static let canvasSize = 256
    private static let canvasPadding: Double = 14
    private static let maximumPixelScale = 8
    private static let minVisibleNormalZ = 0.01
    private static let nodrawFlag = 0x800
    private static let supportedVersions: Set<Int> = [29, 30]

    static func renderImage(
        data: Data,
        appearance: NSAppearance = NSApp.effectiveAppearance,
        options: BspLevelPreviewOptions = .all,
        pixelScale: Int = 1
    ) -> NSImage? {
        guard let header = parseHeader(data) else { return nil }

        var faces = extractFaces(data, header: header)
        let vertices = extractVertices(data, header: header)
        let edges = extractEdges(data, header: header)
        let surfEdges = extractSurfEdges(data, header: header)
        let texInfo = extractTexInfo(data, header: header)
        let mipTextures = extractMipTextures(data, header: header)
        let planes = extractPlanes(data, header: header)
        let markers = extractMapMarkers(data, header: header, options: options)

        guard !faces.isEmpty,
              !vertices.isEmpty,
              !edges.isEmpty,
              !surfEdges.isEmpty else {
            return nil
        }

        linkTextures(to: &faces, texInfo: texInfo, mipTextures: mipTextures)

        let renderableFaces = buildRenderableFaces(
            from: faces,
            vertices: vertices,
            edges: edges,
            surfEdges: surfEdges,
            planes: planes,
            texInfo: texInfo
        )
        guard !renderableFaces.isEmpty else { return nil }

        let bounds = calculateBounds(from: vertices)
        guard bounds.maxX > bounds.minX, bounds.maxY > bounds.minY else { return nil }

        return drawImage(
            renderableFaces,
            markers: markers,
            bounds: bounds,
            appearance: appearance,
            pixelScale: pixelScale
        )
    }

    /// Which marker groups the level actually places items for, so the preview
    /// can offer only the options that would change anything.
    static func availableMapMarkers(data: Data) -> BspLevelPreviewOptions {
        guard let header = parseHeader(data) else { return .geometryOnly }

        let lump = header.lumps[lumpEntities]
        guard lump.size > 0, lump.size <= maximumEntityBytes else { return .geometryOnly }

        var available = BspLevelPreviewOptions.geometryOnly
        for entity in parseEntities(data[lump.offset ..< lump.offset + lump.size]) {
            guard let origin = parseOrigin(entity["origin"]),
                  let className = entity["classname"]?.lowercased() else {
                continue
            }

            for category in BspLevelPreviewOptions.markerCategories
            where !available[keyPath: category] {
                if marker(
                    className: className,
                    entity: entity,
                    origin: origin,
                    options: .showing(only: category)
                ) != nil {
                    available[keyPath: category] = true
                }
            }

            if available == .all {
                break
            }
        }
        return available
    }

    private static func parseHeader(_ data: Data) -> Header? {
        let headerSize = 4 + lumpCount * 8
        guard data.count >= headerSize, let version = readInt32LE(data, offset: 0) else {
            return nil
        }
        guard supportedVersions.contains(version) else { return nil }

        var lumps: [Lump] = []
        lumps.reserveCapacity(lumpCount)

        for index in 0..<lumpCount {
            let base = 4 + index * 8
            guard let offset = readInt32LE(data, offset: base),
                  let size = readInt32LE(data, offset: base + 4),
                  offset >= 0,
                  size >= 0,
                  offset + size <= data.count else {
                return nil
            }

            lumps.append(Lump(offset: offset, size: size))
        }

        return Header(version: version, lumps: lumps)
    }

    private static func extractVertices(_ data: Data, header: Header) -> [Vertex] {
        let lump = header.lumps[lumpVertices]
        guard lump.size >= 12 else { return [] }

        let count = lump.size / 12
        guard count <= maximumGeometryElementCount else { return [] }
        var vertices: [Vertex] = []
        vertices.reserveCapacity(count)

        for index in 0..<count {
            let base = lump.offset + index * 12
            guard let x = readFloat32LE(data, offset: base),
                  let y = readFloat32LE(data, offset: base + 4),
                  let z = readFloat32LE(data, offset: base + 8),
                  x.isFinite, y.isFinite, z.isFinite else {
                continue
            }

            vertices.append(Vertex(x: Double(x), y: Double(y), z: Double(z)))
        }

        return vertices
    }

    private static func extractEdges(_ data: Data, header: Header) -> [(Int, Int)] {
        let lump = header.lumps[lumpEdges]
        guard lump.size >= 4 else { return [] }

        let count = lump.size / 4
        guard count <= maximumGeometryElementCount else { return [] }
        var edges: [(Int, Int)] = []
        edges.reserveCapacity(count)

        for index in 0..<count {
            let base = lump.offset + index * 4
            guard let start = readUInt16LE(data, offset: base),
                  let end = readUInt16LE(data, offset: base + 2) else {
                continue
            }

            edges.append((Int(start), Int(end)))
        }

        return edges
    }

    private static func extractSurfEdges(_ data: Data, header: Header) -> [Int] {
        let lump = header.lumps[lumpSurfEdges]
        guard lump.size >= 4 else { return [] }

        let count = lump.size / 4
        guard count <= maximumGeometryElementCount else { return [] }
        var surfEdges: [Int] = []
        surfEdges.reserveCapacity(count)

        for index in 0..<count {
            let base = lump.offset + index * 4
            if let surfEdge = readInt32LE(data, offset: base) {
                surfEdges.append(surfEdge)
            }
        }

        return surfEdges
    }

    private static func extractFaces(_ data: Data, header: Header) -> [Face] {
        let lump = header.lumps[lumpFaces]
        guard lump.size >= 20 else { return [] }

        let count = lump.size / 20
        guard count <= maximumGeometryElementCount else { return [] }
        var faces: [Face] = []
        faces.reserveCapacity(count)

        for index in 0..<count {
            let base = lump.offset + index * 20
            guard let planenum = readUInt16LE(data, offset: base),
                  let side = readUInt16LE(data, offset: base + 2),
                  let firstEdge = readInt32LE(data, offset: base + 4),
                  let numEdges = readInt16LE(data, offset: base + 8),
                  let texinfo = readInt16LE(data, offset: base + 10) else {
                continue
            }

            faces.append(
                Face(
                    planenum: Int(planenum),
                    side: Int(side),
                    firstEdge: firstEdge,
                    numEdges: Int(numEdges),
                    texinfo: Int(texinfo),
                    textureName: nil,
                    textureColor: nil
                )
            )
        }

        return faces
    }

    private static func extractTexInfo(_ data: Data, header: Header) -> [TexInfo] {
        let lump = header.lumps[lumpTexInfo]
        guard lump.size >= 40 else { return [] }

        let count = lump.size / 40
        guard count <= maximumGeometryElementCount else { return [] }
        var texInfo: [TexInfo] = []
        texInfo.reserveCapacity(count)

        for index in 0..<count {
            let base = lump.offset + index * 40
            guard let miptexIndex = readInt32LE(data, offset: base + 32),
                  let flags = readInt32LE(data, offset: base + 36) else {
                continue
            }

            texInfo.append(TexInfo(miptexIndex: miptexIndex, flags: flags))
        }

        return texInfo
    }

    private static func extractPlanes(_ data: Data, header: Header) -> [Plane] {
        let lump = header.lumps[lumpPlanes]
        guard lump.size >= 20 else { return [] }

        let count = lump.size / 20
        guard count <= maximumGeometryElementCount else { return [] }
        var planes: [Plane] = []
        planes.reserveCapacity(count)

        for index in 0..<count {
            let base = lump.offset + index * 20
            guard let normalX = readFloat32LE(data, offset: base),
                  let normalY = readFloat32LE(data, offset: base + 4),
                  let normalZ = readFloat32LE(data, offset: base + 8) else {
                continue
            }

            planes.append(
                Plane(
                    normalX: Double(normalX),
                    normalY: Double(normalY),
                    normalZ: Double(normalZ)
                )
            )
        }

        return planes
    }

    private static func extractMipTextures(_ data: Data, header: Header) -> [MipTexture?] {
        let lump = header.lumps[lumpTextures]
        guard lump.size >= 4,
              let textureCount = readInt32LE(data, offset: lump.offset),
              textureCount > 0,
              textureCount <= maximumTextureCount,
              textureCount <= (lump.size - 4) / 4 else {
            return []
        }

        let palette = QuakePalette.bytes
        var textures: [MipTexture?] = []
        textures.reserveCapacity(textureCount)

        for index in 0..<textureCount {
            let offsetBase = lump.offset + 4 + index * 4
            guard let relativeOffset = readInt32LE(data, offset: offsetBase) else {
                textures.append(nil)
                continue
            }

            if relativeOffset <= 0 {
                textures.append(nil)
                continue
            }

            let textureBase = lump.offset + relativeOffset
            guard textureBase + 40 <= data.count,
                  textureBase + 40 <= lump.offset + lump.size else {
                textures.append(nil)
                continue
            }

            let name = asciiString(data, offset: textureBase, length: 16)
            guard let width = readUInt32LE(data, offset: textureBase + 16),
                  let height = readUInt32LE(data, offset: textureBase + 20),
                  let mip0Offset = readUInt32LE(data, offset: textureBase + 24),
                  width > 0,
                  height > 0,
                  width <= 512,
                  height <= 512,
                  mip0Offset > 0 else {
                textures.append(MipTexture(name: name, averageColor: generateColorFromHash(simpleHash(name))))
                continue
            }

            let pixelCount = Int(width) * Int(height)
            let pixelDataOffset = textureBase + Int(mip0Offset)
            guard pixelDataOffset >= textureBase,
                  pixelDataOffset + pixelCount <= data.count,
                  pixelDataOffset + pixelCount <= lump.offset + lump.size else {
                textures.append(MipTexture(name: name, averageColor: generateColorFromHash(simpleHash(name))))
                continue
            }

            let averageColor = calculateTextureAverageColor(
                data,
                offset: pixelDataOffset,
                pixelCount: pixelCount,
                palette: palette
            )
            textures.append(MipTexture(name: name, averageColor: averageColor))
        }

        return textures
    }

    private static func linkTextures(to faces: inout [Face], texInfo: [TexInfo], mipTextures: [MipTexture?]) {
        for index in faces.indices {
            let texInfoIndex = faces[index].texinfo
            guard texInfo.indices.contains(texInfoIndex) else { continue }

            let miptexIndex = texInfo[texInfoIndex].miptexIndex
            guard mipTextures.indices.contains(miptexIndex),
                  let mipTexture = mipTextures[miptexIndex] else {
                continue
            }

            faces[index].textureName = mipTexture.name
            faces[index].textureColor = mipTexture.averageColor
        }
    }

    private static func buildRenderableFaces(
        from faces: [Face],
        vertices: [Vertex],
        edges: [(Int, Int)],
        surfEdges: [Int],
        planes: [Plane],
        texInfo: [TexInfo]
    ) -> [RenderableFace] {
        var renderableFaces: [RenderableFace] = []
        renderableFaces.reserveCapacity(faces.count)

        for face in faces {
            guard face.numEdges >= 3 else { continue }
            guard isFaceVisibleFromTop(face, vertices: vertices, edges: edges, surfEdges: surfEdges, planes: planes) else {
                continue
            }
            guard !shouldSkipFace(face, texInfo: texInfo) else { continue }

            let faceVertices = polygonVertices(for: face, vertices: vertices, edges: edges, surfEdges: surfEdges)
            guard faceVertices.count >= 3 else { continue }

            let maxZ = faceVertices.reduce(-Double.infinity) { max($0, $1.z) }
            renderableFaces.append(
                RenderableFace(
                    vertices: faceVertices,
                    maxZ: maxZ,
                    color: baseColor(for: face, texInfo: texInfo)
                )
            )
        }

        guard !renderableFaces.isEmpty else { return [] }

        renderableFaces.sort { $0.maxZ < $1.maxZ }
        return renderableFaces
    }

    private static func polygonVertices(
        for face: Face,
        vertices: [Vertex],
        edges: [(Int, Int)],
        surfEdges: [Int]
    ) -> [Vertex] {
        var polygon: [Vertex] = []
        polygon.reserveCapacity(face.numEdges)

        for edgeOffset in 0..<face.numEdges {
            let surfEdgeIndex = face.firstEdge + edgeOffset
            guard surfEdges.indices.contains(surfEdgeIndex) else { continue }

            let surfEdge = surfEdges[surfEdgeIndex]
            let edgeIndex = abs(surfEdge)
            guard edges.indices.contains(edgeIndex) else { continue }

            let edge = edges[edgeIndex]
            let vertexIndex = surfEdge >= 0 ? edge.0 : edge.1
            guard vertices.indices.contains(vertexIndex) else { continue }

            let vertex = vertices[vertexIndex]
            if let last = polygon.last,
               abs(last.x - vertex.x) < 0.001,
               abs(last.y - vertex.y) < 0.001,
               abs(last.z - vertex.z) < 0.001 {
                continue
            }

            polygon.append(vertex)
        }

        if polygon.count >= 2,
           let first = polygon.first,
           let last = polygon.last,
           abs(first.x - last.x) < 0.001,
           abs(first.y - last.y) < 0.001,
           abs(first.z - last.z) < 0.001 {
            polygon.removeLast()
        }

        return polygon
    }

    private static func isFaceVisibleFromTop(
        _ face: Face,
        vertices: [Vertex],
        edges: [(Int, Int)],
        surfEdges: [Int],
        planes: [Plane]
    ) -> Bool {
        if planes.indices.contains(face.planenum) {
            let plane = planes[face.planenum]
            let normalZ = face.side == 0 ? plane.normalZ : -plane.normalZ
            return normalZ > minVisibleNormalZ
        }

        guard let normal = calculateFaceNormal(face, vertices: vertices, edges: edges, surfEdges: surfEdges) else {
            return false
        }

        return normal.normalZ > minVisibleNormalZ
    }

    private static func calculateFaceNormal(
        _ face: Face,
        vertices: [Vertex],
        edges: [(Int, Int)],
        surfEdges: [Int]
    ) -> Plane? {
        let faceVertices = polygonVertices(for: face, vertices: vertices, edges: edges, surfEdges: surfEdges)
        guard faceVertices.count >= 3 else { return nil }

        let a = faceVertices[0]
        let b = faceVertices[1]
        let c = faceVertices[2]

        let v1x = b.x - a.x
        let v1y = b.y - a.y
        let v1z = b.z - a.z
        let v2x = c.x - a.x
        let v2y = c.y - a.y
        let v2z = c.z - a.z

        let normalX = v1y * v2z - v1z * v2y
        let normalY = v1z * v2x - v1x * v2z
        let normalZ = v1x * v2y - v1y * v2x
        let length = sqrt(normalX * normalX + normalY * normalY + normalZ * normalZ)
        guard length > 0 else { return nil }

        return Plane(normalX: normalX / length, normalY: normalY / length, normalZ: normalZ / length)
    }

    private static func shouldSkipFace(_ face: Face, texInfo: [TexInfo]) -> Bool {
        if texInfo.indices.contains(face.texinfo), (texInfo[face.texinfo].flags & nodrawFlag) != 0 {
            return true
        }

        guard let textureName = face.textureName?.lowercased() else { return false }
        return textureName == "trigger" || textureName.contains("nodraw")
    }

    private static func calculateBounds(from vertices: [Vertex]) -> Bounds {
        var minX = Double.infinity
        var minY = Double.infinity
        var maxX = -Double.infinity
        var maxY = -Double.infinity

        for vertex in vertices {
            minX = min(minX, vertex.x)
            minY = min(minY, vertex.y)
            maxX = max(maxX, vertex.x)
            maxY = max(maxY, vertex.y)
        }

        return Bounds(minX: minX, minY: minY, maxX: maxX, maxY: maxY)
    }

    private static func extractMapMarkers(
        _ data: Data,
        header: Header,
        options: BspLevelPreviewOptions
    ) -> [MapMarker] {
        let lump = header.lumps[lumpEntities]
        guard lump.size > 0, lump.size <= maximumEntityBytes else { return [] }

        let entities = parseEntities(data[lump.offset ..< lump.offset + lump.size])
        var markers: [MapMarker] = []
        markers.reserveCapacity(min(entities.count, 64))

        for entity in entities {
            guard let origin = parseOrigin(entity["origin"]),
                  let className = entity["classname"]?.lowercased(),
                  let marker = marker(
                    className: className,
                    entity: entity,
                    origin: origin,
                    options: options
                  ) else {
                continue
            }
            markers.append(marker)
        }
        return markers
    }

    private static func parseEntities(_ bytes: Data.SubSequence) -> [[String: String]] {
        let source = Array(bytes.prefix { $0 != 0 })
        var entities: [[String: String]] = []
        var index = 0

        func skipWhitespace() {
            while index < source.count, source[index] == 9 || source[index] == 10 ||
                    source[index] == 13 || source[index] == 32 {
                index += 1
            }
        }

        func quotedString() -> String? {
            skipWhitespace()
            guard index < source.count, source[index] == 34 else { return nil }
            index += 1
            var value: [UInt8] = []
            while index < source.count {
                let byte = source[index]
                index += 1
                if byte == 34 {
                    return String(bytes: value, encoding: .utf8)
                }
                if byte == 92, index < source.count {
                    value.append(source[index])
                    index += 1
                } else {
                    value.append(byte)
                }
            }
            return nil
        }

        while index < source.count, entities.count < maximumEntityCount {
            skipWhitespace()
            guard index < source.count else { break }
            guard source[index] == 123 else {
                index += 1
                continue
            }
            index += 1
            var entity: [String: String] = [:]

            while index < source.count {
                skipWhitespace()
                if index < source.count, source[index] == 125 {
                    index += 1
                    break
                }
                guard let key = quotedString(), let value = quotedString() else {
                    while index < source.count, source[index] != 125 { index += 1 }
                    if index < source.count { index += 1 }
                    break
                }
                entity[key.lowercased()] = value
            }
            if !entity.isEmpty {
                entities.append(entity)
            }
        }
        return entities
    }

    private static func parseOrigin(_ value: String?) -> Vertex? {
        guard let value else { return nil }
        let parts = value.split(whereSeparator: \.isWhitespace)
        guard parts.count >= 3,
              let x = Double(parts[0]),
              let y = Double(parts[1]),
              let z = Double(parts[2]),
              x.isFinite, y.isFinite, z.isFinite else {
            return nil
        }
        return Vertex(x: x, y: y, z: z)
    }

    private static func marker(
        className: String,
        entity: [String: String],
        origin: Vertex,
        options: BspLevelPreviewOptions
    ) -> MapMarker? {
        if options.showArmors {
            switch className {
            case "item_armor1", "item_armor", "item_armorgreen", "item_armor_green":
                return MapMarker(position: origin, label: "GA", color: RGBColor(r: 35, g: 174, b: 74))
            case "item_armor2", "item_armoryellow", "item_armor_yellow", "item_suit":
                return MapMarker(position: origin, label: "YA", color: RGBColor(r: 225, g: 190, b: 38))
            case "item_armor3", "item_armorred", "item_armor_red", "item_armourred", "item_armorinv":
                return MapMarker(position: origin, label: "RA", color: RGBColor(r: 205, g: 52, b: 52))
            default:
                break
            }
        }

        if options.showMegaHealth,
           className == "item_megahealth" ||
            (className == "item_health" && (Int(entity["spawnflags"] ?? "0") ?? 0) & 2 != 0) {
            return MapMarker(position: origin, label: "MH", color: RGBColor(r: 61, g: 192, b: 205))
        }

        if options.showPowerups {
            switch className {
            case "item_artifact_super_damage", "item_quad":
                return MapMarker(position: origin, label: "Q", color: RGBColor(r: 74, g: 111, b: 230))
            case "item_artifact_invisibility", "item_ring":
                return MapMarker(position: origin, label: "R", color: RGBColor(r: 164, g: 88, b: 212))
            case "item_artifact_invulnerability", "item_pent":
                return MapMarker(position: origin, label: "P", color: RGBColor(r: 208, g: 49, b: 49))
            default:
                break
            }
        }

        if options.showMajorWeapons {
            switch className {
            case "weapon_rocketlauncher":
                return MapMarker(position: origin, label: "RL", color: RGBColor(r: 240, g: 119, b: 32))
            case "weapon_lightning", "weapon_thunderbolt":
                return MapMarker(position: origin, label: "LG", color: RGBColor(r: 221, g: 230, b: 241))
            default:
                break
            }
        }

        if options.showFlags {
            if className == "item_flag_team1" ||
                (className.contains("flag") &&
                    (className.contains("red") || entity["team"]?.lowercased() == "red" || entity["team"] == "1")) {
                return MapMarker(position: origin, label: "RF", color: RGBColor(r: 210, g: 45, b: 45))
            }
            if className == "item_flag_team2" ||
                (className.contains("flag") &&
                    (className.contains("blue") || entity["team"]?.lowercased() == "blue" || entity["team"] == "2")) {
                return MapMarker(position: origin, label: "BF", color: RGBColor(r: 54, g: 103, b: 218))
            }
        }
        return nil
    }

    private static func drawImage(
        _ renderableFaces: [RenderableFace],
        markers: [MapMarker],
        bounds: Bounds,
        appearance: NSAppearance,
        pixelScale: Int
    ) -> NSImage? {
        let imageSize = NSSize(width: canvasSize, height: canvasSize)
        let scaleFactor = max(1, min(pixelScale, maximumPixelScale))
        let pixels = canvasSize * scaleFactor
        guard let representation = NSBitmapImageRep(
            bitmapDataPlanes: nil,
            pixelsWide: pixels,
            pixelsHigh: pixels,
            bitsPerSample: 8,
            samplesPerPixel: 4,
            hasAlpha: true,
            isPlanar: false,
            colorSpaceName: .deviceRGB,
            bytesPerRow: 0,
            bitsPerPixel: 0
        ) else { return nil }

        let image = NSImage(size: imageSize)
        image.addRepresentation(representation)

        guard let graphicsContext = NSGraphicsContext(bitmapImageRep: representation) else {
            return nil
        }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = graphicsContext
        defer { NSGraphicsContext.restoreGraphicsState() }

        let context = graphicsContext.cgContext
        // Draw in canvas points regardless of pixel scale so line widths, marker
        // badges and label fonts keep the same proportions at every resolution.
        context.scaleBy(x: CGFloat(scaleFactor), y: CGFloat(scaleFactor))

        let frame = CGRect(origin: .zero, size: CGSize(width: canvasSize, height: canvasSize))
        context.setShouldAntialias(true)
        context.setAllowsAntialiasing(true)
        appearance.performAsCurrentDrawingAppearance {
            context.setFillColor(NSColor.windowBackgroundColor.cgColor)
            context.fill(frame)

            context.setStrokeColor(NSColor.separatorColor.cgColor)
            context.setLineWidth(1)
            context.stroke(frame.insetBy(dx: 0.5, dy: 0.5))
        }

        let mapWidth = max(100, bounds.maxX - bounds.minX)
        let mapHeight = max(100, bounds.maxY - bounds.minY)
        let scale = min(
            (Double(canvasSize) - (canvasPadding * 2)) / mapWidth,
            (Double(canvasSize) - (canvasPadding * 2)) / mapHeight
        )
        let offsetX = (Double(canvasSize) - mapWidth * scale) / 2
        let offsetY = (Double(canvasSize) - mapHeight * scale) / 2

        let lowZ = renderableFaces.reduce(Double.infinity) { min($0, $1.vertices.reduce(Double.infinity) { min($0, $1.z) }) }
        let highZ = renderableFaces.reduce(-Double.infinity) { max($0, $1.maxZ) }
        let zRange = max(highZ - lowZ, 1)

        func transform(_ vertex: Vertex) -> CGPoint {
            let x = ((vertex.x - bounds.minX) * scale) + offsetX
            let y = Double(canvasSize) - (((vertex.y - bounds.minY) * scale) + offsetY)
            return CGPoint(x: x, y: y)
        }

        for renderableFace in renderableFaces {
            guard renderableFace.vertices.count >= 3 else { continue }

            let path = CGMutablePath()
            path.move(to: transform(renderableFace.vertices[0]))
            for vertex in renderableFace.vertices.dropFirst() {
                path.addLine(to: transform(vertex))
            }
            path.closeSubpath()

            let shade = 0.6 + (((renderableFace.maxZ - lowZ) / zRange) * 0.4)
            let color = renderableFace.color.shaded(by: shade)

            context.addPath(path)
            context.setFillColor(color.nsColor(alpha: 0.88).cgColor)
            context.setStrokeColor(NSColor(calibratedWhite: 0.22, alpha: 0.3).cgColor)
            context.setLineWidth(0.75)
            context.drawPath(using: .fillStroke)
        }

        for marker in markers {
            let point = transform(marker.position)
            guard frame.insetBy(dx: 3, dy: 3).contains(point) else { continue }
            drawMarker(marker, at: point, in: context)
        }

        representation.size = imageSize
        return image
    }

    private static func drawMarker(_ marker: MapMarker, at point: CGPoint, in context: CGContext) {
        let radius: CGFloat = marker.label.count == 1 ? 5.5 : 7
        let badge = CGRect(
            x: point.x - radius,
            y: point.y - radius,
            width: radius * 2,
            height: radius * 2
        )
        context.saveGState()
        context.setShadow(offset: CGSize(width: 0, height: -1), blur: 2, color: NSColor.black.withAlphaComponent(0.65).cgColor)
        context.setFillColor(marker.color.nsColor(alpha: 0.96).cgColor)
        context.fillEllipse(in: badge)
        context.restoreGState()

        context.setStrokeColor(NSColor.black.withAlphaComponent(0.8).cgColor)
        context.setLineWidth(1)
        context.strokeEllipse(in: badge.insetBy(dx: 0.5, dy: 0.5))

        let paragraph = NSMutableParagraphStyle()
        paragraph.alignment = .center
        let attributes: [NSAttributedString.Key: Any] = [
            .font: NSFont.systemFont(ofSize: marker.label.count == 1 ? 7 : 6, weight: .bold),
            .foregroundColor: NSColor.white,
            .paragraphStyle: paragraph,
        ]
        let textRect = CGRect(
            x: badge.minX,
            y: badge.midY - 4,
            width: badge.width,
            height: 9
        )
        (marker.label as NSString).draw(in: textRect, withAttributes: attributes)
    }

    private static func baseColor(for face: Face, texInfo: [TexInfo]) -> RGBColor {
        if let textureColor = face.textureColor {
            return textureColor
        }

        if let textureName = face.textureName {
            let lowerName = textureName.lowercased()
            if lowerName.contains("sky") {
                return generateSkyColor(textureName)
            }
            if lowerName.contains("lava") {
                return RGBColor(r: 219, g: 127, b: 59)
            }
            if lowerName.contains("slime") {
                return RGBColor(r: 124, g: 252, b: 0)
            }
            if lowerName.contains("water") {
                return RGBColor(r: 30, g: 144, b: 255)
            }
            return generateColorFromHash(simpleHash(textureName))
        }

        if texInfo.indices.contains(face.texinfo) {
            let flags = texInfo[face.texinfo].flags
            if (flags & 1) != 0 {
                return RGBColor(r: 80, g: 130, b: 230)
            }
            if (flags & 2) != 0 {
                return RGBColor(r: 124, g: 252, b: 0)
            }
            if (flags & 4) != 0 {
                return RGBColor(r: 30, g: 144, b: 255)
            }
        }

        return RGBColor(r: 255, g: 255, b: 255)
    }

    private static func calculateTextureAverageColor(
        _ data: Data,
        offset: Int,
        pixelCount: Int,
        palette: [UInt8]
    ) -> RGBColor {
        let maxSamples = 1000
        let sampleRate = max(1, pixelCount / maxSamples)

        var totalR = 0
        var totalG = 0
        var totalB = 0
        var sampleCount = 0

        for index in stride(from: 0, to: pixelCount, by: sampleRate) {
            let paletteIndex = Int(data[offset + index])
            if paletteIndex == 255 {
                continue
            }

            let paletteOffset = paletteIndex * 3
            guard paletteOffset + 2 < palette.count else { continue }

            totalR += Int(palette[paletteOffset])
            totalG += Int(palette[paletteOffset + 1])
            totalB += Int(palette[paletteOffset + 2])
            sampleCount += 1
        }

        guard sampleCount > 0 else {
            return RGBColor(r: 128, g: 128, b: 128)
        }

        return RGBColor(
            r: totalR / sampleCount,
            g: totalG / sampleCount,
            b: totalB / sampleCount
        )
    }

    private static func generateColorFromHash(_ hash: Int) -> RGBColor {
        let baseColors: [RGBColor] = [
            RGBColor(r: 220, g: 200, b: 180),
            RGBColor(r: 200, g: 180, b: 160),
            RGBColor(r: 210, g: 190, b: 150),
            RGBColor(r: 180, g: 160, b: 140),
            RGBColor(r: 200, g: 170, b: 140),
            RGBColor(r: 170, g: 180, b: 190),
            RGBColor(r: 190, g: 190, b: 170),
            RGBColor(r: 170, g: 160, b: 150)
        ]

        let baseColor = baseColors[hash % baseColors.count]
        let variation = 20

        func clamp(_ value: Int) -> Int {
            min(255, max(0, value))
        }

        return RGBColor(
            r: clamp(baseColor.r + ((hash >> 8) % variation) - (variation / 2)),
            g: clamp(baseColor.g + ((hash >> 16) % variation) - (variation / 2)),
            b: clamp(baseColor.b + ((hash >> 24) % variation) - (variation / 2))
        )
    }

    private static func generateSkyColor(_ textureName: String) -> RGBColor {
        let lowerName = textureName.lowercased()
        if lowerName.contains("red") {
            return RGBColor(r: 190, g: 65, b: 60)
        }
        if lowerName.contains("green") {
            return RGBColor(r: 70, g: 170, b: 90)
        }
        if lowerName.contains("purple") || lowerName.contains("violet") {
            return RGBColor(r: 150, g: 85, b: 205)
        }
        if lowerName.contains("yellow") || lowerName.contains("gold") {
            return RGBColor(r: 218, g: 165, b: 32)
        }
        if lowerName.contains("night") || lowerName.contains("black") {
            return RGBColor(r: 35, g: 45, b: 110)
        }
        return RGBColor(r: 80, g: 130, b: 230)
    }

    private static func simpleHash(_ string: String) -> Int {
        var hash = 0
        for scalar in string.unicodeScalars {
            hash = ((hash << 5) &- hash) &+ Int(scalar.value)
        }
        return abs(hash)
    }

    private static func asciiString(_ data: Data, offset: Int, length: Int) -> String {
        guard offset >= 0, offset + length <= data.count else { return "" }
        let bytes = data[offset ..< offset + length]
        let trimmed = bytes.prefix { $0 != 0 }
        return String(bytes: trimmed, encoding: .ascii) ?? ""
    }

    private static func readUInt16LE(_ data: Data, offset: Int) -> UInt16? {
        guard offset >= 0, offset + 2 <= data.count else { return nil }
        return UInt16(data[offset]) | (UInt16(data[offset + 1]) << 8)
    }

    private static func readInt16LE(_ data: Data, offset: Int) -> Int16? {
        guard let value = readUInt16LE(data, offset: offset) else { return nil }
        return Int16(bitPattern: value)
    }

    private static func readUInt32LE(_ data: Data, offset: Int) -> UInt32? {
        guard offset >= 0, offset + 4 <= data.count else { return nil }
        return UInt32(data[offset])
            | (UInt32(data[offset + 1]) << 8)
            | (UInt32(data[offset + 2]) << 16)
            | (UInt32(data[offset + 3]) << 24)
    }

    private static func readInt32LE(_ data: Data, offset: Int) -> Int? {
        guard let value = readUInt32LE(data, offset: offset) else { return nil }
        return Int(Int32(bitPattern: value))
    }

    private static func readFloat32LE(_ data: Data, offset: Int) -> Float32? {
        guard let bits = readUInt32LE(data, offset: offset) else { return nil }
        return Float32(bitPattern: bits)
    }
}
