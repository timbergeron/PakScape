import SwiftUI
import AppKit

private final class PakIconCollectionView: NSCollectionView {
    var onHandledKeyDown: ((NSEvent) -> Bool)?
    var contextMenuProvider: ((IndexPath) -> NSMenu?)?
    var renameHandler: (() -> Void)?

    override func keyDown(with event: NSEvent) {
        if let onHandledKeyDown, onHandledKeyDown(event) {
            return
        }
        super.keyDown(with: event)
    }

    override func menu(for event: NSEvent) -> NSMenu? {
        let point = convert(event.locationInWindow, from: nil)
        if let indexPath = indexPathForItem(at: point) {
            if !selectionIndexPaths.contains(indexPath) {
                selectItems(at: [indexPath], scrollPosition: [])
            }
            if let menu = contextMenuProvider?(indexPath) {
                return menu
            }
        }
        return super.menu(for: event)
    }

    @objc func renameSelectedItem(_ sender: Any?) {
        renameHandler?()
    }
}

fileprivate enum IconZoomLevel: Int {
    case small = 0
    case medium = 1
    case large = 2

    var itemSize: NSSize {
        switch self {
        case .small:
            return NSSize(width: 120, height: 125)
        case .medium:
            return NSSize(width: 150, height: 160)
        case .large:
            return NSSize(width: 190, height: 200)
        }
    }

    var iconDimension: CGFloat {
        switch self {
        case .small:
            return 64
        case .medium:
            return 96
        case .large:
            return 128
        }
    }

    var symbolPointSize: CGFloat {
        switch self {
        case .small:
            return 32
        case .medium:
            return 48
        case .large:
            return 64
        }
    }
}

struct PakIconView: NSViewRepresentable {
    var nodes: [PakNode]
    @Binding var selection: Set<PakNode.ID>
    var zoomLevel: Int
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
        let layout = NSCollectionViewFlowLayout()
        let zoom = IconZoomLevel(rawValue: zoomLevel) ?? .medium
        layout.itemSize = zoom.itemSize
        layout.sectionInset = NSEdgeInsets(top: 8, left: 8, bottom: 8, right: 8)
        layout.minimumInteritemSpacing = 8
        layout.minimumLineSpacing = 12

        let collectionView = PakIconCollectionView()
        collectionView.collectionViewLayout = layout
        collectionView.isSelectable = true
        collectionView.allowsMultipleSelection = true
        collectionView.delegate = context.coordinator
        collectionView.dataSource = context.coordinator
        collectionView.register(PakIconItem.self, forItemWithIdentifier: PakIconItem.reuseIdentifier)
        collectionView.setDraggingSourceOperationMask(.copy, forLocal: false)
        collectionView.onHandledKeyDown = { [weak coordinator = context.coordinator] event in
            coordinator?.handleKeyDown(event) ?? false
        }
        collectionView.contextMenuProvider = { [weak coordinator = context.coordinator] indexPath in
            coordinator?.contextMenu(for: indexPath)
        }
        collectionView.renameHandler = { [weak coordinator = context.coordinator] in
            coordinator?.renameFromEditCommand()
        }

        let doubleClickRecognizer = NSClickGestureRecognizer(target: context.coordinator, action: #selector(Coordinator.handleDoubleClick(_:)))
        doubleClickRecognizer.numberOfClicksRequired = 2
        collectionView.addGestureRecognizer(doubleClickRecognizer)

        let scrollView = NSScrollView()
        scrollView.hasVerticalScroller = true
        scrollView.documentView = collectionView

        context.coordinator.collectionView = collectionView
        return scrollView
    }

