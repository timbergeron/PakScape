import SwiftUI
import AppKit
import Combine
import UniformTypeIdentifiers

final class PakViewModel: ObservableObject {
    @Published var pakFile: PakFile?
    @Published var currentFolder: PakNode? { // Directory shown in right pane
        didSet {
            handleNavigationChange(from: oldValue, to: currentFolder)
        }
    }
    @Published var selectedFile: PakNode?  // File selected in right pane (first of selection for backward compatibility)
    @Published var selectedNodes: [PakNode] = [] // Multi-selection support
    @Published private(set) var hasUnsavedChanges = false
    @Published private var backStack: [PakNode] = []
    @Published private var forwardStack: [PakNode] = []
    private static let previewableImageExtensions: Set<String> = ["png", "jpg", "jpeg", "gif", "tif", "tiff", "bmp", "heic", "heif"]
    var documentURL: URL?
    private var isNavigatingHistory = false

    var canSave: Bool {
        pakFile != nil && hasUnsavedChanges
    }

    var canNavigateBack: Bool {
        !backStack.isEmpty
    }

    var canNavigateForward: Bool {
        !forwardStack.isEmpty
    }

    init(pakFile: PakFile?, documentURL: URL? = nil) {
        self.pakFile = pakFile
        self.documentURL = documentURL
        resetNavigation(to: pakFile?.root)
    }

    func updateDocumentURL(_ url: URL?) {
        documentURL = url
    }

    enum ExportError: Error {
        case missingData
    }

    func exportToTemporaryLocation(node: PakNode) throws -> URL {
        let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)

        if node.isFolder {
            let destination = base.appendingPathComponent(node.name, isDirectory: true)
            try PakFilesystemExporter.export(node: node, originalData: pakFile?.data, to: destination)
            return destination
        }

        guard let data = extractData(for: node) else {
            throw ExportError.missingData
        }

