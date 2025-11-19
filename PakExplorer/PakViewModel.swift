import SwiftUI
import AppKit
import Combine
import UniformTypeIdentifiers

final class PakViewModel: ObservableObject {
    @Published var pakFile: PakFile?
    @Published var currentFolder: PakNode? // Directory shown in right pane
    @Published var selectedFile: PakNode?  // File selected in right pane

    init(pakFile: PakFile?) {
        self.pakFile = pakFile
        self.currentFolder = pakFile?.root
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
        
        // Sort again
        folder.children?.sort {
            if $0.isFolder != $1.isFolder {
                return $0.isFolder && !$1.isFolder
            }
            return $0.name.lowercased() < $1.name.lowercased()
        }
        
        // Trigger UI update
        objectWillChange.send()
    }
}