    func updateNSView(_ nsView: NSScrollView, context: Context) {
        context.coordinator.parent = self
        guard let collectionView = context.coordinator.collectionView else { return }

        if let layout = collectionView.collectionViewLayout as? NSCollectionViewFlowLayout {
            let zoom = IconZoomLevel(rawValue: zoomLevel) ?? .medium
            layout.itemSize = zoom.itemSize
        }

        let firstResponder = collectionView.window?.firstResponder
        let isEditing = (firstResponder is NSTextView) || (firstResponder is NSTextField)
        if !isEditing {
            collectionView.reloadData()

            // Apply selection from SwiftUI to NSCollectionView when not editing.
            let ids = selection
            let indexPaths: Set<IndexPath> = Set(
                nodes.enumerated().compactMap { index, node in
                    ids.contains(node.id) ? IndexPath(item: index, section: 0) : nil
                }
            )
            collectionView.selectItems(at: indexPaths, scrollPosition: [])
        }
    }

    final class Coordinator: NSObject, NSCollectionViewDataSource, NSCollectionViewDelegate, NSTextFieldDelegate {
        var parent: PakIconView
        weak var collectionView: NSCollectionView?
        private let typeSelectionResetInterval: TimeInterval = 1.0
        private var typeSelectionBuffer = ""
        private var lastTypeSelectionDate = Date.distantPast
        private var renameWorkItem: DispatchWorkItem?

        init(parent: PakIconView) {
            self.parent = parent
        }

        func numberOfSections(in collectionView: NSCollectionView) -> Int {
            1
        }

        func collectionView(_ collectionView: NSCollectionView, numberOfItemsInSection section: Int) -> Int {
            parent.nodes.count
        }

        func collectionView(_ collectionView: NSCollectionView, itemForRepresentedObjectAt indexPath: IndexPath) -> NSCollectionViewItem {
            let item = collectionView.makeItem(withIdentifier: PakIconItem.reuseIdentifier, for: indexPath)
            guard let iconItem = item as? PakIconItem else { return item }
            let node = parent.nodes[indexPath.item]
            let preview = previewImage(for: node)
            iconItem.configure(with: node, zoomLevel: parent.zoomLevel, previewImage: preview)
            iconItem.nameField.tag = indexPath.item
            return iconItem
        }

        func collectionView(_ collectionView: NSCollectionView, pasteboardWriterForItemAt indexPath: IndexPath) -> NSPasteboardWriting? {
            guard indexPath.item >= 0 && indexPath.item < parent.nodes.count else { return nil }
            let node = parent.nodes[indexPath.item]
            do {
                let url = try parent.viewModel.exportToTemporaryLocation(node: node)
                return url as NSURL
            } catch {
                return nil
            }
        }

        func collectionView(_ collectionView: NSCollectionView, didSelectItemsAt indexPaths: Set<IndexPath>) {
            cancelPendingRename()
            updateSelection(from: collectionView)

            guard indexPaths.count == 1,
                  let indexPath = indexPaths.first else { return }

            let event = NSApp.currentEvent
            let modifiers = event?.modifierFlags ?? []
            if modifiers.contains(.command) ||
                modifiers.contains(.shift) ||
                modifiers.contains(.option) ||
                modifiers.contains(.control) {
                return
            }
            if let type = event?.type,
               type != .leftMouseUp && type != .leftMouseDown {
                return
            }

            let selection = collectionView.selectionIndexPaths
            guard selection.contains(indexPath), selection.count == 1 else { return }

            let workItem = DispatchWorkItem { [weak self] in
                guard let self = self,
                      let collectionView = self.collectionView else { return }

                let currentSelection = collectionView.selectionIndexPaths
                guard currentSelection.contains(indexPath), currentSelection.count == 1 else { return }

                // Verify mouse is still over the item
                let mouseLocation = collectionView.window?.mouseLocationOutsideOfEventStream ?? .zero
                let localPoint = collectionView.convert(mouseLocation, from: nil)
                if let mouseIndexPath = collectionView.indexPathForItem(at: localPoint),
                   mouseIndexPath == indexPath {
                    if let item = collectionView.item(at: indexPath) as? PakIconItem {
                        item.beginRenaming(delegate: self)
                    }
                }
            }

            renameWorkItem = workItem
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5, execute: workItem)
        }

