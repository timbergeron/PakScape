import SwiftUI

fileprivate enum DetailViewStyle: String, CaseIterable, Identifiable {
    case list = "List"
    case icons = "Icons"

    var id: Self { self }
}

struct ContentView: View {
    @Binding var document: PakDocument
    @StateObject private var model: PakViewModel
    @State private var selectedFileID: PakNode.ID? // Selection in the detail table
    @State private var detailViewStyle: DetailViewStyle = .list
    @State private var renamingNodeID: PakNode.ID?
    @State private var renamingText: String = ""
    @State private var renamingNode: PakNode?
    @FocusState private var renamingFocus: PakNode.ID?

    init(document: Binding<PakDocument>) {
        self._document = document
        self._model = StateObject(wrappedValue: PakViewModel(pakFile: document.wrappedValue.pakFile))
    }

    @State private var sortOrder = [KeyPathComparator(\PakNode.name)]

    var body: some View {
        NavigationSplitView {
            sidebar
        } detail: {
            detailView
        }
    }

    private var sidebar: some View {
        List(selection: $model.currentFolder) {
            if let root = model.pakFile?.root {
                // Root node itself
                NavigationLink(value: root) {
                    Label("/", systemImage: "folder.fill")
                        .foregroundStyle(.yellow)
                }
                
                // Recursive children
                OutlineGroup(root.folderChildren ?? [], children: \.folderChildren) { node in
                    NavigationLink(value: node) {
                        Label(node.name, systemImage: "folder.fill")
                            .foregroundStyle(.yellow)
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
                let sortedChildren = (folder.children ?? []).sorted(using: sortOrder)
                
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

                    Divider()

                    Group {
                        switch detailViewStyle {
                        case .list:
                            Table(sortedChildren, selection: $selectedFileID, sortOrder: $sortOrder) {
                                TableColumn("Name", value: \.name) { node in
                                    HStack {
                                        Image(systemName: node.isFolder ? "folder.fill" : "doc")
                                            .foregroundStyle(node.isFolder ? .yellow : .primary)
                                        nameLabel(for: node)
                                            .frame(maxWidth: .infinity, alignment: .leading)
                                    }
                                    .contentShape(Rectangle())
                                    .onTapGesture {
                                        select(node)
                                    }
                                    .simultaneousGesture(
                                        TapGesture(count: 2).onEnded {
                                            select(node)
                                            if node.isFolder {
                                                model.currentFolder = node
                                            }
                                        }
                                    )
                                    .contextMenu {
                                        contextMenuActions(for: node)
                                    }
                                }
                                TableColumn("Size", value: \.fileSize) { node in
                                    Text(node.isFolder ? "--" : "\(node.fileSize)")
                                        .monospacedDigit()
                                }
                                TableColumn("Type", value: \.fileType) { node in
                                    Text(node.fileType)
                                        .foregroundStyle(.secondary)
                                }
                            }

                        case .icons:
                            ScrollView {
                                let columns = [GridItem(.adaptive(minimum: 110), spacing: 12)]
                                LazyVGrid(columns: columns, spacing: 12) {
                                    ForEach(sortedChildren) { node in
                                        let isSelected = selectedFileID == node.id
                                        Button {
                                            select(node)
                                        } label: {
                                            VStack(spacing: 6) {
                                                Image(systemName: node.isFolder ? "folder.fill" : "doc")
                                                    .font(.system(size: 36))
                                                    .foregroundStyle(node.isFolder ? .yellow : .primary)
                                                nameLabel(for: node, font: .caption, alignment: .center)
                                                    .lineLimit(2)
                                            }
                                            .padding()
                                            .frame(maxWidth: .infinity)
                                            .background(
                                                RoundedRectangle(cornerRadius: 8)
                                                    .fill(isSelected ? Color.accentColor.opacity(0.2) : Color.clear)
                                            )
                                            .overlay(
                                                RoundedRectangle(cornerRadius: 8)
                                                    .stroke(isSelected ? Color.accentColor : Color.secondary.opacity(0.2), lineWidth: isSelected ? 2 : 1)
                                            )
                                        }
                                        .buttonStyle(.plain)
                                        .simultaneousGesture(
                                            TapGesture(count: 2).onEnded {
                                                select(node)
                                                if node.isFolder {
                                                    model.currentFolder = node
                                                }
                                            }
                                        )
                                        .contextMenu {
                                            contextMenuActions(for: node)
                                        }
                                    }
                                }
                                .padding()
                            }
                        }
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                }
                .onChange(of: selectedFileID) { newValue in
                    // Update model selection for export
                    if let id = newValue {
                        model.selectedFile = folder.children?.first(where: { $0.id == id })
                    } else {
                        model.selectedFile = nil
                    }
                    if let renamingID = renamingNodeID, renamingID != newValue {
                        cancelRenaming()
                    }
                }
            } else {
                Text("Select a folder in the sidebar")
                    .foregroundStyle(.secondary)
            }
        }
        .onChange(of: model.currentFolder) { _ in
            selectedFileID = nil
            model.selectedFile = nil
            cancelRenaming()
        }
        .onChange(of: renamingFocus) { newValue in
            if let renameID = renamingNodeID, renameID != newValue {
                commitRename()
            }
        }
    }

    private func select(_ node: PakNode) {
        selectedFileID = node.id
        model.selectedFile = node
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

        let trimmed = renamingText.trimmingCharacters(in: .whitespacesAndNewlines)
        if !trimmed.isEmpty {
            node.name = trimmed
            if let entry = node.entry {
                let updatedPath: String
                if let slashIndex = entry.name.lastIndex(of: "/") {
                    let prefix = entry.name[..<entry.name.index(after: slashIndex)]
                    updatedPath = String(prefix) + trimmed
                } else {
                    updatedPath = trimmed
                }
                node.entry = PakEntry(name: updatedPath, offset: entry.offset, length: entry.length)
            }
        }
        cancelRenaming()
    }

    private func cancelRenaming() {
        renamingNode = nil
        renamingNodeID = nil
        renamingText = ""
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
        }
    }

    @ViewBuilder
    private func contextMenuActions(for node: PakNode) -> some View {
        Button("Select") {
            select(node)
        }
        Button("Rename…") {
            beginRenaming(node)
        }
        if node.isFolder {
            Button("Open Folder") {
                model.currentFolder = node
            }
        }
        if !node.isFolder {
            Button("Export…") {
                select(node)
                model.exportSelectedFile()
            }
        }
    }
}
