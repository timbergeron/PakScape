import SwiftUI
import AppKit

fileprivate enum DetailViewStyle: String, CaseIterable, Identifiable {
    case list = "List"
    case icons = "Icons"

    var id: Self { self }
}

struct ContentView: View {
    let document: PakDocument
    let isEditable: Bool
    @Environment(\.undoManager) private var undoManager
    @StateObject private var model: PakViewModel
    @State private var selectedFileIDs: Set<PakNode.ID> = [] // Selection in the detail table (supports multi-select)
    @State private var detailViewStyle: DetailViewStyle = .list
    @State private var renamingNodeID: PakNode.ID?
    @State private var renamingText: String = ""
    @State private var renamingNode: PakNode?
    @FocusState private var renamingFocus: PakNode.ID?
    @State private var window: NSWindow?
    @State private var iconZoomLevel: Int = 1
    @State private var searchText = ""
    @State private var itemInfo: PakItemInfo?

    init(document: PakDocument, isEditable: Bool) {
        self.document = document
        self.isEditable = isEditable
        self._model = StateObject(wrappedValue: PakViewModel(pakFile: document.pakFile, isEditable: isEditable))
    }

    @State private var sortOrder = [KeyPathComparator(\PakNode.name)]

    var body: some View {
        NavigationSplitView {
            sidebar
        } detail: {
            detailView
        }
        .searchable(
            text: $searchText,
            placement: .toolbar,
            prompt: "Search all paths"
        )
        .sheet(item: $itemInfo) { info in
            PakItemInfoView(info: info)
        }
        .focusedSceneValue(\.pakCommands, currentPakCommands)
        .onAppear {
            model.connectDocument(undoManager: undoManager) { pakFile in
                document.pakFile = pakFile
            }
            model.updateEditableState(isEditable)
        }
        .onChange(of: isEditable) { _, newValue in
            model.updateEditableState(newValue)
        }
        .onChange(of: model.selectionResetVersion) { _, _ in
            selectedFileIDs.removeAll()
            cancelRenaming()
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
                window = newWindow
            }
        )
    }

    private func closeSearch() {
        searchText = ""
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
                let searchResults = archiveSearchResults
                let displayedChildren = searchResults?.map(\.node)
                    ?? (folder.children ?? []).sorted(using: sortOrder)
                let searchPaths = Dictionary(
                    uniqueKeysWithValues: (searchResults ?? []).map { ($0.node.id, $0.path) }
                )

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
                        if let searchResults {
                            Text("\(searchResults.count) \(searchResults.count == 1 ? "result" : "results")")
                                .foregroundStyle(.secondary)
                        }
                    }
                    .padding(.horizontal)
                    .padding(.top)
                    .padding(.bottom)

                    Divider()

                    Group {
                        switch detailViewStyle {
                        case .list:
                            listView(nodes: displayedChildren, searchPaths: searchPaths)
                        case .icons:
                            iconsView(displayedChildren, searchPaths: searchPaths)
                        }
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .overlay {
                        if searchResults != nil, displayedChildren.isEmpty {
                            VStack(spacing: 8) {
                                Image(systemName: "magnifyingglass")
                                    .font(.title)
                                    .foregroundStyle(.secondary)
                                Text("No Results")
                                    .font(.headline)
                                Text("Try a partial name, path, or extension.")
                                    .foregroundStyle(.secondary)
                            }
                        }
                    }
                }
                .dropDestination(for: URL.self) { urls, _ in
                    let fileURLs = urls.filter(\.isFileURL)
                    guard model.isEditable,
                          let folder = model.currentFolder,
                          !fileURLs.isEmpty else { return false }
                    model.importFiles(urls: fileURLs, to: folder)
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
        .onChange(of: searchText) { _, _ in
            guard let folder = model.currentFolder else { return }
            let visibleIDs = Set(displayedNodes(in: folder).map(\.id))
            let visibleSelection = selectedFileIDs.intersection(visibleIDs)
            updateSelection(ids: visibleSelection, in: folder)
            if let renamingID = renamingNodeID, !visibleIDs.contains(renamingID) {
                cancelRenaming()
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
        guard model.isEditable else { return }
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
        if let searchResults = archiveSearchResults {
            return searchResults.map(\.node).filter { ids.contains($0.id) }
        }
        return (folder.children ?? []).filter { ids.contains($0.id) }
    }

    private var archiveSearchResults: [PakSearchResult]? {
        let query = searchText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !query.isEmpty, let root = model.pakFile?.root else { return nil }
        return PakArchiveSearch.search(root: root, query: query)
    }

    private func displayedNodes(in folder: PakNode) -> [PakNode] {
        archiveSearchResults?.map(\.node) ?? (folder.children ?? []).sorted(using: sortOrder)
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
        .disabled(!model.isEditable)
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
    private func listView(nodes: [PakNode], searchPaths: [PakNode.ID: String]) -> some View {
        PakListView(
            nodes: nodes,
            searchPaths: searchPaths,
            selection: $selectedFileIDs,
            sortOrder: $sortOrder,
            viewModel: model,
            onOpenFolder: { folder in
                openFolder(folder)
            },
            onGetInfo: { node in
                showInfo(for: node)
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
    private func iconsView(_ children: [PakNode], searchPaths: [PakNode.ID: String]) -> some View {
        PakIconView(
            nodes: children,
            searchPaths: searchPaths,
            selection: $selectedFileIDs,
            zoomLevel: iconZoomLevel,
            viewModel: model,
            onOpenFolder: { folder in
                openFolder(folder)
            },
            onGetInfo: { node in
                showInfo(for: node)
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

    private func openFolder(_ folder: PakNode) {
        if archiveSearchResults != nil {
            closeSearch()
        }
        model.navigate(to: folder)
    }

    private func showInfo(for node: PakNode) {
        guard let pakFile = model.pakFile else { return }
        itemInfo = PakItemInfo(node: node, root: pakFile.root, archiveName: pakFile.name)
    }

}

struct PakCommands {
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
    let canCut: Bool
    let canCopy: Bool
    let canPaste: Bool
    let selectAll: () -> Void
    let canSelectAll: Bool
    let enclosingFolder: () -> Void
    let canEnclosingFolder: Bool
    let openSelection: () -> Void
    let canOpenSelection: Bool
    let getInfo: () -> Void
    let canGetInfo: Bool
    let quickLook: () -> Void
    let canQuickLook: Bool
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

private extension ContentView {
    var currentPakCommands: PakCommands {
        PakCommands(
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
            canAddFiles: model.canAddFiles,
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
            canCut: model.canCut,
            canCopy: model.canCopy,
            canPaste: model.canPaste,
            selectAll: {
                selectAllInCurrentFolder()
            },
            canSelectAll: canSelectAllInCurrentFolder,
            enclosingFolder: {
                model.navigateToParent()
            },
            canEnclosingFolder: model.canNavigateToParent,
            openSelection: {
                model.openSelectedFolder()
            },
            canOpenSelection: model.canOpenSelectedFolder,
            getInfo: {
                if let node = selectedInfoNode {
                    showInfo(for: node)
                }
            },
            canGetInfo: selectedInfoNode != nil,
            quickLook: {
                quickLookSelection()
            },
            canQuickLook: renamingNodeID == nil && !model.selectedNodes.isEmpty
        )
    }

    func quickLookSelection() {
        model.toggleQuickLook(for: model.selectedNodes)
    }

    var selectedInfoNode: PakNode? {
        if model.selectedNodes.count == 1 {
            return model.selectedNodes.first
        }
        if model.selectedNodes.isEmpty {
            return model.currentFolder
        }
        return nil
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
        let ids = Set(displayedNodes(in: folder).map { $0.id })
        updateSelection(ids: ids, in: folder)
    }

    var canSelectAllInCurrentFolder: Bool {
        guard let folder = model.currentFolder else { return false }
        return !displayedNodes(in: folder).isEmpty
    }

    var canRenameSelectedNode: Bool {
        model.isEditable && renamingNodeID == nil && model.selectedFile != nil
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
