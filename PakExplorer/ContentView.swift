import SwiftUI
import UniformTypeIdentifiers
import AppKit

fileprivate enum DetailViewStyle: String, CaseIterable, Identifiable {
    case list = "List"
    case icons = "Icons"

    var id: Self { self }
}

struct ContentView: View {
    @Binding var document: PakDocument
    let fileURL: URL?
    @StateObject private var model: PakViewModel
    @State private var selectedFileIDs: Set<PakNode.ID> = [] // Selection in the detail table (supports multi-select)
    @State private var detailViewStyle: DetailViewStyle = .list
    @State private var renamingNodeID: PakNode.ID?
    @State private var renamingText: String = ""
    @State private var renamingNode: PakNode?
    @FocusState private var renamingFocus: PakNode.ID?
    @State private var window: NSWindow?
    @State private var windowDelegate = PakWindowDelegate()
    @State private var iconZoomLevel: Int = 1

    init(document: Binding<PakDocument>, fileURL: URL?) {
        self._document = document
        self.fileURL = fileURL
        self._model = StateObject(wrappedValue: PakViewModel(pakFile: document.wrappedValue.pakFile, documentURL: fileURL))
    }

    @State private var sortOrder = [KeyPathComparator(\PakNode.name)]

    var body: some View {
        NavigationSplitView {
            sidebar
        } detail: {
            detailView
        }
        .focusedSceneValue(\.pakCommands, currentPakCommands)
        .onAppear {
            model.updateDocumentURL(fileURL)
            window?.isDocumentEdited = model.hasUnsavedChanges
        }
        .onChange(of: fileURL) { _, newValue in
            model.updateDocumentURL(newValue)
        }
        .onChange(of: model.hasUnsavedChanges) { _, newValue in
            window?.isDocumentEdited = newValue
        }
        .toolbar {
            ToolbarItem(placement: .navigation) {
                HStack(spacing: 8) {
                    Button {
                        model.navigateBack()
                    } label: {
                        Image(systemName: "chevron.left")
                            .font(.title2)
                    }
                    .help("Back")
                    .disabled(!model.canNavigateBack)

                    Button {
                        model.navigateForward()
                    } label: {
                        Image(systemName: "chevron.right")
                            .font(.title2)
                    }
                    .help("Forward")
                    .disabled(!model.canNavigateForward)
                }
                .padding(.leading, 10)
                .padding(.trailing, 10)
                .controlSize(.regular)
                .buttonStyle(.borderless)
            }
        }
        .background(
            WindowAccessor { newWindow in
                guard let newWindow else { return }
                windowDelegate.viewModel = model
                if window !== newWindow {
                    window = newWindow
                    windowDelegate.forwardingDelegate = newWindow.delegate
                    newWindow.delegate = windowDelegate
                }
                newWindow.isDocumentEdited = model.hasUnsavedChanges
            }
        )
    }

    private var sidebar: some View {
        List(selection: $model.currentFolder) {
            if let root = model.pakFile?.root {
                // Root node itself
                NavigationLink(value: root) {
                    Label("/", systemImage: "folder.fill")
                }
                
                // Recursive children
                OutlineGroup(root.folderChildren ?? [], children: \.folderChildren) { node in
                    NavigationLink(value: node) {
                        Label(node.name, systemImage: "folder.fill")
                    }
                }
            } else {
                Text("Open a Quake .pak file (File → Open PAK…)")
                    .foregroundStyle(.secondary)
            }
        }
        .frame(minWidth: 200)
    }

