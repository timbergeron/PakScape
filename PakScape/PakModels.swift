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
        guard !isFolder else { return "—" }
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
        let pathExtension = (name as NSString).pathExtension
        return pathExtension.isEmpty ? "File" : "\(pathExtension.uppercased()) File"
    }
}

// Whole loaded PAK
final class PakFile {
    var name: String
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
    case unsafePath(String)
    case pathTooLong(String)
    case unsupportedPathCharacters(String)
    case duplicatePath(String)
    case missingData(String)
    case archiveTooLarge
    case invalidZip
    case unsupportedZipFeature(String)
    case expandedArchiveTooLarge
    case unknown(String)

    var errorDescription: String? {
        switch self {
        case .invalidHeader:
            return "Not a valid Quake PAK (missing PACK header)."
        case .badDirectory:
            return "Directory table is corrupt or truncated."
        case .unsafePath(let path):
            return "The archive contains an unsafe path: \(path)"
        case .pathTooLong(let path):
            return "The PAK path exceeds the 55-byte format limit: \(path)"
        case .unsupportedPathCharacters(let path):
            return "The PAK format only supports printable ASCII path names: \(path)"
        case .duplicatePath(let path):
            return "The archive contains a duplicate or conflicting path: \(path)"
        case .missingData(let path):
            return "The data for '\(path)' is missing or no longer readable."
        case .archiveTooLarge:
            return "The archive is too large for the Quake PAK format."
        case .invalidZip:
            return "The PK3 central directory is invalid or unsupported."
        case .unsupportedZipFeature(let feature):
            return "The PK3 uses an unsupported ZIP feature: \(feature)."
        case .expandedArchiveTooLarge:
            return "The PK3 would expand beyond PakScape's 2 GiB safety limit."
        case .unknown(let msg):
            return msg
        }
    }
}

enum PakPathValidator {
    static func normalizeArchiveEntryName(_ rawName: String) throws -> String {
        let normalized = rawName.replacingOccurrences(of: "\\", with: "/")
        let components = normalized.split(separator: "/", omittingEmptySubsequences: false).map(String.init)

        guard !normalized.isEmpty,
              !normalized.hasPrefix("/"),
              !normalized.hasSuffix("/"),
              components.allSatisfy(isSafeNodeName) else {
            throw PakError.unsafePath(rawName)
        }

        return components.joined(separator: "/")
    }

    static func validateArchivePath(_ path: String) throws {
        let components = path.split(separator: "/", omittingEmptySubsequences: false).map(String.init)
        guard !path.isEmpty,
              !path.hasPrefix("/"),
              !path.hasSuffix("/"),
              components.allSatisfy(isSafeNodeName) else {
            throw PakError.unsafePath(path)
        }
    }

    static func validateNodeName(_ name: String) throws {
        guard isSafeNodeName(name) else {
            throw PakError.unsafePath(name)
        }
    }

    static func isSafeNodeName(_ name: String) -> Bool {
        !name.isEmpty &&
        name != "." &&
        name != ".." &&
        !name.contains("/") &&
        !name.contains("\\") &&
        name.unicodeScalars.allSatisfy { !CharacterSet.controlCharacters.contains($0) }
    }
}

