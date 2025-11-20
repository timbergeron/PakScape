import SwiftUI
import AppKit

private final class PakIconCollectionView: NSCollectionView {
    var onHandledKeyDown: ((NSEvent) -> Bool)?

    override func keyDown(with event: NSEvent) {
        if let onHandledKeyDown, onHandledKeyDown(event) {
            return
        }
        super.keyDown(with: event)
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
        collectionView.reloadData()

        // Apply selection from SwiftUI to NSCollectionView
        let ids = selection
        let indexPaths: Set<IndexPath> = Set(
            nodes.enumerated().compactMap { index, node in
                ids.contains(node.id) ? IndexPath(item: index, section: 0) : nil
            }
        )
        collectionView.selectItems(at: indexPaths, scrollPosition: [])
    }

    final class Coordinator: NSObject, NSCollectionViewDataSource, NSCollectionViewDelegate {
        var parent: PakIconView
        weak var collectionView: NSCollectionView?
        private let typeSelectionResetInterval: TimeInterval = 1.0
        private var typeSelectionBuffer = ""
        private var lastTypeSelectionDate = Date.distantPast

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
            updateSelection(from: collectionView)
        }

        func collectionView(_ collectionView: NSCollectionView, didDeselectItemsAt indexPaths: Set<IndexPath>) {
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
            guard recognizer.state == .ended,
                  let collectionView = collectionView else { return }

            let location = recognizer.location(in: collectionView)
            guard let indexPath = collectionView.indexPathForItem(at: location),
                  indexPath.item >= 0,
                  indexPath.item < parent.nodes.count else { return }

            let node = parent.nodes[indexPath.item]
            if node.isFolder {
                parent.onOpenFolder(node)
            }
        }
    }
}

final class PakIconItem: NSCollectionViewItem {
    static let reuseIdentifier = NSUserInterfaceItemIdentifier("PakIconItem")

    private let iconContainerView = NSView()
    private let iconView = NSImageView()
    private let nameField = NSTextField(labelWithString: "")
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