        func collectionView(_ collectionView: NSCollectionView, didDeselectItemsAt indexPaths: Set<IndexPath>) {
            cancelPendingRename()
            updateSelection(from: collectionView)
        }

        private func updateSelection(from collectionView: NSCollectionView) {
            var ids = Set<PakNode.ID>()
            for indexPath in collectionView.selectionIndexPaths {
                let item = indexPath.item
                if item >= 0 && item < parent.nodes.count {
                    ids.insert(parent.nodes[item].id)
                }
            }
            parent.selection = ids
        }

        private func previewImage(for node: PakNode) -> NSImage? {
            parent.viewModel.previewImage(for: node)
        }

        func renameFromEditCommand() {
            cancelPendingRename()
            guard let collectionView = collectionView else { return }

            let selection = collectionView.selectionIndexPaths
            guard selection.count == 1, let indexPath = selection.first else { return }
            guard indexPath.item >= 0, indexPath.item < parent.nodes.count else { return }

            let menuItem = NSMenuItem()
            menuItem.representedObject = indexPath
            renameItem(menuItem)
        }

        func handleKeyDown(_ event: NSEvent) -> Bool {
            guard let collectionView = collectionView else { return false }
            let modifiers = event.modifierFlags.intersection([.command, .option, .control])
            guard modifiers.isEmpty else { return false }
            guard let characters = event.charactersIgnoringModifiers, !characters.isEmpty else { return false }

            let scalars = characters.unicodeScalars.filter { scalar in
                // Ignore control characters and space (space is reserved for Quick Look-like actions).
                guard scalar.isASCII, scalar.value >= 0x21 else { return false }
                return !CharacterSet.controlCharacters.contains(scalar)
            }

            guard !scalars.isEmpty else { return false }

            let input = String(String.UnicodeScalarView(scalars))
            updateTypeSelectionBuffer(with: input)

            guard let match = findMatch(for: typeSelectionBuffer, in: collectionView) else {
                // No match, just ignore without beeping.
                return true
            }

            let indexPath = IndexPath(item: match, section: 0)
            let set = Set([indexPath])
            collectionView.selectItems(at: set, scrollPosition: .centeredVertically)
            collectionView.scrollToItems(at: set, scrollPosition: .centeredVertically)
            updateSelection(from: collectionView)
            return true
        }

        private func updateTypeSelectionBuffer(with input: String) {
            let now = Date()
            if now.timeIntervalSince(lastTypeSelectionDate) > typeSelectionResetInterval {
                typeSelectionBuffer = ""
            } else if typeSelectionBuffer.count == 1, typeSelectionBuffer == input.lowercased() {
                // Repeatedly pressing the same key cycles through matches like Finder.
                typeSelectionBuffer = ""
            }

            typeSelectionBuffer += input.lowercased()
            lastTypeSelectionDate = now
        }

