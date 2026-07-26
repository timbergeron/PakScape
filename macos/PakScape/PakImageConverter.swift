import AppKit
import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

enum PakImageFormat: String, CaseIterable {
    case lmp
    case jpeg
    case png
    case tga

    var menuTitle: String {
        switch self {
        case .lmp: "LMP…"
        case .jpeg: "JPEG…"
        case .png: "PNG…"
        case .tga: "TGA…"
        }
    }

    var pathExtension: String {
        switch self {
        case .jpeg: "jpg"
        default: rawValue
        }
    }
}

final class PakImageSaveRequest: NSObject {
    let node: PakNode
    let format: PakImageFormat

    init(node: PakNode, format: PakImageFormat) {
        self.node = node
        self.format = format
    }
}

final class PakModelSkinSaveRequest: NSObject {
    let node: PakNode
    let format: PakImageFormat

    init(node: PakNode, format: PakImageFormat) {
        self.node = node
        self.format = format
    }
}

final class PakBspTextureSaveRequest: NSObject {
    let node: PakNode
    let format: PakImageFormat

    init(node: PakNode, format: PakImageFormat) {
        self.node = node
        self.format = format
    }
}

final class PakWadTextureSaveRequest: NSObject {
    let node: PakNode
    let format: PakImageFormat

    init(node: PakNode, format: PakImageFormat) {
        self.node = node
        self.format = format
    }
}

enum PakImageConversionError: LocalizedError {
    case unsupportedSourceFormat
    case invalidImage
    case imageTooLarge
    case encodingFailed

    var errorDescription: String? {
        switch self {
        case .unsupportedSourceFormat:
            "Save As supports LMP, JPEG, PNG, and TGA images."
        case .invalidImage:
            "The image data could not be decoded."
        case .imageTooLarge:
            "The image dimensions are too large for the selected format."
        case .encodingFailed:
            "The image could not be encoded in the selected format."
        }
    }
}

enum PakImageConverter {
    static let supportedSourceExtensions: Set<String> = ["lmp", "jpg", "jpeg", "png", "tga"]

    static func convert(
        fileName: String,
        data: Data,
        to format: PakImageFormat
    ) throws -> Data {
        let sourceExtension = (fileName as NSString).pathExtension.lowercased()
        guard supportedSourceExtensions.contains(sourceExtension) else {
            throw PakImageConversionError.unsupportedSourceFormat
        }

        let image: NSImage?
        switch sourceExtension {
        case "lmp":
            image = LmpPreviewRenderer.renderImage(fileName: fileName, data: data)
        case "tga":
            image = TgaPreviewRenderer.renderImage(data: data)
        default:
            image = NSImage(data: data)
        }

        guard let image else {
            throw PakImageConversionError.invalidImage
        }

        return try convert(image: image, to: format)
    }

    static func convert(image: NSImage, to format: PakImageFormat) throws -> Data {
        guard let cgImage = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else {
            throw PakImageConversionError.invalidImage
        }

        switch format {
        case .lmp:
            return try encodeLmp(cgImage)
        case .jpeg:
            return try encodeWithImageIO(cgImage, type: .jpeg, properties: [
                kCGImageDestinationLossyCompressionQuality: 0.9,
            ])
        case .png:
            return try encodeWithImageIO(cgImage, type: .png)
        case .tga:
            return try encodeTga(cgImage)
        }
    }

    private static func encodeWithImageIO(
        _ image: CGImage,
        type: UTType,
        properties: [CFString: Any] = [:]
    ) throws -> Data {
        let output = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(
            output,
            type.identifier as CFString,
            1,
            nil
        ) else {
            throw PakImageConversionError.encodingFailed
        }

        CGImageDestinationAddImage(destination, image, properties as CFDictionary)
        guard CGImageDestinationFinalize(destination) else {
            throw PakImageConversionError.encodingFailed
        }
        return output as Data
    }

    private static func rgbaBytes(for image: CGImage) throws -> (width: Int, height: Int, bytes: [UInt8]) {
        let width = image.width
        let height = image.height
        guard PakPreviewLimits.isSafe(width: width, height: height) else {
            throw PakImageConversionError.imageTooLarge
        }

        let byteCountResult = width.multipliedReportingOverflow(by: height)
        guard !byteCountResult.overflow else {
            throw PakImageConversionError.imageTooLarge
        }
        let rgbaCountResult = byteCountResult.partialValue.multipliedReportingOverflow(by: 4)
        guard !rgbaCountResult.overflow else {
            throw PakImageConversionError.imageTooLarge
        }

        var bytes = [UInt8](repeating: 0, count: rgbaCountResult.partialValue)
        let rendered = bytes.withUnsafeMutableBytes { buffer -> Bool in
            guard let baseAddress = buffer.baseAddress,
                  let context = CGContext(
                    data: baseAddress,
                    width: width,
                    height: height,
                    bitsPerComponent: 8,
                    bytesPerRow: width * 4,
                    space: CGColorSpaceCreateDeviceRGB(),
                    bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
                        | CGBitmapInfo.byteOrder32Big.rawValue
                  ) else {
                return false
            }
            context.interpolationQuality = .none
            context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
            return true
        }
        guard rendered else { throw PakImageConversionError.encodingFailed }
        return (width, height, bytes)
    }

