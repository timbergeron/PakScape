import SwiftUI
import AppKit

fileprivate enum IconZoomLevel: Int {
    case small = 0
    case medium = 1
    case large = 2

    var itemSize: NSSize {
        switch self {
        case .small:
            return NSSize(width: 120, height: 170)
        case .medium:
            return NSSize(width: 150, height: 200)
        case .large:
            return NSSize(width: 190, height: 240)
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

        let collectionView = NSCollectionView()
        collectionView.collectionViewLayout = layout
        collectionView.isSelectable = true
        collectionView.allowsMultipleSelection = true
        collectionView.delegate = context.coordinator
        collectionView.dataSource = context.coordinator
        collectionView.register(PakIconItem.self, forItemWithIdentifier: PakIconItem.reuseIdentifier)
        collectionView.setDraggingSourceOperationMask(.copy, forLocal: false)

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
            iconItem.configure(with: node, zoomLevel: parent.zoomLevel)
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

    private let iconView = NSImageView()
    private let nameField = NSTextField(labelWithString: "")
    private var iconWidthConstraint: NSLayoutConstraint!
    private var iconHeightConstraint: NSLayoutConstraint!

    override func loadView() {
        view = NSView()
        view.wantsLayer = true

        iconView.translatesAutoresizingMaskIntoConstraints = false
        iconView.imageScaling = .scaleProportionallyUpOrDown
        nameField.translatesAutoresizingMaskIntoConstraints = false
        nameField.alignment = .center
        nameField.lineBreakMode = .byWordWrapping
        nameField.maximumNumberOfLines = 2
        nameField.cell?.wraps = true
        nameField.cell?.isScrollable = false
        nameField.font = NSFont.systemFont(ofSize: NSFont.smallSystemFontSize)

        view.addSubview(iconView)
        view.addSubview(nameField)

        iconWidthConstraint = iconView.widthAnchor.constraint(equalToConstant: 96)
        iconHeightConstraint = iconView.heightAnchor.constraint(equalToConstant: 96)

        NSLayoutConstraint.activate([
            iconView.topAnchor.constraint(equalTo: view.topAnchor, constant: 4),
            iconView.centerXAnchor.constraint(equalTo: view.centerXAnchor),
            iconWidthConstraint,
            iconHeightConstraint,

            nameField.topAnchor.constraint(equalTo: iconView.bottomAnchor, constant: 4),
            nameField.leadingAnchor.constraint(equalTo: view.leadingAnchor, constant: 4),
            nameField.trailingAnchor.constraint(equalTo: view.trailingAnchor, constant: -4),
            nameField.bottomAnchor.constraint(lessThanOrEqualTo: view.bottomAnchor, constant: -4)
        ])
    }

    func configure(with node: PakNode, zoomLevel: Int) {
        let level = IconZoomLevel(rawValue: zoomLevel) ?? .medium

        iconWidthConstraint.constant = level.iconDimension
        iconHeightConstraint.constant = level.iconDimension

        let symbolName = node.isFolder ? "folder.fill" : "doc"
        if let baseImage = NSImage(systemSymbolName: symbolName, accessibilityDescription: nil) {
            let config = NSImage.SymbolConfiguration(pointSize: level.symbolPointSize, weight: .regular)
            iconView.image = baseImage.withSymbolConfiguration(config)
        } else {
            iconView.image = nil
        }
        nameField.stringValue = node.name
        nameField.toolTip = node.name
    }

    override var isSelected: Bool {
        didSet {
            if isSelected {
                view.layer?.backgroundColor = NSColor.selectedContentBackgroundColor.cgColor
                view.layer?.cornerRadius = 8
            } else {
                view.layer?.backgroundColor = NSColor.clear.cgColor
            }
        }
    }
}
