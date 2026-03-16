import SwiftUI
import AppKit
import Combine
import AVFoundation
import UniformTypeIdentifiers

final class PakViewModel: NSObject, ObservableObject, AVAudioPlayerDelegate {
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
    private static var sharedClipboard: ClipboardPayload?
    private var clipboard: ClipboardPayload? {
        get { Self.sharedClipboard }
        set { Self.sharedClipboard = newValue }
    }
    private static let previewableImageExtensions: Set<String> = ["png", "jpg", "jpeg", "gif", "tif", "tiff", "bmp", "heic", "heif", "tga"]
    private static let previewableAudioExtensions: Set<String> = ["wav", "mp3", "m4a", "aac", "aif", "aiff", "caf", "au", "snd"]
    var documentURL: URL?
    private var isNavigatingHistory = false
    private var audioPreviewPlayer: AVAudioPlayer?
    private var audioPreviewTimer: Timer?
    private var audioPreviewNodeID: PakNode.ID?
    private var audioPreviewProgress: Double = 0
    private var previewImageCacheVersion: UUID?
    private var previewImageCache: [PakNode.ID: NSImage] = [:]
    private var previewImageMisses: Set<PakNode.ID> = []

    var canSave: Bool {
        pakFile != nil && hasUnsavedChanges
    }

    var canNavigateBack: Bool {
        !backStack.isEmpty
    }

    var canNavigateForward: Bool {
        !forwardStack.isEmpty
    }

    var canCutCopy: Bool {
        !selectedNodes.isEmpty
    }

    var canPaste: Bool {
        guard currentFolder != nil else { return false }
        if clipboard != nil { return true }
        return !pasteboardFileURLs().isEmpty
    }

    init(pakFile: PakFile?, documentURL: URL? = nil) {
        self.pakFile = pakFile
        self.documentURL = documentURL
        super.init()
        resetNavigation(to: pakFile?.root)
    }

    deinit {
        stopAudioPreview()
    }

    func updateDocumentURL(_ url: URL?) {
        documentURL = url
    }

    enum ExportError: Error {
        case missingData
    }

    struct AudioPreviewVisualState {
        let isCurrent: Bool
        let isPlaying: Bool
        let progress: Double

        static let inactive = AudioPreviewVisualState(isCurrent: false, isPlaying: false, progress: 0)
    }

    private final class ClipboardPayload {
        let nodes: [PakNode]       // Deep copies used as templates for pasting
        let isCut: Bool
        let originalIDs: [PakNode.ID]
        let exportedURLs: [URL]    // Temp file URLs for Finder paste
        weak var sourceModel: PakViewModel?

        init(
            nodes: [PakNode],
            isCut: Bool,
            originalIDs: [PakNode.ID],
            exportedURLs: [URL],
            sourceModel: PakViewModel?
        ) {
            self.nodes = nodes
            self.isCut = isCut
            self.originalIDs = originalIDs
            self.exportedURLs = exportedURLs
            self.sourceModel = sourceModel
        }
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

    func openInDefaultApp(node: PakNode) {
        guard !node.isFolder else { return }

        do {
            let url = try exportToTemporaryLocation(node: node)
            NSWorkspace.shared.open(url)
        } catch {
            let alert = NSAlert()
            alert.alertStyle = .warning
            alert.messageText = "Unable to open \(node.name)"
            alert.informativeText = error.localizedDescription
            alert.runModal()
        }
    }

    func canPreviewAudio(_ node: PakNode) -> Bool {
        guard !node.isFolder else { return false }

        let ext = (node.name as NSString).pathExtension.lowercased()
        guard !ext.isEmpty else { return false }

        if Self.previewableAudioExtensions.contains(ext) {
            return true
        }

        return UTType(filenameExtension: ext)?.conforms(to: .audio) == true
    }

    func audioPreviewState(for node: PakNode) -> AudioPreviewVisualState {
        guard canPreviewAudio(node), audioPreviewNodeID == node.id else {
            return .inactive
        }

        return AudioPreviewVisualState(
            isCurrent: true,
            isPlaying: audioPreviewPlayer?.isPlaying == true,
            progress: audioPreviewProgress
        )
    }

    func toggleAudioPreview(for node: PakNode) {
        guard canPreviewAudio(node), !node.isFolder else { return }

        if audioPreviewNodeID == node.id, audioPreviewPlayer?.isPlaying == true {
            stopAudioPreview()
            return
        }

        guard let data = extractData(for: node) else { return }

        stopAudioPreview()

        do {
            let player = try AVAudioPlayer(data: data)
            player.delegate = self
            player.prepareToPlay()

            audioPreviewPlayer = player
            audioPreviewNodeID = node.id
            audioPreviewProgress = 0

            if player.play() {
                startAudioPreviewTimer()
                postAudioPreviewStateDidChange()
            } else {
                stopAudioPreview()
            }
        } catch {
            stopAudioPreview()
        }
    }

    func stopAudioPreview() {
        audioPreviewTimer?.invalidate()
        audioPreviewTimer = nil

        audioPreviewPlayer?.stop()
        audioPreviewPlayer = nil

        let hadState = audioPreviewNodeID != nil || audioPreviewProgress > 0
        audioPreviewNodeID = nil
        audioPreviewProgress = 0

        if hadState {
            postAudioPreviewStateDidChange()
        }
    }