        let destination = base.appendingPathComponent(node.name)
        try data.write(to: destination)
        return destination
    }

    func rename(node: PakNode, to newName: String) {
        let trimmed = newName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed != node.name else { return }

        node.name = trimmed
        if let entry = node.entry {
            let updatedPath: String
            if let slashIndex = entry.name.lastIndex(of: "/") {
                let prefix = entry.name[..<entry.name.index(after: slashIndex)]
                updatedPath = String(prefix) + trimmed
            } else {
                updatedPath = trimmed
            }
            node.entry = PakEntry(name: updatedPath, offset: entry.offset, length: entry.length)
        }
        markDirty()
    }

    func write(node: PakNode, toDirectory directory: URL) throws {
        let destination = directory.appendingPathComponent(node.name, isDirectory: node.isFolder)
        if node.isFolder {
            try PakFilesystemExporter.export(node: node, originalData: pakFile?.data, to: destination)
        } else {
            guard let data = extractData(for: node) else {
                throw ExportError.missingData
            }
            try data.write(to: destination)
        }
    }

    func exportSelectionToTemporaryLocation(nodes: [PakNode]) throws -> URL {
        precondition(!nodes.isEmpty)
        if nodes.count == 1, let first = nodes.first {
            return try exportToTemporaryLocation(node: first)
        }

        let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)

        for node in nodes {
            let destination = base.appendingPathComponent(node.name, isDirectory: node.isFolder)
            try PakFilesystemExporter.export(node: node, originalData: pakFile?.data, to: destination)
        }
        return base
    }


    // Export the currently-selected file
    func exportSelectedFile() {
        guard let node = selectedNodes.first ?? selectedFile, let data = extractData(for: node) else { return }

        let save = NSSavePanel()
        save.nameFieldStringValue = node.name
        
        save.begin { response in
            guard response == .OK, let outURL = save.url else { return }
            do {
                try data.write(to: outURL)
            } catch {
                let alert = NSAlert(error: error)
                alert.runModal()
            }
        }
    }
    
    func extractData(for node: PakNode) -> Data? {
        if let local = node.localData {
            return local
        }
        guard let pakFile = pakFile, let entry = node.entry else { return nil }
        
        let range = entry.offset ..< (entry.offset + entry.length)
        // Safety check
        if range.upperBound <= pakFile.data.count {
            return pakFile.data.subdata(in: range)
        }
        return nil
    }

    func previewImage(for node: PakNode) -> NSImage? {
        guard !node.isFolder, let data = extractData(for: node) else { return nil }

        let ext = (node.name as NSString).pathExtension.lowercased()
        if ext == "lmp" {
            return LmpPreviewRenderer.renderImage(fileName: node.name, data: data)
        }

        guard Self.previewableImageExtensions.contains(ext) else { return nil }
        return NSImage(data: data)
    }
    
    func importFiles(urls: [URL], to folder: PakNode) {
        for url in urls {
            importItem(at: url, into: folder)
        }
        sortFolder(folder)
        markDirty()
    }

    private func importItem(at url: URL, into folder: PakNode) {
        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir) else { return }

        if isDir.boolValue {
            folder.children = folder.children ?? []
            let targetFolder: PakNode
            if let existing = folder.children?.first(where: { $0.name == url.lastPathComponent && $0.isFolder }) {
                targetFolder = existing
            } else {
                let newFolder = PakNode(name: url.lastPathComponent)
                folder.children?.append(newFolder)
                targetFolder = newFolder
            }

            let contents = (try? FileManager.default.contentsOfDirectory(at: url, includingPropertiesForKeys: nil)) ?? []
            for child in contents {
                importItem(at: child, into: targetFolder)
            }
            sortFolder(targetFolder)
            return
        }

        do {
            let data = try Data(contentsOf: url)
            let name = url.lastPathComponent

            folder.children = folder.children ?? []
            if let existingIndex = folder.children?.firstIndex(where: { $0.name == name }) {
                folder.children?[existingIndex].localData = data
                folder.children?[existingIndex].entry = nil
            } else {
                let newNode = PakNode(name: name)
                newNode.localData = data
                folder.children?.append(newNode)
            }
        } catch {
            print("Failed to import \(url): \(error)")
        }
    }
    func deleteSelectedFile() {
        guard let folder = currentFolder else { return }

        let idsToDelete: Set<PakNode.ID>
        if !selectedNodes.isEmpty {
            idsToDelete = Set(selectedNodes.map { $0.id })
        } else if let single = selectedFile {
            idsToDelete = [single.id]
        } else {
            return
        }

        folder.children?.removeAll { idsToDelete.contains($0.id) }
        selectedNodes = []
        selectedFile = nil
        markDirty()
    }
    
    var canCreateFolder: Bool {
        pakFile != nil
    }
    
    var canAddFiles: Bool {
        pakFile != nil
    }

    var canDeleteFile: Bool {
        !selectedNodes.isEmpty || selectedFile != nil
    }

    @discardableResult
    func addFolder(in folder: PakNode?) -> PakNode? {
        guard let target = folder ?? currentFolder ?? pakFile?.root else { return nil }
        target.children = target.children ?? []

        let baseName = "New Folder"
        var candidate = baseName
        var suffix = 1
        while target.children?.contains(where: { $0.name == candidate }) == true {
            suffix += 1
            candidate = "\(baseName) \(suffix)"
        }

        let newNode = PakNode(name: candidate)
        target.children?.append(newNode)
        sortFolder(target)
        markDirty()
        return newNode
    }

    func exportPakAs() {
        guard let pakFile = pakFile else { return }
        
        let save = NSSavePanel()
        save.allowedContentTypes = PakDocument.readableContentTypes
        save.nameFieldStringValue = pakFile.name
        
        save.begin { response in
            guard response == .OK, let url = save.url else { return }
            let result = PakWriter.write(root: pakFile.root, originalData: pakFile.data)
            do {
                let data = try self.outputData(for: url, with: result)
                try data.write(to: url)
            } catch {
                let alert = NSAlert(error: error)
                alert.runModal()
            }
        }
    }

    @discardableResult
    func saveCurrentPak(promptForLocationIfNeeded: Bool = true) -> Bool {
        guard let pakFile = pakFile else { return false }

        if let url = documentURL {
            return write(pakFile: pakFile, to: url)
        }

        guard promptForLocationIfNeeded else { return false }

        let save = NSSavePanel()
        save.allowedContentTypes = PakDocument.readableContentTypes
        save.nameFieldStringValue = pakFile.name
        let response = save.runModal()
        if response == .OK, let url = save.url {
            let success = write(pakFile: pakFile, to: url)
            if success {
                documentURL = url
            }
            return success
        }
        return false
    }

    private func write(pakFile: PakFile, to url: URL) -> Bool {
        let result = PakWriter.write(root: pakFile.root, originalData: pakFile.data)
        do {
            let data = try outputData(for: url, with: result)
            try data.write(to: url)
            pakFile.data = result.data
            pakFile.entries = result.entries
            pakFile.version = UUID()
            hasUnsavedChanges = false
            return true
        } catch {
            let alert = NSAlert(error: error)
            alert.runModal()
            return false
        }
    }
    
    private func outputData(for url: URL, with result: PakWriter.Output) throws -> Data {
        let ext = url.pathExtension.lowercased()
        if ext == "pk3", let root = pakFile?.root {
            return try PakZipWriter.write(root: root, originalData: result.data)
        }
        return result.data
    }
    
    func markDirty() {
        pakFile?.version = UUID()
        objectWillChange.send()
        hasUnsavedChanges = true
    }
    
    private func sortFolder(_ folder: PakNode) {
        folder.children?.sort {
            if $0.isFolder != $1.isFolder {
                return $0.isFolder && !$1.isFolder
            }
            return $0.name.lowercased() < $1.name.lowercased()
        }
    }

    func navigate(to folder: PakNode?) {
        guard currentFolder !== folder else { return }
        currentFolder = folder
    }

    func navigateBack() {
        guard let previous = backStack.popLast() else { return }
        if let current = currentFolder {
            forwardStack.append(current)
        }
        isNavigatingHistory = true
        currentFolder = previous
        isNavigatingHistory = false
    }

    func navigateForward() {
        guard let next = forwardStack.popLast() else { return }
        if let current = currentFolder {
            backStack.append(current)
        }
        isNavigatingHistory = true
        currentFolder = next
        isNavigatingHistory = false
    }

    func resetNavigation(to folder: PakNode?) {
        isNavigatingHistory = true
        backStack.removeAll()
        forwardStack.removeAll()
        currentFolder = folder
        isNavigatingHistory = false
    }

    private func handleNavigationChange(from oldValue: PakNode?, to newValue: PakNode?) {
        if newValue == nil {
            backStack.removeAll()
            forwardStack.removeAll()
            return
        }
        guard !isNavigatingHistory else { return }
        guard let previous = oldValue,
              let destination = newValue,
              previous != destination else { return }

        backStack.append(previous)
        forwardStack.removeAll()
    }
}

