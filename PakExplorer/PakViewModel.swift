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
            // If it's a directory, we might want to recurse, but for now let's just handle flat files or ignore folders
            var isDir: ObjCBool = false
            if FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir), isDir.boolValue {
                // Create folder and recurse?
                // For simplicity, let's just create a folder node and try to import children?
                // Or just skip folders for MVP.
                // Let's skip folders for now to keep it simple, or maybe just add the folder node.
                continue 
            }
            
            do {
                let data = try Data(contentsOf: url)
                let name = url.lastPathComponent
                
                // Check if file already exists in this folder
                if let existingIndex = folder.children?.firstIndex(where: { $0.name == name }) {
                    // Overwrite? Or skip?
                    // Let's overwrite
                    folder.children?[existingIndex].localData = data
                    folder.children?[existingIndex].entry = nil // It's now a local file, not from PAK
                } else {
                    let newNode = PakNode(name: name)
                    newNode.localData = data
                    folder.children?.append(newNode)
                }
            } catch {
                print("Failed to import \(url): \(error)")
            }
        }
        sortFolder(folder)
        markDirty()
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