    private var detailView: some View {
        VStack(spacing: 0) {
            if let folder = model.currentFolder {
                VStack(spacing: 0) {
                    HStack {
                        Picker("Detail View", selection: $detailViewStyle) {
                            ForEach(DetailViewStyle.allCases) { style in
                                Text(style.rawValue).tag(style)
                            }
                        }
                        .pickerStyle(.segmented)
                        .labelsHidden()
                        Spacer()
                    }
                    .padding(.horizontal)
                    .padding(.top)
                    .padding(.bottom)

                    Divider()

                    let sortedChildren = (folder.children ?? []).sorted(using: sortOrder)
                    Group {
                        switch detailViewStyle {
                        case .list:
                            listView(for: folder)
                        case .icons:
                            iconsView(sortedChildren)
                        }
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                }
                .onDrop(of: [UTType.fileURL], isTargeted: nil) { providers in
                    guard let folder = model.currentFolder else { return false }
                    let dispatchGroup = DispatchGroup()
                    var urls: [URL] = []
                    
                    for provider in providers {
                        if provider.hasItemConformingToTypeIdentifier(UTType.fileURL.identifier) {
                            dispatchGroup.enter()
                            provider.loadItem(forTypeIdentifier: UTType.fileURL.identifier, options: nil) { (item, error) in
                                if let data = item as? Data, let url = URL(dataRepresentation: data, relativeTo: nil) {
                                    urls.append(url)
                                } else if let url = item as? URL {
                                    urls.append(url)
                                }
                                dispatchGroup.leave()
                            }
                        }
                    }
                    
                    dispatchGroup.notify(queue: .main) {
                        model.importFiles(urls: urls, to: folder)
                    }
                    return true
                }
                .contextMenu {
                    Button("Paste") {
                        let inserted = model.pasteIntoCurrentFolder()
                        if !inserted.isEmpty {
                            selectedFileIDs = Set(inserted.map { $0.id })
                        }
                    }
                    .disabled(!model.canPaste)
                    Button("New Folder") {
                        createFolder(at: folder)
                    }
                    .disabled(!model.canCreateFolder)
                    Button("Add File(s)…") {
                        presentAddFilesPanel(target: folder)
                    }
                    .disabled(!model.canAddFiles)
                }
                .onChange(of: selectedFileIDs) { _, newValue in
                    updateSelection(ids: newValue, in: folder)
                    if let renamingID = renamingNodeID, !newValue.contains(renamingID) {
                        cancelRenaming()
                    }
                }
            } else {
                Text("Select a folder in the sidebar")
                    .foregroundStyle(.secondary)
            }
        }
        .onChange(of: model.currentFolder) { _, _ in
            selectedFileIDs.removeAll()
            model.selectedFile = nil
            model.selectedNodes = []
            cancelRenaming()
        }
        .onChange(of: renamingFocus) { _, newValue in
            if let renameID = renamingNodeID, renameID != newValue {
                commitRename()
            }
        }
        // Global shortcut for delete
        .onKeyPress(keys: ["d"]) { press in
            if press.modifiers.contains(.command) {
                model.deleteSelectedFile()
                return .handled
            }
            return .ignored
        }
    }

    private func select(_ node: PakNode, toggle: Bool = false) {
        guard let folder = model.currentFolder else { return }
        var ids = selectedFileIDs
        if toggle {
            if ids.contains(node.id) {
                ids.remove(node.id)
            } else {
                ids.insert(node.id)
            }
        } else {
            ids = [node.id]
        }
        updateSelection(ids: ids, in: folder)
    }

    private func beginRenaming(_ node: PakNode) {
        select(node)
        renamingNode = node
        renamingNodeID = node.id
        renamingText = node.name
        renamingFocus = node.id
    }

    private func commitRename() {
        guard let node = renamingNode else {
            cancelRenaming()
            return
        }

        model.rename(node: node, to: renamingText)
        cancelRenaming()
    }

    private func cancelRenaming() {
        renamingNode = nil
        renamingNodeID = nil
        renamingText = ""
    }

    private func updateSelection(ids: Set<PakNode.ID>, in folder: PakNode?) {
        if selectedFileIDs != ids {
            selectedFileIDs = ids
        }
        let nodes = selectionNodes(for: ids, in: folder)
        model.selectedNodes = nodes
        model.selectedFile = nodes.first
    }

    private func selectionNodes(for ids: Set<PakNode.ID>, in folder: PakNode?) -> [PakNode] {
        guard let folder, !ids.isEmpty else { return [] }
        return (folder.children ?? []).filter { ids.contains($0.id) }
    }

    private func handleIconSelection(for node: PakNode) {
        let modifiers = NSApp.currentEvent?.modifierFlags ?? []
        if modifiers.contains(.command) {
            select(node, toggle: true)
        } else {
            select(node)
        }
    }

    @ViewBuilder
    private func nameLabel(for node: PakNode, font: Font = .body, alignment: TextAlignment = .leading) -> some View {
        if renamingNodeID == node.id {
            TextField("Name", text: $renamingText)
                .textFieldStyle(.plain)
                .font(font)
                .multilineTextAlignment(alignment)
                .focused($renamingFocus, equals: node.id)
                .onSubmit {
                    commitRename()
                }
                .onAppear {
                    DispatchQueue.main.async {
                        renamingFocus = node.id
                    }
                }
                .onExitCommand {
                    cancelRenaming()
                }
        } else {
            Text(node.name)
                .font(font)
                .multilineTextAlignment(alignment)
                .onTapGesture {
                    if selectedFileIDs.contains(node.id) {
                        // Delay to mimic file system behavior and avoid conflict with double-click
                        DispatchQueue.main.asyncAfter(deadline: .now() + 0.5) {
                            if selectedFileIDs.contains(node.id) {
                                beginRenaming(node)
                            }
                        }
                    } else {
                        select(node)
                    }
                }
        }
    }

    @ViewBuilder
    private func contextMenuActions(for node: PakNode) -> some View {
        Button("Add File(s)…") {
            presentAddFilesPanel(target: node.isFolder ? node : model.currentFolder)
        }
        .disabled(!model.canAddFiles)

        Button("New Folder") {
            let parent = node.isFolder ? node : model.currentFolder
            createFolder(at: parent)
        }
        .disabled(!model.canCreateFolder)
        Button("Select") {
            select(node)
        }
        Button("Rename…") {
            beginRenaming(node)
        }
        if node.isFolder {
            Button("Open Folder") {
                model.navigate(to: node)
            }
        }
        if !node.isFolder {
            Button("Export…") {
                select(node)
                model.exportSelectedFile()
            }
        }
        
        Divider()
        
        Button("Delete", role: .destructive) {
            // If we are right-clicking a node that isn't selected, select it first?
            // Or just delete that specific node?
            // The model.deleteSelectedFile() relies on selectedFile.
            // Let's update selection first if needed, or pass node to delete.
            // For consistency with other actions:
            select(node)
            model.deleteSelectedFile()
        }
    }
    @ViewBuilder
    private func listView(for folder: PakNode) -> some View {
        let nodesBinding = Binding<[PakNode]>(
            get: {
                (folder.children ?? []).sorted(using: sortOrder)
            },
            set: { newValue in
                folder.children = newValue
            }
        )

        PakListView(
            nodes: nodesBinding,
            selection: $selectedFileIDs,
            sortOrder: $sortOrder,
            viewModel: model,
            onOpenFolder: { folder in
                model.navigate(to: folder)
            },
            onNewFolder: {
                createFolder(at: model.currentFolder)
            },
            onAddFiles: {
                presentAddFilesPanel(target: model.currentFolder)
            },
            onCut: {
                model.cutSelection()
            },
            onCopy: {
                model.copySelection()
            },
            onPaste: {
                model.pasteIntoCurrentFolder()
            }
        )
    }

    @ViewBuilder
    private func iconsView(_ children: [PakNode]) -> some View {
        PakIconView(
            nodes: children,
            selection: $selectedFileIDs,
            zoomLevel: iconZoomLevel,
            viewModel: model,
            onOpenFolder: { folder in
                model.navigate(to: folder)
            },
            onNewFolder: {
                createFolder(at: model.currentFolder)
            },
            onAddFiles: {
                presentAddFilesPanel(target: model.currentFolder)
            },
            onCut: {
                model.cutSelection()
            },
            onCopy: {
                model.copySelection()
            },
            onPaste: {
                model.pasteIntoCurrentFolder()
            }
        )
    }

    private func dragItem(for node: PakNode) -> NSItemProvider {
        // Kept only for compatibility if used elsewhere; list/icons now use AppKit views.
        do {
            let url = try model.exportToTemporaryLocation(node: node)
            let provider = NSItemProvider(object: url as NSURL)
            provider.suggestedName = node.name
            return provider
        } catch {
            return NSItemProvider()
        }
    }
}

struct PakCommands {
    let save: () -> Void
    let saveAs: () -> Void
    let canSave: Bool
    let deleteFile: () -> Void
    let canDeleteFile: Bool
    let rename: () -> Void
    let canRename: Bool
    let newFolder: () -> Void
    let canNewFolder: Bool
    let addFiles: () -> Void
    let canAddFiles: Bool
    let zoomInIcons: () -> Void
    let zoomOutIcons: () -> Void
    let canZoomInIcons: Bool
    let canZoomOutIcons: Bool
    let cut: () -> Void
    let copy: () -> Void
    let paste: () -> Void
    let canCutCopy: Bool
    let canPaste: Bool
    let selectAll: () -> Void
    let canSelectAll: Bool
}

struct PakCommandsKey: FocusedValueKey {
    typealias Value = PakCommands
}

extension FocusedValues {
    var pakCommands: PakCommands? {
        get { self[PakCommandsKey.self] }
        set { self[PakCommandsKey.self] = newValue }
    }
}

private struct WindowAccessor: NSViewRepresentable {
    let callback: (NSWindow?) -> Void

    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        DispatchQueue.main.async {
            callback(view.window)
        }
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        DispatchQueue.main.async {
            callback(nsView.window)
        }
    }
}