private enum LmpPreviewRenderer {
    private enum HeaderType {
        case none
        case simple
    }

    private enum PixelType {
        case palettized
        case rgb

        var bytesPerPixel: Int {
            switch self {
            case .palettized:
                return 1
            case .rgb:
                return 3
            }
        }
    }

    private struct SpecialCase {
        let width: Int
        let height: Int
        let header: HeaderType
        let pixelType: PixelType
        let transparentIndex: Int?
    }

    private static let specialCases: [String: SpecialCase] = [
        "conchars": SpecialCase(width: 128, height: 128, header: .none, pixelType: .palettized, transparentIndex: 0),
        "conchars.lmp": SpecialCase(width: 128, height: 128, header: .none, pixelType: .palettized, transparentIndex: 0),
        "pop.lmp": SpecialCase(width: 16, height: 16, header: .none, pixelType: .palettized, transparentIndex: 255),
        "colormap.lmp": SpecialCase(width: 256, height: 64, header: .none, pixelType: .palettized, transparentIndex: 255)
    ]

    static func renderImage(fileName: String, data: Data) -> NSImage? {
        let normalizedName = fileName.lowercased()
        let baseName = normalizedName.split(separator: "/").last.map(String.init) ?? normalizedName
        let rootName = baseName.split(separator: ".").first.map(String.init) ?? baseName
        let matchedSpecial = specialCases[baseName] ?? specialCases[rootName]

        var header: HeaderType = matchedSpecial?.header ?? .simple
        var pixelType: PixelType = matchedSpecial?.pixelType ?? .palettized
        var width = matchedSpecial?.width ?? 0
        var height = matchedSpecial?.height ?? 0
        var offset = header == .simple ? 8 : 0

        let transparencyIndex: Int?
        if pixelType == .palettized {
            transparencyIndex = matchedSpecial?.transparentIndex ?? 255
        } else {
            transparencyIndex = nil
        }

        if matchedSpecial == nil && data.count == 768 {
            // Palette files do not carry a header; treat as a 16x16 RGB swatch.
            header = .none
            pixelType = .rgb
            width = 16
            height = 16
            offset = 0
        }

        if header == .simple {
            guard let parsedWidth = readInt32LE(data, offset: 0),
                  let parsedHeight = readInt32LE(data, offset: 4) else {
                return nil
            }
            width = parsedWidth
            height = parsedHeight
        }

        guard width > 0, height > 0 else { return nil }
        let pixelCountResult = width.multipliedReportingOverflow(by: height)
        guard !pixelCountResult.overflow else { return nil }
        let pixelCount = pixelCountResult.partialValue

        let bytesPerPixel = pixelType.bytesPerPixel
        let expectedBytesResult = pixelCount.multipliedReportingOverflow(by: bytesPerPixel)
        guard !expectedBytesResult.overflow else { return nil }
        let expectedBytes = expectedBytesResult.partialValue

        guard data.count >= offset + expectedBytes else { return nil }
        let pixelData = data.subdata(in: offset ..< offset + expectedBytes)

        let rgbaBytesResult = pixelCount.multipliedReportingOverflow(by: 4)
        guard !rgbaBytesResult.overflow else { return nil }
        var rgba = Data(count: rgbaBytesResult.partialValue)

        var conversionSucceeded = true
        rgba.withUnsafeMutableBytes { destBuffer in
            guard let dest = destBuffer.bindMemory(to: UInt8.self).baseAddress else {
                conversionSucceeded = false
                return
            }

            switch pixelType {
            case .rgb:
                pixelData.withUnsafeBytes { srcBuffer in
                    guard let src = srcBuffer.bindMemory(to: UInt8.self).baseAddress else {
                        conversionSucceeded = false
                        return
                    }
                    for i in 0..<pixelCount {
                        let srcIndex = i * 3
                        let destIndex = i * 4
                        dest[destIndex] = src[srcIndex]
                        dest[destIndex + 1] = src[srcIndex + 1]
                        dest[destIndex + 2] = src[srcIndex + 2]
                        dest[destIndex + 3] = 255
                    }
                }
            case .palettized:
                let palette = QuakePalette.bytes
                guard palette.count >= 768 else {
                    conversionSucceeded = false
                    return
                }

                pixelData.withUnsafeBytes { srcBuffer in
                    guard let src = srcBuffer.bindMemory(to: UInt8.self).baseAddress else {
                        conversionSucceeded = false
                        return
                    }

                    for i in 0..<pixelCount {
                        let paletteIndex = Int(src[i])
                        let destIndex = i * 4

                        if let transparencyIndex, paletteIndex == transparencyIndex {
                            dest[destIndex] = 0
                            dest[destIndex + 1] = 0
                            dest[destIndex + 2] = 0
                            dest[destIndex + 3] = 0
                            continue
                        }

                        let paletteOffset = paletteIndex * 3
                        guard paletteOffset + 2 < palette.count else {
                            conversionSucceeded = false
                            return
                        }

                        dest[destIndex] = palette[paletteOffset]
                        dest[destIndex + 1] = palette[paletteOffset + 1]
                        dest[destIndex + 2] = palette[paletteOffset + 2]
                        dest[destIndex + 3] = 255
                    }
                }
            }
        }

        guard conversionSucceeded else { return nil }

        guard let provider = CGDataProvider(data: rgba as CFData) else { return nil }
        let colorSpace = CGColorSpaceCreateDeviceRGB()
        let bitmapInfo = CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue)
        guard let cgImage = CGImage(
            width: width,
            height: height,
            bitsPerComponent: 8,
            bitsPerPixel: 32,
            bytesPerRow: width * 4,
            space: colorSpace,
            bitmapInfo: bitmapInfo,
            provider: provider,
            decode: nil,
            shouldInterpolate: true,
            intent: .defaultIntent
        ) else {
            return nil
        }

