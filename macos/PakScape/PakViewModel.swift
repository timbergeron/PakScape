import SwiftUI
import AppKit
import Combine
import AVFoundation
import ImageIO
import QuickLookThumbnailing
import UniformTypeIdentifiers

private enum QuickLookPreparationError: LocalizedError {
    case tooManyItems(maximum: Int)
    case fileTooLarge(name: String, maximumSize: Int)
    case selectionTooLarge(maximumSize: Int)

    var errorDescription: String? {
        switch self {
        case .tooManyItems(let maximum):
            return "Quick Look supports up to \(maximum) selected items at a time."
        case .fileTooLarge(let name, let maximumSize):
            return "“\(name)” is larger than the \(formattedSize(maximumSize)) preview limit."
        case .selectionTooLarge(let maximumSize):
            return "The selection is larger than the \(formattedSize(maximumSize)) combined preview limit."
        }
    }

    private func formattedSize(_ bytes: Int) -> String {
        ByteCountFormatter.string(fromByteCount: Int64(bytes), countStyle: .file)
    }
}

private actor ThumbnailWorkLimiter {
    private let maximumConcurrentWork: Int
    private var activeWork = 0
    private var waiters: [CheckedContinuation<Void, Never>] = []

    init(maximumConcurrentWork: Int) {
        self.maximumConcurrentWork = maximumConcurrentWork
    }

    func acquire() async {
        if activeWork < maximumConcurrentWork {
            activeWork += 1
            return
        }

        await withCheckedContinuation { continuation in
            waiters.append(continuation)
        }
    }

    func release() {
        if waiters.isEmpty {
            activeWork = max(0, activeWork - 1)
        } else {
            waiters.removeFirst().resume()
        }
    }
}

final class PakViewModel: NSObject, ObservableObject, AVAudioPlayerDelegate {
    @Published var pakFile: PakFile?
    @Published var currentFolder: PakNode? { // Directory shown in right pane
        didSet {
            handleNavigationChange(from: oldValue, to: currentFolder)
        }
    }
    @Published var selectedFile: PakNode?  // File selected in right pane (first of selection for backward compatibility)
    @Published var selectedNodes: [PakNode] = [] // Multi-selection support
    @Published private(set) var selectionResetVersion = UUID()
    @Published private var backStack: [PakNode] = []
    @Published private var forwardStack: [PakNode] = []
    private(set) var isEditable: Bool
    private static var sharedClipboard: ClipboardPayload?
    private var clipboard: ClipboardPayload? {
        get { Self.sharedClipboard }
        set { Self.sharedClipboard = newValue }
    }
    private static let previewableAudioExtensions: Set<String> = ["wav", "mp3", "m4a", "aac", "aif", "aiff", "caf", "au", "snd"]
    private static let renderedQuickLookExtensions: Set<String> = ["bsp", "lmp", "mdl", "pcx", "spr", "tga", "wad"]
    private static let textQuickLookExtensions: Set<String> = [
        "arena", "cfg", "csv", "def", "ent", "ini", "json", "log", "map", "md",
        "menu", "qc", "rc", "shader", "txt", "xml", "yaml", "yml",
    ]
    private static let maximumPreviewFileSize = 128 * 1_024 * 1_024
    private static let maximumNativeThumbnailFileSize = 32 * 1_024 * 1_024
    private static let maximumTextThumbnailBytes = 2 * 1_024 * 1_024
    private static let nativeThumbnailWorkLimiter = ThumbnailWorkLimiter(maximumConcurrentWork: 4)
    private static let maximumPendingNativeThumbnailRequests = 32
    private static let maximumQuickLookSelectionSize = 256 * 1_024 * 1_024
    private static let maximumQuickLookItemCount = 1_000
    private static let maximumUndoLevels = 50
    private weak var undoManager: UndoManager?
    private var documentDidChange: ((PakFile) -> Void)?
    private var isNavigatingHistory = false
    private var audioPreviewPlayer: AVAudioPlayer?
    private var audioPreviewTimer: Timer?
    private var audioPreviewNodeID: PakNode.ID?
    private var audioPreviewProgress: Double = 0
    private var previewImageCacheVersion: UUID?
    private var previewImageCache: [PakNode.ID: NSImage] = [:]
    private var previewImageMisses: Set<PakNode.ID> = []
    private var previewImageRequests: [PakNode.ID: Task<Void, Never>] = [:]