private final class PakWindowDelegate: NSObject, NSWindowDelegate {
    weak var viewModel: PakViewModel?
    weak var forwardingDelegate: NSWindowDelegate?

    override func responds(to selector: Selector!) -> Bool {
        if super.responds(to: selector) { return true }
        return forwardingDelegate?.responds(to: selector) ?? false
    }

    override func forwardingTarget(for selector: Selector!) -> Any? {
        if let delegate = forwardingDelegate, delegate.responds(to: selector) {
            return delegate
        }
        return super.forwardingTarget(for: selector)
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        guard let model = viewModel, model.hasUnsavedChanges else {
            return forwardingDelegate?.windowShouldClose?(sender) ?? true
        }

        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "Do you want to save changes to this PAK?"
        alert.informativeText = "Your changes will be lost if you don’t save them."
        alert.addButton(withTitle: "Save")
        alert.addButton(withTitle: "Cancel")
        alert.addButton(withTitle: "Don't Save")

        let response = alert.runModal()
        switch response {
        case .alertFirstButtonReturn:
            if model.saveCurrentPak(promptForLocationIfNeeded: true) {
                return forwardingDelegate?.windowShouldClose?(sender) ?? true
            }
            return false
        case .alertSecondButtonReturn:
            return false
        default:
            return forwardingDelegate?.windowShouldClose?(sender) ?? true
        }
    }

}

