import SwiftUI
import AppKit
import Combine
import UniformTypeIdentifiers

final class PakViewModel: ObservableObject {
    @Published var pakFile: PakFile?
    @Published var currentFolder: PakNode? // Directory shown in right pane
    @Published var selectedFile: PakNode?  // File selected in right pane
    @Published private(set) var hasUnsavedChanges = false
    var documentURL: URL?

    var canSave: Bool {
        pakFile != nil && hasUnsavedChanges
    }

    init(pakFile: PakFile?, documentURL: URL? = nil) {
        self.pakFile = pakFile
        self.currentFolder = pakFile?.root
        self.documentURL = documentURL
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


    // Export the currently-selected file
    func exportSelectedFile() {
        guard let node = selectedFile, let data = extractData(for: node) else { return }

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
        guard let folder = currentFolder, let selected = selectedFile else { return }
        
        if let index = folder.children?.firstIndex(where: { $0.id == selected.id }) {
            folder.children?.remove(at: index)
            selectedFile = nil
            markDirty()
        }
    }
    
    var canCreateFolder: Bool {
        pakFile != nil
    }
    
    var canAddFiles: Bool {
        pakFile != nil
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
}