enum PakNodeData {
    static func data(for node: PakNode, originalData: Data?) throws -> Data {
        if let localData = node.localData {
            return localData
        }

        let path = node.entry?.name ?? node.name
        guard let entry = node.entry,
              let originalData,
              entry.offset >= 0,
              entry.length >= 0,
              entry.offset <= originalData.count,
              entry.length <= originalData.count - entry.offset else {
            throw PakError.missingData(path)
        }

        return originalData.subdata(in: entry.offset ..< entry.offset + entry.length)
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

        guard dirOffset >= 12, dirLength >= 0,
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
        var filePaths = Set<String>()
        var folderPaths = Set<String>()
        var dataRanges: [(range: Range<Int>, name: String)] = []

        for i in 0..<count {
            let base = dirOffset + i * entrySize

            let nameData = data.subdata(in: base ..< base + 56)
            let rawName = asciiStringFromNullTerminated(nameData)
            let name = try PakPathValidator.normalizeArchiveEntryName(rawName)
            try registerPath(name, filePaths: &filePaths, folderPaths: &folderPaths)

            let filePos = Int(readInt32LE(data, at: base + 56))
            let fileLen = Int(readInt32LE(data, at: base + 60))

            guard filePos >= 0, fileLen >= 0,
                  filePos + fileLen <= data.count else {
                throw PakError.badDirectory
            }

            if fileLen > 0 {
                guard filePos >= 12,
                      !rangesOverlap(filePos, fileLen, dirOffset, dirLength) else {
                    throw PakError.badDirectory
                }
                dataRanges.append((filePos ..< filePos + fileLen, name))
            }

            entries.append(PakEntry(name: name, offset: filePos, length: fileLen))
        }

        if dataRanges.count > 1 {
            dataRanges.sort { $0.range.lowerBound < $1.range.lowerBound }
            for index in 1..<dataRanges.count {
                guard dataRanges[index].range.lowerBound >= dataRanges[index - 1].range.upperBound else {
                    throw PakError.badDirectory
                }
            }
        }

        let root = buildTree(from: entries)
        return PakFile(name: name, data: data, entries: entries, root: root)
    }

    // MARK: - Helpers

    private static func readInt32LE(_ data: Data, at offset: Int) -> Int32 {
        let value = UInt32(data[offset]) |
            (UInt32(data[offset + 1]) << 8) |
            (UInt32(data[offset + 2]) << 16) |
            (UInt32(data[offset + 3]) << 24)
        return Int32(bitPattern: value)
    }

    private static func asciiStringFromNullTerminated(_ data: Data) -> String {
        let trimmed = data.prefix { $0 != 0 }
        return String(bytes: trimmed, encoding: .ascii) ?? ""
    }

    private static func rangesOverlap(
        _ leftOffset: Int,
        _ leftLength: Int,
        _ rightOffset: Int,
        _ rightLength: Int
    ) -> Bool {
        let leftEnd = leftOffset + leftLength
        let rightEnd = rightOffset + rightLength
        return leftOffset < rightEnd && rightOffset < leftEnd
    }

    private static func registerPath(
        _ path: String,
        filePaths: inout Set<String>,
        folderPaths: inout Set<String>
    ) throws {
        let components = path.split(separator: "/").map(String.init)
        var prefix = ""

        for component in components.dropLast() {
            prefix = prefix.isEmpty ? component : "\(prefix)/\(component)"
            let key = prefix.lowercased()
            guard !filePaths.contains(key) else {
                throw PakError.duplicatePath(path)
            }
            folderPaths.insert(key)
        }

        let fileKey = path.lowercased()
        guard !filePaths.contains(fileKey), !folderPaths.contains(fileKey) else {
            throw PakError.duplicatePath(path)
        }
        filePaths.insert(fileKey)
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

    static func sortNodeRecursively(_ node: PakNode) {
        node.children?.sort {
            // Folders first, then files, then alpha
            if $0.isFolder != $1.isFolder {
                return $0.isFolder && !$1.isFolder
            }
            return $0.name.lowercased() < $1.name.lowercased()
        }
        node.children?.forEach { sortNodeRecursively($0) }
    }

    static func loadDirectoryTree(at directory: URL) throws -> PakNode {
        let values = try directory.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
        guard values.isDirectory == true, values.isSymbolicLink != true else {
            throw PakError.unsafePath(directory.lastPathComponent)
        }

        let root = PakNode(name: "/")
        try buildTree(from: directory, into: root)
        sortNodeRecursively(root)
        return root
    }

    static func loadZip(from url: URL, name: String) throws -> PakFile {
        let archiveData = try Data(contentsOf: url, options: .mappedIfSafe)
        try PakZipValidator.validate(data: archiveData)

        let tempDir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: tempDir) }

        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/unzip")
        process.arguments = ["-qq", url.path, "-d", tempDir.path]
        try process.run()
        process.waitUntilExit()

