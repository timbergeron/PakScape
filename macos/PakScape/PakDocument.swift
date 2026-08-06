import Combine
import SwiftUI
import UniformTypeIdentifiers

extension UTType {
    static let pakArchive = UTType(importedAs: "com.timbergeron.PakScape.pak")
    static let pk3Archive = UTType(importedAs: "com.timbergeron.PakScape.pk3")
    static let kpfArchive = UTType(importedAs: "com.timbergeron.PakScape.kpf")
}

final class PakDocument: ReferenceFileDocument, @unchecked Sendable {
    struct Snapshot: @unchecked Sendable {
        let pakFile: PakFile
    }

    @Published var pakFile: PakFile
    var fileURL: URL?

    static var readableContentTypes: [UTType] {
        [UTType.pakArchive, UTType.pk3Archive, UTType.kpfArchive]
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
        if ext == "pk3" || ext == "kpf" {
            let temporary = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + "." + ext)
            try data.write(to: temporary, options: .atomic)
            defer { try? FileManager.default.removeItem(at: temporary) }
            self.pakFile = try PakLoader.loadZip(from: temporary, name: filename)
        } else {
            self.pakFile = try PakLoader.load(data: data, name: filename)
        }
    }

    func snapshot(contentType: UTType) throws -> Snapshot {
        Snapshot(pakFile: pakFile.documentSnapshot())
    }

    func fileWrapper(snapshot: Snapshot, configuration: WriteConfiguration) throws -> FileWrapper {
        let pakFile = snapshot.pakFile
        let root = pakFile.root
        createBackupIfNeeded()
        let preferredExt = configuration.contentType.preferredFilenameExtension?.lowercased()
        let ext = preferredExt ?? "pak"
        if ext == "pk3" || ext == "kpf" {
            let zipData = try PakZipWriter.write(root: root, originalData: pakFile.data)
            return FileWrapper(regularFileWithContents: zipData)
        }

        let packResult = try PakWriter.write(root: root, originalData: pakFile.data)
        return FileWrapper(regularFileWithContents: packResult.data)
    }

    private func createBackupIfNeeded() {
        let shouldBackUp = UserDefaults.standard.object(
            forKey: PakScapePreferencesKey.backupBeforeSave
        ) as? Bool ?? false
        guard shouldBackUp,
              let fileURL,
              FileManager.default.fileExists(atPath: fileURL.path) else { return }

        let fileManager = FileManager.default
        let backupURL = fileURL.appendingPathExtension("bak")
        let temporaryURL = backupURL.deletingLastPathComponent()
            .appendingPathComponent(".\(backupURL.lastPathComponent).\(UUID().uuidString)")

        do {
            try fileManager.copyItem(at: fileURL, to: temporaryURL)
            if fileManager.fileExists(atPath: backupURL.path) {
                try fileManager.removeItem(at: backupURL)
            }
            try fileManager.moveItem(at: temporaryURL, to: backupURL)
        } catch {
            try? fileManager.removeItem(at: temporaryURL)
        }
    }
}

extension PakFile {
    func documentSnapshot() -> PakFile {
        func copyNode(_ node: PakNode) -> PakNode {
            let copy = PakNode(name: node.name, entry: node.entry, id: node.id)
            copy.localData = node.localData
            if let children = node.children {
                copy.children = children.map(copyNode)
            } else {
                copy.children = nil
            }
            return copy
        }

        let copy = PakFile(
            name: name,
            data: data,
            entries: entries,
            root: copyNode(root)
        )
        copy.version = version
        return copy
    }
}
