import AppKit
import Foundation
import SwiftUI

struct PakItemInfo: Identifiable {
    let id: PakNode.ID
    let name: String
    let kind: String
    let path: String
    let archiveName: String
    let size: String
    let contents: String?
    let isFolder: Bool

    init(node: PakNode, root: PakNode, archiveName: String) {
        id = node.id
        name = node.name
        kind = node.fileType
        path = Self.path(to: node, in: root) ?? node.name
        self.archiveName = archiveName
        isFolder = node.isFolder

        if node.isFolder {
            let summary = Self.folderSummary(node)
            size = Self.formattedSize(summary.bytes)
            contents = "\(summary.files) \(summary.files == 1 ? "file" : "files"), "
                + "\(summary.folders) \(summary.folders == 1 ? "folder" : "folders")"
        } else {
            size = Self.formattedSize(Int64(max(0, node.fileSize)))
            contents = nil
        }
    }

    private static func path(to target: PakNode, in root: PakNode) -> String? {
        if target === root { return "/" }

        var visited = Set<PakNode.ID>()
        var pending = (root.children ?? []).reversed().map { ($0, [$0.name]) }

        while let (node, components) = pending.popLast() {
            guard visited.insert(node.id).inserted else { continue }
            if node === target {
                return "/" + components.joined(separator: "/")
            }

            for child in (node.children ?? []).reversed() {
                pending.append((child, components + [child.name]))
            }
        }

        return nil
    }

    private static func folderSummary(_ folder: PakNode) -> (bytes: Int64, files: Int, folders: Int) {
        var bytes: Int64 = 0
        var files = 0
        var folders = 0
        var visited = Set<PakNode.ID>()
        var pending = folder.children ?? []

        while let node = pending.popLast() {
            guard visited.insert(node.id).inserted else { continue }

            if node.isFolder {
                folders = addingClamped(folders, 1)
                pending.append(contentsOf: node.children ?? [])
            } else {
                files = addingClamped(files, 1)
                bytes = addingClamped(bytes, Int64(max(0, node.fileSize)))
            }
        }

        return (bytes, files, folders)
    }

    private static func addingClamped<T: FixedWidthInteger>(_ lhs: T, _ rhs: T) -> T {
        let result = lhs.addingReportingOverflow(rhs)
        return result.overflow ? T.max : result.partialValue
    }

    private static func formattedSize(_ bytes: Int64) -> String {
        let concise = ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
        let exact = NumberFormatter.localizedString(from: NSNumber(value: bytes), number: .decimal)
        return "\(concise) (\(exact) bytes)"
    }
}

struct PakItemInfoView: View {
    let info: PakItemInfo
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack(spacing: 16) {
                Image(systemName: info.isFolder ? "folder.fill" : "doc.fill")
                    .font(.system(size: 48))
                    .foregroundStyle(
                        info.isFolder
                            ? Color.accentColor
                            : Color(nsColor: .secondaryLabelColor)
                    )
                    .frame(width: 58, height: 58)
                    .accessibilityHidden(true)

                VStack(alignment: .leading, spacing: 4) {
                    Text(info.name)
                        .font(.title2.weight(.semibold))
                        .lineLimit(2)
                        .truncationMode(.middle)
                        .textSelection(.enabled)
                        .help(info.name)
                    Text(info.kind)
                        .foregroundStyle(.secondary)
                }
            }

            Divider()

            Grid(alignment: .leading, horizontalSpacing: 14, verticalSpacing: 10) {
                infoRow("Archive:", info.archiveName)
                infoRow("Path:", info.path)
                infoRow("Size:", info.size)
                if let contents = info.contents {
                    infoRow("Contents:", contents)
                }
            }

            HStack {
                Spacer()
                Button("Done") {
                    dismiss()
                }
                .keyboardShortcut(.defaultAction)
            }
        }
        .padding(24)
        .frame(width: 460)
    }

    @ViewBuilder
    private func infoRow(_ label: String, _ value: String) -> some View {
        GridRow {
            Text(label)
                .foregroundStyle(.secondary)
            Text(value)
                .lineLimit(3)
                .truncationMode(.middle)
                .textSelection(.enabled)
                .help(value)
        }
    }
}