        guard process.terminationStatus == 0 else {
            throw PakError.unknown("Failed to unzip PK3 archive")
        }

        let root = PakNode(name: "/")
        try buildTree(from: tempDir, into: root)
        sortNodeRecursively(root)
        return PakFile(name: name, data: Data(), entries: [], root: root)
    }

    static func buildTree(from directory: URL, into parent: PakNode) throws {
        let resourceKeys: Set<URLResourceKey> = [.isDirectoryKey, .isSymbolicLinkKey]
        let contents = try FileManager.default.contentsOfDirectory(
            at: directory,
            includingPropertiesForKeys: Array(resourceKeys),
            options: []
        )
        for item in contents {
            try PakPathValidator.validateNodeName(item.lastPathComponent)
            let values = try item.resourceValues(forKeys: resourceKeys)
            guard values.isSymbolicLink != true else {
                throw PakError.unsafePath(item.lastPathComponent)
            }

            if values.isDirectory == true {
                let folder = PakNode(name: item.lastPathComponent)
                parent.children?.append(folder)
                try buildTree(from: item, into: folder)
            } else {
                let child = PakNode(name: item.lastPathComponent)
                child.localData = try Data(contentsOf: item)
                parent.children?.append(child)
            }
        }
        parent.children?.sort {
            if $0.isFolder != $1.isFolder {
                return $0.isFolder && !$1.isFolder
            }
            return $0.name.lowercased() < $1.name.lowercased()
        }
    }
}

enum PakZipValidator {
    private static let endOfCentralDirectorySignature: UInt32 = 0x0605_4B50
    private static let centralDirectorySignature: UInt32 = 0x0201_4B50
    private static let localFileSignature: UInt32 = 0x0403_4B50
    private static let maximumCommentLength = 65_535
    private static let maximumEntryCount = 50_000
    private static let maximumExpandedFileSize = 1 * 1_024 * 1_024 * 1_024
    private static let maximumExpandedArchiveSize = 2 * 1_024 * 1_024 * 1_024

