import AppKit
import Foundation

enum QuakeModelFormat: Int32 {
    case unknown = 0
    case mdl = 1
    case md3 = 2
    case md5 = 3
    case spr = 4
    case bsp = 5

    var displayName: String {
        switch self {
        case .mdl: return "Quake MDL"
        case .md3: return "Quake III MD3"
        case .md5: return "Doom 3 MD5"
        case .spr: return "Quake sprite"
        case .bsp: return "Quake brush model"
        case .unknown: return "Model"
        }
    }
}

enum QuakeModelNudge: Int32 {
    case left = 0
    case right = 1
    case up = 2
    case down = 3
    case zoomIn = 4
    case zoomOut = 5
}

enum QuakeModelViewerError: LocalizedError {
    case unavailable(String)

    var errorDescription: String? {
        switch self {
        case .unavailable(let message):
            return message
        }
    }
}

/// The model formats QSS-M loads. Sprites join them as an animated billboard, and
/// BSP covers the brush models Quake stores its ammo boxes and health kits as.
enum QuakeModelFormats {
    static let extensions: Set<String> = [
        "mdl", "md3", "md5mesh", "md5", "spr", "spr32", "bsp",
    ]

    static func supports(fileExtension: String) -> Bool {
        extensions.contains(fileExtension.lowercased())
    }

    /// A `.bsp` holds either a brush model or a playable level. Only the first belongs
    /// in the viewer, and the file's own content decides which it is: the `b_` prefix
    /// is an id1 habit that mods do not keep.
    static func isBrushModel(data: Data) -> Bool {
        data.withUnsafeBytes { bytes in
            pkm_bsp_is_brush_model(bytes.baseAddress, bytes.count) != 0
        }
    }
}

/// Finds the skins an MD3 or MD5 mesh names inside the open archive. Shader paths
/// are written against the game directory, use either slash, and often name an
/// extension the archive does not carry, so every plausible spelling is tried.
struct QuakeModelTextureResolver {
    private static let imageExtensions = ["tga", "png", "jpg", "jpeg", "pcx", "lmp", "bmp"]

    let files: [String: PakNode]
    let folderPath: String
    let baseName: String
    let loadData: (PakNode) -> Data?
    let decodeImage: (String, Data) -> NSImage?

    /// Indexes an archive by lowercased path so lookups do not walk the tree.
    static func archiveFiles(root: PakNode) -> [String: PakNode] {
        var files: [String: PakNode] = [:]
        var stack: [(node: PakNode, path: String)] = [(root, "")]

        while let current = stack.popLast() {
            for child in current.node.children ?? [] {
                let path = current.path.isEmpty ? child.name : current.path + "/" + child.name
                if child.isFolder {
                    stack.append((child, path))
                } else {
                    files[path.lowercased()] = child
                }
            }
        }
        return files
    }

    /// Reads a Quake III skin file that remaps surfaces to textures.
    func skinOverrides() -> [String: String] {
        var overrides: [String: String] = [:]
        for index in 0 ..< 4 {
            let path = combine(folderPath, "\(baseName)_\(index).skin")
            guard let node = files[path.lowercased()],
                  let data = loadData(node),
                  let text = String(data: data, encoding: .utf8) else {
                break
            }

            for line in text.split(separator: "\n", omittingEmptySubsequences: false) {
                guard let separator = line.firstIndex(of: ",") else { continue }
                let surface = line[line.startIndex ..< separator]
                    .trimmingCharacters(in: .whitespaces)
                let texture = line[line.index(after: separator) ..< line.endIndex]
                    .trimmingCharacters(in: .whitespacesAndNewlines)
                if !surface.isEmpty, !texture.isEmpty {
                    overrides[surface.lowercased()] = texture
                }
            }
        }
        return overrides
    }

