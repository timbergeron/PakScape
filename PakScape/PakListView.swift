import SwiftUI
import AppKit
import Foundation

private let pakListNameColumnIdentifier = NSUserInterfaceItemIdentifier("name")

// Custom table view so we can intercept key events and implement our own
// Finder-style type-to-select behavior without triggering system beeps.
private final class PakListTableView: NSTableView {
    var onHandledKeyDown: ((NSEvent) -> Bool)?
    var contextMenuProvider: ((Int) -> NSMenu?)?
    var renameHandler: (() -> Void)?

    var lastMouseDownRow: Int = -1
    var lastMouseDownWasOnAlreadySelectedRow: Bool = false
    var lastMouseDownWasOnNameText: Bool = false

    override func mouseDown(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        let row = self.row(at: point)
        let column = self.column(at: point)

        lastMouseDownRow = row
        lastMouseDownWasOnAlreadySelectedRow = row >= 0 && selectedRowIndexes.contains(row)
        lastMouseDownWasOnNameText = false

        if row >= 0,
           column >= 0,
           column < tableColumns.count,
           tableColumns[column].identifier == pakListNameColumnIdentifier,
           let cell = view(atColumn: column, row: row, makeIfNecessary: false) as? NSTableCellView,
           let textField = cell.textField {
            let pointInCell = cell.convert(point, from: self)
            let pointInTextField = textField.convert(pointInCell, from: cell)
            lastMouseDownWasOnNameText = isPointInRenderedText(pointInTextField, textField: textField)
        }

        super.mouseDown(with: event)
    }

    private func isPointInRenderedText(_ point: NSPoint, textField: NSTextField) -> Bool {
        guard textField.bounds.contains(point) else { return false }

        let drawingRect: NSRect
        if let cell = textField.cell {
            drawingRect = cell.drawingRect(forBounds: textField.bounds)
        } else {
            drawingRect = textField.bounds
        }

        let font = textField.font ?? NSFont.systemFont(ofSize: NSFont.systemFontSize)
        let attributes: [NSAttributedString.Key: Any] = [.font: font]
        let measuredWidth = (textField.stringValue as NSString).size(withAttributes: attributes).width
        let displayedWidth = max(1, min(measuredWidth, drawingRect.width))

        let renderedTextRect = NSRect(
            x: drawingRect.minX,
            y: drawingRect.minY,
            width: displayedWidth,
            height: drawingRect.height
        )
        return renderedTextRect.contains(point)
    }

    override func keyDown(with event: NSEvent) {
        if let handler = onHandledKeyDown, handler(event) {
            // Event was handled (or intentionally consumed) by our coordinator.
            return
        }
        super.keyDown(with: event)
    }

    override func menu(for event: NSEvent) -> NSMenu? {
        let point = convert(event.locationInWindow, from: nil)
        let row = self.row(at: point)
        guard row >= 0, row < numberOfRows else {
            return super.menu(for: event)
        }

        if !selectedRowIndexes.contains(row) {
            selectRowIndexes(IndexSet(integer: row), byExtendingSelection: false)
        }

        if let menu = contextMenuProvider?(row) {
            return menu
        }

        return super.menu(for: event)
    }

    @objc func renameSelectedItem(_ sender: Any?) {
        renameHandler?()
    }
}

struct PakListView: NSViewRepresentable {
    @Binding var nodes: [PakNode]
    @Binding var selection: Set<PakNode.ID>
    @Binding var sortOrder: [KeyPathComparator<PakNode>]
    var viewModel: PakViewModel
    var onOpenFolder: (PakNode) -> Void
    var onNewFolder: () -> Void
    var onAddFiles: () -> Void
    var onCut: () -> Void
    var onCopy: () -> Void
    var onPaste: () -> [PakNode]

    func makeCoordinator() -> Coordinator {
        Coordinator(parent: self)
    }