    static func validate(data: Data) throws {
        guard let endOffset = endOfCentralDirectoryOffset(in: data) else {
            throw PakError.invalidZip
        }

        let diskNumber = readUInt16(data, at: endOffset + 4)
        let directoryDisk = readUInt16(data, at: endOffset + 6)
        let diskEntryCount = Int(readUInt16(data, at: endOffset + 8))
        let entryCount = Int(readUInt16(data, at: endOffset + 10))
        let directorySizeValue = readUInt32(data, at: endOffset + 12)
        let directoryOffsetValue = readUInt32(data, at: endOffset + 16)

        guard diskNumber == 0,
              directoryDisk == 0,
              diskEntryCount == entryCount else {
            throw PakError.unsupportedZipFeature("multi-disk archives")
        }
        guard entryCount <= maximumEntryCount else {
            throw PakError.expandedArchiveTooLarge
        }
        guard directorySizeValue != UInt32.max,
              directoryOffsetValue != UInt32.max else {
            throw PakError.unsupportedZipFeature("ZIP64")
        }

        let directorySize = Int(directorySizeValue)
        let directoryOffset = Int(directoryOffsetValue)
        guard directoryOffset <= data.count,
              directorySize <= data.count - directoryOffset,
              directoryOffset + directorySize <= endOffset else {
            throw PakError.invalidZip
        }

        let directoryEnd = directoryOffset + directorySize
        var cursor = directoryOffset
        var totalExpandedSize = 0
        var filePaths = Set<String>()
        var folderPaths = Set<String>()
        var explicitFolderPaths = Set<String>()

        for _ in 0..<entryCount {
            guard cursor <= directoryEnd - 46,
                  readUInt32(data, at: cursor) == centralDirectorySignature else {
                throw PakError.invalidZip
            }

            let versionMadeBy = readUInt16(data, at: cursor + 4)
            let flags = readUInt16(data, at: cursor + 8)
            let compressionMethod = readUInt16(data, at: cursor + 10)
            let compressedSizeValue = readUInt32(data, at: cursor + 20)
            let expandedSizeValue = readUInt32(data, at: cursor + 24)
            let nameLength = Int(readUInt16(data, at: cursor + 28))
            let extraLength = Int(readUInt16(data, at: cursor + 30))
            let commentLength = Int(readUInt16(data, at: cursor + 32))
            let externalAttributes = readUInt32(data, at: cursor + 38)
            let localHeaderOffsetValue = readUInt32(data, at: cursor + 42)
            let recordLength = 46 + nameLength + extraLength + commentLength

            guard recordLength <= directoryEnd - cursor,
                  compressedSizeValue != UInt32.max,
                  expandedSizeValue != UInt32.max,
                  localHeaderOffsetValue != UInt32.max else {
                throw PakError.invalidZip
            }
            guard flags & 0x0001 == 0 else {
                throw PakError.unsupportedZipFeature("encrypted entries")
            }
            guard compressionMethod == 0 || compressionMethod == 8 else {
                throw PakError.unsupportedZipFeature("compression method \(compressionMethod)")
            }

            let nameStart = cursor + 46
            let nameBytes = data[nameStart ..< nameStart + nameLength]
            guard let rawName = String(data: nameBytes, encoding: .utf8) else {
                throw PakError.unsupportedZipFeature("non-UTF-8 path names")
            }
            let normalizedName = rawName.replacingOccurrences(of: "\\", with: "/")
            let isDirectory = normalizedName.hasSuffix("/")
            let path = isDirectory ? String(normalizedName.dropLast()) : normalizedName
            try PakPathValidator.validateArchivePath(path)
            try register(
                path: path,
                isDirectory: isDirectory,
                filePaths: &filePaths,
                folderPaths: &folderPaths,
                explicitFolderPaths: &explicitFolderPaths
            )

            let creatorSystem = UInt8(truncatingIfNeeded: versionMadeBy >> 8)
            let unixMode = UInt16(truncatingIfNeeded: externalAttributes >> 16)
            if (creatorSystem == 3 || creatorSystem == 19), unixMode & 0xF000 == 0xA000 {
                throw PakError.unsafePath(path)
            }

            let expandedSize = Int(expandedSizeValue)
            guard expandedSize <= maximumExpandedFileSize,
                  totalExpandedSize <= maximumExpandedArchiveSize - expandedSize else {
                throw PakError.expandedArchiveTooLarge
            }
            totalExpandedSize += expandedSize

            try validateLocalHeader(
                data: data,
                offset: Int(localHeaderOffsetValue),
                expectedName: nameBytes,
                expectedMethod: compressionMethod,
                compressedSize: Int(compressedSizeValue),
                directoryOffset: directoryOffset
            )
            cursor += recordLength
        }

        guard cursor == directoryEnd else {
            throw PakError.invalidZip
        }
    }

    private static func endOfCentralDirectoryOffset(in data: Data) -> Int? {
        guard data.count >= 22 else { return nil }
        let firstPossibleOffset = max(0, data.count - 22 - maximumCommentLength)

        for offset in stride(from: data.count - 22, through: firstPossibleOffset, by: -1) {
            guard readUInt32(data, at: offset) == endOfCentralDirectorySignature else { continue }
            let commentLength = Int(readUInt16(data, at: offset + 20))
            if offset + 22 + commentLength == data.count {
                return offset
            }
        }
        return nil
    }

    private static func validateLocalHeader(
        data: Data,
        offset: Int,
        expectedName: Data.SubSequence,
        expectedMethod: UInt16,
        compressedSize: Int,
        directoryOffset: Int
    ) throws {
        guard offset >= 0,
              offset <= directoryOffset - 30,
              readUInt32(data, at: offset) == localFileSignature else {
            throw PakError.invalidZip
        }

        let flags = readUInt16(data, at: offset + 6)
        let method = readUInt16(data, at: offset + 8)
        let nameLength = Int(readUInt16(data, at: offset + 26))
        let extraLength = Int(readUInt16(data, at: offset + 28))
        let nameStart = offset + 30
        let dataStart = nameStart + nameLength + extraLength

        guard flags & 0x0001 == 0,
              method == expectedMethod,
              nameStart <= directoryOffset,
              nameLength <= directoryOffset - nameStart,
              dataStart <= directoryOffset,
              compressedSize <= directoryOffset - dataStart,
              data[nameStart ..< nameStart + nameLength].elementsEqual(expectedName) else {
            throw PakError.invalidZip
        }
    }

