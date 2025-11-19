import Foundation

// One file entry in the PAK directory
struct PakEntry {
    let name: String      // full path: "progs/v_shot.mdl"
    let offset: Int       // byte offset into the PAK
    let length: Int       // length in bytes
}

// Node used for the folder/file tree
final class PakNode: Identifiable, Hashable {
    static func == (lhs: PakNode, rhs: PakNode) -> Bool {
        lhs.id == rhs.id
    }

    func hash(into hasher: inout Hasher) {
        hasher.combine(id)
    }

    let id = UUID()
    var name: String
    var children: [PakNode]? = []   // NOTE: optional array for OutlineGroup
    var entry: PakEntry?            // nil for folders
    var localData: Data?            // For newly added files or modified content

    init(name: String, entry: PakEntry? = nil) {
        self.name = name
        self.entry = entry
    }

    var isFolder: Bool { entry == nil && localData == nil }

    var folderChildren: [PakNode]? {
        children?.filter { $0.isFolder }
    }

    var fileSize: Int {
        if let local = localData { return local.count }
        return entry?.length ?? 0
    }

    var formattedFileSize: String {
        guard !isFolder else { return "--" }
        return PakNode.sizeFormatter.string(fromByteCount: Int64(fileSize))
    }

    private static let sizeFormatter: ByteCountFormatter = {
        let formatter = ByteCountFormatter()
        formatter.countStyle = .file
        formatter.includesActualByteCount = false
        return formatter
    }()

    var fileType: String {
        if isFolder { return "Folder" }
        return (name as NSString).pathExtension.uppercased() + " File"
    }
}

// Whole loaded PAK
final class PakFile {
    let name: String
    var data: Data
    var entries: [PakEntry]
    let root: PakNode
    var version = UUID()

    init(name: String, data: Data, entries: [PakEntry], root: PakNode) {
        self.name = name
        self.data = data
        self.entries = entries
        self.root = root
    }

    static func empty(name: String) -> PakFile {
        let root = PakNode(name: "/")
        return PakFile(name: name, data: Data(), entries: [], root: root)
    }
}

enum PakError: Error, LocalizedError {
    case invalidHeader
    case badDirectory
    case unknown(String)

    var errorDescription: String? {
        switch self {
        case .invalidHeader:
            return "Not a valid Quake PAK (missing PACK header)."
        case .badDirectory:
            return "Directory table is corrupt or truncated."
        case .unknown(let msg):
            return msg
        }
    }
}

struct PakLoader {

    static func load(data: Data, name: String) throws -> PakFile {
        // Header: "PACK" + dirOffset (int32) + dirLength (int32)
        guard data.count >= 12 else { throw PakError.invalidHeader }

        let ident = String(bytes: data[0..<4], encoding: .ascii) ?? ""
        guard ident == "PACK" else { throw PakError.invalidHeader }

        let dirOffset = Int(readInt32LE(data, at: 4))
        let dirLength = Int(readInt32LE(data, at: 8))

        guard dirOffset >= 0, dirLength >= 0,
              dirOffset + dirLength <= data.count else {
            throw PakError.badDirectory
        }

        let entrySize = 64 // 56 bytes name + 4 offset + 4 length
        guard dirLength % entrySize == 0 else {
            throw PakError.badDirectory
        }

        let count = dirLength / entrySize
        var entries: [PakEntry] = []
        entries.reserveCapacity(count)

        for i in 0..<count {
            let base = dirOffset + i * entrySize

            let nameData = data.subdata(in: base ..< base + 56)
            let name = asciiStringFromNullTerminated(nameData)

            let filePos = Int(readInt32LE(data, at: base + 56))
            let fileLen = Int(readInt32LE(data, at: base + 60))

            guard filePos >= 0, fileLen >= 0,
                  filePos + fileLen <= data.count else {
                throw PakError.badDirectory
            }

            entries.append(PakEntry(name: name, offset: filePos, length: fileLen))
        }

        let root = buildTree(from: entries)
        return PakFile(name: name, data: data, entries: entries, root: root)
    }

    // MARK: - Helpers

    private static func readInt32LE(_ data: Data, at offset: Int) -> Int32 {
        let range = offset ..< offset + 4
        return data.subdata(in: range).withUnsafeBytes {
            Int32(littleEndian: $0.load(as: Int32.self))
        }
    }

