import AppKit
import Foundation

enum FinderPreferencesKey {
    static let actionsEnabled = "finderActionsEnabled"
}

@MainActor
final class FinderServiceManager {
    static let shared = FinderServiceManager()
    private let provider = FinderServiceProvider()

    private init() {}

    func applyInitialSettings() {
        let defaults = UserDefaults.standard
        let enabled: Bool
        if defaults.object(forKey: FinderPreferencesKey.actionsEnabled) == nil {
            enabled = true // default on for new installs
            defaults.set(enabled, forKey: FinderPreferencesKey.actionsEnabled)
        } else {
            enabled = defaults.bool(forKey: FinderPreferencesKey.actionsEnabled)
        }
        updateRegistration(isEnabled: enabled)
    }

    func updateRegistration(isEnabled: Bool) {
        NSApp.servicesProvider = isEnabled ? provider : nil
    }
}

final class FinderServiceProvider: NSObject {
    private let fileManager = FileManager.default
    private static let supportedPakExtensions: Set<String> = ["pak", "pk3"]

    @objc func extractPakService(_ pboard: NSPasteboard, userData: String?, error errorOut: AutoreleasingUnsafeMutablePointer<NSString?>?) {
        let urls = fileURLs(from: pboard)
        let pakURLs = urls.filter { Self.supportedPakExtensions.contains($0.pathExtension.lowercased()) }

        guard !pakURLs.isEmpty else {
            errorOut?.pointee = "Select a .pak or .pk3 file to extract.".NSStringValue
            return
        }

        guard let outputDirectory = chooseOutputDirectory(
            title: "Choose an Extraction Location",
            message: pakURLs.count == 1
                ? "PakScape will create a folder for the extracted archive."
                : "PakScape will create one folder for each extracted archive.",
            initialDirectory: pakURLs.first?.deletingLastPathComponent()
        ) else {
            return
        }

        let accessedOutputScope = outputDirectory.startAccessingSecurityScopedResource()
        defer {
            if accessedOutputScope {
                outputDirectory.stopAccessingSecurityScopedResource()
            }
        }

        var revealURLs: [URL] = []

        for pakURL in pakURLs {
            let accessedSecurityScope = pakURL.startAccessingSecurityScopedResource()
            defer {
                if accessedSecurityScope {
                    pakURL.stopAccessingSecurityScopedResource()
                }
            }

            do {
                let pakFile = try loadPak(at: pakURL)
                let destination = nextAvailableURL(
                    in: outputDirectory,
                    baseName: pakURL.deletingPathExtension().lastPathComponent,
                    pathExtension: nil
                )
                try PakFilesystemExporter.export(root: pakFile.root, originalData: pakFile.data, to: destination)
                revealURLs.append(destination)
            } catch let serviceError {
                errorOut?.pointee = serviceError.localizedDescription.NSStringValue
                return
            }
        }

        if !revealURLs.isEmpty {
            NSWorkspace.shared.activateFileViewerSelecting(revealURLs)
        }
    }

    @objc func packFolderService(_ pboard: NSPasteboard, userData: String?, error errorOut: AutoreleasingUnsafeMutablePointer<NSString?>?) {
        let urls = fileURLs(from: pboard)
        let folders = urls.filter(isDirectory)

        guard !folders.isEmpty else {
            errorOut?.pointee = "Select a folder to pack.".NSStringValue
            return
        }

        guard let outputDirectory = chooseOutputDirectory(
            title: "Choose Where to Save the PAK",
            message: folders.count == 1
                ? "PakScape will create a PAK named after the selected folder."
                : "PakScape will create one PAK for each selected folder.",
            initialDirectory: folders.first?.deletingLastPathComponent()
        ) else {
            return
        }

        let accessedOutputScope = outputDirectory.startAccessingSecurityScopedResource()
        defer {
            if accessedOutputScope {
                outputDirectory.stopAccessingSecurityScopedResource()
            }
        }

        var revealURLs: [URL] = []

        for folder in folders {
            let accessedSecurityScope = folder.startAccessingSecurityScopedResource()
            defer {
                if accessedSecurityScope {
                    folder.stopAccessingSecurityScopedResource()
                }
            }

            do {
                let root = try PakLoader.loadDirectoryTree(at: folder)
                let output = try PakWriter.write(root: root, originalData: nil)
                let destination = nextAvailableURL(
                    in: outputDirectory,
                    baseName: folder.lastPathComponent,
                    pathExtension: "pak"
                )
                try output.data.write(to: destination, options: .atomic)
                revealURLs.append(destination)
            } catch let serviceError {
                errorOut?.pointee = serviceError.localizedDescription.NSStringValue
                return
            }
        }

        if !revealURLs.isEmpty {
            NSWorkspace.shared.activateFileViewerSelecting(revealURLs)
        }
    }
}

private extension FinderServiceProvider {
    func fileURLs(from pasteboard: NSPasteboard) -> [URL] {
        let classes = [NSURL.self]
        let options: [NSPasteboard.ReadingOptionKey: Any] = [
            .urlReadingFileURLsOnly: true
        ]
        return pasteboard.readObjects(forClasses: classes, options: options) as? [URL] ?? []
    }

    func loadPak(at url: URL) throws -> PakFile {
        let ext = url.pathExtension.lowercased()
        if ext == "pk3" {
            return try PakLoader.loadZip(from: url, name: url.lastPathComponent)
        }

        let data = try Data(contentsOf: url)
        return try PakLoader.load(data: data, name: url.lastPathComponent)
    }

    func isDirectory(_ url: URL) -> Bool {
        var isDir: ObjCBool = false
        fileManager.fileExists(atPath: url.path, isDirectory: &isDir)
        return isDir.boolValue
    }

    func chooseOutputDirectory(title: String, message: String, initialDirectory: URL?) -> URL? {
        let panel = NSOpenPanel()
        panel.title = title
        panel.message = message
        panel.prompt = "Choose"
        panel.directoryURL = initialDirectory
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.allowsMultipleSelection = false
        return panel.runModal() == .OK ? panel.url : nil
    }

    func nextAvailableURL(in directory: URL, baseName: String, pathExtension: String?) -> URL {
        var candidateName = baseName
        var suffix = 1

        while true {
            let filename: String
            if let pathExtension, !pathExtension.isEmpty {
                filename = "\(candidateName).\(pathExtension)"
            } else {
                filename = candidateName
            }

            let candidate = directory.appendingPathComponent(filename, isDirectory: pathExtension == nil)
            if !fileManager.fileExists(atPath: candidate.path) {
                return candidate
            }

            suffix += 1
            candidateName = "\(baseName) \(suffix)"
        }
    }
}

private extension String {
    var NSStringValue: NSString {
        self as NSString
    }
}