    private static func encodeLmp(_ image: CGImage) throws -> Data {
        let rgba = try rgbaBytes(for: image)
        guard rgba.width <= Int(UInt32.max), rgba.height <= Int(UInt32.max) else {
            throw PakImageConversionError.imageTooLarge
        }

        var output = Data()
        appendUInt32LE(UInt32(rgba.width), to: &output)
        appendUInt32LE(UInt32(rgba.height), to: &output)

        let palette = QuakePalette.bytes
        guard palette.count >= 768 else {
            throw PakImageConversionError.encodingFailed
        }

        var colorCache: [UInt32: UInt8] = [:]
        colorCache.reserveCapacity(min(rgba.width * rgba.height, 65_536))
        var indexed = [UInt8]()
        indexed.reserveCapacity(rgba.width * rgba.height)

        for offset in stride(from: 0, to: rgba.bytes.count, by: 4) {
            let alpha = rgba.bytes[offset + 3]
            if alpha <= 128 {
                indexed.append(255)
                continue
            }

            let red = unpremultiply(rgba.bytes[offset], alpha: alpha)
            let green = unpremultiply(rgba.bytes[offset + 1], alpha: alpha)
            let blue = unpremultiply(rgba.bytes[offset + 2], alpha: alpha)
            let key = (UInt32(red) << 16) | (UInt32(green) << 8) | UInt32(blue)
            if let cached = colorCache[key] {
                indexed.append(cached)
                continue
            }

            var bestIndex = 1
            var bestDistance = Int.max
            // Index 0 is conventionally reserved and 255 is the transparent color.
            for paletteIndex in 1...254 {
                let paletteOffset = paletteIndex * 3
                let deltaRed = Int(red) - Int(palette[paletteOffset])
                let deltaGreen = Int(green) - Int(palette[paletteOffset + 1])
                let deltaBlue = Int(blue) - Int(palette[paletteOffset + 2])
                let distance = deltaRed * deltaRed + deltaGreen * deltaGreen + deltaBlue * deltaBlue
                if distance < bestDistance {
                    bestDistance = distance
                    bestIndex = paletteIndex
                    if distance == 0 { break }
                }
            }

            let result = UInt8(bestIndex)
            colorCache[key] = result
            indexed.append(result)
        }

        output.append(contentsOf: indexed)
        return output
    }

    private static func encodeTga(_ image: CGImage) throws -> Data {
        let rgba = try rgbaBytes(for: image)
        guard rgba.width <= Int(UInt16.max), rgba.height <= Int(UInt16.max) else {
            throw PakImageConversionError.imageTooLarge
        }

        var output = Data(count: 18)
        output[2] = 2 // Uncompressed true-color image.
        writeUInt16LE(UInt16(rgba.width), to: &output, offset: 12)
        writeUInt16LE(UInt16(rgba.height), to: &output, offset: 14)
        output[16] = 32
        output[17] = 0x28 // Top-left origin and eight alpha bits.

        var pixels = [UInt8]()
        pixels.reserveCapacity(rgba.bytes.count)
        for offset in stride(from: 0, to: rgba.bytes.count, by: 4) {
            let alpha = rgba.bytes[offset + 3]
            pixels.append(unpremultiply(rgba.bytes[offset + 2], alpha: alpha))
            pixels.append(unpremultiply(rgba.bytes[offset + 1], alpha: alpha))
            pixels.append(unpremultiply(rgba.bytes[offset], alpha: alpha))
            pixels.append(alpha)
        }
        output.append(contentsOf: pixels)
        return output
    }

    private static func unpremultiply(_ component: UInt8, alpha: UInt8) -> UInt8 {
        guard alpha > 0, alpha < 255 else { return alpha == 0 ? 0 : component }
        return UInt8(min(255, (Int(component) * 255 + Int(alpha) / 2) / Int(alpha)))
    }

    private static func appendUInt32LE(_ value: UInt32, to data: inout Data) {
        data.append(UInt8(truncatingIfNeeded: value))
        data.append(UInt8(truncatingIfNeeded: value >> 8))
        data.append(UInt8(truncatingIfNeeded: value >> 16))
        data.append(UInt8(truncatingIfNeeded: value >> 24))
    }

    private static func writeUInt16LE(_ value: UInt16, to data: inout Data, offset: Int) {
        data[offset] = UInt8(truncatingIfNeeded: value)
        data[offset + 1] = UInt8(truncatingIfNeeded: value >> 8)
    }
}