    private static func asciiStringFromNullTerminated(_ data: Data) -> String {
        let trimmed = data.prefix { $0 != 0 }
        return String(bytes: trimmed, encoding: .ascii) ?? ""
    }

    // Build a folder/file tree from flat entry list
    private static func buildTree(from entries: [PakEntry]) -> PakNode {
        let root = PakNode(name: "/")

        for entry in entries {
            let parts = entry.name.split(separator: "/").map(String.init)
            guard !parts.isEmpty else { continue }

            var current = root

            for (index, part) in parts.enumerated() {
                let isLast = (index == parts.count - 1)

                if isLast {
                    // File node
                    let fileNode = PakNode(name: part, entry: entry)
                    current.children?.append(fileNode)
                } else {
                    // Folder node
                    if let existingFolder = current.children?.first(where: { $0.name == part && $0.entry == nil }) {
                        current = existingFolder
                    } else {
                        let newFolder = PakNode(name: part)
                        current.children?.append(newFolder)
                        current = newFolder
                    }
                }
            }
        }

        sortNodeRecursively(root)
        return root
    }

    private static func sortNodeRecursively(_ node: PakNode) {
        node.children?.sort {
            // Folders first, then files, then alpha
            if $0.isFolder != $1.isFolder {
                return $0.isFolder && !$1.isFolder
            }
            return $0.name.lowercased() < $1.name.lowercased()
        }
        node.children?.forEach { sortNodeRecursively($0) }
    }
}

struct PakWriter {
    struct Output {
        let data: Data
        let entries: [PakEntry]
    }

    static func write(root: PakNode, originalData: Data?) -> Output {
        var files: [(path: String, node: PakNode)] = []
        collectFiles(node: root, currentPath: "", into: &files)
        files.sort { $0.path.localizedCaseInsensitiveCompare($1.path) == .orderedAscending }

        var output = Data()
        output.append(Data(count: 12))

        var newEntries: [PakEntry] = []

        for (path, node) in files {
            let offset = output.count
            let payload: Data

            if let local = node.localData {
                payload = local
            } else if let entry = node.entry, let original = originalData,
                      entry.offset + entry.length <= original.count {
                payload = original.subdata(in: entry.offset ..< entry.offset + entry.length)
            } else {
                payload = Data()
            }

            output.append(payload)
            let newEntry = PakEntry(name: path, offset: offset, length: payload.count)
            node.entry = newEntry
            node.localData = nil
            newEntries.append(newEntry)
        }

        let dirOffset = output.count
        let dirLength = newEntries.count * 64

        for entry in newEntries {
            var nameBytes = [UInt8](repeating: 0, count: 56)
            let ascii = asciiBytes(forName: entry.name)
            for (i, byte) in ascii.prefix(55).enumerated() {
                nameBytes[i] = byte
            }
            output.append(contentsOf: nameBytes)
            output.append(int32ToData(Int32(entry.offset)))
            output.append(int32ToData(Int32(entry.length)))
        }

        let ident = "PACK".data(using: .ascii)!
        output.replaceSubrange(0..<4, with: ident)
        output.replaceSubrange(4..<8, with: int32ToData(Int32(dirOffset)))
        output.replaceSubrange(8..<12, with: int32ToData(Int32(dirLength)))

        return Output(data: output, entries: newEntries)
    }

    private static func collectFiles(node: PakNode, currentPath: String, into files: inout [(path: String, node: PakNode)]) {
        guard let children = node.children else { return }

        for child in children {
            let nextPath: String
            if currentPath.isEmpty {
                nextPath = child.name
            } else {
                nextPath = "\(currentPath)/\(child.name)"
            }

            if child.isFolder {
                collectFiles(node: child, currentPath: nextPath, into: &files)
            } else {
                files.append((nextPath, child))
            }
        }
    }

    private static func asciiBytes(forName name: String) -> [UInt8] {
        var result: [UInt8] = []
        result.reserveCapacity(55)

        for scalar in name.unicodeScalars {
            if result.count >= 55 { break }
            let v = scalar.value
            // Printable ASCII range; map everything else to '?'
            if v >= 0x20 && v <= 0x7E {
                result.append(UInt8(v))
            } else if v != 0 {
                result.append(UInt8(63)) // '?'
            }
        }

        return result
    }

    private static func int32ToData(_ value: Int32) -> Data {
        var v = value.littleEndian
        return Data(bytes: &v, count: 4)
    }
}