    private static func register(
        path: String,
        isDirectory: Bool,
        filePaths: inout Set<String>,
        folderPaths: inout Set<String>,
        explicitFolderPaths: inout Set<String>
    ) throws {
        let components = path.split(separator: "/").map(String.init)
        var prefix = ""

        for component in components.dropLast() {
            prefix = prefix.isEmpty ? component : "\(prefix)/\(component)"
            let key = prefix.lowercased()
            guard !filePaths.contains(key) else { throw PakError.duplicatePath(path) }
            folderPaths.insert(key)
        }

        let key = path.lowercased()
        if isDirectory {
            guard !filePaths.contains(key), explicitFolderPaths.insert(key).inserted else {
                throw PakError.duplicatePath(path)
            }
            folderPaths.insert(key)
        } else {
            guard !filePaths.contains(key), !folderPaths.contains(key) else {
                throw PakError.duplicatePath(path)
            }
            filePaths.insert(key)
        }
    }

    private static func readUInt16(_ data: Data, at offset: Int) -> UInt16 {
        UInt16(data[offset]) | (UInt16(data[offset + 1]) << 8)
    }

    private static func readUInt32(_ data: Data, at offset: Int) -> UInt32 {
        UInt32(data[offset]) |
            (UInt32(data[offset + 1]) << 8) |
            (UInt32(data[offset + 2]) << 16) |
            (UInt32(data[offset + 3]) << 24)
    }
}

struct PakWriter {
    struct Output {
        let data: Data
        let entries: [PakEntry]
        fileprivate let nodes: [PakNode]

        func applyToNodes() {
            for (node, entry) in zip(nodes, entries) {
                node.entry = entry
                node.localData = nil
            }
        }
    }

    static func write(root: PakNode, originalData: Data?) throws -> Output {
        var files: [(path: String, node: PakNode)] = []
        collectFiles(node: root, currentPath: "", into: &files)

        var encodedPaths = Set<String>()
        for (path, _) in files {
            try PakPathValidator.validateArchivePath(path)
            let encoded = try asciiBytes(forName: path)
            guard encoded.count <= 55 else {
                throw PakError.pathTooLong(path)
            }
            guard encodedPaths.insert(String(decoding: encoded, as: UTF8.self).lowercased()).inserted else {
                throw PakError.duplicatePath(path)
            }
        }
        // PAK names are ASCII, so this produces stable output independent of the
        // user's locale while keeping the familiar case-insensitive ordering.
        files.sort { $0.path.lowercased() < $1.path.lowercased() }

        var output = Data()
        output.append(Data(count: 12))

        var newEntries: [PakEntry] = []

        for (path, node) in files {
            let offset = output.count
            let payload = try PakNodeData.data(for: node, originalData: originalData)

            guard output.count <= Int(Int32.max) - payload.count else {
                throw PakError.archiveTooLarge
            }
            output.append(payload)
            let newEntry = PakEntry(name: path, offset: offset, length: payload.count)
            newEntries.append(newEntry)
        }

        let dirOffset = output.count
        guard newEntries.count <= (Int(Int32.max) - dirOffset) / 64 else {
            throw PakError.archiveTooLarge
        }
        let dirLength = newEntries.count * 64

        for entry in newEntries {
            var nameBytes = [UInt8](repeating: 0, count: 56)
            let ascii = try asciiBytes(forName: entry.name)
            for (i, byte) in ascii.enumerated() {
                nameBytes[i] = byte
            }
            output.append(contentsOf: nameBytes)
            output.append(int32ToData(Int32(entry.offset)))
            output.append(int32ToData(Int32(entry.length)))
        }

        let ident = Data("PACK".utf8)
        output.replaceSubrange(0..<4, with: ident)
        output.replaceSubrange(4..<8, with: int32ToData(Int32(dirOffset)))
        output.replaceSubrange(8..<12, with: int32ToData(Int32(dirLength)))

        return Output(data: output, entries: newEntries, nodes: files.map { $0.node })
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

    private static func asciiBytes(forName name: String) throws -> [UInt8] {
        var result: [UInt8] = []
        result.reserveCapacity(name.count)

        for scalar in name.unicodeScalars {
            let v = scalar.value
            guard v >= 0x20, v <= 0x7E else {
                throw PakError.unsupportedPathCharacters(name)
            }
            result.append(UInt8(v))
        }

        return result
    }

    private static func int32ToData(_ value: Int32) -> Data {
        var v = value.littleEndian
        return Data(bytes: &v, count: 4)
    }
}

struct PakZipWriter {
    enum ZipError: Error, LocalizedError {
        case processFailed