    func rename(node: PakNode, to newName: String) {
        let trimmed = newName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, trimmed != node.name else { return }

        // Calculate potential path length
        let currentEntryName = node.entry?.name ?? node.name
        let parentPath: String
        if let slashIndex = currentEntryName.lastIndex(of: "/") {
            parentPath = String(currentEntryName[..<currentEntryName.index(after: slashIndex)])
        } else {
            parentPath = ""
        }

        // Enforce 56-byte limit for the full path
        let maxPathLength = 55 // 56 bytes null-terminated
        let parentLength = parentPath.utf8.count
        let maxNameLength = maxPathLength - parentLength
        
        var validName = trimmed
        if validName.utf8.count > maxNameLength {
            let allowedBytes = validName.utf8.prefix(maxNameLength)
            if let truncated = String(allowedBytes) {
                validName = truncated
            }
        }
        
        guard !validName.isEmpty else { return }

        node.name = validName
        if let entry = node.entry {
            let updatedPath = parentPath + validName
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

    func copySelection() {
        createClipboard(isCut: false)
    }

    func cutSelection() {
        createClipboard(isCut: true)
    }

    @discardableResult
    func pasteIntoCurrentFolder() -> [PakNode] {
        guard let destination = currentFolder else { return [] }

        if let payload = clipboard {
            if payload.isCut,
               let source = payload.sourceModel {
                for id in payload.originalIDs {
                    if let original = findNode(with: id, in: source.pakFile?.root),
                       isDescendant(destination, of: original) {
                        let alert = NSAlert()
                        alert.alertStyle = .warning
                        alert.messageText = "Cannot Move Into Itself"
                        alert.informativeText = "You cannot move a folder into itself or one of its subfolders."
                        alert.runModal()
                        return []
                    }
                }
                source.removeNodes(withIDs: Set(payload.originalIDs), from: source.pakFile?.root)
                source.markDirty()
            }

            var inserted: [PakNode] = []
            for template in payload.nodes {
                let clone = cloneNode(template)
                clone.name = availableName(for: clone.name, in: destination)
                insert(node: clone, into: destination)
                inserted.append(clone)
            }

            sortFolder(destination)
            markDirty()

            if payload.isCut {
                clipboard = nil
            }

            selectedNodes = inserted
            selectedFile = inserted.first
            return inserted
        }

        let urls = pasteboardFileURLs()
        guard !urls.isEmpty else { return [] }
        let inserted = pasteFileURLs(urls, into: destination)
        return inserted
    }

    private func createClipboard(isCut: Bool) {
        guard !selectedNodes.isEmpty else { return }

        do {
            let exportedURLs = try exportSelectionForPasteboard(nodes: selectedNodes)
            let snapshots = selectedNodes.map { cloneNodeForClipboard($0) }
            let ids = isCut ? selectedNodes.map { $0.id } : []
            clipboard = ClipboardPayload(
                nodes: snapshots,
                isCut: isCut,
                originalIDs: ids,
                exportedURLs: exportedURLs,
                sourceModel: isCut ? self : nil
            )
            prepareFinderPasteboard(with: exportedURLs)
        } catch {
            let alert = NSAlert(error: error)
            alert.alertStyle = .warning
            alert.messageText = "Copy failed"
            alert.informativeText = error.localizedDescription
            alert.runModal()
        }
    }

    private func cloneNodeForClipboard(_ node: PakNode) -> PakNode {
        let copy = PakNode(name: node.name)
        if node.isFolder {
            copy.children = node.children?.map { cloneNodeForClipboard($0) }
        } else {
            if let data = node.localData {
                copy.localData = data
            } else if let data = extractData(for: node) {
                copy.localData = data
            }
            copy.entry = nil
        }
        return copy
    }

    private func cloneNode(_ node: PakNode) -> PakNode {
        let copy = PakNode(name: node.name)
        if node.isFolder {
            copy.children = node.children?.map { cloneNode($0) }
        } else {
            copy.localData = node.localData
            copy.entry = node.entry
        }
        return copy
    }

    private func availableName(for originalName: String, in folder: PakNode) -> String {
        let existing = Set((folder.children ?? []).map { $0.name.lowercased() })
        if !existing.contains(originalName.lowercased()) {
            return originalName
        }

        let ext = (originalName as NSString).pathExtension
        let base = (originalName as NSString).deletingPathExtension

        var attempt = 1
        while true {
            let suffix = attempt == 1 ? " copy" : " copy \(attempt)"
            let candidateBase = base + suffix
            let candidate = ext.isEmpty ? candidateBase : "\(candidateBase).\(ext)"
            if !existing.contains(candidate.lowercased()) {
                return candidate
            }
            attempt += 1
        }
    }

    private func insert(node: PakNode, into folder: PakNode) {
        if folder.children == nil { folder.children = [] }
        folder.children?.append(node)
    }

    private func removeNodes(withIDs ids: Set<PakNode.ID>, from root: PakNode?) {
        guard let root else { return }
        if var children = root.children {
            children.removeAll { ids.contains($0.id) }
            root.children = children
        }
        for child in root.children ?? [] where child.isFolder {
            removeNodes(withIDs: ids, from: child)
        }
    }

    private func findNode(with id: PakNode.ID, in root: PakNode?) -> PakNode? {
        guard let root else { return nil }
        if root.id == id { return root }
        for child in root.children ?? [] {
            if child.id == id {
                return child
            }
            if let found = findNode(with: id, in: child) {
                return found
            }
        }
        return nil
    }

    private func isDescendant(_ node: PakNode, of possibleAncestor: PakNode) -> Bool {
        for child in possibleAncestor.children ?? [] {
            if child === node {
                return true
            }
            if isDescendant(node, of: child) {
                return true
            }
        }
        return false
    }

    private func pasteboardFileURLs() -> [URL] {
        let pasteboard = NSPasteboard.general
        let classes = [NSURL.self]
        let options: [NSPasteboard.ReadingOptionKey: Any] = [
            .urlReadingFileURLsOnly: true
        ]
        let objects = pasteboard.readObjects(forClasses: classes, options: options) as? [URL] ?? []
        return objects
    }

    private func pasteFileURLs(_ urls: [URL], into folder: PakNode) -> [PakNode] {
        var inserted: [PakNode] = []
        for url in urls {
            if let node = createNodeFromFileURL(url, in: folder) {
                insert(node: node, into: folder)
                inserted.append(node)
            }
        }
        sortFolder(folder)
        markDirty()
        selectedNodes = inserted
        selectedFile = inserted.first
        return inserted
    }

    private func createNodeFromFileURL(_ url: URL, in folder: PakNode) -> PakNode? {
        var isDir: ObjCBool = false
        guard FileManager.default.fileExists(atPath: url.path, isDirectory: &isDir) else { return nil }
        let name = availableName(for: url.lastPathComponent, in: folder)

        if isDir.boolValue {
            let node = PakNode(name: name)
            do {
                try PakLoader.buildTree(from: url, into: node)
                PakLoader.sortNodeRecursively(node)
                return node
            } catch {
                return nil
            }
        }

        do {
            let data = try Data(contentsOf: url)
            let node = PakNode(name: name)
            node.localData = data
            return node
        } catch {
            return nil
        }
    }

    private func exportSelectionForPasteboard(nodes: [PakNode]) throws -> [URL] {
        guard !nodes.isEmpty else { return [] }

        if nodes.count == 1, let first = nodes.first {
            let url = try exportToTemporaryLocation(node: first)
            return [url]
        }

        let base = try exportSelectionToTemporaryLocation(nodes: nodes)
        return nodes.map { node in
            base.appendingPathComponent(node.name, isDirectory: node.isFolder)
        }
    }

    private func prepareFinderPasteboard(with urls: [URL]) {
        guard !urls.isEmpty else { return }
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.writeObjects(urls as [NSURL])
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
        invalidatePreviewImageCacheIfNeeded()

        if let cachedImage = previewImageCache[node.id] {
            return cachedImage
        }
        if previewImageMisses.contains(node.id) {
            return nil
        }

        guard !node.isFolder, let data = extractData(for: node) else { return nil }

        let ext = (node.name as NSString).pathExtension.lowercased()
        let preview: NSImage?
        if ext == "lmp" {
            preview = LmpPreviewRenderer.renderImage(fileName: node.name, data: data)
        } else if ext == "pcx" {
            preview = PcxPreviewRenderer.renderImage(data: data) ?? NSImage(data: data)
        } else if ext == "tga" {
            preview = TgaPreviewRenderer.renderImage(data: data)
        } else if ext == "mdl" {
            preview = MdlPreviewRenderer.renderImage(data: data)
        } else if ext == "spr" {
            preview = SprPreviewRenderer.renderImage(data: data)
        } else if ext == "bsp" {
            preview = BspPreviewRenderer.renderImage(fileName: node.name, data: data)
                ?? BspLevelPreviewRenderer.renderImage(data: data)
        } else if ext == "wad" {
            preview = WadPreviewRenderer.renderImage(fileName: node.name, data: data)
        } else if Self.previewableImageExtensions.contains(ext) {
            preview = NSImage(data: data)
        } else {
            preview = nil
        }

        if let preview {
            previewImageCache[node.id] = preview
        } else {
            previewImageMisses.insert(node.id)
        }

        return preview
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

        if let audioPreviewNodeID, idsToDelete.contains(audioPreviewNodeID) {
            stopAudioPreview()
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

    private func invalidatePreviewImageCacheIfNeeded() {
        let currentVersion = pakFile?.version
        guard previewImageCacheVersion != currentVersion else { return }

        previewImageCacheVersion = currentVersion
        previewImageCache.removeAll(keepingCapacity: true)
        previewImageMisses.removeAll(keepingCapacity: true)
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
        if oldValue !== newValue {
            stopAudioPreview()
        }

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

    private func startAudioPreviewTimer() {
        audioPreviewTimer?.invalidate()

        let timer = Timer(timeInterval: 0.1, repeats: true) { [weak self] _ in
            self?.updateAudioPreviewProgress()
        }
        audioPreviewTimer = timer
        RunLoop.main.add(timer, forMode: .common)
    }

    private func updateAudioPreviewProgress() {
        guard let player = audioPreviewPlayer else {
            stopAudioPreview()
            return
        }

        guard player.duration > 0 else {
            audioPreviewProgress = 0
            postAudioPreviewStateDidChange()
            return
        }

        audioPreviewProgress = min(max(player.currentTime / player.duration, 0), 1)
        postAudioPreviewStateDidChange()
    }

    private func postAudioPreviewStateDidChange() {
        NotificationCenter.default.post(name: .pakAudioPreviewStateDidChange, object: self)
    }

    func audioPlayerDidFinishPlaying(_ player: AVAudioPlayer, successfully flag: Bool) {
        stopAudioPreview()
    }

    func audioPlayerDecodeErrorDidOccur(_ player: AVAudioPlayer, error: Error?) {
        stopAudioPreview()
    }
}

extension Notification.Name {
    static let pakAudioPreviewStateDidChange = Notification.Name("PakAudioPreviewStateDidChange")
}

private enum MdlPreviewRenderer {
    private static let headerSize = 84

    static func renderImage(data: Data) -> NSImage? {
        guard data.count >= headerSize else { return nil }

        let reportedSkins = readInt32LE(data, offset: 48) ?? 0
        guard reportedSkins > 0,
              let skinWidth = readInt32LE(data, offset: 52), skinWidth > 0,
              let skinHeight = readInt32LE(data, offset: 56), skinHeight > 0 else {
            return nil
        }

        let width = skinWidth
        let height = skinHeight

        guard let skinData = firstSkinBytes(in: data, width: width, height: height) else {
            return nil
        }

        return image(from: skinData, width: width, height: height)
    }

    private static func firstSkinBytes(in data: Data, width: Int, height: Int) -> Data? {
        let skinSizeResult = width.multipliedReportingOverflow(by: height)
        guard !skinSizeResult.overflow else { return nil }
        let skinByteCount = skinSizeResult.partialValue
        guard skinByteCount > 0 else { return nil }

        var cursor = headerSize
        guard cursor + 4 <= data.count else { return nil }

        var groupIndicator = readInt32LE(data, offset: cursor) ?? 0
        cursor += 4

        // Some legacy or malformed files might omit/garble the group flag.
        if groupIndicator != 0 && groupIndicator != 1 {
            cursor -= 4
            groupIndicator = 0
        }

        if groupIndicator == 0 {
            guard cursor + skinByteCount <= data.count else { return nil }
            return data.subdata(in: cursor ..< cursor + skinByteCount)
        }

        guard cursor + 4 <= data.count else { return nil }
        let groupCount = readInt32LE(data, offset: cursor) ?? 0
        cursor += 4
        guard groupCount > 0 else { return nil }

        let timeBytesResult = groupCount.multipliedReportingOverflow(by: MemoryLayout<Float32>.size)
        guard !timeBytesResult.overflow else { return nil }
        let timeBytes = timeBytesResult.partialValue
        guard cursor + timeBytes <= data.count else { return nil }
        cursor += timeBytes

        guard cursor + skinByteCount <= data.count else { return nil }
        return data.subdata(in: cursor ..< cursor + skinByteCount)
    }

    private static func image(from skin: Data, width: Int, height: Int) -> NSImage? {
        let palette = QuakePalette.bytes
        guard palette.count >= 768 else { return nil }

        let pixelCountResult = width.multipliedReportingOverflow(by: height)
        guard !pixelCountResult.overflow else { return nil }
        let pixelCount = pixelCountResult.partialValue
        guard skin.count >= pixelCount else { return nil }

        let rgbaBytesResult = pixelCount.multipliedReportingOverflow(by: 4)
        guard !rgbaBytesResult.overflow else { return nil }
        var rgba = Data(count: rgbaBytesResult.partialValue)

        var conversionSucceeded = true
        rgba.withUnsafeMutableBytes { destBuffer in
            guard let dest = destBuffer.bindMemory(to: UInt8.self).baseAddress else {
                conversionSucceeded = false
                return
            }

            skin.withUnsafeBytes { srcBuffer in
                guard let src = srcBuffer.bindMemory(to: UInt8.self).baseAddress else {
                    conversionSucceeded = false
                    return
                }

                for i in 0..<pixelCount {
                    let paletteIndex = Int(src[i])
                    if paletteIndex == 255 {
                        let destIndex = i * 4
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
                    let destIndex = i * 4
                    dest[destIndex] = palette[paletteOffset]
                    dest[destIndex + 1] = palette[paletteOffset + 1]
                    dest[destIndex + 2] = palette[paletteOffset + 2]
                    dest[destIndex + 3] = 255
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

    @inline(__always)
    private static func readInt32LE(_ data: Data, offset: Int) -> Int? {
        guard offset >= 0, offset + 4 <= data.count else { return nil }
        return data.subdata(in: offset ..< offset + 4).withUnsafeBytes {
            Int(Int32(littleEndian: $0.load(as: Int32.self)))
        }
    }
}

private enum SprPreviewRenderer {
    private static let headerSize = 36
    private static let spriteSingle = 0
    private static let spriteGroup = 1

    static func renderImage(data: Data) -> NSImage? {
        guard data.count >= headerSize else { return nil }

        // Magic ("IDSP") and version (expect 1) are advisory; be lenient.
        let width = readInt32LE(data, offset: 16) ?? 0
        let height = readInt32LE(data, offset: 20) ?? 0
        let frames = readInt32LE(data, offset: 24) ?? 0
        guard width > 0, height > 0, frames > 0 else { return nil }

        guard let frame = firstFrame(in: data, defaultWidth: width, defaultHeight: height) else {
            return nil
        }
        return image(from: frame.pixels, width: frame.width, height: frame.height)
    }

    private struct SpriteFrame {
        let pixels: Data
        let width: Int
        let height: Int
    }

    private static func firstFrame(in data: Data, defaultWidth: Int, defaultHeight: Int) -> SpriteFrame? {
        var cursor = headerSize
        guard cursor + 4 <= data.count else { return nil }

        var type = readInt32LE(data, offset: cursor) ?? spriteSingle
        cursor += 4
        if type != spriteSingle && type != spriteGroup {
            type = spriteSingle
            cursor -= 4
        }

        if type == spriteSingle {
            return readFrame(data: data, cursor: &cursor, defaultWidth: defaultWidth, defaultHeight: defaultHeight)
        }

        guard cursor + 4 <= data.count else { return nil }
        let groupCount = readInt32LE(data, offset: cursor) ?? 0
        cursor += 4
        guard groupCount > 0 else { return nil }

        let intervalBytesResult = groupCount.multipliedReportingOverflow(by: MemoryLayout<Float32>.size)
        guard !intervalBytesResult.overflow else { return nil }
        let intervalBytes = intervalBytesResult.partialValue
        guard cursor + intervalBytes <= data.count else { return nil }
        cursor += intervalBytes

        return readFrame(data: data, cursor: &cursor, defaultWidth: defaultWidth, defaultHeight: defaultHeight)
    }

    private static func readFrame(data: Data, cursor: inout Int, defaultWidth: Int, defaultHeight: Int) -> SpriteFrame? {
        guard cursor + 16 <= data.count else { return nil }
        // originX/originY are ignored for preview
        let frameWidth = readInt32LE(data, offset: cursor + 8) ?? defaultWidth
        let frameHeight = readInt32LE(data, offset: cursor + 12) ?? defaultHeight
        cursor += 16

        let pixelCountResult = frameWidth.multipliedReportingOverflow(by: frameHeight)
        guard !pixelCountResult.overflow else { return nil }
        let pixelCount = pixelCountResult.partialValue
        guard pixelCount > 0 else { return nil }

        guard cursor + pixelCount <= data.count else { return nil }
        let pixels = data.subdata(in: cursor ..< cursor + pixelCount)
        cursor += pixelCount
        return SpriteFrame(pixels: pixels, width: frameWidth, height: frameHeight)
    }

    private static func image(from skin: Data, width: Int, height: Int) -> NSImage? {
        let palette = QuakePalette.bytes
        guard palette.count >= 768 else { return nil }

        let pixelCountResult = width.multipliedReportingOverflow(by: height)
        guard !pixelCountResult.overflow else { return nil }
        let pixelCount = pixelCountResult.partialValue
        guard skin.count >= pixelCount else { return nil }

        let rgbaBytesResult = pixelCount.multipliedReportingOverflow(by: 4)
        guard !rgbaBytesResult.overflow else { return nil }
        var rgba = Data(count: rgbaBytesResult.partialValue)

        var conversionSucceeded = true
        rgba.withUnsafeMutableBytes { destBuffer in
            guard let dest = destBuffer.bindMemory(to: UInt8.self).baseAddress else {
                conversionSucceeded = false
                return
            }

            skin.withUnsafeBytes { srcBuffer in
                guard let src = srcBuffer.bindMemory(to: UInt8.self).baseAddress else {
                    conversionSucceeded = false
                    return
                }

                for i in 0..<pixelCount {
                    let paletteIndex = Int(src[i])
                    let paletteOffset = paletteIndex * 3
                    guard paletteOffset + 2 < palette.count else {
                        conversionSucceeded = false
                        return
                    }
                    let destIndex = i * 4
                    dest[destIndex] = palette[paletteOffset]
                    dest[destIndex + 1] = palette[paletteOffset + 1]
                    dest[destIndex + 2] = palette[paletteOffset + 2]
                    dest[destIndex + 3] = 255
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

    @inline(__always)
    private static func readInt32LE(_ data: Data, offset: Int) -> Int? {
        guard offset >= 0, offset + 4 <= data.count else { return nil }
        return data.subdata(in: offset ..< offset + 4).withUnsafeBytes {
            Int(Int32(littleEndian: $0.load(as: Int32.self)))
        }
    }
}

private enum BspPreviewRenderer {
    private static let interestedNames: Set<String> = [
        "b_rock0.bsp", "b_rock1.bsp",
        "b_shell0.bsp", "b_shell1.bsp",
        "b_nail0.bsp", "b_nail1.bsp",
        "b_explob.bsp",
        "b_bh100.bsp", "b_bh25.bsp", "b_bh10.bsp",
        "b_batt0.bsp", "b_batt1.bsp"
    ]

    private static let texturesLumpIndex = 2
    private static let lumpCountBsp29 = 15
    private static let lumpCountBsp23 = 14

    static func renderImage(fileName: String, data: Data) -> NSImage? {
        let lower = fileName.lowercased()
        guard interestedNames.contains(lower) else { return nil }
        guard data.count >= 4 else { return nil }

        let version = readInt32LE(data, offset: 0) ?? 0
        let lumpCount = version == 23 ? lumpCountBsp23 : lumpCountBsp29
        guard lumpCount > texturesLumpIndex else { return nil }

        let minHeaderBytes = 4 + lumpCount * 8
        guard data.count >= minHeaderBytes else { return nil }

        let textureLump = readLump(index: texturesLumpIndex, lumpCount: lumpCount, in: data)
        guard let lump = textureLump,
              lump.offset >= 0,
              lump.length > 4,
              lump.offset + lump.length <= data.count else {
            return nil
        }

        let lumpStart = lump.offset
        guard let textureCount = readInt32LE(data, offset: lumpStart), textureCount > 0 else {
            return nil
        }

        let offsetsBytesResult = textureCount.multipliedReportingOverflow(by: 4)
        guard !offsetsBytesResult.overflow else { return nil }
        let offsetsBytes = offsetsBytesResult.partialValue
        let offsetsTableEndResult = (lumpStart + 4).addingReportingOverflow(offsetsBytes)
        guard !offsetsTableEndResult.overflow else { return nil }
        let offsetsTableEnd = offsetsTableEndResult.partialValue
        guard offsetsTableEnd <= lump.offset + lump.length, offsetsTableEnd <= data.count else {
            return nil
        }

        guard let firstOffset = readInt32LE(data, offset: lumpStart + 4) else { return nil }
        let mipBaseResult = lumpStart.addingReportingOverflow(firstOffset)
        guard !mipBaseResult.overflow else { return nil }
        let mipBase = mipBaseResult.partialValue
        guard mipBase + 40 <= data.count,
              mipBase + 40 <= lump.offset + lump.length else { return nil } // name + w/h + offsets

        guard let width = readInt32LE(data, offset: mipBase + 16),
              let height = readInt32LE(data, offset: mipBase + 20),
              width > 0, height > 0 else {
            return nil
        }

        guard let mipOffset = readInt32LE(data, offset: mipBase + 24), mipOffset >= 0 else {
            return nil
        }

        let pixelCountResult = width.multipliedReportingOverflow(by: height)
        guard !pixelCountResult.overflow else { return nil }
        let pixelCount = pixelCountResult.partialValue
        guard pixelCount > 0 else { return nil }

        let pixelStartResult = mipBase.addingReportingOverflow(mipOffset)
        guard !pixelStartResult.overflow else { return nil }
        let pixelStart = pixelStartResult.partialValue
        guard pixelStart >= 0,
              pixelStart + pixelCount <= data.count,
              pixelStart + pixelCount <= lump.offset + lump.length else { return nil }

        let pixels = data.subdata(in: pixelStart ..< pixelStart + pixelCount)
        return image(from: pixels, width: width, height: height)
    }

    private static func readLump(index: Int, lumpCount: Int, in data: Data) -> (offset: Int, length: Int)? {
        guard index >= 0, index < lumpCount else { return nil }
        let lumpOffset = 4 + index * 8
        guard lumpOffset + 8 <= data.count else { return nil }
        guard let offset = readInt32LE(data, offset: lumpOffset),
              let length = readInt32LE(data, offset: lumpOffset + 4) else {
            return nil
        }
        return (offset, length)
    }

    private static func image(from pixels: Data, width: Int, height: Int) -> NSImage? {
        let palette = QuakePalette.bytes
        guard palette.count >= 768 else { return nil }

        let pixelCountResult = width.multipliedReportingOverflow(by: height)
        guard !pixelCountResult.overflow else { return nil }
        let pixelCount = pixelCountResult.partialValue
        guard pixels.count >= pixelCount else { return nil }

        let rgbaBytesResult = pixelCount.multipliedReportingOverflow(by: 4)
        guard !rgbaBytesResult.overflow else { return nil }
        var rgba = Data(count: rgbaBytesResult.partialValue)

        var conversionSucceeded = true
        rgba.withUnsafeMutableBytes { destBuffer in
            guard let dest = destBuffer.bindMemory(to: UInt8.self).baseAddress else {
                conversionSucceeded = false
                return
            }

            pixels.withUnsafeBytes { srcBuffer in
                guard let src = srcBuffer.bindMemory(to: UInt8.self).baseAddress else {
                    conversionSucceeded = false
                    return
                }

                for i in 0..<pixelCount {
                    let paletteIndex = Int(src[i])
                    if paletteIndex == 255 {
                        let destIndex = i * 4
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
                    let destIndex = i * 4
                    dest[destIndex] = palette[paletteOffset]
                    dest[destIndex + 1] = palette[paletteOffset + 1]
                    dest[destIndex + 2] = palette[paletteOffset + 2]
                    dest[destIndex + 3] = 255
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

    @inline(__always)
    static func readInt32LE(_ data: Data, offset: Int) -> Int? {
        guard offset >= 0, offset + 4 <= data.count else { return nil }
        return data.subdata(in: offset ..< offset + 4).withUnsafeBytes {
            Int(Int32(littleEndian: $0.load(as: Int32.self)))
        }
    }
}

private enum WadPreviewRenderer {
    private struct Entry {
        let offset: Int
        let dsize: Int
        let size: Int
        let type: UInt8
        let name: String
    }

    static func renderImage(fileName: String, data: Data) -> NSImage? {
        guard fileName.lowercased() == "gfx.wad" else { return nil }
        guard data.count >= 12 else { return nil }

        guard let dirEntries = BspPreviewRenderer.readInt32LE(data, offset: 4),
              let dirOffset = BspPreviewRenderer.readInt32LE(data, offset: 8),
              dirEntries > 0, dirOffset >= 0 else {
            return nil
        }

        let entrySize = 32
        let directoryEndResult = dirOffset.addingReportingOverflow(dirEntries * entrySize)
        guard !directoryEndResult.overflow else { return nil }
        let directoryEnd = directoryEndResult.partialValue
        guard directoryEnd <= data.count else { return nil }

        var entries: [Entry] = []
        entries.reserveCapacity(dirEntries)

        for i in 0..<dirEntries {
            let base = dirOffset + i * entrySize
            guard base + entrySize <= data.count else { continue }
            guard let offset = BspPreviewRenderer.readInt32LE(data, offset: base),
                  let dsize = BspPreviewRenderer.readInt32LE(data, offset: base + 4),
                  let size = BspPreviewRenderer.readInt32LE(data, offset: base + 8) else {
                continue
            }
            let type = data[base + 12]
            // base+13 compression, base+14 padding(2 bytes)
            let nameData = data.subdata(in: base + 16 ..< base + 32)
            let name = asciiStringFromNullTerminated(nameData)

            guard offset >= 0, size > 0, offset + size <= data.count else { continue }
            entries.append(Entry(offset: offset, dsize: dsize, size: size, type: type, name: name))
        }

        let images = entries.compactMap { decodeImage(entry: $0, data: data) }
        guard !images.isEmpty else { return nil }
        return contactSheet(from: images)
    }

    private static func decodeImage(entry: Entry, data: Data) -> NSImage? {
        // Palette entries are not images.
        if entry.type == Character("@").asciiValue { return nil }

        if entry.type == Character("D").asciiValue {
            return decodeMiptex(entry: entry, data: data)
        }

        // Treat others as simple header (width, height, then palettized pixels).
        guard entry.offset + 8 <= data.count else { return nil }
        guard let width = BspPreviewRenderer.readInt32LE(data, offset: entry.offset),
              let height = BspPreviewRenderer.readInt32LE(data, offset: entry.offset + 4),
              width > 0, height > 0 else { return nil }

        let pixelCountResult = width.multipliedReportingOverflow(by: height)
        guard !pixelCountResult.overflow else { return nil }
        let pixelCount = pixelCountResult.partialValue
        guard pixelCount > 0 else { return nil }

        let pixelStart = entry.offset + 8
        guard pixelStart + pixelCount <= entry.offset + entry.size,
              pixelStart + pixelCount <= data.count else { return nil }

        let pixels = data.subdata(in: pixelStart ..< pixelStart + pixelCount)
        return image(from: pixels, width: width, height: height)
    }

    private static func decodeMiptex(entry: Entry, data: Data) -> NSImage? {
        let base = entry.offset
        guard base + 40 <= data.count else { return nil }

        guard let width = BspPreviewRenderer.readInt32LE(data, offset: base + 16),
              let height = BspPreviewRenderer.readInt32LE(data, offset: base + 20),
              let ofs1 = BspPreviewRenderer.readInt32LE(data, offset: base + 24),
              width > 0, height > 0, ofs1 >= 0 else {
            return nil
        }

        let pixelCountResult = width.multipliedReportingOverflow(by: height)
        guard !pixelCountResult.overflow else { return nil }
        let pixelCount = pixelCountResult.partialValue
        guard pixelCount > 0 else { return nil }

        let pixelStartResult = base.addingReportingOverflow(ofs1)
        guard !pixelStartResult.overflow else { return nil }
        let pixelStart = pixelStartResult.partialValue
        guard pixelStart + pixelCount <= base + entry.size,
              pixelStart + pixelCount <= data.count else { return nil }

        let pixels = data.subdata(in: pixelStart ..< pixelStart + pixelCount)
        return image(from: pixels, width: width, height: height)
    }

    private static func image(from pixels: Data, width: Int, height: Int) -> NSImage? {
        let palette = QuakePalette.bytes
        guard palette.count >= 768 else { return nil }

        let pixelCountResult = width.multipliedReportingOverflow(by: height)
        guard !pixelCountResult.overflow else { return nil }
        let pixelCount = pixelCountResult.partialValue
        guard pixels.count >= pixelCount else { return nil }

        let rgbaBytesResult = pixelCount.multipliedReportingOverflow(by: 4)
        guard !rgbaBytesResult.overflow else { return nil }
        var rgba = Data(count: rgbaBytesResult.partialValue)

        var conversionSucceeded = true
        rgba.withUnsafeMutableBytes { destBuffer in
            guard let dest = destBuffer.bindMemory(to: UInt8.self).baseAddress else {
                conversionSucceeded = false
                return
            }

            pixels.withUnsafeBytes { srcBuffer in
                guard let src = srcBuffer.bindMemory(to: UInt8.self).baseAddress else {
                    conversionSucceeded = false
                    return
                }

                for i in 0..<pixelCount {
                    let paletteIndex = Int(src[i])
                    if paletteIndex == 255 {
                        let destIndex = i * 4
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
                    let destIndex = i * 4
                    dest[destIndex] = palette[paletteOffset]
                    dest[destIndex + 1] = palette[paletteOffset + 1]
                    dest[destIndex + 2] = palette[paletteOffset + 2]
                    dest[destIndex + 3] = 255
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

    private static func contactSheet(from images: [NSImage]) -> NSImage? {
        let tileSize: CGFloat = 64
        let columns = 4
        let rows = Int(ceil(Double(images.count) / Double(columns)))
        let sheetSize = NSSize(width: CGFloat(columns) * tileSize, height: CGFloat(rows) * tileSize)
        guard sheetSize.width > 0, sheetSize.height > 0 else { return nil }

        let sheet = NSImage(size: sheetSize)
        sheet.lockFocus()
        NSColor.clear.setFill()
        NSRect(origin: .zero, size: sheetSize).fill()

        for (index, image) in images.enumerated() {
            let col = index % columns
            let row = index / columns
            let tileOrigin = NSPoint(x: CGFloat(col) * tileSize, y: sheetSize.height - CGFloat(row + 1) * tileSize)
            let targetRect = NSRect(origin: tileOrigin, size: NSSize(width: tileSize, height: tileSize))

            let rep = image.bestRepresentation(for: NSRect(origin: .zero, size: image.size), context: nil, hints: nil)
            NSGraphicsContext.current?.imageInterpolation = .high
            rep?.draw(in: targetRect)
        }

        sheet.unlockFocus()
        return sheet
    }

    private static func asciiStringFromNullTerminated(_ data: Data) -> String {
        let trimmed = data.prefix { $0 != 0 }
        return String(bytes: trimmed, encoding: .ascii) ?? ""
    }
}

private enum PcxPreviewRenderer {
    static func renderImage(data: Data) -> NSImage? {
        guard data.count >= 128, data[0] == 0x0A else { return nil }

        let encoding = data[2]
        guard encoding == 0 || encoding == 1 else { return nil }

        let bitsPerPixel = Int(data[3])
        let xMin = readUInt16LE(data, offset: 4) ?? 0
        let yMin = readUInt16LE(data, offset: 6) ?? 0
        let xMax = readUInt16LE(data, offset: 8) ?? 0
        let yMax = readUInt16LE(data, offset: 10) ?? 0
        let width = xMax - xMin + 1
        let height = yMax - yMin + 1

        guard width > 0, height > 0 else { return nil }

        let colorPlanes = Int(data[65])
        let bytesPerLine = readUInt16LE(data, offset: 66) ?? 0
        guard colorPlanes > 0, bytesPerLine >= width else { return nil }

        let rowStrideResult = colorPlanes.multipliedReportingOverflow(by: bytesPerLine)
        guard !rowStrideResult.overflow else { return nil }
        let rowStride = rowStrideResult.partialValue

        let decodedByteCountResult = rowStride.multipliedReportingOverflow(by: height)
        guard !decodedByteCountResult.overflow else { return nil }
        let decodedByteCount = decodedByteCountResult.partialValue

        let sourceLimit: Int
        let paletteOffset: Int?
        if bitsPerPixel == 8, colorPlanes == 1, data.count >= 897 {
            let candidate = data.count - 769
            if candidate >= 128, data[candidate] == 0x0C {
                sourceLimit = candidate
                paletteOffset = candidate + 1
            } else {
                sourceLimit = data.count
                paletteOffset = nil
            }
        } else {
            sourceLimit = data.count
            paletteOffset = nil
        }

        guard let decoded = decodeImageData(
            data,
            start: 128,
            limit: sourceLimit,
            expectedByteCount: decodedByteCount,
            isRLE: encoding == 1
        ) else {
            return nil
        }

        let rgbaByteCountResult = width.multipliedReportingOverflow(by: height)
        guard !rgbaByteCountResult.overflow else { return nil }
        let pixelCount = rgbaByteCountResult.partialValue

        let rgbaStorageResult = pixelCount.multipliedReportingOverflow(by: 4)
        guard !rgbaStorageResult.overflow else { return nil }
        var rgba = Data(count: rgbaStorageResult.partialValue)

        var conversionSucceeded = true
        rgba.withUnsafeMutableBytes { destBuffer in
            guard let dest = destBuffer.bindMemory(to: UInt8.self).baseAddress else {
                conversionSucceeded = false
                return
            }

            decoded.withUnsafeBytes { srcBuffer in
                guard let src = srcBuffer.bindMemory(to: UInt8.self).baseAddress else {
                    conversionSucceeded = false
                    return
                }

                if bitsPerPixel == 8, colorPlanes == 1 {
                    guard let paletteOffset, paletteOffset + 768 <= data.count else {
                        conversionSucceeded = false
                        return
                    }

                    for y in 0..<height {
                        let rowBase = y * rowStride
                        for x in 0..<width {
                            let paletteIndex = Int(src[rowBase + x])
                            let colorBase = paletteOffset + paletteIndex * 3
                            guard colorBase + 2 < data.count else {
                                conversionSucceeded = false
                                return
                            }

                            let destIndex = (y * width + x) * 4
                            dest[destIndex] = data[colorBase]
                            dest[destIndex + 1] = data[colorBase + 1]
                            dest[destIndex + 2] = data[colorBase + 2]
                            dest[destIndex + 3] = 255
                        }
                    }
                    return
                }

                guard bitsPerPixel == 8, colorPlanes == 3 || colorPlanes == 4 else {
                    conversionSucceeded = false
                    return
                }

                for y in 0..<height {
                    let rowBase = y * rowStride
                    let redBase = rowBase
                    let greenBase = rowBase + bytesPerLine
                    let blueBase = rowBase + bytesPerLine * 2
                    let alphaBase = colorPlanes == 4 ? rowBase + bytesPerLine * 3 : -1

                    for x in 0..<width {
                        let destIndex = (y * width + x) * 4
                        dest[destIndex] = src[redBase + x]
                        dest[destIndex + 1] = src[greenBase + x]
                        dest[destIndex + 2] = src[blueBase + x]
                        dest[destIndex + 3] = colorPlanes == 4 ? src[alphaBase + x] : 255
                    }
                }
            }
        }

        guard conversionSucceeded else { return nil }
        return image(fromRGBA: rgba, width: width, height: height)
    }

    private static func decodeImageData(
        _ data: Data,
        start: Int,
        limit: Int,
        expectedByteCount: Int,
        isRLE: Bool
    ) -> Data? {
        guard start >= 0, limit >= start, limit <= data.count, expectedByteCount >= 0 else {
            return nil
        }

        if !isRLE {
            guard limit - start >= expectedByteCount else { return nil }
            return data.subdata(in: start ..< start + expectedByteCount)
        }

        var output = Data(count: expectedByteCount)
        var sourceIndex = start
        var success = true

        output.withUnsafeMutableBytes { destBuffer in
            guard let dest = destBuffer.bindMemory(to: UInt8.self).baseAddress else {
                success = false
                return
            }

            data.withUnsafeBytes { srcBuffer in
                guard let src = srcBuffer.bindMemory(to: UInt8.self).baseAddress else {
                    success = false
                    return
                }

                var destIndex = 0
                while destIndex < expectedByteCount {
                    guard sourceIndex < limit else {
                        success = false
                        break
                    }

                    let value = src[sourceIndex]
                    sourceIndex += 1

                    if (value & 0xC0) == 0xC0 {
                        let runLength = Int(value & 0x3F)
                        guard runLength > 0, sourceIndex < limit else {
                            success = false
                            break
                        }

                        let repeatedValue = src[sourceIndex]
                        sourceIndex += 1

                        guard destIndex + runLength <= expectedByteCount else {
                            success = false
                            break
                        }

                        for _ in 0..<runLength {
                            dest[destIndex] = repeatedValue
                            destIndex += 1
                        }
                    } else {
                        dest[destIndex] = value
                        destIndex += 1
                    }
                }
            }
        }

        return success ? output : nil
    }

    private static func image(fromRGBA rgba: Data, width: Int, height: Int) -> NSImage? {
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

    private static func readUInt16LE(_ data: Data, offset: Int) -> Int? {
        guard offset + 2 <= data.count else { return nil }
        let low = Int(data[offset])
        let high = Int(data[offset + 1]) << 8
        return low | high
    }
}

private enum TgaPreviewRenderer {
    static func renderImage(data: Data) -> NSImage? {
        guard data.count >= 18 else { return nil }

        let idLength = Int(data[0])
        let colorMapType = data[1]
        let imageType = data[2]
        guard colorMapType == 0 else { return nil }

        let colorMapLength = readUInt16LE(data, offset: 5) ?? 0
        let colorMapEntrySize = Int(data[7])
        let colorMapBytesResult = colorMapLength.multipliedReportingOverflow(by: (colorMapEntrySize + 7) / 8)
        guard !colorMapBytesResult.overflow else { return nil }
        let baseOffset = 18 + idLength
        let pixelDataOffsetResult = baseOffset.addingReportingOverflow(colorMapBytesResult.partialValue)
        guard !pixelDataOffsetResult.overflow else { return nil }
        let pixelDataOffset = pixelDataOffsetResult.partialValue
        guard pixelDataOffset <= data.count else { return nil }

        guard let width = readUInt16LE(data, offset: 12),
              let height = readUInt16LE(data, offset: 14),
              width > 0, height > 0 else { return nil }

        let pixelDepth = data[16]
        let descriptor = data[17]
        let originTop = (descriptor & 0x20) != 0
        let originLeft = (descriptor & 0x10) == 0

        let supportedType = imageType == 2 || imageType == 3 || imageType == 10 || imageType == 11
        guard supportedType else { return nil }
        let isGrayscale = imageType == 3 || imageType == 11
        let isRle = imageType == 10 || imageType == 11

        let bytesPerPixel: Int
        switch (isGrayscale, pixelDepth) {
        case (true, 8):
            bytesPerPixel = 1
        case (true, 16):
            bytesPerPixel = 2
        case (false, 24):
            bytesPerPixel = 3
        case (false, 32):
            bytesPerPixel = 4
        case (false, 16):
            bytesPerPixel = 2
        default:
            return nil
        }

        let pixelCountResult = width.multipliedReportingOverflow(by: height)
        guard !pixelCountResult.overflow else { return nil }
        let pixelCount = pixelCountResult.partialValue

        let rgbaBytesResult = pixelCount.multipliedReportingOverflow(by: 4)
        guard !rgbaBytesResult.overflow else { return nil }
        var rgba = Data(count: rgbaBytesResult.partialValue)

        var conversionSucceeded = true
        rgba.withUnsafeMutableBytes { destBuffer in
            guard let dest = destBuffer.bindMemory(to: UInt8.self).baseAddress else {
                conversionSucceeded = false
                return
            }

            data.withUnsafeBytes { srcBuffer in
                guard let src = srcBuffer.bindMemory(to: UInt8.self).baseAddress else {
                    conversionSucceeded = false
                    return
                }

                func pixel(from offset: Int) -> (UInt8, UInt8, UInt8, UInt8)? {
                    guard offset >= 0, offset + bytesPerPixel <= data.count else { return nil }
                    let base = src + offset

                    if isGrayscale {
                        let value = base[0]
                        let alpha: UInt8 = bytesPerPixel == 2 ? base[1] : 255
                        return (value, value, value, alpha)
                    }

                    switch bytesPerPixel {
                    case 2:
                        let raw = UInt16(base[0]) | (UInt16(base[1]) << 8)
                        let b5 = raw & 0x1F
                        let g5 = (raw >> 5) & 0x1F
                        let r5 = (raw >> 10) & 0x1F
                        let alphaBit = (descriptor & 0x0F) > 0 ? ((raw & 0x8000) != 0) : true
                        let r = UInt8((r5 << 3) | (r5 >> 2))
                        let g = UInt8((g5 << 3) | (g5 >> 2))
                        let b = UInt8((b5 << 3) | (b5 >> 2))
                        let a: UInt8 = alphaBit ? 255 : 0
                        return (r, g, b, a)
                    case 3:
                        return (base[2], base[1], base[0], 255)
                    case 4:
                        return (base[2], base[1], base[0], base[3])
                    default:
                        return nil
                    }
                }

                func write(_ pixel: (UInt8, UInt8, UInt8, UInt8), at index: Int) -> Bool {
                    guard index >= 0, index < pixelCount else { return false }
                    let x = index % width
                    let y = index / width
                    let targetX = originLeft ? x : (width - 1 - x)
                    let targetY = originTop ? y : (height - 1 - y)
                    let destIndex = (targetY * width + targetX) * 4
                    dest[destIndex] = pixel.0
                    dest[destIndex + 1] = pixel.1
                    dest[destIndex + 2] = pixel.2
                    dest[destIndex + 3] = pixel.3
                    return true
                }

                var sourceIndex = pixelDataOffset
                var pixelIndex = 0

                func decodeRawPixels(count: Int) -> Bool {
                    for _ in 0..<count {
                        guard let rgbaPixel = pixel(from: sourceIndex) else { return false }
                        guard write(rgbaPixel, at: pixelIndex) else { return false }
                        pixelIndex += 1
                        sourceIndex += bytesPerPixel
                    }
                    return true
                }

                if isRle {
                    while pixelIndex < pixelCount {
                        guard sourceIndex < data.count else {
                            conversionSucceeded = false
                            break
                        }
                        let packetHeader = src[sourceIndex]
                        sourceIndex += 1
                        let packetCount = Int(packetHeader & 0x7F) + 1

                        if (packetHeader & 0x80) != 0 {
                            guard let rgbaPixel = pixel(from: sourceIndex) else {
                                conversionSucceeded = false
                                break
                            }
                            sourceIndex += bytesPerPixel
                            for _ in 0..<packetCount {
                                if pixelIndex >= pixelCount { break }
                                guard write(rgbaPixel, at: pixelIndex) else {
                                    conversionSucceeded = false
                                    break
                                }
                                pixelIndex += 1
                            }
                            if !conversionSucceeded { break }
                        } else {
                            if !decodeRawPixels(count: packetCount) {
                                conversionSucceeded = false
                                break
                            }
                        }
                    }
                } else {
                    let expectedBytesResult = pixelCount.multipliedReportingOverflow(by: bytesPerPixel)
                    guard !expectedBytesResult.overflow else {
                        conversionSucceeded = false
                        return
                    }
                    let expectedBytes = expectedBytesResult.partialValue
                    let neededBytes = pixelDataOffset.addingReportingOverflow(expectedBytes)
                    guard !neededBytes.overflow, neededBytes.partialValue <= data.count else {
                        conversionSucceeded = false
                        return
                    }
                    conversionSucceeded = decodeRawPixels(count: pixelCount)
                }

                if conversionSucceeded && pixelIndex != pixelCount {
                    conversionSucceeded = false
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

    private static func readUInt16LE(_ data: Data, offset: Int) -> Int? {
        guard offset + 2 <= data.count else { return nil }
        let low = UInt16(data[offset])
        let high = UInt16(data[offset + 1]) << 8
        return Int(low | high)
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

enum QuakePalette {
    static let bytes: [UInt8] = {
        guard let data = Data(base64Encoded: """
AAAADw8PHx8fLy8vPz8/S0tLW1tba2tre3t7i4uLm5ubq6uru7u7y8vL29vb6+vrDwsHFw8LHxcLJxsPLyMTNysXPy8XSzcbUzsbW0MfY0sfa1Mfc1cfe18jg2cjj28jCwsPExMbGxsnJyczLy8/NzdLPz9XR0dnT09zW1t/Y2OLa2uXc3Oje3uvg4O7i4vLAAAABwcACwsAExMAGxsAIyMAKysHLy8HNzcHPz8HR0cHS0sLU1MLW1sLY2MLa2sPBwAADwAAFwAAHwAAJwAALwAANwAAPwAARwAATwAAVwAAXwAAZwAAbwAAdwAAfwAAExMAGxsAIyMALysANy8AQzcASzsHV0MHX0cHa0sLd1MPg1cTi1sTl18bo2Mfr2cjIxMHLxcLOx8PSyMTVysXYy8fczcjfzsrj0Mzn08zr2Mvv3cvz48r36sn78sf//MbCwcAGxMAKyMPNysTRzMbUzcjYz8rb0czf1M/i19Hm2tTp3tft4drw5N706OL47OXq4ujn3+Xk3OHi2d7f1tvd1Nja0tXXz9LVzdDSy83QycvNx8jKxcbIxMTFwsLDwcHu3Ofr2uPo1+Dl1d3i09rf0tfc0NTaztLXzM/Uys3RyMrOx8jLxcbIxMTFwsLDwcH28O7y7Onv6Obr5eLo4d7l3tvh29fe2NTa1dHX0s7Uz8zQzMnNysfJx8XGxMPDwsHb4N7Z3tvX3NnV2tfT2NXR1tPP1NHN0s/L0M3KzsvIzMnHysfFyMXDxsTCxMLBwsH//Mb798X28sTy7cPu6cPq5cLm4MHi3MHe2MHa1MAW0cASzcAOysAKx8AGw8ACwcAAAD/CwvvExPfGxvPIyO/KyuvLy+fLy+PLy9/Ly9vLy9fKytPIyM/GxsvExMfCwsPKwAAOwAASwcAXwcAbw8AfxcHkx8HoycLtzMPw0sbz2Mr238745dP56tf779399OLp3s7t5s3x8M35+NXf7//q+f/1///ZwAAiwAAswAA1wAA/wAA//OT//fH////n1tT
""") else {
            return []
        }
        return Array(data)
    }()
}