        private func findMatch(for prefix: String, in collectionView: NSCollectionView) -> Int? {
            guard !prefix.isEmpty, !parent.nodes.isEmpty else { return nil }
            let lowerPrefix = prefix.lowercased()

            let currentSelection = collectionView.selectionIndexPaths.sorted { $0.item < $1.item }.first?.item ?? -1
            let start = currentSelection + 1

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
                if parent.nodes[index].name.lowercased().contains(prefix) {
                    return index
                }
            }
            return nil
        }

        @objc func handleDoubleClick(_ recognizer: NSClickGestureRecognizer) {
            cancelPendingRename()
            guard recognizer.state == .ended,
                  let collectionView = collectionView else { return }

            let location = recognizer.location(in: collectionView)
            guard let indexPath = collectionView.indexPathForItem(at: location),
                  indexPath.item >= 0,
                  indexPath.item < parent.nodes.count else { return }

            let node = parent.nodes[indexPath.item]
            open(node: node)
        }

        private func cancelPendingRename() {
            renameWorkItem?.cancel()
            renameWorkItem = nil
        }

        func contextMenu(for indexPath: IndexPath) -> NSMenu? {
            guard indexPath.item >= 0, indexPath.item < parent.nodes.count else { return nil }
            let node = parent.nodes[indexPath.item]

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

            let addFilesItem = NSMenuItem(title: "Add File(s)…", action: #selector(addFiles(_:)), keyEquivalent: "")
            addFilesItem.target = self
            addFilesItem.isEnabled = parent.viewModel.canAddFiles
            menu.addItem(addFilesItem)

            let newFolderItem = NSMenuItem(title: "New Folder", action: #selector(newFolder(_:)), keyEquivalent: "")
            newFolderItem.target = self
            newFolderItem.isEnabled = parent.viewModel.canCreateFolder
            menu.addItem(newFolderItem)

            menu.addItem(.separator())

            let renameItem = NSMenuItem(title: "Rename", action: #selector(renameItem(_:)), keyEquivalent: "")
            renameItem.target = self
            renameItem.representedObject = indexPath
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
                collectionView?.reloadData()
            }
        }

        @objc private func addFiles(_ sender: NSMenuItem) {
            parent.onAddFiles()
        }

        @objc private func newFolder(_ sender: NSMenuItem) {
            parent.onNewFolder()
        }

        @objc private func renameItem(_ sender: NSMenuItem) {
            guard let indexPath = sender.representedObject as? IndexPath,
                  let collectionView = collectionView,
                  let item = collectionView.item(at: indexPath) as? PakIconItem else { return }

            collectionView.selectItems(at: [indexPath], scrollPosition: [])
            
            // Use a work item to be consistent with other rename paths
            let workItem = DispatchWorkItem { [weak self] in
                item.beginRenaming(delegate: self!)
                self?.renameWorkItem = nil
            }
            renameWorkItem = workItem
            DispatchQueue.main.async(execute: workItem)
        }

        @objc private func deleteSelection(_ sender: NSMenuItem) {
            parent.viewModel.deleteSelectedFile()
        }

        private func open(node: PakNode) {
            if node.isFolder {
                parent.onOpenFolder(node)
            } else {
                parent.viewModel.openInDefaultApp(node: node)
            }
        }

        func controlTextDidEndEditing(_ obj: Notification) {
            guard let collectionView = collectionView else { return }

            let textField: NSTextField
            if let tf = obj.object as? NSTextField {
                textField = tf
            } else if let tv = obj.object as? NSTextView,
                      let tf = tv.delegate as? NSTextField {
                textField = tf
            } else {
                return
            }

            let newName = textField.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
            guard !newName.isEmpty else { return }

            let index = textField.tag
            guard index >= 0, index < parent.nodes.count else { return }

            let node = parent.nodes[index]
            guard newName != node.name else { return }

            parent.viewModel.rename(node: node, to: newName)

            let indexPath = IndexPath(item: index, section: 0)
            if let item = collectionView.item(at: indexPath) as? PakIconItem {
                item.endRenaming()
            }
        }
    }
}

final class PakIconItem: NSCollectionViewItem {
    static let reuseIdentifier = NSUserInterfaceItemIdentifier("PakIconItem")

    private let iconContainerView = NSView()
    private let iconView = NSImageView()
    let nameField = NSTextField(string: "")
    private var iconWidthConstraint: NSLayoutConstraint!
    private var iconHeightConstraint: NSLayoutConstraint!