    var canNavigateBack: Bool {
        !backStack.isEmpty
    }

    var canNavigateForward: Bool {
        !forwardStack.isEmpty
    }

    var canNavigateToParent: Bool {
        guard let current = currentFolder, let root = pakFile?.root else { return false }
        return current !== root
    }

    var canOpenSelectedFolder: Bool {
        guard let selected = selectedFile else { return false }
        return selected.isFolder
    }

    var canCut: Bool {
        isEditable && !selectedNodes.isEmpty
    }

    var canCopy: Bool {
        !selectedNodes.isEmpty
    }

    var canPaste: Bool {
        guard isEditable, currentFolder != nil else { return false }
        if clipboard != nil { return true }
        return !pasteboardFileURLs().isEmpty
    }

    init(pakFile: PakFile?, isEditable: Bool = true) {
        self.pakFile = pakFile
        self.isEditable = isEditable
        super.init()
        resetNavigation(to: pakFile?.root)
    }

    deinit {
        previewImageRequests.values.forEach { $0.cancel() }
        stopAudioPreview()
    }

    func connectDocument(
        undoManager: UndoManager?,
        onChange: @escaping (PakFile) -> Void
    ) {
        self.undoManager = undoManager
        if let undoManager, undoManager.levelsOfUndo == 0 {
            undoManager.levelsOfUndo = Self.maximumUndoLevels
        }
        documentDidChange = onChange
    }

    func updateEditableState(_ isEditable: Bool) {
        self.isEditable = isEditable
        objectWillChange.send()
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
        try PakPathValidator.validateNodeName(node.name)
        let base = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: base, withIntermediateDirectories: true)

        if node.isFolder {
            let destination = base.appendingPathComponent(node.name, isDirectory: true)
            try PakFilesystemExporter.export(node: node, originalData: pakFile?.data, to: destination)
            return destination
        }

        let data = try PakNodeData.data(for: node, originalData: pakFile?.data)