    func makeNSView(context: Context) -> NSScrollView {
        let tableView = PakListTableView()
        tableView.usesAlternatingRowBackgroundColors = true
        tableView.allowsMultipleSelection = true
        tableView.rowSizeStyle = .medium
        tableView.delegate = context.coordinator
        tableView.dataSource = context.coordinator
        tableView.action = #selector(Coordinator.tableViewSingleClicked(_:))
        tableView.doubleAction = #selector(Coordinator.tableViewDoubleClicked(_:))
        tableView.target = context.coordinator
        tableView.setDraggingSourceOperationMask(.copy, forLocal: false)

        let nameColumn = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("name"))
        nameColumn.title = "Name"
        nameColumn.minWidth = 200
        nameColumn.isEditable = true
        nameColumn.sortDescriptorPrototype = NSSortDescriptor(
            key: "name",
            ascending: true,
            selector: #selector(NSString.localizedStandardCompare(_:))
        )
        let sizeColumn = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("size"))
        sizeColumn.title = "Size"
        sizeColumn.minWidth = 80
        sizeColumn.sortDescriptorPrototype = NSSortDescriptor(key: "size", ascending: true)
        let typeColumn = NSTableColumn(identifier: NSUserInterfaceItemIdentifier("type"))
        typeColumn.title = "Type"
        typeColumn.minWidth = 120
        typeColumn.sortDescriptorPrototype = NSSortDescriptor(
            key: "type",
            ascending: true,
            selector: #selector(NSString.localizedStandardCompare(_:))
        )

        tableView.addTableColumn(nameColumn)
        tableView.addTableColumn(sizeColumn)
        tableView.addTableColumn(typeColumn)
        tableView.headerView = NSTableHeaderView()
        tableView.onHandledKeyDown = { [weak coordinator = context.coordinator] event in
            coordinator?.handleKeyDown(event) ?? false
        }
        tableView.contextMenuProvider = { [weak coordinator = context.coordinator] row in
            coordinator?.contextMenu(forRow: row)
        }
        tableView.renameHandler = { [weak coordinator = context.coordinator] in
            coordinator?.renameFromEditCommand()
        }

        let scrollView = NSScrollView()
        scrollView.hasVerticalScroller = true
        scrollView.documentView = tableView

        context.coordinator.tableView = tableView
        return scrollView
    }

    func updateNSView(_ nsView: NSScrollView, context: Context) {
        context.coordinator.parent = self
        guard let tableView = context.coordinator.tableView else { return }
        let isEditing = tableView.currentEditor() != nil || (tableView.window?.firstResponder is NSTextView)
        if isEditing || context.coordinator.isRenamePending {
            return
        }

        tableView.reloadData()

        // Apply selection from SwiftUI to NSTableView when not editing to avoid dropping focus.
        let ids = selection
        let indexes = IndexSet(nodes.enumerated().compactMap { index, node in
            ids.contains(node.id) ? index : nil
        })
        if tableView.selectedRowIndexes != indexes {
            context.coordinator.cancelPendingRename()
            tableView.selectRowIndexes(indexes, byExtendingSelection: false)
            context.coordinator.lastSelectionChange = Date()
        } else {
            tableView.selectRowIndexes(indexes, byExtendingSelection: false)
        }
    }

    final class Coordinator: NSObject, NSTableViewDataSource, NSTableViewDelegate, NSTextFieldDelegate {
        var parent: PakListView
        weak var tableView: NSTableView?
        private var renameWorkItem: DispatchWorkItem?
        var isRenamePending: Bool { renameWorkItem != nil }
        var lastSelectionChange = Date.distantPast
        private let nameColumnIdentifier = pakListNameColumnIdentifier
        private let typeSelectionResetInterval: TimeInterval = 1.0
        private var typeSelectionBuffer = ""
        private var lastTypeSelectionDate = Date.distantPast

        init(parent: PakListView) {
            self.parent = parent
        }

        func numberOfRows(in tableView: NSTableView) -> Int {
            parent.nodes.count
        }

        func tableView(_ tableView: NSTableView, viewFor tableColumn: NSTableColumn?, row: Int) -> NSView? {
            guard row >= 0 && row < parent.nodes.count else { return nil }
            let node = parent.nodes[row]
            let identifier = tableColumn?.identifier.rawValue ?? ""

            let cellIdentifier = NSUserInterfaceItemIdentifier("\(identifier)Cell")
            let cell: NSTableCellView
            if let existing = tableView.makeView(withIdentifier: cellIdentifier, owner: self) as? NSTableCellView {
                cell = existing
            } else {
                cell = NSTableCellView()
                cell.identifier = cellIdentifier
                let textField: NSTextField
                if identifier == "name" {
                    textField = NSTextField(string: "")
                    textField.isBordered = false
                    textField.isBezeled = false
                    textField.drawsBackground = false
                    textField.isEditable = true
                    textField.isSelectable = true
                    textField.lineBreakMode = .byTruncatingMiddle
                    textField.delegate = self
                } else {
                    textField = NSTextField(labelWithString: "")
                }
                textField.translatesAutoresizingMaskIntoConstraints = false
                cell.textField = textField
                cell.addSubview(textField)

                if identifier == "name" {
                    let imageView = NSImageView()
                    imageView.translatesAutoresizingMaskIntoConstraints = false
                    cell.imageView = imageView
                    cell.addSubview(imageView)
                    NSLayoutConstraint.activate([
                        imageView.leadingAnchor.constraint(equalTo: cell.leadingAnchor, constant: 4),
                        imageView.centerYAnchor.constraint(equalTo: cell.centerYAnchor),
                        imageView.widthAnchor.constraint(equalToConstant: 16),
                        imageView.heightAnchor.constraint(equalToConstant: 16),

                        textField.leadingAnchor.constraint(equalTo: imageView.trailingAnchor, constant: 4),
                        textField.trailingAnchor.constraint(equalTo: cell.trailingAnchor, constant: -4),
                        textField.centerYAnchor.constraint(equalTo: cell.centerYAnchor)
                    ])
                } else {
                    NSLayoutConstraint.activate([
                        textField.leadingAnchor.constraint(equalTo: cell.leadingAnchor, constant: 4),
                        textField.trailingAnchor.constraint(equalTo: cell.trailingAnchor, constant: -4),
                        textField.centerYAnchor.constraint(equalTo: cell.centerYAnchor)
                    ])
                }
            }

            if identifier == "name" {
                cell.textField?.stringValue = node.name
                cell.imageView?.image = iconImage(for: node)
                cell.objectValue = node.id
            } else if identifier == "size" {
                cell.textField?.stringValue = node.formattedFileSize
            } else if identifier == "type" {
                cell.textField?.stringValue = node.fileType
            }

            return cell
        }

        private func iconImage(for node: PakNode) -> NSImage? {
            if node.isFolder {
                return NSImage(systemSymbolName: "folder.fill", accessibilityDescription: nil)
            }
            if let preview = parent.viewModel.previewImage(for: node) {
                return preview
            }
            return NSImage(systemSymbolName: "doc", accessibilityDescription: nil)
        }

        private func commitRename(row: Int, newNameRaw: String) {
            let newName = newNameRaw.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !newName.isEmpty else { return }
            guard row >= 0, row < parent.nodes.count else { return }

            let node = parent.nodes[row]
            guard newName != node.name else { return }

            parent.viewModel.rename(node: node, to: newName)
        }

        func controlTextDidBeginEditing(_ obj: Notification) {
            // For NSTableView inline editing, the notification's object is typically the shared field editor.
            guard let editor = obj.object as? NSTextView else { return }
            let range = filenameBaseRange(for: editor.string)

            // Apply after AppKit's initial selection, and re-apply once to win any late selection changes.
            DispatchQueue.main.async { [weak editor] in
                editor?.selectedRange = range
            }
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.01) { [weak editor] in
                editor?.selectedRange = range
            }
        }

        func controlTextDidEndEditing(_ obj: Notification) {
            guard let tableView = tableView else { return }

            if let movement = obj.userInfo?[NSText.movementUserInfoKey] as? Int,
               movement == NSCancelTextMovement {
                if let textField = obj.object as? NSTextField {
                    let row = tableView.row(for: textField)
                    if row >= 0, row < parent.nodes.count {
                        textField.stringValue = parent.nodes[row].name
                    }
                } else if let textView = obj.object as? NSTextView,
                          let textField = textView.delegate as? NSTextField {
                    let row = tableView.row(for: textField)
                    if row >= 0, row < parent.nodes.count {
                        textField.stringValue = parent.nodes[row].name
                    }
                }
                return
            }

            if let textField = obj.object as? NSTextField {
                let row = tableView.row(for: textField)
                commitRename(row: row, newNameRaw: textField.stringValue)
            } else if let textView = obj.object as? NSTextView,
                      let textField = textView.delegate as? NSTextField {
                let row = tableView.row(for: textField)
                commitRename(row: row, newNameRaw: textView.string)
            }
        }

        private func filenameBaseRange(for name: String) -> NSRange {
            let ns = name as NSString
            let length = ns.length
            guard length > 0 else { return NSRange(location: 0, length: 0) }

            let lastDot = ns.range(of: ".", options: .backwards)
            if lastDot.location != NSNotFound, lastDot.location > 0, lastDot.location < length - 1 {
                return NSRange(location: 0, length: lastDot.location)
            }
            return NSRange(location: 0, length: length)
        }

        private func selectFilenameBase(row: Int) {
            guard let tableView else { return }
            guard row >= 0, row < parent.nodes.count else { return }
            guard let editor = tableView.currentEditor() as? NSTextView else { return }
            editor.selectedRange = filenameBaseRange(for: parent.nodes[row].name)
        }

        private func beginRenaming(row: Int) {
            guard let tableView else { return }
            let nameColumn = tableView.column(withIdentifier: nameColumnIdentifier)
            guard nameColumn != -1 else { return }

            // Avoid AppKit "select all"; we'll set Finder-style selection in controlTextDidBeginEditing.
            tableView.editColumn(nameColumn, row: row, with: nil, select: false)
        }

        func tableViewSelectionDidChange(_ notification: Notification) {
            // If we are currently editing, do NOT cancel or change selection logic
            // that might disrupt the editor.
            guard let tableView = tableView else { return }
            if tableView.editedRow != -1 || tableView.currentEditor() != nil {
                return
            }
            
            cancelPendingRename()
            lastSelectionChange = Date()
            
            let indexes = tableView.selectedRowIndexes
            var ids = Set<PakNode.ID>()
            for index in indexes {
                if index >= 0 && index < parent.nodes.count {
                    ids.insert(parent.nodes[index].id)
                }
            }
            parent.selection = ids
        }

        func tableView(_ tableView: NSTableView, shouldEdit tableColumn: NSTableColumn?, row: Int) -> Bool {
            tableColumn?.identifier == nameColumnIdentifier
        }

        func tableView(_ tableView: NSTableView, pasteboardWriterForRow row: Int) -> NSPasteboardWriting? {
            guard row >= 0 && row < parent.nodes.count else { return nil }
            let node = parent.nodes[row]
            do {
                let url = try parent.viewModel.exportToTemporaryLocation(node: node)
                return url as NSURL
            } catch {
                return nil
            }
        }

        func tableView(_ tableView: NSTableView, sortDescriptorsDidChange oldDescriptors: [NSSortDescriptor]) {
            guard let descriptor = tableView.sortDescriptors.first,
                  let key = descriptor.key else { return }

            let ascending = descriptor.ascending
            switch key {
            case "name":
                parent.sortOrder = [KeyPathComparator(\PakNode.name, order: ascending ? .forward : .reverse)]
            case "size":
                parent.sortOrder = [KeyPathComparator(\PakNode.fileSize, order: ascending ? .forward : .reverse)]
            case "type":
                parent.sortOrder = [KeyPathComparator(\PakNode.fileType, order: ascending ? .forward : .reverse)]
            default:
                break
            }
        }

        func renameFromEditCommand() {
            cancelPendingRename()
            guard let tableView = tableView else { return }

            let row = tableView.selectedRow
            guard row >= 0, row < parent.nodes.count else { return }

            let workItem = DispatchWorkItem { [weak self] in
                self?.beginRenaming(row: row)
                self?.renameWorkItem = nil
            }
            renameWorkItem = workItem
            DispatchQueue.main.async(execute: workItem)
        }
        // MARK: - Type-to-select handling

        func handleKeyDown(_ event: NSEvent) -> Bool {
            guard let tableView = tableView else { return false }

            // Ignore Command/Option/Control-modified keys so shortcuts keep working.
            let modifiers = event.modifierFlags.intersection([.command, .option, .control])
            guard modifiers.isEmpty else { return false }

            guard let characters = event.charactersIgnoringModifiers, !characters.isEmpty else {
                return false
            }

            // Finder uses Space for Quick Look.
            if characters == " " {
                cancelPendingRename()
                quickLookSelection()
                return true
            }

            // Filter to printable ASCII characters; ignore control keys like arrows, etc.
            let scalars = characters.unicodeScalars.filter { scalar in
                guard scalar.isASCII else { return false }
                if CharacterSet.controlCharacters.contains(scalar) { return false }
                return scalar.value >= 0x20
            }
            guard !scalars.isEmpty else { return false }

            cancelPendingRename()

            let input = String(String.UnicodeScalarView(scalars)).lowercased()
            updateTypeSelectionBuffer(with: input)

            guard let match = findMatch(for: typeSelectionBuffer, in: tableView) else {
                // No match  consume the event so we don't get the system beep.
                return true
            }

            tableView.selectRowIndexes(IndexSet(integer: match), byExtendingSelection: false)
            tableView.scrollRowToVisible(match)
            return true
        }

        private func quickLookSelection() {
            guard let tableView else { return }
            let rows = tableView.selectedRowIndexes
            guard !rows.isEmpty else { return }

            let selectedNodes: [PakNode] = rows.compactMap { row in
                guard row >= 0, row < parent.nodes.count else { return nil }
                return parent.nodes[row]
            }
            guard !selectedNodes.isEmpty else { return }

            var urls: [URL] = []
            urls.reserveCapacity(selectedNodes.count)
            for node in selectedNodes {
                do {
                    let url = try parent.viewModel.exportToTemporaryLocation(node: node)
                    urls.append(url)
                } catch {
                    continue
                }
            }
            guard !urls.isEmpty else { return }

            PakQuickLook.shared.toggle(urls: urls)
        }

        private func updateTypeSelectionBuffer(with input: String) {
            let now = Date()
            if now.timeIntervalSince(lastTypeSelectionDate) > typeSelectionResetInterval {
                // Too much time has passed  start a new sequence.
                typeSelectionBuffer = ""
            } else if typeSelectionBuffer.count == 1, typeSelectionBuffer == input {
                // Repeatedly pressing the same key within the interval should
                // cycle through items starting with that letter, not build a
                // longer prefix like "aa".
                typeSelectionBuffer = ""
            }

            typeSelectionBuffer += input
            lastTypeSelectionDate = now
        }

        private func findMatch(for prefix: String, in tableView: NSTableView) -> Int? {
            guard !prefix.isEmpty, !parent.nodes.isEmpty else { return nil }
            let lowerPrefix = prefix.lowercased()

            let start = max(tableView.selectedRow + 1, 0)
            if let result = search(prefix: lowerPrefix, range: start ..< parent.nodes.count) {
                return result
            }
            if start > 0, let wrapResult = search(prefix: lowerPrefix, range: 0 ..< start) {
                return wrapResult
            }
            return nil
        }

        private func search(prefix: String, range: Range<Int>) -> Int? {
            for index in range {
                if parent.nodes[index].name.lowercased().hasPrefix(prefix) {
                    return index
                }
            }
            return nil
        }

        @objc func tableViewSingleClicked(_ sender: NSTableView) {
            cancelPendingRename()

            let event = NSApp.currentEvent
            let modifiers = event?.modifierFlags ?? []
            if modifiers.contains(.command) ||
                modifiers.contains(.shift) ||
                modifiers.contains(.option) ||
                modifiers.contains(.control) {
                return
            }
            guard (event?.clickCount ?? 1) == 1 else { return }
            guard let pakTableView = sender as? PakListTableView else { return }

            let row = sender.clickedRow
            let column = sender.clickedColumn
            let nameColumnIndex = sender.column(withIdentifier: nameColumnIdentifier)
            guard row >= 0,
                  row < parent.nodes.count,
                  nameColumnIndex != -1,
                  column == nameColumnIndex else { return }

            // Finder-like behavior: only begin renaming if the row was already selected
            // before the click, and the click was directly on the rendered filename text.
            guard pakTableView.lastMouseDownRow == row,
                  pakTableView.lastMouseDownWasOnAlreadySelectedRow,
                  pakTableView.lastMouseDownWasOnNameText else { return }
            
            let workItem = DispatchWorkItem { [weak self] in
                guard let self = self, let tableView = self.tableView else { return }
                guard row >= 0,
                      row < self.parent.nodes.count else { return }

                // Verify it's still the only selected item.
                let selected = tableView.selectedRowIndexes
                guard selected.contains(row), selected.count == 1 else { return }

                    let nameColumn = tableView.column(withIdentifier: self.nameColumnIdentifier)
                    guard nameColumn != -1 else { return }

                    self.beginRenaming(row: row)
                }
            renameWorkItem = workItem
            DispatchQueue.main.asyncAfter(deadline: .now() + NSEvent.doubleClickInterval, execute: workItem)
        }

        @objc func tableViewDoubleClicked(_ sender: Any?) {
            cancelPendingRename()
            guard let tableView = tableView else { return }
            let row = tableView.clickedRow
            guard row >= 0 && row < parent.nodes.count else { return }
            let node = parent.nodes[row]
            open(node: node)
        }

        func cancelPendingRename() {
            renameWorkItem?.cancel()
            renameWorkItem = nil
        }

        func contextMenu(forRow row: Int) -> NSMenu? {
            guard row >= 0, row < parent.nodes.count else { return nil }
            let node = parent.nodes[row]

            let menu = NSMenu()
            let openItem = NSMenuItem(title: "Open", action: #selector(openFromMenu(_:)), keyEquivalent: "")
            openItem.target = self
            openItem.representedObject = node
            menu.addItem(openItem)

            menu.addItem(.separator())

            let cutItem = NSMenuItem(title: "Cut", action: #selector(cutSelection(_:)), keyEquivalent: "")
            cutItem.target = self
            cutItem.isEnabled = parent.viewModel.canCutCopy
            menu.addItem(cutItem)

            let copyItem = NSMenuItem(title: "Copy", action: #selector(copySelection(_:)), keyEquivalent: "")
            copyItem.target = self
            copyItem.isEnabled = parent.viewModel.canCutCopy
            menu.addItem(copyItem)

            let pasteItem = NSMenuItem(title: "Paste", action: #selector(pasteIntoCurrentFolder(_:)), keyEquivalent: "")
            pasteItem.target = self
            pasteItem.isEnabled = parent.viewModel.canPaste
            menu.addItem(pasteItem)

            menu.addItem(.separator())

            let addFilesItem = NSMenuItem(title: "Add File(s)", action: #selector(addFiles(_:)), keyEquivalent: "")
            addFilesItem.target = self
            addFilesItem.isEnabled = parent.viewModel.canAddFiles
            menu.addItem(addFilesItem)

            let newFolderItem = NSMenuItem(title: "New Folder", action: #selector(newFolder(_:)), keyEquivalent: "")
            newFolderItem.target = self
            newFolderItem.isEnabled = parent.viewModel.canCreateFolder
            menu.addItem(newFolderItem)

            menu.addItem(.separator())

            let renameItem = NSMenuItem(title: "Rename", action: #selector(renameFromMenu(_:)), keyEquivalent: "")
            renameItem.target = self
            renameItem.representedObject = row
            menu.addItem(renameItem)

            let deleteItem = NSMenuItem(title: "Delete", action: #selector(deleteSelection(_:)), keyEquivalent: "")
            deleteItem.target = self
            deleteItem.isEnabled = parent.viewModel.canDeleteFile
            menu.addItem(deleteItem)
            return menu
        }

        @objc private func openFromMenu(_ sender: NSMenuItem) {
            guard let node = sender.representedObject as? PakNode else { return }
            open(node: node)
        }

        @objc private func cutSelection(_ sender: NSMenuItem) {
            parent.onCut()
        }

        @objc private func copySelection(_ sender: NSMenuItem) {
            parent.onCopy()
        }

        @objc private func pasteIntoCurrentFolder(_ sender: NSMenuItem) {
            let newNodes = parent.onPaste()
            if !newNodes.isEmpty {
                parent.selection = Set(newNodes.map { $0.id })
                tableView?.reloadData()
            }
        }

        @objc private func addFiles(_ sender: NSMenuItem) {
            parent.onAddFiles()
        }

        @objc private func newFolder(_ sender: NSMenuItem) {
            parent.onNewFolder()
        }

        @objc private func deleteSelection(_ sender: NSMenuItem) {
            parent.viewModel.deleteSelectedFile()
        }

        @objc private func renameFromMenu(_ sender: NSMenuItem) {
            cancelPendingRename()
            guard let tableView = tableView else { return }
            
            let row: Int
            if let representedRow = sender.representedObject as? Int {
                row = representedRow
            } else {
                row = tableView.selectedRow
            }
            
            guard row >= 0, row < parent.nodes.count else { return }

            // Ensure selection is correct if we came from context menu
            if tableView.selectedRow != row {
                tableView.selectRowIndexes(IndexSet(integer: row), byExtendingSelection: false)
            }
            
            // Set pending flag so updateNSView doesn't kill the edit
            let workItem = DispatchWorkItem { [weak self] in
                self?.beginRenaming(row: row)
                self?.renameWorkItem = nil
            }
            renameWorkItem = workItem
            DispatchQueue.main.async(execute: workItem)
        }

        private func open(node: PakNode) {
            if node.isFolder {
                parent.onOpenFolder(node)
            } else {
                parent.viewModel.openInDefaultApp(node: node)
            }
        }
    }
}
