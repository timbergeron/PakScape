import SwiftUI
import UniformTypeIdentifiers

struct PakDocument: FileDocument {
    var pakFile: PakFile

    static var readableContentTypes: [UTType] {
        var types: [UTType] = []
        if let pakType = UTType(filenameExtension: "pak") {
            types.append(pakType)
        }
        if let pk3Type = UTType(filenameExtension: "pk3") {
            types.append(pk3Type)
        }
        return types
    }

    static var writableContentTypes: [UTType] {
        readableContentTypes
    }

    init(pakFile: PakFile? = nil) {
        self.pakFile = pakFile ?? PakFile.empty(name: "Untitled.pak")
    }

    init(configuration: ReadConfiguration) throws {
        guard let data = configuration.file.regularFileContents else {
            throw CocoaError(.fileReadCorruptFile)
        }
        let filename = configuration.file.filename ?? "Untitled.pak"
        let preferredExt = configuration.contentType.preferredFilenameExtension?.lowercased()
        let ext = preferredExt ?? ((filename as NSString).pathExtension.lowercased())
        if ext == "pk3" {
            let temporary = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".pk3")
            try data.write(to: temporary)
            defer { try? FileManager.default.removeItem(at: temporary) }
            self.pakFile = try PakLoader.loadZip(from: temporary, name: filename)
        } else {
            self.pakFile = try PakLoader.load(data: data, name: filename)
        }
    }

    func fileWrapper(configuration: WriteConfiguration) throws -> FileWrapper {
        let root = pakFile.root
        let packResult = PakWriter.write(root: root, originalData: pakFile.data)
        pakFile.data = packResult.data
        pakFile.entries = packResult.entries
        pakFile.version = UUID()

        let preferredExt = configuration.contentType.preferredFilenameExtension?.lowercased()
        let ext = preferredExt ?? "pak"
        if ext == "pk3" {
            let zipData = try PakZipWriter.write(root: root, originalData: packResult.data)
            return FileWrapper(regularFileWithContents: zipData)
        }

        return FileWrapper(regularFileWithContents: packResult.data)
    }
}