private extension ContentView {
    var currentPakCommands: PakCommands {
        PakCommands(
            save: {
                _ = model.saveCurrentPak(promptForLocationIfNeeded: true)
            },
            saveAs: {
                model.exportPakAs()
            },
            canSave: model.canSave,
            deleteFile: {
                model.deleteSelectedFile()
            },
            canDeleteFile: model.canDeleteFile,
            rename: {
                renameSelectedNode()
            },
            canRename: canRenameSelectedNode,
            newFolder: {
                createFolder(at: model.currentFolder)
            },
            canNewFolder: model.canCreateFolder,
            addFiles: {
                presentAddFilesPanel(target: model.currentFolder)
            },
            canAddFiles: model.pakFile != nil,
            zoomInIcons: {
                if iconZoomLevel < 2 {
                    iconZoomLevel += 1
                }
            },
            zoomOutIcons: {
                if iconZoomLevel > 0 {
                    iconZoomLevel -= 1
                }
            },
            canZoomInIcons: detailViewStyle == .icons && iconZoomLevel < 2,
            canZoomOutIcons: detailViewStyle == .icons && iconZoomLevel > 0,
            cut: {
                model.cutSelection()
            },
            copy: {
                if !copyNameIfEditing() {
                    model.copySelection()
                }
            },
            paste: {
                let inserted = model.pasteIntoCurrentFolder()
                if !inserted.isEmpty {
                    selectedFileIDs = Set(inserted.map { $0.id })
                }
            },
            canCutCopy: model.canCutCopy,
            canPaste: model.canPaste,
            selectAll: {
                selectAllInCurrentFolder()
            },
            canSelectAll: canSelectAllInCurrentFolder
        )
    }

    func createFolder(at parent: PakNode?) {
        guard let newNode = model.addFolder(in: parent) else { return }
        select(newNode)
        beginRenaming(newNode)
        if let folder = parent {
            model.navigate(to: folder)
        }
    }

    func selectAllInCurrentFolder() {
        guard let folder = model.currentFolder else { return }
        let ids = Set((folder.children ?? []).map { $0.id })
        updateSelection(ids: ids, in: folder)
    }

    var canSelectAllInCurrentFolder: Bool {
        guard let folder = model.currentFolder else { return false }
        return !(folder.children ?? []).isEmpty
    }

    var canRenameSelectedNode: Bool {
        renamingNodeID == nil && model.selectedFile != nil
    }

    func renameSelectedNode() {
        // Prefer letting the active AppKit view (list or icon view)
        // handle rename for the current selection, so we get inline
        // editing behavior consistent with context menus and hover.
        let selector = NSSelectorFromString("renameSelectedItem:")
        if let window = window ?? NSApp.keyWindow {
            if window.firstResponder?.tryToPerform(selector, with: nil) == true {
                return
            }
        }
        _ = NSApp.sendAction(selector, to: nil, from: nil)
    }

    func copyNameIfEditing() -> Bool {
        guard let window = window ?? NSApp.keyWindow else { return false }

        if let textView = window.firstResponder as? NSTextView,
           let textField = textView.delegate as? NSTextField {
            copyText(from: textView, in: textField)
            return true
        }

        if let textField = window.firstResponder as? NSTextField {
            if let editor = textField.currentEditor() {
                copyText(from: editor, in: textField)
            } else {
                copyString(textField.stringValue)
            }
            return true
        }

        return false
    }

    private func copyText(from editor: NSText, in textField: NSTextField) {
        let full = textField.stringValue as NSString
        let range = editor.selectedRange
        let text: String
        if range.location != NSNotFound,
           range.length > 0,
           range.location + range.length <= full.length {
            text = full.substring(with: range)
        } else {
            text = textField.stringValue
        }
        copyString(text)
    }

    private func copyString(_ text: String) {
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(text, forType: .string)
    }

    func presentAddFilesPanel(target folder: PakNode?) {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = false
        panel.allowsMultipleSelection = true
        panel.begin { response in
            guard response == .OK else { return }
            let destination = folder ?? model.currentFolder ?? model.pakFile?.root
            guard let targetFolder = destination else { return }
            model.importFiles(urls: panel.urls, to: targetFolder)
        }
    }
}
