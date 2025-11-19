import SwiftUI
import AppKit
import Foundation

struct PakListView: NSViewRepresentable {
    var nodes: [PakNode]
    @Binding var selection: Set<PakNode.ID>
    @Binding var sortOrder: [KeyPathComparator<PakNode>]
    var viewModel: PakViewModel
    var onOpenFolder: (PakNode) -> Void

    func makeCoordinator() -> Coordinator {
        Coordinator(parent: self)
    }

    func makeNSView(context: Context) -> NSScrollView {
        let tableView = NSTableView()
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

        let scrollView = NSScrollView()
        scrollView.hasVerticalScroller = true
        scrollView.documentView = tableView

        context.coordinator.tableView = tableView
        return scrollView
    }

    func updateNSView(_ nsView: NSScrollView, context: Context) {
        context.coordinator.parent = self
        guard let tableView = context.coordinator.tableView else { return }
        tableView.reloadData()

        // Apply selection from SwiftUI to NSTableView
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

    final class Coordinator: NSObject, NSTableViewDataSource, NSTableViewDelegate {
        var parent: PakListView
        weak var tableView: NSTableView?
        private var renameWorkItem: DispatchWorkItem?
        var lastSelectionChange = Date.distantPast
        private let nameColumnIdentifier = NSUserInterfaceItemIdentifier("name")

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
                    textField.target = self
                    textField.action = #selector(nameFieldEdited(_:))
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
                cell.imageView?.image = NSImage(systemSymbolName: node.isFolder ? "folder.fill" : "doc", accessibilityDescription: nil)
            } else if identifier == "size" {
                cell.textField?.stringValue = node.formattedFileSize
            } else if identifier == "type" {
                cell.textField?.stringValue = node.fileType
            }

            return cell
        }

        @objc private func nameFieldEdited(_ sender: NSTextField) {
            guard let tableView = tableView else { return }
            let row = tableView.row(for: sender)
            guard row >= 0, row < parent.nodes.count else { return }
            let node = parent.nodes[row]
            parent.viewModel.rename(node: node, to: sender.stringValue)
        }

        func tableViewSelectionDidChange(_ notification: Notification) {
            cancelPendingRename()
            lastSelectionChange = Date()
            guard let tableView = tableView else { return }
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

            let row = sender.clickedRow
            let column = sender.clickedColumn
            let nameColumnIndex = sender.column(withIdentifier: nameColumnIdentifier)
            guard row >= 0,
                  row < parent.nodes.count,
                  nameColumnIndex != -1,
                  column == nameColumnIndex,
                  sender.selectedRowIndexes.contains(row),
                  sender.selectedRowIndexes.count == 1 else { return }

            let workItem = DispatchWorkItem { [weak self] in
                guard let self = self, let tableView = self.tableView else { return }
                guard row >= 0,
                      row < self.parent.nodes.count,
                      tableView.selectedRowIndexes.contains(row) else { return }
                let nameColumn = tableView.column(withIdentifier: self.nameColumnIdentifier)
                guard nameColumn != -1 else { return }

                if let cell = tableView.view(atColumn: nameColumn, row: row, makeIfNecessary: false) as? NSTableCellView,
                   let textField = cell.textField {
                    tableView.window?.makeFirstResponder(textField)
                    textField.selectText(nil)
                } else {
                    tableView.editColumn(nameColumn, row: row, with: event, select: true)
                }
            }
            renameWorkItem = workItem
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5, execute: workItem)
        }

        @objc func tableViewDoubleClicked(_ sender: Any?) {
            cancelPendingRename()
            guard let tableView = tableView else { return }
            let row = tableView.clickedRow
            guard row >= 0 && row < parent.nodes.count else { return }
            let node = parent.nodes[row]
            if node.isFolder {
                parent.onOpenFolder(node)
            }
        }

        func cancelPendingRename() {
            renameWorkItem?.cancel()
            renameWorkItem = nil
        }
    }
}
