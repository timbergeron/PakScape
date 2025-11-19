import SwiftUI
import UniformTypeIdentifiers

struct PakDocument: FileDocument {
    var pakFile: PakFile?

    static var readableContentTypes: [UTType] {
        if let pakType = UTType(filenameExtension: "pak") {
            return [pakType]
        }
        return []
    }

    init(pakFile: PakFile? = nil) {
        self.pakFile = pakFile
    }

    init(configuration: ReadConfiguration) throws {
        guard let data = configuration.file.regularFileContents else {
            throw CocoaError(.fileReadCorruptFile)
        }
        let filename = configuration.file.filename ?? "Untitled.pak"
        self.pakFile = try PakLoader.load(data: data, name: filename)
    }

    func fileWrapper(configuration: WriteConfiguration) throws -> FileWrapper {
        // Rebuild the PAK file from the current node tree
        if let root = pakFile?.root {
            let newData = PakWriter.write(root: root, originalData: pakFile?.data)
            return FileWrapper(regularFileWithContents: newData)
        } else {
            throw CocoaError(.fileWriteUnknown)
        }
    }
}