        return NSImage(cgImage: cgImage, size: NSSize(width: width, height: height))
    }

    private static func readInt32LE(_ data: Data, offset: Int) -> Int? {
        guard offset + 4 <= data.count else { return nil }
        return data.withUnsafeBytes { rawBuffer in
            let value = rawBuffer.load(fromByteOffset: offset, as: Int32.self)
            return Int(Int32(littleEndian: value))
        }
    }
}

private enum QuakePalette {
    static let bytes: [UInt8] = {
        guard let data = Data(base64Encoded: """
AAAADw8PHx8fLy8vPz8/S0tLW1tba2tre3t7i4uLm5ubq6uru7u7y8vL29vb6+vrDwsHFw8LHxcLJxsPLyMTNysXPy8XSzcbUzsbW0MfY0sfa1Mfc1cfe18jg2cjj28jCwsPExMbGxsnJyczLy8/NzdLPz9XR0dnT09zW1t/Y2OLa2uXc3Oje3uvg4O7i4vLAAAABwcACwsAExMAGxsAIyMAKysHLy8HNzcHPz8HR0cHS0sLU1MLW1sLY2MLa2sPBwAADwAAFwAAHwAAJwAALwAANwAAPwAARwAATwAAVwAAXwAAZwAAbwAAdwAAfwAAExMAGxsAIyMALysANy8AQzcASzsHV0MHX0cHa0sLd1MPg1cTi1sTl18bo2Mfr2cjIxMHLxcLOx8PSyMTVysXYy8fczcjfzsrj0Mzn08zr2Mvv3cvz48r36sn78sf//MbCwcAGxMAKyMPNysTRzMbUzcjYz8rb0czf1M/i19Hm2tTp3tft4drw5N706OL47OXq4ujn3+Xk3OHi2d7f1tvd1Nja0tXXz9LVzdDSy83QycvNx8jKxcbIxMTFwsLDwcHu3Ofr2uPo1+Dl1d3i09rf0tfc0NTaztLXzM/Uys3RyMrOx8jLxcbIxMTFwsLDwcH28O7y7Onv6Obr5eLo4d7l3tvh29fe2NTa1dHX0s7Uz8zQzMnNysfJx8XGxMPDwsHb4N7Z3tvX3NnV2tfT2NXR1tPP1NHN0s/L0M3KzsvIzMnHysfFyMXDxsTCxMLBwsH//Mb798X28sTy7cPu6cPq5cLm4MHi3MHe2MHa1MAW0cASzcAOysAKx8AGw8ACwcAAAD/CwvvExPfGxvPIyO/KyuvLy+fLy+PLy9/Ly9vLy9fKytPIyM/GxsvExMfCwsPKwAAOwAASwcAXwcAbw8AfxcHkx8HoycLtzMPw0sbz2Mr238745dP56tf779399OLp3s7t5s3x8M35+NXf7//q+f/1///ZwAAiwAAswAA1wAA/wAA//OT//fH////n1tT
""") else {
            return []
        }
        return Array(data)
    }()
}