    override func loadView() {
        view = NSView()
        view.wantsLayer = true

        iconContainerView.translatesAutoresizingMaskIntoConstraints = false
        iconContainerView.wantsLayer = true
        
        iconView.translatesAutoresizingMaskIntoConstraints = false
        iconView.imageScaling = .scaleProportionallyUpOrDown
        iconView.wantsLayer = true
        
        nameField.translatesAutoresizingMaskIntoConstraints = false
        nameField.isBordered = false
        nameField.isBezeled = false
        nameField.drawsBackground = false
        nameField.isEditable = false
        nameField.isSelectable = false
        nameField.alignment = .center
        nameField.lineBreakMode = .byCharWrapping
        nameField.maximumNumberOfLines = 2
        nameField.cell?.wraps = true
        nameField.cell?.isScrollable = false
        nameField.font = NSFont.systemFont(ofSize: NSFont.smallSystemFontSize)
        nameField.wantsLayer = true

        view.addSubview(iconContainerView)
        iconContainerView.addSubview(iconView)
        view.addSubview(nameField)

        iconWidthConstraint = iconContainerView.widthAnchor.constraint(equalToConstant: 96)
        iconHeightConstraint = iconContainerView.heightAnchor.constraint(equalToConstant: 96)

        NSLayoutConstraint.activate([
            iconContainerView.topAnchor.constraint(equalTo: view.topAnchor, constant: 4),
            iconContainerView.centerXAnchor.constraint(equalTo: view.centerXAnchor),
            iconWidthConstraint,
            iconHeightConstraint,

            // Inset iconView within the container to create padding for the selection background
            iconView.topAnchor.constraint(equalTo: iconContainerView.topAnchor, constant: 6),
            iconView.leadingAnchor.constraint(equalTo: iconContainerView.leadingAnchor, constant: 6),
            iconView.trailingAnchor.constraint(equalTo: iconContainerView.trailingAnchor, constant: -6),
            iconView.bottomAnchor.constraint(equalTo: iconContainerView.bottomAnchor, constant: -6),

            nameField.topAnchor.constraint(equalTo: iconContainerView.bottomAnchor, constant: 4),
            nameField.centerXAnchor.constraint(equalTo: view.centerXAnchor),
            nameField.widthAnchor.constraint(lessThanOrEqualTo: view.widthAnchor, constant: -8),
            nameField.bottomAnchor.constraint(lessThanOrEqualTo: view.bottomAnchor, constant: -4)
        ])
    }

    func configure(with node: PakNode, zoomLevel: Int, previewImage: NSImage?) {
        let level = IconZoomLevel(rawValue: zoomLevel) ?? .medium

        iconWidthConstraint.constant = level.iconDimension
        iconHeightConstraint.constant = level.iconDimension

        if let previewImage {
            iconView.image = previewImage
        } else {
            let symbolName = node.isFolder ? "folder.fill" : "doc"
            if let baseImage = NSImage(systemSymbolName: symbolName, accessibilityDescription: nil) {
                let config = NSImage.SymbolConfiguration(pointSize: level.symbolPointSize, weight: .regular)
                iconView.image = baseImage.withSymbolConfiguration(config)
            } else {
                iconView.image = nil
            }
        }
        nameField.stringValue = node.name
        nameField.toolTip = node.name
        
        updateSelectionState()
    }

    func beginRenaming(delegate: NSTextFieldDelegate) {
        nameField.isEditable = true
        nameField.isSelectable = true
        nameField.delegate = delegate
        view.window?.makeFirstResponder(nameField)
        nameField.selectText(nil)
    }

    func endRenaming() {
        nameField.isEditable = false
        nameField.isSelectable = false
        nameField.delegate = nil
    }

    override var isSelected: Bool {
        didSet {
            updateSelectionState()
        }
    }
    
    private func updateSelectionState() {
        if isSelected {
            // Icon background (applied to container)
            iconContainerView.layer?.backgroundColor = NSColor.selectedContentBackgroundColor.cgColor
            iconContainerView.layer?.cornerRadius = 8
            
            // Text background
            nameField.drawsBackground = true
            nameField.backgroundColor = NSColor.selectedContentBackgroundColor
            nameField.textColor = NSColor.selectedControlTextColor
            nameField.layer?.cornerRadius = 6
            nameField.layer?.masksToBounds = true
            
            // Clear main view background
            view.layer?.backgroundColor = NSColor.clear.cgColor
        } else {
            iconContainerView.layer?.backgroundColor = NSColor.clear.cgColor
            
            nameField.drawsBackground = false
            nameField.backgroundColor = .clear
            nameField.textColor = NSColor.labelColor
            nameField.layer?.cornerRadius = 0
            
            view.layer?.backgroundColor = NSColor.clear.cgColor
        }
    }
}