    func image(for requestedName: String) -> NSImage? {
        let normalized = normalize(requestedName)
        guard !normalized.isEmpty else { return nil }

        for candidate in candidates(for: normalized) {
            guard let node = files[candidate.lowercased()],
                  let data = loadData(node),
                  !data.isEmpty,
                  let image = decodeImage(node.name, data) else {
                continue
            }
            return image
        }
        return nil
    }

    private func candidates(for normalized: String) -> [String] {
        let withoutExtension = stripImageExtension(normalized)
        let leaf = (withoutExtension as NSString).lastPathComponent

        var candidates = spellings(exact: normalized, withoutExtension: withoutExtension)
        if !folderPath.isEmpty {
            candidates += spellings(
                exact: combine(folderPath, normalized),
                withoutExtension: combine(folderPath, withoutExtension)
            )
            let inFolder = combine(folderPath, leaf)
            candidates += spellings(exact: inFolder, withoutExtension: inFolder)
        }
        return candidates
    }

    private func spellings(exact: String, withoutExtension: String) -> [String] {
        [exact]
            + Self.imageExtensions.map { withoutExtension + "." + $0 }
            // MD5 exporters commonly save the first mesh/material image with
            // this suffix while leaving the shader name itself unsuffixed.
            + Self.imageExtensions.map { withoutExtension + "_00_00." + $0 }
    }

    private func stripImageExtension(_ path: String) -> String {
        let pathExtension = (path as NSString).pathExtension.lowercased()
        guard !pathExtension.isEmpty, Self.imageExtensions.contains(pathExtension) else {
            return path
        }
        return (path as NSString).deletingPathExtension
    }

    private func normalize(_ path: String) -> String {
        path.replacingOccurrences(of: "\\", with: "/")
            .split(separator: "/", omittingEmptySubsequences: true)
            .joined(separator: "/")
    }

    private func combine(_ folder: String, _ name: String) -> String {
        folder.isEmpty ? name : folder + "/" + name
    }
}

/// Owns one parsed model plus its orbit camera. The view renders BGRA rows that
/// Core Graphics can wrap without any conversion.
final class QuakeModelViewer {
    private let model: OpaquePointer
    private let view: OpaquePointer
    private var statistics = pkm_model_stats()
    private let missingTextureCount: Int
    private(set) var skinIndex = 0

    init(data: Data, fileExtension: String, resolver: QuakeModelTextureResolver?) throws {
        var errorBuffer = [CChar](repeating: 0, count: 512)
        let handle = errorBuffer.withUnsafeMutableBufferPointer { errorBytes in
            data.withUnsafeBytes { bytes in
                fileExtension.withCString { extensionBytes in
                    pkm_model_create(
                        bytes.baseAddress,
                        bytes.count,
                        extensionBytes,
                        errorBytes.baseAddress,
                        errorBytes.count
                    )
                }
            }
        }

        guard let handle else {
            let message = String(cString: errorBuffer)
            throw QuakeModelViewerError.unavailable(
                message.isEmpty ? "The model could not be read." : message
            )
        }

        guard let viewHandle = pkm_view_create(handle) else {
            pkm_model_destroy(handle)
            throw QuakeModelViewerError.unavailable("The model viewer could not be started.")
        }

        model = handle
        view = viewHandle
        missingTextureCount = Self.uploadTextures(model: handle, resolver: resolver)
        pkm_model_get_stats(handle, &statistics)
    }

    deinit {
        pkm_view_destroy(view)
        pkm_model_destroy(model)
    }

