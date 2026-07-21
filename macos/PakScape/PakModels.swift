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

    let id: UUID
    var name: String
    var children: [PakNode]? = []   // NOTE: optional array for OutlineGroup
    var entry: PakEntry?            // nil for folders
    var localData: Data?            // For newly added files or modified content

    init(name: String, entry: PakEntry? = nil, id: UUID = UUID()) {
        self.id = id
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

struct PakSearchResult {
    let node: PakNode
    let path: String
    let score: Int
}

/// Archive-wide, relevance-ranked search used by the macOS browser.
///
/// Matching is deliberately forgiving: it understands full paths, file names,
/// stems, extensions, separated words (for example `vshot` → `v_shot.mdl`),
/// subsequences, and small typing mistakes. Every query term must match, which
/// keeps multi-word searches useful even in large archives.
enum PakArchiveSearch {
    static func search(root: PakNode, query: String) -> [PakSearchResult] {
        let rawQuery = normalize(query).trimmingCharacters(in: .whitespacesAndNewlines)
        let isPathQuery = rawQuery.contains("/")
        let normalizedQuery = rawQuery
            .trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        guard !normalizedQuery.isEmpty else { return [] }

        let terms = normalizedQuery
            .split(whereSeparator: { $0.isWhitespace })
            .map(String.init)
            .compactMap(searchableTerm)
        guard !terms.isEmpty else { return [] }

        var results: [PakSearchResult] = []
        collectMatches(
            in: root,
            parentPath: "",
            normalizedQuery: normalizedQuery,
            terms: terms,
            isPathQuery: isPathQuery,
            allowsFuzzyMatching: false,
            results: &results
        )

        // Keep live filtering predictable. Fuzzy matches are useful for a typo,
        // but they should never keep unrelated rows visible alongside genuine
        // substring, extension, glob, or path matches.
        if results.isEmpty {
            collectMatches(
                in: root,
                parentPath: "",
                normalizedQuery: normalizedQuery,
                terms: terms,
                isPathQuery: isPathQuery,
                allowsFuzzyMatching: true,
                results: &results
            )
        }

        return results.sorted { lhs, rhs in
            if lhs.score != rhs.score { return lhs.score > rhs.score }
            return lhs.path.localizedStandardCompare(rhs.path) == .orderedAscending
        }
    }

    private struct Candidate {
        let name: String
        let stem: String
        let fileExtension: String
        let path: String
        let pathComponents: [String]
        let type: String
        let compactName: String
        let compactPath: String
    }

    private static func collectMatches(
        in parent: PakNode,
        parentPath: String,
        normalizedQuery: String,
        terms: [String],
        isPathQuery: Bool,
        allowsFuzzyMatching: Bool,
        results: inout [PakSearchResult]
    ) {
        for node in parent.children ?? [] {
            let path = parentPath.isEmpty ? node.name : parentPath + "/" + node.name
            if let score = matchScore(
                node: node,
                path: path,
                normalizedQuery: normalizedQuery,
                terms: terms,
                isPathQuery: isPathQuery,
                allowsFuzzyMatching: allowsFuzzyMatching
            ) {
                results.append(PakSearchResult(node: node, path: "/" + path, score: score))
            }
            if node.isFolder {
                collectMatches(
                    in: node,
                    parentPath: path,
                    normalizedQuery: normalizedQuery,
                    terms: terms,
                    isPathQuery: isPathQuery,
                    allowsFuzzyMatching: allowsFuzzyMatching,
                    results: &results
                )
            }
        }
    }

    private static func matchScore(
        node: PakNode,
        path: String,
        normalizedQuery: String,
        terms: [String],
        isPathQuery: Bool,
        allowsFuzzyMatching: Bool
    ) -> Int? {
        let name = normalize(node.name)
        let stem = normalize((node.name as NSString).deletingPathExtension)
        let fileExtension = normalize((node.name as NSString).pathExtension)
        let normalizedPath = normalize(path)
        let candidate = Candidate(
            name: name,
            stem: stem,
            fileExtension: fileExtension,
            path: normalizedPath,
            pathComponents: normalizedPath.split(separator: "/").map(String.init),
            type: normalize(node.fileType),
            compactName: compact(name),
            compactPath: compact(normalizedPath)
        )

        var total = 0
        var hasLeafMatch = false
        let allowsParentPathTerms = terms.count > 1
        for term in terms {
            let match = strictMatch(
                term: term,
                candidate: candidate,
                isPathQuery: isPathQuery,
                allowsParentPathTerms: allowsParentPathTerms
            ) ?? (allowsFuzzyMatching
                ? fuzzyMatch(term: term, candidate: candidate, allowsParentPathTerms: allowsParentPathTerms)
                : nil)
            guard let match else { return nil }
            total += match.score
            hasLeafMatch = hasLeafMatch || match.matchesLeaf
        }
        guard hasLeafMatch || isPathQuery else { return nil }

        // Prefer an uninterrupted phrase/path match over the same words spread
        // across unrelated path components.
        if !normalizedQuery.contains("*") && !normalizedQuery.contains("?") {
            if candidate.name == normalizedQuery { total += 1_200 }
            else if candidate.stem == normalizedQuery { total += 1_100 }
            else if isPathQuery, candidate.path == normalizedQuery { total += 1_050 }
            else if candidate.name.hasPrefix(normalizedQuery) { total += 700 }
            else if isPathQuery, candidate.path.contains(normalizedQuery) { total += 500 }
            else if candidate.compactName.contains(compact(normalizedQuery)) { total += 350 }
        }

        // For otherwise-equal results, shorter names and paths are usually the
        // result the user intended.
        total -= min(candidate.name.count, 80)
        total -= min(candidate.path.count / 4, 80)
        return total
    }

    private struct TermMatch {
        let score: Int
        let matchesLeaf: Bool
    }

    private static func strictMatch(
        term: String,
        candidate: Candidate,
        isPathQuery: Bool,
        allowsParentPathTerms: Bool
    ) -> TermMatch? {
        let extensionTerm = term.hasPrefix(".") ? String(term.dropFirst()) : term
        let compactTerm = compact(extensionTerm)
        let hasWildcard = term.contains("*") || term.contains("?")

        if hasWildcard {
            if globMatches(pattern: term, value: candidate.name) {
                return TermMatch(score: 900, matchesLeaf: true)
            }
            if isPathQuery, globMatches(pattern: term, value: candidate.path) {
                return TermMatch(score: 875, matchesLeaf: true)
            }
            return nil
        }

        if candidate.name == term { return TermMatch(score: 1_000, matchesLeaf: true) }
        if candidate.stem == term { return TermMatch(score: 970, matchesLeaf: true) }
        if !candidate.fileExtension.isEmpty, candidate.fileExtension == extensionTerm {
            return TermMatch(score: 925, matchesLeaf: true)
        }
        if candidate.name.hasPrefix(term) {
            return TermMatch(score: 850 - min(candidate.name.count - term.count, 60), matchesLeaf: true)
        }
        if candidate.stem.hasPrefix(term) {
            return TermMatch(score: 830 - min(candidate.stem.count - term.count, 60), matchesLeaf: true)
        }
        if !candidate.fileExtension.isEmpty, candidate.fileExtension.hasPrefix(extensionTerm) {
            return TermMatch(
                score: 810 - min(candidate.fileExtension.count - extensionTerm.count, 40),
                matchesLeaf: true
            )
        }
        if let range = candidate.name.range(of: term) {
            return TermMatch(
                score: 730 - min(candidate.name.distance(from: candidate.name.startIndex, to: range.lowerBound), 80),
                matchesLeaf: true
            )
        }
        if candidate.type.contains(term) { return TermMatch(score: 600, matchesLeaf: true) }
        if compactTerm.count >= 2, candidate.compactName.contains(compactTerm) {
            return TermMatch(score: candidate.compactName.hasPrefix(compactTerm) ? 620 : 590, matchesLeaf: true)
        }

        if isPathQuery {
            if candidate.path == term || "/" + candidate.path == term {
                return TermMatch(score: 950, matchesLeaf: true)
            }
            if let range = candidate.path.range(of: term) {
                return TermMatch(
                    score: 680 - min(candidate.path.distance(from: candidate.path.startIndex, to: range.lowerBound), 100),
                    matchesLeaf: true
                )
            }
            if compactTerm.count >= 2, candidate.compactPath.contains(compactTerm) {
                return TermMatch(score: 540, matchesLeaf: true)
            }
        }

        if allowsParentPathTerms,
           candidate.pathComponents.dropLast().contains(where: { $0.contains(term) }) {
            return TermMatch(score: 500, matchesLeaf: false)
        }

        return nil
    }

    private static func fuzzyMatch(
        term: String,
        candidate: Candidate,
        allowsParentPathTerms: Bool
    ) -> TermMatch? {
        let compactTerm = compact(term)
        guard compactTerm.count >= 3 else { return nil }

        if allowsParentPathTerms,
           candidate.pathComponents.dropLast().contains(where: { component in
               let compactComponent = compact(component)
               return compactTerm.count * 2 >= compactComponent.count
                   && subsequenceScore(needle: compactTerm, haystack: compactComponent) != nil
           }) {
            return TermMatch(score: 300, matchesLeaf: false)
        }

        if compactTerm.count * 2 >= candidate.compactName.count,
           let subsequenceScore = subsequenceScore(needle: compactTerm, haystack: candidate.compactName) {
            return TermMatch(score: 420 + subsequenceScore, matchesLeaf: true)
        }

        if compactTerm.count >= 4 {
            let allowedDistance = compactTerm.count >= 7 ? 2 : 1
            let typoCandidates = [candidate.stem, candidate.name]
            let closestDistance = typoCandidates
                .map { compact($0) }
                .map { editDistance(compactTerm, $0, limit: allowedDistance) }
                .min() ?? (allowedDistance + 1)
            if closestDistance <= allowedDistance {
                return TermMatch(score: 350 - (closestDistance * 50), matchesLeaf: true)
            }
        }

        return nil
    }

    private static func searchableTerm(_ rawTerm: String) -> String? {
        rawTerm.isEmpty ? nil : rawTerm
    }

    private static func normalize(_ value: String) -> String {
        value
            .replacingOccurrences(of: "\\", with: "/")
            .folding(options: [.caseInsensitive, .diacriticInsensitive, .widthInsensitive], locale: .current)
            .lowercased()
    }

    private static func compact(_ value: String) -> String {
        let scalars = value.unicodeScalars.filter { CharacterSet.alphanumerics.contains($0) }
        return String(String.UnicodeScalarView(scalars))
    }

    /// Matches Cyberduck-style `*` and `?` globs without interpreting any other
    /// characters as regular-expression syntax.
    private static func globMatches(pattern: String, value: String) -> Bool {
        let patternCharacters = Array(pattern)
        let valueCharacters = Array(value)
        var previous = [Bool](repeating: false, count: valueCharacters.count + 1)
        previous[0] = true

        for patternCharacter in patternCharacters {
            var current = [Bool](repeating: false, count: valueCharacters.count + 1)
            if patternCharacter == "*" {
                current[0] = previous[0]
            }

            if !valueCharacters.isEmpty {
                for valueOffset in 1 ... valueCharacters.count {
                    if patternCharacter == "*" {
                        current[valueOffset] = previous[valueOffset] || current[valueOffset - 1]
                    } else if patternCharacter == "?" || patternCharacter == valueCharacters[valueOffset - 1] {
                        current[valueOffset] = previous[valueOffset - 1]
                    }
                }
            }
            previous = current
        }

        return previous[valueCharacters.count]
    }

    /// Rewards ordered characters that are close together, while still allowing
    /// abbreviated searches such as `vshmdl` for `v_shot.mdl`.
    private static func subsequenceScore(needle: String, haystack: String) -> Int? {
        var needleIndex = needle.startIndex
        var lastMatch: String.Index?
        var gaps = 0

        for index in haystack.indices {
            guard needleIndex < needle.endIndex else { break }
            if haystack[index] == needle[needleIndex] {
                if let lastMatch {
                    gaps += max(0, haystack.distance(from: lastMatch, to: index) - 1)
                }
                lastMatch = index
                needle.formIndex(after: &needleIndex)
            }
        }

        guard needleIndex == needle.endIndex else { return nil }
        return max(0, 100 - (gaps * 5) - max(0, haystack.count - needle.count))
    }

    /// Bounded Levenshtein distance. Rows that can no longer reach `limit` stop
    /// early, keeping typo matching cheap for archives near the entry limit.
    private static func editDistance(_ lhs: String, _ rhs: String, limit: Int) -> Int {
        let left = Array(lhs)
        let right = Array(rhs)
        guard abs(left.count - right.count) <= limit else { return limit + 1 }
        if left == right { return 0 }
        if left.isEmpty { return right.count }
        if right.isEmpty { return left.count }

        var previous = Array(0 ... right.count)
        for (leftOffset, leftCharacter) in left.enumerated() {
            var current = [leftOffset + 1]
            current.reserveCapacity(right.count + 1)
            var rowMinimum = current[0]

            for (rightOffset, rightCharacter) in right.enumerated() {
                let insertion = current[rightOffset] + 1
                let deletion = previous[rightOffset + 1] + 1
                let substitution = previous[rightOffset] + (leftCharacter == rightCharacter ? 0 : 1)
                let value = min(insertion, min(deletion, substitution))
                current.append(value)
                rowMinimum = min(rowMinimum, value)
            }

            if rowMinimum > limit { return limit + 1 }
            previous = current
        }
        return previous[right.count]
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
    case tooManyEntries
    case fileTooLarge(String)
    case importTooLarge
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
        case .tooManyEntries:
            return "The archive contains more than 50,000 entries."
        case .fileTooLarge(let path):
            return "'\(path)' exceeds PakScape's 1 GiB per-file safety limit."
        case .importTooLarge:
            return "The operation exceeds PakScape's 2 GiB total-size safety limit."
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

enum PakSafetyLimits {
    static let maximumEntryCount = 50_000
    static let maximumPathDepth = 256
    static let maximumFileSize = 1 * 1_024 * 1_024 * 1_024
    static let maximumTotalSize = 2 * 1_024 * 1_024 * 1_024
}

struct PakImportBudget {
    private(set) var entryCount = 0
    private(set) var totalSize = 0

    init() {}

    init(existingRoot: PakNode?) throws {
        if let existingRoot {
            for child in existingRoot.children ?? [] {
                try registerTree(child)
            }
        }
    }

    mutating func registerEntry() throws {
        guard entryCount < PakSafetyLimits.maximumEntryCount else {
            throw PakError.tooManyEntries
        }
        entryCount += 1
    }

    func validateFile(size: Int, name: String) throws {
        guard size >= 0, size <= PakSafetyLimits.maximumFileSize else {
            throw PakError.fileTooLarge(name)
        }
        guard totalSize <= PakSafetyLimits.maximumTotalSize - size else {
            throw PakError.importTooLarge
        }
    }

    mutating func commitFile(size: Int, name: String) throws {
        try validateFile(size: size, name: name)
        totalSize += size
    }

    mutating func registerTree(_ node: PakNode, depth: Int = 1) throws {
        guard depth <= PakSafetyLimits.maximumPathDepth else {
            throw PakError.unsafePath(node.name)
        }
        try registerEntry()
        if node.isFolder {
            for child in node.children ?? [] {
                try registerTree(child, depth: depth + 1)
            }
        } else {
            try commitFile(size: node.fileSize, name: node.name)
        }
    }
}

enum PakPreviewLimits {
    static let maximumDimension = 8_192
    static let maximumPixelCount = 16_777_216

    static func isSafe(width: Int, height: Int) -> Bool {
        guard width > 0,
              height > 0,
              width <= maximumDimension,
              height <= maximumDimension else {
            return false
        }
        let result = width.multipliedReportingOverflow(by: height)
        return !result.overflow && result.partialValue <= maximumPixelCount
    }
}

enum PakPathValidator {
    static func normalizeArchiveEntryName(_ rawName: String) throws -> String {
        let normalized = rawName.replacingOccurrences(of: "\\", with: "/")
        let components = normalized.split(separator: "/", omittingEmptySubsequences: false).map(String.init)

        guard !normalized.isEmpty,
              !normalized.hasPrefix("/"),
              !normalized.hasSuffix("/"),
              components.count <= PakSafetyLimits.maximumPathDepth,
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
              components.count <= PakSafetyLimits.maximumPathDepth,
              components.allSatisfy(isSafeNodeName) else {
            throw PakError.unsafePath(path)
        }
    }

    static func validateNodeName(_ name: String) throws {
        guard isSafeNodeName(name) else {
            throw PakError.unsafePath(name)
        }
    }

    nonisolated static func isSafeNodeName(_ name: String) -> Bool {
        !name.isEmpty &&
        !name.unicodeScalars.allSatisfy { CharacterSet.whitespaces.contains($0) } &&
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
        guard data.count <= PakSafetyLimits.maximumTotalSize else { throw PakError.importTooLarge }

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
        guard count <= PakSafetyLimits.maximumEntryCount else { throw PakError.tooManyEntries }
        var entries: [PakEntry] = []
        entries.reserveCapacity(count)
        var filePaths = Set<String>()
        var folderPaths = Set<String>()
        var dataRanges: [(range: Range<Int>, name: String)] = []
        var totalPayloadSize = 0

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
            guard fileLen <= PakSafetyLimits.maximumFileSize else {
                throw PakError.fileTooLarge(name)
            }
            guard totalPayloadSize <= PakSafetyLimits.maximumTotalSize - fileLen else {
                throw PakError.importTooLarge
            }
            totalPayloadSize += fileLen

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
        var budget = PakImportBudget()
        try buildTree(from: directory, into: root, budget: &budget, depth: 1)
        sortNodeRecursively(root)
        return root
    }

    static func loadZip(from url: URL, name: String) throws -> PakFile {
        let values = try url.resourceValues(forKeys: [.fileSizeKey, .isSymbolicLinkKey])
        guard values.isSymbolicLink != true else { throw PakError.unsafePath(name) }
        if let fileSize = values.fileSize,
           fileSize > PakSafetyLimits.maximumTotalSize {
            throw PakError.importTooLarge
        }
        let archiveData = try Data(contentsOf: url, options: .mappedIfSafe)
        guard archiveData.count <= PakSafetyLimits.maximumTotalSize else {
            throw PakError.importTooLarge
        }
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
        var budget = PakImportBudget()
        try buildTree(from: tempDir, into: root, budget: &budget, depth: 1)
        sortNodeRecursively(root)
        return PakFile(name: name, data: Data(), entries: [], root: root)
    }

    static func buildTree(from directory: URL, into parent: PakNode) throws {
        var budget = PakImportBudget()
        try buildTree(from: directory, into: parent, budget: &budget, depth: 1)
    }

    static func buildTree(
        from directory: URL,
        into parent: PakNode,
        budget: inout PakImportBudget,
        depth: Int = 1
    ) throws {
        let resourceKeys: Set<URLResourceKey> = [.isDirectoryKey, .isRegularFileKey, .isSymbolicLinkKey]
        let contents = try FileManager.default.contentsOfDirectory(
            at: directory,
            includingPropertiesForKeys: Array(resourceKeys),
            options: []
        )
        for item in contents {
            try budget.registerEntry()
            try PakPathValidator.validateNodeName(item.lastPathComponent)
            let values = try item.resourceValues(forKeys: resourceKeys)
            guard values.isSymbolicLink != true else {
                throw PakError.unsafePath(item.lastPathComponent)
            }

            if values.isDirectory == true {
                let childDepth = depth + 1
                guard childDepth <= PakSafetyLimits.maximumPathDepth else {
                    throw PakError.unsafePath(item.path)
                }
                let folder = PakNode(name: item.lastPathComponent)
                parent.children?.append(folder)
                try buildTree(from: item, into: folder, budget: &budget, depth: childDepth)
            } else {
                let child = PakNode(name: item.lastPathComponent)
                child.localData = try readFile(at: item, budget: &budget)
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

    static func readFile(at url: URL, budget: inout PakImportBudget) throws -> Data {
        let resourceKeys: Set<URLResourceKey> = [
            .contentModificationDateKey,
            .fileSizeKey,
            .isRegularFileKey,
            .isSymbolicLinkKey
        ]
        let values = try url.resourceValues(forKeys: resourceKeys)
        guard values.isRegularFile == true, values.isSymbolicLink != true else {
            throw PakError.unsafePath(url.lastPathComponent)
        }

        let handle = try FileHandle(forReadingFrom: url)
        defer { try? handle.close() }
        let sizeValue = try handle.seekToEnd()
        guard sizeValue <= UInt64(Int.max) else {
            throw PakError.fileTooLarge(url.lastPathComponent)
        }
        let expectedSize = Int(sizeValue)
        try budget.validateFile(size: expectedSize, name: url.lastPathComponent)
        try handle.seek(toOffset: 0)

        var data = Data()
        data.reserveCapacity(expectedSize)
        while data.count < expectedSize {
            let requested = min(1_048_576, expectedSize - data.count)
            guard let chunk = try handle.read(upToCount: requested), !chunk.isEmpty else {
                throw PakError.missingData(url.lastPathComponent)
            }
            data.append(chunk)
        }
        if let trailing = try handle.read(upToCount: 1), !trailing.isEmpty {
            throw PakError.unknown("'\(url.lastPathComponent)' changed size while it was being imported.")
        }
        let finalValues = try url.resourceValues(forKeys: resourceKeys)
        guard finalValues.isRegularFile == true,
              finalValues.isSymbolicLink != true,
              finalValues.fileSize == data.count,
              finalValues.contentModificationDate == values.contentModificationDate else {
            throw PakError.unknown("'\(url.lastPathComponent)' changed while it was being imported.")
        }

        try budget.commitFile(size: data.count, name: url.lastPathComponent)
        return data
    }
}

enum PakZipValidator {
    private static let endOfCentralDirectorySignature: UInt32 = 0x0605_4B50
    private static let centralDirectorySignature: UInt32 = 0x0201_4B50
    private static let localFileSignature: UInt32 = 0x0403_4B50
    private static let maximumCommentLength = 65_535

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
        guard entryCount <= PakSafetyLimits.maximumEntryCount else {
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
            guard expandedSize <= PakSafetyLimits.maximumFileSize,
                  totalExpandedSize <= PakSafetyLimits.maximumTotalSize - expandedSize else {
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
        try collectFiles(node: root, currentPath: "", depth: 0, into: &files)
        guard files.count <= PakSafetyLimits.maximumEntryCount else {
            throw PakError.tooManyEntries
        }

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
        var totalPayloadSize = 0

        for (path, node) in files {
            let offset = output.count
            let payload = try PakNodeData.data(for: node, originalData: originalData)

            guard payload.count <= PakSafetyLimits.maximumFileSize else {
                throw PakError.fileTooLarge(path)
            }
            guard totalPayloadSize <= PakSafetyLimits.maximumTotalSize - payload.count else {
                throw PakError.importTooLarge
            }
            totalPayloadSize += payload.count

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

    private static func collectFiles(
        node: PakNode,
        currentPath: String,
        depth: Int,
        into files: inout [(path: String, node: PakNode)]
    ) throws {
        guard let children = node.children else { return }

        for child in children {
            let childDepth = depth + 1
            guard childDepth <= PakSafetyLimits.maximumPathDepth else {
                throw PakError.unsafePath(child.name)
            }
            let nextPath: String
            if currentPath.isEmpty {
                nextPath = child.name
            } else {
                nextPath = "\(currentPath)/\(child.name)"
            }

            if child.isFolder {
                try collectFiles(node: child, currentPath: nextPath, depth: childDepth, into: &files)
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
        var budget = PakImportBudget()
        try validateForWrite(root.children ?? [], originalData: originalData, budget: &budget)

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

        let data = try Data(contentsOf: zipURL)
        try PakZipValidator.validate(data: data)
        return data
    }

    private static func validateForWrite(
        _ nodes: [PakNode],
        originalData: Data?,
        budget: inout PakImportBudget,
        depth: Int = 1
    ) throws {
        guard depth <= PakSafetyLimits.maximumPathDepth else {
            throw PakError.unsafePath("archive path")
        }
        for node in nodes {
            try budget.registerEntry()
            try PakPathValidator.validateNodeName(node.name)
            if node.isFolder {
                try validateForWrite(
                    node.children ?? [],
                    originalData: originalData,
                    budget: &budget,
                    depth: depth + 1
                )
            } else {
                let payload = try PakNodeData.data(for: node, originalData: originalData)
                try budget.commitFile(size: payload.count, name: node.name)
            }
        }
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
        try exportFolderContents(root.children ?? [], originalData: originalData, to: directory)
    }

    static func export(node: PakNode, originalData: Data?, to url: URL) throws {
        try PakPathValidator.validateNodeName(node.name)
        if node.isFolder {
            try exportFolderContents(node.children ?? [], originalData: originalData, to: url)
        } else {
            let payload = try PakNodeData.data(for: node, originalData: originalData)
            try payload.write(to: url, options: .atomic)
        }
    }

    private static func exportFolderContents(
        _ nodes: [PakNode],
        originalData: Data?,
        to destination: URL
    ) throws {
        let fileManager = FileManager.default
        let parent = destination.deletingLastPathComponent()
        try fileManager.createDirectory(at: parent, withIntermediateDirectories: true)
        let staging = parent.appendingPathComponent(".pakscape-export-\(UUID().uuidString)", isDirectory: true)
        try fileManager.createDirectory(at: staging, withIntermediateDirectories: false)
        do {
            try writeChildren(nodes, to: staging, originalData: originalData)
            if fileManager.fileExists(atPath: destination.path) {
                _ = try fileManager.replaceItemAt(destination, withItemAt: staging)
            } else {
                try fileManager.moveItem(at: staging, to: destination)
            }
        } catch {
            try? fileManager.removeItem(at: staging)
            throw error
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
                try payload.write(to: fileURL, options: .atomic)
            }
        }
    }
}
