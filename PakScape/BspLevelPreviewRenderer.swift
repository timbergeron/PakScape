import AppKit
import CoreGraphics
import Foundation

enum BspLevelPreviewRenderer {
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
    private static let lumpPlanes = 1
    private static let lumpTextures = 2
    private static let lumpVertices = 3
    private static let lumpTexInfo = 6
    private static let lumpFaces = 7
    private static let lumpEdges = 12
    private static let lumpSurfEdges = 13
    private static let canvasSize = 256
    private static let canvasPadding: Double = 14
    private static let minVisibleNormalZ = 0.01
    private static let nodrawFlag = 0x800
    private static let supportedVersions: Set<Int> = [29, 30]

    static func renderImage(data: Data) -> NSImage? {
        guard let header = parseHeader(data) else { return nil }

        var faces = extractFaces(data, header: header)
        let vertices = extractVertices(data, header: header)
        let edges = extractEdges(data, header: header)
        let surfEdges = extractSurfEdges(data, header: header)
        let texInfo = extractTexInfo(data, header: header)
        let mipTextures = extractMipTextures(data, header: header)
        let planes = extractPlanes(data, header: header)

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

        return drawImage(renderableFaces, bounds: bounds)
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
              textureCount > 0 else {
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

    private static func drawImage(_ renderableFaces: [RenderableFace], bounds: Bounds) -> NSImage? {
        let imageSize = NSSize(width: canvasSize, height: canvasSize)
        let image = NSImage(size: imageSize)
        image.lockFocus()
        defer { image.unlockFocus() }

        guard let context = NSGraphicsContext.current?.cgContext else { return nil }

        let frame = CGRect(origin: .zero, size: CGSize(width: canvasSize, height: canvasSize))
        context.setShouldAntialias(true)
        context.setAllowsAntialiasing(true)
        context.setFillColor(NSColor(calibratedWhite: 0.965, alpha: 1).cgColor)
        context.fill(frame)

        context.setStrokeColor(NSColor(calibratedWhite: 0.78, alpha: 1).cgColor)
        context.setLineWidth(1)
        context.stroke(frame.insetBy(dx: 0.5, dy: 0.5))

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
        return image
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