    private static func uploadTextures(
        model: OpaquePointer,
        resolver: QuakeModelTextureResolver?
    ) -> Int {
        let requestCount = Int(pkm_model_texture_request_count(model))
        guard requestCount > 0 else { return 0 }
        guard let resolver else { return requestCount }

        let overrides = resolver.skinOverrides()
        var missing = 0

        for index in 0 ..< requestCount {
            var surfaceBuffer = [CChar](repeating: 0, count: 512)
            var nameBuffer = [CChar](repeating: 0, count: 512)
            _ = surfaceBuffer.withUnsafeMutableBufferPointer { buffer in
                pkm_model_texture_request_surface(
                    model,
                    Int32(index),
                    buffer.baseAddress,
                    buffer.count
                )
            }
            _ = nameBuffer.withUnsafeMutableBufferPointer { buffer in
                pkm_model_texture_request_name(model, Int32(index), buffer.baseAddress, buffer.count)
            }

            let surface = String(cString: surfaceBuffer)
            let requested = overrides[surface.lowercased()] ?? String(cString: nameBuffer)

            guard let image = resolver.image(for: requested),
                  let texture = rgbaPixels(from: image) else {
                missing += 1
                continue
            }

            let uploaded = texture.pixels.withUnsafeBytes { bytes in
                pkm_model_set_texture(
                    model,
                    Int32(index),
                    bytes.baseAddress,
                    Int32(texture.width),
                    Int32(texture.height)
                )
            }
            if uploaded != 0 {
                missing += 1
            }
        }
        return missing
    }

    private static func rgbaPixels(
        from image: NSImage
    ) -> (pixels: [UInt8], width: Int, height: Int)? {
        guard let cgImage = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else {
            return nil
        }

        let width = cgImage.width
        let height = cgImage.height
        guard width > 0, height > 0, width <= 8192, height <= 8192 else { return nil }

        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        let success = pixels.withUnsafeMutableBytes { buffer -> Bool in
            guard let context = CGContext(
                data: buffer.baseAddress,
                width: width,
                height: height,
                bitsPerComponent: 8,
                bytesPerRow: width * 4,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
            ) else {
                return false
            }
            context.draw(cgImage, in: CGRect(x: 0, y: 0, width: width, height: height))
            return true
        }

        return success ? (pixels, width, height) : nil
    }

    var format: QuakeModelFormat {
        QuakeModelFormat(rawValue: statistics.format) ?? .unknown
    }

    var skinCount: Int { Int(statistics.skin_count) }
    var frameCount: Int { Int(statistics.frame_count) }

    var showsInteractionPrompt: Bool {
        pkm_view_show_interaction_prompt(view) != 0
    }

    /// A short description of what is on screen, for the panel subtitle.
    var statusLine: String {
        var parts = [format.displayName]

        /* A sprite is one quad, so its frame count is the only count worth showing. */
        if format == .spr {
            let frames = Int(statistics.frame_count)
            parts.append("\(frames.formatted()) \(frames == 1 ? "frame" : "frames")")
            return parts.joined(separator: " • ")
        }

        let triangles = Int(statistics.triangle_count)
        parts.append("\(triangles.formatted()) \(triangles == 1 ? "triangle" : "triangles")")
        if statistics.surface_count > 1 {
            parts.append("\(Int(statistics.surface_count).formatted()) surfaces")
        }
        if statistics.frame_count > 1 {
            parts.append("\(Int(statistics.frame_count).formatted()) frames")
        }
        if skinCount > 1 {
            parts.append("skin \(skinIndex + 1) of \(skinCount)")
        }
        if missingTextureCount > 0 {
            parts.append(
                missingTextureCount == 1
                    ? "1 skin not in this archive"
                    : "\(missingTextureCount.formatted()) skins not in this archive"
            )
        }
        return parts.joined(separator: " • ")
    }

    @discardableResult
    func selectSkin(_ index: Int) -> Bool {
        guard index != skinIndex, pkm_model_set_skin(model, Int32(index)) == 0 else {
            return false
        }
        skinIndex = index
        pkm_model_get_stats(model, &statistics)
        return true
    }

    func selectedSkinImage() -> NSImage? {
        var width: Int32 = 0
        var height: Int32 = 0
        guard pkm_model_get_skin_size(model, Int32(skinIndex), &width, &height) == 0,
              width > 0, height > 0 else {
            return nil
        }

        let byteCount = Int(width) * Int(height) * 4
        var pixels = [UInt8](repeating: 0, count: byteCount)
        guard pixels.withUnsafeMutableBytes({
            pkm_model_copy_skin_rgba(model, Int32(skinIndex), $0.baseAddress, $0.count)
        }) == 0 else {
            return nil
        }
        return Self.image(rgba: pixels, width: Int(width), height: Int(height))
    }