        var errorDescription: String? {
            "Failed to create the PK3 archive."
        }
    }

    static func write(root: PakNode, originalData: Data?) throws -> Data {
        if root.children?.isEmpty != false {
            // End-of-central-directory record for a valid empty ZIP archive.
            let signature: [UInt8] = [0x50, 0x4B, 0x05, 0x06]
            return Data(signature + [UInt8](repeating: 0, count: 18))
        }

        let staging = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: staging, withIntermediateDirectories: true)
        let zipURL = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".pk3")
        defer {
            try? FileManager.default.removeItem(at: staging)
            try? FileManager.default.removeItem(at: zipURL)
        }

        try writeChildren(root.children ?? [], to: staging, originalData: originalData)

        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/zip")
        process.arguments = ["-qr", zipURL.path, "."]
        process.currentDirectoryURL = staging
        try process.run()
        process.waitUntilExit()

        guard process.terminationStatus == 0 else {
            throw ZipError.processFailed
        }

        return try Data(contentsOf: zipURL)
    }

    private static func writeChildren(_ nodes: [PakNode], to directory: URL, originalData: Data?) throws {
        for node in nodes {
            try PakPathValidator.validateNodeName(node.name)
            if node.isFolder {
                let folderURL = directory.appendingPathComponent(node.name)
                try FileManager.default.createDirectory(at: folderURL, withIntermediateDirectories: true)
                try writeChildren(node.children ?? [], to: folderURL, originalData: originalData)
            } else {
                let fileURL = directory.appendingPathComponent(node.name)
                let payload = try PakNodeData.data(for: node, originalData: originalData)
                try payload.write(to: fileURL)
            }
        }
    }
}

struct PakFilesystemExporter {
    static func export(root: PakNode, originalData: Data?, to directory: URL) throws {
        let fileManager = FileManager.default
        try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        try writeChildren(root.children ?? [], to: directory, originalData: originalData)
    }

    static func export(node: PakNode, originalData: Data?, to url: URL) throws {
        try PakPathValidator.validateNodeName(node.name)
        if node.isFolder {
            let fileManager = FileManager.default
            try fileManager.createDirectory(at: url, withIntermediateDirectories: true)
            try writeChildren(node.children ?? [], to: url, originalData: originalData)
        } else {
            let payload = try PakNodeData.data(for: node, originalData: originalData)
            try payload.write(to: url)
        }
    }

    private static func writeChildren(_ nodes: [PakNode], to directory: URL, originalData: Data?) throws {
        for node in nodes {
            try PakPathValidator.validateNodeName(node.name)
            if node.isFolder {
                let folderURL = directory.appendingPathComponent(node.name, isDirectory: true)
                try FileManager.default.createDirectory(at: folderURL, withIntermediateDirectories: true)
                try writeChildren(node.children ?? [], to: folderURL, originalData: originalData)
            } else {
                let fileURL = directory.appendingPathComponent(node.name, isDirectory: false)
                let payload = try PakNodeData.data(for: node, originalData: originalData)
                try payload.write(to: fileURL)
            }
        }
    }
}
