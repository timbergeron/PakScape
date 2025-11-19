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

    init(name: String, entry: PakEntry? = nil) {
        self.name = name
        self.entry = entry
    }

    var isFolder: Bool { entry == nil }

    var folderChildren: [PakNode]? {
        children?.filter { $0.isFolder }
    }

    var fileSize: Int {
        entry?.length ?? 0
    }

    var fileType: String {
        if isFolder { return "Folder" }
        return (name as NSString).pathExtension.uppercased() + " File"
    }
}

// Whole loaded PAK
struct PakFile {
    let name: String
    let data: Data
    let entries: [PakEntry]
    let root: PakNode
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