    func embeddedTextures() -> [(name: String, image: NSImage)] {
        let count = Int(pkm_model_texture_count(model))
        guard count > 0 else { return [] }

        var results: [(name: String, image: NSImage)] = []
        results.reserveCapacity(count)
        for textureIndex in 0 ..< count {
            var nameBytes = [CChar](repeating: 0, count: 256)
            var width: Int32 = 0
            var height: Int32 = 0
            let readName = nameBytes.withUnsafeMutableBufferPointer {
                pkm_model_texture_name(
                    model, Int32(textureIndex), $0.baseAddress, $0.count)
            }
            guard readName == 0,
                  pkm_model_get_texture_size(
                    model, Int32(textureIndex), &width, &height) == 0,
                  width > 0, height > 0 else {
                continue
            }
            let byteCount = Int(width) * Int(height) * 4
            var pixels = [UInt8](repeating: 0, count: byteCount)
            guard pixels.withUnsafeMutableBytes({
                pkm_model_copy_texture_rgba(
                    model, Int32(textureIndex), $0.baseAddress, $0.count)
            }) == 0,
            let image = Self.image(rgba: pixels, width: Int(width), height: Int(height)) else {
                continue
            }
            results.append((String(cString: nameBytes), image))
        }
        return results
    }

    private static func image(rgba pixels: [UInt8], width: Int, height: Int) -> NSImage? {
        guard
        let provider = CGDataProvider(data: Data(pixels) as CFData),
        let image = CGImage(
            width: width,
            height: height,
            bitsPerComponent: 8,
            bitsPerPixel: 32,
            bytesPerRow: width * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.last.rawValue),
            provider: provider,
            decode: nil,
            shouldInterpolate: false,
            intent: .defaultIntent
        ) else {
            return nil
        }
        return NSImage(cgImage: image, size: NSSize(width: width, height: height))
    }

    func setDarkBackground(_ dark: Bool) {
        pkm_view_set_dark_background(view, dark ? 1 : 0)
    }

    func setAutoRotate(_ enabled: Bool) {
        pkm_view_set_auto_rotate(view, enabled ? 1 : 0)
    }

    func setAnimationEnabled(_ enabled: Bool) {
        pkm_view_set_animation_enabled(view, enabled ? 1 : 0)
    }

    func setAnimationSpeed(_ speed: Float) {
        pkm_view_set_animation_speed(view, speed)
    }

    func beginInteraction() {
        pkm_view_begin_interaction(view)
    }

    /// Pointer deltas are in rendered device pixels.
    func orbit(dx: CGFloat, dy: CGFloat) {
        pkm_view_orbit(view, Float(dx), Float(dy))
    }

    func pan(dx: CGFloat, dy: CGFloat) {
        pkm_view_pan(view, Float(dx), Float(dy))
    }

    func zoom(steps: CGFloat) {
        pkm_view_zoom(view, Float(steps))
    }

    func endInteraction() {
        pkm_view_end_interaction(view)
    }

    func nudge(_ nudge: QuakeModelNudge) {
        pkm_view_nudge(view, nudge.rawValue)
    }

    func reset() {
        pkm_view_reset(view)
    }

    /// Returns true when the next frame differs from the one already on screen.
    func advance(_ elapsedSeconds: Double) -> Bool {
        pkm_view_advance(view, elapsedSeconds) != 0
    }

    func render(
        into buffer: UnsafeMutableRawPointer,
        width: Int,
        height: Int,
        stride: Int
    ) -> Bool {
        pkm_view_render(view, buffer, Int32(width), Int32(height), Int32(stride)) == 0
    }
}