        let destination = base.appendingPathComponent(node.name)
        try data.write(to: destination)
        return destination
    }

    func toggleQuickLook(for nodes: [PakNode]) {
        if PakQuickLook.shared.isVisible {
            PakQuickLook.shared.hide()
            return
        }

        guard !nodes.isEmpty else { return }

        do {
            let items = try prepareQuickLookItems(for: nodes)
            PakQuickLook.shared.show(items: items)
        } catch {
            let alert = NSAlert()
            alert.alertStyle = .warning
            alert.messageText = "Unable to Preview Selection"
            alert.informativeText = error.localizedDescription
            alert.runModal()
        }
    }

    private func prepareQuickLookItems(for nodes: [PakNode]) throws -> [PakQuickLookItem] {
        guard nodes.count <= Self.maximumQuickLookItemCount else {
            throw QuickLookPreparationError.tooManyItems(maximum: Self.maximumQuickLookItemCount)
        }

        var totalSize = 0
        for node in nodes where !node.isFolder {
            guard node.fileSize <= Self.maximumPreviewFileSize else {
                throw QuickLookPreparationError.fileTooLarge(
                    name: node.name,
                    maximumSize: Self.maximumPreviewFileSize
                )
            }

            let newTotal = totalSize.addingReportingOverflow(node.fileSize)
            guard !newTotal.overflow, newTotal.partialValue <= Self.maximumQuickLookSelectionSize else {
                throw QuickLookPreparationError.selectionTooLarge(
                    maximumSize: Self.maximumQuickLookSelectionSize
                )
            }
            totalSize = newTotal.partialValue
        }

        var items: [PakQuickLookItem] = []
        items.reserveCapacity(nodes.count)

        do {
            for node in nodes {
                items.append(try prepareQuickLookItem(for: node))
            }
            return items
        } catch {
            let cleanupURLs = Set(items.map(\.cleanupURL))
            for url in cleanupURLs {
                try? FileManager.default.removeItem(at: url)
            }
            throw error
        }
    }

    private func prepareQuickLookItem(for node: PakNode) throws -> PakQuickLookItem {
        try PakPathValidator.validateNodeName(node.name)

        let fileManager = FileManager.default
        let base = fileManager.temporaryDirectory
            .appendingPathComponent("PakScape-QuickLook-" + UUID().uuidString, isDirectory: true)
        try fileManager.createDirectory(
            at: base,
            withIntermediateDirectories: false,
            attributes: [.posixPermissions: 0o700]
        )

        do {
            if node.isFolder {
                let destination = base.appendingPathComponent(node.name, isDirectory: true)
                try fileManager.createDirectory(at: destination, withIntermediateDirectories: false)
                return PakQuickLookItem(url: destination, title: node.name, cleanupURL: base)
            }

            let data = try PakNodeData.data(for: node, originalData: pakFile?.data)
            let ext = (node.name as NSString).pathExtension.lowercased()

            if Self.textQuickLookExtensions.contains(ext) {
                let destination = base.appendingPathComponent("preview.txt")
                try data.write(to: destination, options: .atomic)
                return PakQuickLookItem(url: destination, title: node.name, cleanupURL: base)
            }

            if Self.renderedQuickLookExtensions.contains(ext),
               let pngData = quickLookPNGData(fileName: node.name, data: data) {
                let destination = base.appendingPathComponent("preview.png")
                try pngData.write(to: destination, options: .atomic)
                return PakQuickLookItem(url: destination, title: node.name, cleanupURL: base)
            }

            let destination = base.appendingPathComponent(node.name)
            try data.write(to: destination, options: .atomic)
            return PakQuickLookItem(url: destination, title: node.name, cleanupURL: base)
        } catch {
            try? fileManager.removeItem(at: base)
            throw error
        }
    }

    private func pngData(for image: NSImage) -> Data? {
        guard let cgImage = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else {
            return nil
        }
        let output = NSMutableData()
        guard let destination = CGImageDestinationCreateWithData(
            output,
            UTType.png.identifier as CFString,
            1,
            nil
        ) else {
            return nil
        }
        CGImageDestinationAddImage(destination, cgImage, nil)
        guard CGImageDestinationFinalize(destination) else { return nil }
        return output as Data
    }

    private func quickLookPNGData(fileName: String, data: Data) -> Data? {
        autoreleasepool {
            guard let image = renderPreviewImage(fileName: fileName, data: data) else { return nil }
            return pngData(for: image)
        }
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
        guard !node.isFolder, node.fileSize <= Self.maximumPreviewFileSize else { return false }

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

    @discardableResult
    func rename(node: PakNode, to newName: String) -> Bool {
        guard isEditable else { return false }
        let trimmed = newName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            presentWarning(title: "Invalid Name", message: "Names cannot be empty.")
            return false
        }

        guard trimmed != node.name else { return true }

        guard PakPathValidator.isSafeNodeName(trimmed) else {
            presentWarning(
                title: "Invalid Name",
                message: "Names cannot be '.', '..', or contain slashes or control characters."
            )
            return false
        }

        if let root = pakFile?.root,
           let parent = parentNode(of: node, in: root),
           parent.children?.contains(where: {
               $0 !== node && $0.name.caseInsensitiveCompare(trimmed) == .orderedSame
           }) == true {
            presentWarning(
                title: "Name Already in Use",
                message: "An item named '\(trimmed)' already exists in this folder."
            )
            return false
        }

        applyRename(node: node, name: trimmed, actionName: "Rename")
        return true
    }

    private func presentWarning(title: String, message: String) {
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = title
        alert.informativeText = message
        alert.runModal()
    }

    func write(node: PakNode, toDirectory directory: URL) throws {
        try PakPathValidator.validateNodeName(node.name)
        let destination = directory.appendingPathComponent(node.name, isDirectory: node.isFolder)
        if node.isFolder {
            try PakFilesystemExporter.export(node: node, originalData: pakFile?.data, to: destination)
        } else {
            let data = try PakNodeData.data(for: node, originalData: pakFile?.data)
            try data.write(to: destination)
        }
    }

    func exportSelectionToTemporaryLocation(nodes: [PakNode]) throws -> URL {
        guard !nodes.isEmpty else {
            throw PakError.unknown("No items were selected for export.")
        }
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
        guard isEditable else { return }
        createClipboard(isCut: true)
    }

    @discardableResult
    func pasteIntoCurrentFolder() -> [PakNode] {
        guard isEditable, let destination = currentFolder else { return [] }

        if let payload = clipboard {
            let isSameDocumentMove = payload.isCut && payload.sourceModel === self
            if !isSameDocumentMove {
                do {
                    var budget = try PakImportBudget(existingRoot: pakFile?.root)
                    for template in payload.nodes {
                        try budget.registerTree(template)
                    }
                } catch {
                    presentWarning(title: "Couldn’t Paste Items", message: error.localizedDescription)
                    return []
                }
            }

            if isSameDocumentMove {
                for id in payload.originalIDs {
                    if let original = findNode(with: id, in: pakFile?.root),
                       destination === original || isDescendant(destination, of: original) {
                        let alert = NSAlert()
                        alert.alertStyle = .warning
                        alert.messageText = "Cannot Move Into Itself"
                        alert.informativeText = "You cannot move a folder into itself or one of its subfolders."
                        alert.runModal()
                        return []
                    }
                }
            }

            let removedPlacements: [PakNodePlacement]
            if isSameDocumentMove, let root = pakFile?.root {
                removedPlacements = PakTreeMutation.placements(for: Set(payload.originalIDs), in: root)
                removeNodes(withIDs: Set(payload.originalIDs), from: root)
            } else {
                // Cross-document cut/paste is intentionally a copy. Keeping Undo
                // scoped to one archive avoids partially undoing a two-window move.
                removedPlacements = []
            }

            var inserted: [PakNode] = []
            for template in payload.nodes {
                let clone = cloneNode(template)
                clone.name = availableName(for: clone.name, in: destination)
                insert(node: clone, into: destination)
                inserted.append(clone)
            }

            sortFolder(destination)
            let insertedPlacements = PakTreeMutation.placements(for: inserted, in: destination)
            registerTreeUndo(
                removing: insertedPlacements,
                inserting: removedPlacements,
                actionName: removedPlacements.isEmpty ? "Paste" : "Move"
            )
            notifyDocumentChanged()

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

    private func parentNode(of node: PakNode, in root: PakNode) -> PakNode? {
        if root.children?.contains(where: { $0 === node }) == true {
            return root
        }

        for child in root.children ?? [] where child.isFolder {
            if let parent = parentNode(of: node, in: child) {
                return parent
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
        guard isEditable else { return [] }
        var inserted: [PakNode] = []
        var failures: [String] = []
        var budget: PakImportBudget
        do {
            budget = try PakImportBudget(existingRoot: pakFile?.root)
        } catch {
            presentImportFailures([error.localizedDescription])
            return []
        }

        for url in urls {
            do {
                let node = try createNodeFromFileURL(url, in: folder, budget: &budget)
                insert(node: node, into: folder)
                inserted.append(node)
            } catch {
                failures.append("\(url.lastPathComponent): \(error.localizedDescription)")
            }
        }

        if !inserted.isEmpty {
            sortFolder(folder)
            let insertedPlacements = PakTreeMutation.placements(for: inserted, in: folder)
            registerTreeUndo(
                removing: insertedPlacements,
                inserting: [],
                actionName: "Import"
            )
            notifyDocumentChanged()
        }

        if !failures.isEmpty {
            presentImportFailures(failures)
        }

        selectedNodes = inserted
        selectedFile = inserted.first
        return inserted
    }

    private func createNodeFromFileURL(
        _ url: URL,
        in folder: PakNode,
        budget: inout PakImportBudget
    ) throws -> PakNode {
        let accessedSecurityScope = url.startAccessingSecurityScopedResource()
        defer {
            if accessedSecurityScope {
                url.stopAccessingSecurityScopedResource()
            }
        }

        let resourceKeys: Set<URLResourceKey> = [.isDirectoryKey, .isSymbolicLinkKey]
        let values = try url.resourceValues(forKeys: resourceKeys)
        guard values.isSymbolicLink != true else {
            throw PakError.unsafePath(url.lastPathComponent)
        }
        try budget.registerEntry()

        let name = availableName(for: url.lastPathComponent, in: folder)
        try PakPathValidator.validateNodeName(name)

        if values.isDirectory == true {
            let node = PakNode(name: name)
            try PakLoader.buildTree(from: url, into: node, budget: &budget)
            PakLoader.sortNodeRecursively(node)
            return node
        }

        let data = try PakLoader.readFile(at: url, budget: &budget)
        let node = PakNode(name: name)
        node.localData = data
        return node
    }

    private func presentImportFailures(_ failures: [String]) {
        let shownFailures = failures.prefix(4).joined(separator: "\n")
        let remainingCount = failures.count - min(failures.count, 4)
        let suffix = remainingCount > 0 ? "\n…and \(remainingCount) more." : ""
        presentWarning(
            title: failures.count == 1 ? "Couldn’t Import Item" : "Some Items Weren’t Imported",
            message: shownFailures + suffix
        )
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
        guard let node = selectedNodes.first ?? selectedFile else { return }

        let data: Data
        do {
            data = try PakNodeData.data(for: node, originalData: pakFile?.data)
        } catch {
            let alert = NSAlert(error: error)
            alert.runModal()
            return
        }

        let save = NSSavePanel()
        save.nameFieldStringValue = node.name
        
        save.begin { response in
            guard response == .OK, let outURL = save.url else { return }
            do {
                try data.write(to: outURL, options: .atomic)
            } catch {
                let alert = NSAlert(error: error)
                alert.runModal()
            }
        }
    }
    
    func extractData(for node: PakNode) -> Data? {
        try? PakNodeData.data(for: node, originalData: pakFile?.data)
    }

    func previewImage(for node: PakNode) -> NSImage? {
        invalidatePreviewImageCacheIfNeeded()

        if let cachedImage = previewImageCache[node.id] {
            return cachedImage
        }
        if previewImageMisses.contains(node.id) {
            return nil
        }
        if previewImageRequests[node.id] != nil {
            return nil
        }

        guard !node.isFolder, node.fileSize <= Self.maximumPreviewFileSize else {
            previewImageMisses.insert(node.id)
            return nil
        }

        let ext = (node.name as NSString).pathExtension.lowercased()
        let hasCustomRenderer = Self.renderedQuickLookExtensions.contains(ext)
        let nativeContentType = nativeThumbnailContentType(forExtension: ext)
        guard hasCustomRenderer || nativeContentType != nil else {
            previewImageMisses.insert(node.id)
            return nil
        }
        if !hasCustomRenderer,
           let nativeContentType,
           !nativeContentType.conforms(to: .text),
           node.fileSize > Self.maximumNativeThumbnailFileSize {
            previewImageMisses.insert(node.id)
            return nil
        }
        if hasCustomRenderer {
            guard let data = extractData(for: node) else {
                previewImageMisses.insert(node.id)
                return nil
            }
            if let preview = renderPreviewImage(fileName: node.name, data: data) {
                previewImageCache[node.id] = preview
                return preview
            }
        }

        if let nativeContentType {
            requestNativeThumbnail(
                for: node,
                contentType: nativeContentType,
                fileExtension: ext
            )
            return nil
        }

        previewImageMisses.insert(node.id)
        return nil
    }

    func systemIcon(for node: PakNode) -> NSImage {
        if node.isFolder {
            return NSWorkspace.shared.icon(for: .folder)
        }

        let ext = (node.name as NSString).pathExtension.lowercased()
        let contentType = nativeThumbnailContentType(forExtension: ext)
            ?? UTType(filenameExtension: ext)
            ?? .data
        return NSWorkspace.shared.icon(for: contentType)
    }

    private func nativeThumbnailContentType(forExtension ext: String) -> UTType? {
        guard !ext.isEmpty else { return nil }
        if Self.textQuickLookExtensions.contains(ext) {
            return .plainText
        }
        if let contentType = UTType(filenameExtension: ext) {
            if contentType.conforms(to: .image) ||
                contentType.conforms(to: .text) ||
                contentType.conforms(to: .pdf) ||
                contentType.conforms(to: .audiovisualContent) ||
                contentType.conforms(to: .compositeContent) ||
                contentType.conforms(to: .spreadsheet) ||
                contentType.conforms(to: .presentation) ||
                contentType.conforms(to: .threeDContent) {
                return contentType
            }
        }
        return nil
    }

    private func requestNativeThumbnail(
        for node: PakNode,
        contentType: UTType,
        fileExtension: String
    ) {
        guard previewImageRequests.count < Self.maximumPendingNativeThumbnailRequests else { return }
        let isText = contentType.conforms(to: .text)
        let thumbnailFileExtension = isText ? "txt" : fileExtension
        guard isText || node.fileSize <= Self.maximumNativeThumbnailFileSize else {
            previewImageMisses.insert(node.id)
            return
        }

        let byteLimit = isText
            ? Self.maximumTextThumbnailBytes
            : Self.maximumNativeThumbnailFileSize
        guard let source = try? PakNodeData.boundedSource(
            for: node,
            originalData: pakFile?.data,
            maximumLength: byteLimit
        ) else {
            previewImageMisses.insert(node.id)
            return
        }

        let nodeID = node.id
        let version = pakFile?.version

        previewImageRequests[nodeID] = Task { @MainActor [weak self] in
            guard let self else { return }
            await Self.nativeThumbnailWorkLimiter.acquire()

            do {
                try Task.checkCancellation()
                let stagedURLs = try await Task.detached(priority: .utility) {
                    let fileManager = FileManager.default
                    let base = fileManager.temporaryDirectory
                        .appendingPathComponent("PakScape-Thumbnail-" + UUID().uuidString, isDirectory: true)
                    try fileManager.createDirectory(
                        at: base,
                        withIntermediateDirectories: false,
                        attributes: [.posixPermissions: 0o700]
                    )

                    do {
                        // Use a canonical text extension so Quick Look does not
                        // reinterpret formats such as .cfg as opaque data.
                        let sourceURL = base.appendingPathComponent("preview.\(thumbnailFileExtension)")
                        let thumbnailData = source.materialize()
                        try thumbnailData.write(to: sourceURL, options: .atomic)
                        return (base: base, source: sourceURL)
                    } catch {
                        try? fileManager.removeItem(at: base)
                        throw error
                    }
                }.value
                defer { try? FileManager.default.removeItem(at: stagedURLs.base) }

                try Task.checkCancellation()

                let request = QLThumbnailGenerator.Request(
                    fileAt: stagedURLs.source,
                    size: CGSize(width: 128, height: 128),
                    scale: NSScreen.main?.backingScaleFactor ?? 2,
                    representationTypes: .thumbnail
                )
                request.contentType = contentType
                request.iconMode = true

                let representation = try await QLThumbnailGenerator.shared
                    .generateBestRepresentation(for: request)

                await Self.nativeThumbnailWorkLimiter.release()
                guard !Task.isCancelled, self.pakFile?.version == version else { return }
                self.previewImageCache[nodeID] = representation.nsImage
                self.previewImageRequests[nodeID] = nil
                self.objectWillChange.send()
            } catch {
                await Self.nativeThumbnailWorkLimiter.release()
                guard !Task.isCancelled, self.pakFile?.version == version else { return }
                self.previewImageRequests[nodeID] = nil
                self.previewImageMisses.insert(nodeID)
                self.objectWillChange.send()
            }
        }
    }

    private func renderPreviewImage(fileName: String, data: Data) -> NSImage? {
        let ext = (fileName as NSString).pathExtension.lowercased()
        if ext == "lmp" {
            return LmpPreviewRenderer.renderImage(fileName: fileName, data: data)
        } else if ext == "pcx" {
            return PcxPreviewRenderer.renderImage(data: data) ?? NativeImagePreviewRenderer.renderImage(data: data)
        } else if ext == "tga" {
            return TgaPreviewRenderer.renderImage(data: data)
        } else if ext == "mdl" {
            return MdlPreviewRenderer.renderImage(data: data)
        } else if ext == "spr" {
            return SprPreviewRenderer.renderImage(data: data)
        } else if ext == "bsp" {
            return BspPreviewRenderer.renderImage(fileName: fileName, data: data)
                ?? BspLevelPreviewRenderer.renderImage(data: data)
        } else if ext == "wad" {
            return WadPreviewRenderer.renderImage(fileName: fileName, data: data)
        }
        return nil
    }
    
    func importFiles(urls: [URL], to folder: PakNode) {
        guard isEditable else { return }
        _ = pasteFileURLs(urls, into: folder)
    }
    func deleteSelectedFile() {
        guard isEditable, currentFolder != nil, let root = pakFile?.root else { return }

        let idsToDelete: Set<PakNode.ID>
        if !selectedNodes.isEmpty {
            idsToDelete = Set(selectedNodes.map { $0.id })
        } else if let single = selectedFile {
            idsToDelete = [single.id]
        } else {
            return
        }

        let selectedCount = idsToDelete.count
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = selectedCount == 1 ? "Delete This Item?" : "Delete \(selectedCount) Items?"
        alert.informativeText = "This removes the selection from the archive. You can undo this operation."
        alert.addButton(withTitle: "Delete")
        alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return }

        let removedPlacements = PakTreeMutation.placements(for: idsToDelete, in: root)
        guard !removedPlacements.isEmpty else { return }
        applyTreeChange(
            removing: removedPlacements,
            inserting: [],
            actionName: "Delete"
        )
    }
    
    var canCreateFolder: Bool {
        isEditable && pakFile != nil
    }
    
    var canAddFiles: Bool {
        isEditable && pakFile != nil
    }

    var canDeleteFile: Bool {
        isEditable && (!selectedNodes.isEmpty || selectedFile != nil)
    }

    @discardableResult
    func addFolder(in folder: PakNode?) -> PakNode? {
        guard isEditable,
              let target = folder ?? currentFolder ?? pakFile?.root else { return nil }
        target.children = target.children ?? []

        let baseName = "New Folder"
        var candidate = baseName
        var suffix = 1
        while target.children?.contains(where: {
            $0.name.caseInsensitiveCompare(candidate) == .orderedSame
        }) == true {
            suffix += 1
            candidate = "\(baseName) \(suffix)"
        }

        let newNode = PakNode(name: candidate)
        target.children?.append(newNode)
        sortFolder(target)
        let insertedPlacements = PakTreeMutation.placements(for: [newNode], in: target)
        registerTreeUndo(
            removing: insertedPlacements,
            inserting: [],
            actionName: "New Folder"
        )
        notifyDocumentChanged()
        return newNode
    }

    private func notifyDocumentChanged() {
        pakFile?.version = UUID()
        objectWillChange.send()
        if let pakFile {
            documentDidChange?(pakFile)
        }
    }

    private func registerUndo(
        actionName: String,
        handler: @escaping (PakViewModel) -> Void
    ) {
        guard let undoManager else { return }
        undoManager.registerUndo(withTarget: self) { target in
            handler(target)
        }
        undoManager.setActionName(actionName)
    }

    private func applyRename(node: PakNode, name: String, actionName: String) {
        let previousName = node.name
        registerUndo(actionName: actionName) { target in
            target.applyRename(node: node, name: previousName, actionName: actionName)
        }
        node.name = name
        notifyDocumentChanged()
    }

    private func registerTreeUndo(
        removing: [PakNodePlacement],
        inserting: [PakNodePlacement],
        actionName: String
    ) {
        registerUndo(actionName: actionName) { target in
            target.applyTreeChange(
                removing: removing,
                inserting: inserting,
                actionName: actionName
            )
        }
    }

    private func applyTreeChange(
        removing: [PakNodePlacement],
        inserting: [PakNodePlacement],
        actionName: String
    ) {
        let fallbackFolder = currentFolder.flatMap { currentFolder in
            removing.first(where: {
                findNode(with: currentFolder.id, in: $0.node) != nil
            })?.parent
        }

        registerTreeUndo(
            removing: inserting,
            inserting: removing,
            actionName: actionName
        )

        PakTreeMutation.apply(removing: removing, inserting: inserting)

        stopAudioPreview()
        if let fallbackFolder {
            navigate(to: fallbackFolder)
        }
        selectedNodes = []
        selectedFile = nil
        selectionResetVersion = UUID()
        notifyDocumentChanged()
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

        previewImageRequests.values.forEach { $0.cancel() }
        previewImageRequests.removeAll(keepingCapacity: true)
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

    func navigateToParent() {
        guard let current = currentFolder, let root = pakFile?.root, current !== root else { return }
        if let parent = findParent(of: current, in: root) {
            navigate(to: parent)
        }
    }

    func openSelectedFolder() {
        guard let selected = selectedFile, selected.isFolder else { return }
        navigate(to: selected)
    }

    private func findParent(of target: PakNode, in node: PakNode) -> PakNode? {
        guard let children = node.children else { return nil }
        for child in children {
            if child === target { return node }
            if let found = findParent(of: target, in: child) { return found }
        }
        return nil
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

private enum NativeImagePreviewRenderer {
    static func renderImage(data: Data) -> NSImage? {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let width = (properties[kCGImagePropertyPixelWidth] as? NSNumber)?.intValue,
              let height = (properties[kCGImagePropertyPixelHeight] as? NSNumber)?.intValue,
              PakPreviewLimits.isSafe(width: width, height: height) else {
            return nil
        }

        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceThumbnailMaxPixelSize: 2_048,
            kCGImageSourceShouldCacheImmediately: true
        ]
        guard let image = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) else {
            return nil
        }
        return NSImage(
            cgImage: image,
            size: NSSize(width: CGFloat(image.width), height: CGFloat(image.height))
        )
    }
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
        guard PakPreviewLimits.isSafe(width: width, height: height) else { return nil }

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
        guard palette.count >= 768,
              PakPreviewLimits.isSafe(width: width, height: height) else { return nil }

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
        guard PakPreviewLimits.isSafe(width: width, height: height), frames > 0 else { return nil }

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
        guard PakPreviewLimits.isSafe(width: frameWidth, height: frameHeight) else { return nil }

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
        guard palette.count >= 768,
              PakPreviewLimits.isSafe(width: width, height: height) else { return nil }

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
              PakPreviewLimits.isSafe(width: width, height: height) else {
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
        guard palette.count >= 768,
              PakPreviewLimits.isSafe(width: width, height: height) else { return nil }

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
              dirEntries > 0, dirEntries <= 4_096, dirOffset >= 0 else {
            return nil
        }

        let entrySize = 32
        let directorySizeResult = dirEntries.multipliedReportingOverflow(by: entrySize)
        guard !directorySizeResult.overflow else { return nil }
        let directoryEndResult = dirOffset.addingReportingOverflow(directorySizeResult.partialValue)
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

        let images = entries.prefix(64).compactMap { decodeImage(entry: $0, data: data) }
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
              PakPreviewLimits.isSafe(width: width, height: height) else { return nil }

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
              PakPreviewLimits.isSafe(width: width, height: height), ofs1 >= 0 else {
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
        guard palette.count >= 768,
              PakPreviewLimits.isSafe(width: width, height: height) else { return nil }

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

        guard PakPreviewLimits.isSafe(width: width, height: height) else { return nil }

        let colorPlanes = Int(data[65])
        let bytesPerLine = readUInt16LE(data, offset: 66) ?? 0
        guard colorPlanes > 0, bytesPerLine >= width else { return nil }

        let rowStrideResult = colorPlanes.multipliedReportingOverflow(by: bytesPerLine)
        guard !rowStrideResult.overflow else { return nil }
        let rowStride = rowStrideResult.partialValue

        let decodedByteCountResult = rowStride.multipliedReportingOverflow(by: height)
        guard !decodedByteCountResult.overflow else { return nil }
        let decodedByteCount = decodedByteCountResult.partialValue
        guard decodedByteCount <= PakPreviewLimits.maximumPixelCount * 4 else { return nil }

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
        guard PakPreviewLimits.isSafe(width: width, height: height) else { return nil }
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
              PakPreviewLimits.isSafe(width: width, height: height) else { return nil }

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

        guard PakPreviewLimits.isSafe(width: width, height: height) else { return nil }
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
