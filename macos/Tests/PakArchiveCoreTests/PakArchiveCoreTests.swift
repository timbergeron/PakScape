import Foundation
import XCTest
@testable import PakArchiveCore

final class PakArchiveCoreTests: XCTestCase {
    func testFolderChildrenOnlyMarksFoldersWithSubfoldersAsExpandable() {
        let folder = PakNode(name: "maps")
        let file = PakNode(name: "start.bsp")
        file.localData = Data([0x01])
        folder.children?.append(file)

        XCTAssertNil(folder.folderChildren)

        let subfolder = PakNode(name: "episode1")
        folder.children?.append(subfolder)

        XCTAssertEqual(folder.folderChildren, [subfolder])
    }

    func testPakWriterAndLoaderRoundTrip() throws {
        let root = PakNode(name: "/")
        let maps = PakNode(name: "maps")
        let start = PakNode(name: "start.txt")
        start.localData = Data([0x01, 0x02, 0x03])
        maps.children?.append(start)
        root.children?.append(maps)

        let output = try PakWriter.write(root: root, originalData: nil)
        let loaded = try PakLoader.load(data: output.data, name: "roundtrip.pak")
        let loadedMaps = try XCTUnwrap(loaded.root.children?.first)
        let loadedStart = try XCTUnwrap(loadedMaps.children?.first)

        XCTAssertEqual(loadedMaps.name, "maps")
        XCTAssertEqual(loadedStart.name, "start.txt")
        XCTAssertEqual(
            try PakNodeData.data(for: loadedStart, originalData: loaded.data),
            Data([0x01, 0x02, 0x03])
        )
    }

    func testLoaderRejectsParentTraversal() throws {
        let data = makePak(path: "../outside.txt", payload: Data([0x01]))

        XCTAssertThrowsError(try PakLoader.load(data: data, name: "unsafe.pak")) { error in
            guard let pakError = error as? PakError,
                  case .unsafePath = pakError else {
                return XCTFail("Expected unsafePath, got \(error)")
            }
        }
    }

    func testLoaderRejectsMalformedPathsInsteadOfNormalizingThem() {
        for path in ["/absolute.txt", "maps//bad.txt", "maps/"] {
            let data = makePak(path: path, payload: Data([0x01]))
            XCTAssertThrowsError(try PakLoader.load(data: data, name: "unsafe.pak"), path)
        }
    }

    func testWriterRejectsMissingPayloadInsteadOfWritingEmptyFile() {
        let root = PakNode(name: "/")
        let file = PakNode(
            name: "missing.txt",
            entry: PakEntry(name: "missing.txt", offset: 99, length: 10)
        )
        root.children?.append(file)

        XCTAssertThrowsError(try PakWriter.write(root: root, originalData: Data())) { error in
            guard let pakError = error as? PakError,
                  case .missingData = pakError else {
                return XCTFail("Expected missingData, got \(error)")
            }
        }
    }

    func testBoundedNodeDataSourceMaterializesOnlyRequestedPrefix() throws {
        let originalData = Data((0..<32).map { UInt8($0) })
        let node = PakNode(
            name: "large.txt",
            entry: PakEntry(name: "large.txt", offset: 8, length: 16)
        )

        let source = try PakNodeData.boundedSource(
            for: node,
            originalData: originalData,
            maximumLength: 4
        )

        XCTAssertEqual(source.range, 8 ..< 12)
        XCTAssertEqual(source.materialize(), Data([8, 9, 10, 11]))
    }

    func testWriterDoesNotMutateDocumentUntilOutputIsCommitted() throws {
        let root = PakNode(name: "/")
        let file = PakNode(name: "readme.txt")
        file.localData = Data([0x41])
        root.children?.append(file)

        let output = try PakWriter.write(root: root, originalData: nil)

        XCTAssertEqual(file.localData, Optional(Data([0x41])))
        XCTAssertNil(file.entry)

        output.applyToNodes()

        XCTAssertNil(file.localData)
        XCTAssertEqual(file.entry?.name, "readme.txt")
    }

    func testWriterRejectsPathsOverFormatLimit() {
        let root = PakNode(name: "/")
        let file = PakNode(name: String(repeating: "a", count: 56))
        file.localData = Data([0x01])
        root.children?.append(file)

        XCTAssertThrowsError(try PakWriter.write(root: root, originalData: nil)) { error in
            guard let pakError = error as? PakError,
                  case .pathTooLong = pakError else {
                return XCTFail("Expected pathTooLong, got \(error)")
            }
        }
    }

    func testWriterRejectsUnicodePathInsteadOfRenamingIt() {
        let root = PakNode(name: "/")
        let file = PakNode(name: "café.txt")
        file.localData = Data([0x01])
        root.children?.append(file)

        XCTAssertThrowsError(try PakWriter.write(root: root, originalData: nil)) { error in
            guard let pakError = error as? PakError,
                  case .unsupportedPathCharacters = pakError else {
                return XCTFail("Expected unsupportedPathCharacters, got \(error)")
            }
        }
    }

    func testWriterRejectsCaseInsensitiveDuplicatePaths() {
        let root = PakNode(name: "/")
        let first = PakNode(name: "readme.txt")
        first.localData = Data([0x01])
        let second = PakNode(name: "README.TXT")
        second.localData = Data([0x02])
        root.children?.append(contentsOf: [first, second])

        XCTAssertThrowsError(try PakWriter.write(root: root, originalData: nil)) { error in
            guard let pakError = error as? PakError,
                  case .duplicatePath = pakError else {
                return XCTFail("Expected duplicatePath, got \(error)")
            }
        }
    }

    func testEmptyPK3IsAValidEmptyZip() throws {
        let data = try PakZipWriter.write(root: PakNode(name: "/"), originalData: nil)

        XCTAssertEqual(data.count, 22)
        XCTAssertEqual(Array(data.prefix(4)), [0x50, 0x4B, 0x05, 0x06])
        XCTAssertNoThrow(try PakZipValidator.validate(data: data))
    }

    func testPK3WriterOutputPassesPreflight() throws {
        let root = PakNode(name: "/")
        let maps = PakNode(name: "maps")
        let file = PakNode(name: "start.txt")
        file.localData = Data("hello".utf8)
        maps.children?.append(file)
        root.children?.append(maps)

        let data = try PakZipWriter.write(root: root, originalData: nil)

        XCTAssertNoThrow(try PakZipValidator.validate(data: data))
    }

    func testPK3ValidatorRejectsTraversalBeforeExtraction() {
        let data = makeCentralDirectoryOnlyZip(path: "../outside.txt", expandedSize: 1)

        XCTAssertThrowsError(try PakZipValidator.validate(data: data)) { error in
            guard let pakError = error as? PakError,
                  case .unsafePath = pakError else {
                return XCTFail("Expected unsafePath, got \(error)")
            }
        }
    }

    func testPK3ValidatorRejectsOversizedExpandedFileBeforeExtraction() {
        let data = makeCentralDirectoryOnlyZip(
            path: "oversized.bin",
            expandedSize: 1_073_741_825
        )

        XCTAssertThrowsError(try PakZipValidator.validate(data: data)) { error in
            guard let pakError = error as? PakError,
                  case .expandedArchiveTooLarge = pakError else {
                return XCTFail("Expected expandedArchiveTooLarge, got \(error)")
            }
        }
    }

    func testLoaderRejectsTooManyEntriesBeforeWalkingTheDirectory() {
        let entryCount = PakSafetyLimits.maximumEntryCount + 1
        let directoryLength = entryCount * 64
        var data = Data("PACK".utf8)
        appendInt32(12, to: &data)
        appendInt32(directoryLength, to: &data)
        data.append(Data(count: directoryLength))

        XCTAssertThrowsError(try PakLoader.load(data: data, name: "oversized.pak")) { error in
            guard let pakError = error as? PakError,
                  case .tooManyEntries = pakError else {
                return XCTFail("Expected tooManyEntries, got \(error)")
            }
        }
    }

    func testImportBudgetIncludesExistingArchiveEntries() throws {
        let root = PakNode(name: "/")
        root.children = (0..<PakSafetyLimits.maximumEntryCount).map { PakNode(name: "file-\($0)") }
        var budget = try PakImportBudget(existingRoot: root)

        XCTAssertThrowsError(try budget.registerEntry()) { error in
            guard let pakError = error as? PakError,
                  case .tooManyEntries = pakError else {
                return XCTFail("Expected tooManyEntries, got \(error)")
            }
        }
    }

    func testTreeMutationRecordsOnlyTopLevelSelectedNodes() throws {
        let root = PakNode(name: "/")
        let folder = PakNode(name: "maps")
        let child = PakNode(name: "start.bsp")
        child.localData = Data()
        folder.children = [child]
        root.children = [folder]

        let placements = PakTreeMutation.placements(for: [folder.id, child.id], in: root)

        XCTAssertEqual(placements.count, 1)
        XCTAssertTrue(placements.first?.node === folder)
    }

    func testTreeMutationRemovalAndInverseRestoreIdentityAndOrder() throws {
        let root = PakNode(name: "/")
        let first = PakNode(name: "a")
        let second = PakNode(name: "b")
        let third = PakNode(name: "c")
        root.children = [first, second, third]

        let placements = PakTreeMutation.placements(for: [first.id, third.id], in: root)
        PakTreeMutation.apply(removing: placements, inserting: [])

        XCTAssertEqual(root.children?.map(\.name), ["b"])

        PakTreeMutation.apply(removing: [], inserting: placements)

        XCTAssertEqual(root.children?.map(\.name), ["a", "b", "c"])
        XCTAssertTrue(root.children?[0] === first)
        XCTAssertTrue(root.children?[2] === third)
    }

    func testTreeMutationMoveInverseRestoresBothFolders() throws {
        let root = PakNode(name: "/")
        let sourceFolder = PakNode(name: "source")
        let destinationFolder = PakNode(name: "destination")
        let original = PakNode(name: "item.txt")
        original.localData = Data([1])
        sourceFolder.children = [original]
        root.children = [sourceFolder, destinationFolder]

        let sourcePlacement = PakTreeMutation.placements(for: [original.id], in: root)
        PakTreeMutation.apply(removing: sourcePlacement, inserting: [])

        let movedCopy = PakNode(name: original.name)
        movedCopy.localData = original.localData
        destinationFolder.children = [movedCopy]
        let destinationPlacement = PakTreeMutation.placements(
            for: [movedCopy],
            in: destinationFolder
        )

        PakTreeMutation.apply(
            removing: destinationPlacement,
            inserting: sourcePlacement
        )

        XCTAssertTrue(sourceFolder.children?.first === original)
        XCTAssertTrue(destinationFolder.children?.isEmpty == true)

        PakTreeMutation.apply(
            removing: sourcePlacement,
            inserting: destinationPlacement
        )

        XCTAssertTrue(sourceFolder.children?.isEmpty == true)
        XCTAssertTrue(destinationFolder.children?.first === movedCopy)
    }

    func testPreviewDimensionsAreBounded() {
        XCTAssertTrue(PakPreviewLimits.isSafe(width: 4_096, height: 4_096))
        XCTAssertFalse(PakPreviewLimits.isSafe(width: 8_193, height: 1))
        XCTAssertFalse(PakPreviewLimits.isSafe(width: 8_192, height: 8_192))
    }

    func testArchivePathsRejectExcessiveDepth() {
        let path = Array(repeating: "folder", count: PakSafetyLimits.maximumPathDepth).joined(separator: "/")
            + "/file.txt"

        XCTAssertThrowsError(try PakPathValidator.validateArchivePath(path))
    }

    func testArchiveSearchFindsFilesAcrossNestedPaths() throws {
        let fixture = makeSearchFixture()

        let results = PakArchiveSearch.search(root: fixture.root, query: "episode1 start")

        XCTAssertEqual(results.map(\.path), ["/maps/episode1/start.bsp"])
    }

    func testArchiveSearchMatchesExtensionsAndFullPathsCaseInsensitively() throws {
        let fixture = makeSearchFixture()

        XCTAssertTrue(
            PakArchiveSearch.search(root: fixture.root, query: "*.MDL")
                .contains { $0.node === fixture.viewModel }
        )
        XCTAssertEqual(
            PakArchiveSearch.search(root: fixture.root, query: "PROGS/V_SHOT.MDL").first?.node,
            fixture.viewModel
        )
    }

    func testArchiveSearchIgnoresNameSeparatorsForPartialQueries() throws {
        let fixture = makeSearchFixture()

        let results = PakArchiveSearch.search(root: fixture.root, query: "vshot")

        XCTAssertEqual(results.first?.node, fixture.viewModel)
    }

    func testArchiveSearchDoesNotIncludeEveryDescendantOfMatchingFolder() throws {
        let fixture = makeSearchFixture()

        let results = PakArchiveSearch.search(root: fixture.root, query: "maps")

        XCTAssertEqual(results.map(\.path), ["/maps"])
    }

    func testArchiveSearchToleratesSmallTyposAndRanksExactStemFirst() throws {
        let fixture = makeSearchFixture()
        let shotgun = PakNode(name: "shotgun.mdl")
        shotgun.localData = Data()
        let restart = PakNode(name: "restart.cfg")
        restart.localData = Data()
        fixture.root.children?.append(contentsOf: [shotgun, restart])

        XCTAssertEqual(PakArchiveSearch.search(root: fixture.root, query: "shotgn").first?.node, shotgun)
        XCTAssertEqual(PakArchiveSearch.search(root: fixture.root, query: "start").first?.node, fixture.start)
    }

    func testArchiveSearchUsesFuzzyMatchingOnlyWhenStrictSearchIsEmpty() throws {
        let fixture = makeSearchFixture()
        let strict = PakNode(name: "shotgn-notes.txt")
        strict.localData = Data()
        let fuzzy = PakNode(name: "shotgun.mdl")
        fuzzy.localData = Data()
        fixture.root.children?.append(contentsOf: [strict, fuzzy])

        let results = PakArchiveSearch.search(root: fixture.root, query: "shotgn")

        XCTAssertEqual(results.map(\.node), [strict])
    }

    private func makeSearchFixture() -> (root: PakNode, start: PakNode, viewModel: PakNode) {
        let root = PakNode(name: "/")
        let maps = PakNode(name: "maps")
        let episode = PakNode(name: "episode1")
        let start = PakNode(name: "start.bsp")
        start.localData = Data()
        episode.children?.append(start)
        maps.children?.append(episode)

        let progs = PakNode(name: "progs")
        let viewModel = PakNode(name: "v_shot.mdl")
        viewModel.localData = Data()
        progs.children?.append(viewModel)
        root.children?.append(contentsOf: [maps, progs])
        return (root, start, viewModel)
    }

    private func makePak(path: String, payload: Data) -> Data {
        let directoryOffset = 12 + payload.count
        var data = Data("PACK".utf8)
        appendInt32(directoryOffset, to: &data)
        appendInt32(64, to: &data)
        data.append(payload)

        var name = [UInt8](repeating: 0, count: 56)
        for (index, byte) in path.utf8.prefix(55).enumerated() {
            name[index] = byte
        }
        data.append(contentsOf: name)
        appendInt32(12, to: &data)
        appendInt32(payload.count, to: &data)
        return data
    }

    private func appendInt32(_ value: Int, to data: inout Data) {
        var littleEndian = Int32(value).littleEndian
        withUnsafeBytes(of: &littleEndian) { bytes in
            data.append(contentsOf: bytes)
        }
    }

    private func makeCentralDirectoryOnlyZip(path: String, expandedSize: UInt32) -> Data {
        let nameBytes = Data(path.utf8)
        var data = Data()
        appendUInt32(0x0201_4B50, to: &data)
        appendUInt16(20, to: &data) // version made by
        appendUInt16(20, to: &data) // version needed
        appendUInt16(0, to: &data)  // flags
        appendUInt16(0, to: &data)  // stored
        appendUInt16(0, to: &data)  // modification time
        appendUInt16(0, to: &data)  // modification date
        appendUInt32(0, to: &data)  // CRC-32
        appendUInt32(0, to: &data)  // compressed size
        appendUInt32(expandedSize, to: &data)
        appendUInt16(UInt16(nameBytes.count), to: &data)
        appendUInt16(0, to: &data)  // extra length
        appendUInt16(0, to: &data)  // comment length
        appendUInt16(0, to: &data)  // disk number
        appendUInt16(0, to: &data)  // internal attributes
        appendUInt32(0, to: &data)  // external attributes
        appendUInt32(0, to: &data)  // local header offset
        data.append(nameBytes)

        let directorySize = UInt32(data.count)
        appendUInt32(0x0605_4B50, to: &data)
        appendUInt16(0, to: &data)  // disk number
        appendUInt16(0, to: &data)  // directory disk
        appendUInt16(1, to: &data)  // entries on disk
        appendUInt16(1, to: &data)  // total entries
        appendUInt32(directorySize, to: &data)
        appendUInt32(0, to: &data)  // directory offset
        appendUInt16(0, to: &data)  // comment length
        return data
    }

    private func appendUInt16(_ value: UInt16, to data: inout Data) {
        var littleEndian = value.littleEndian
        withUnsafeBytes(of: &littleEndian) { bytes in
            data.append(contentsOf: bytes)
        }
    }

    private func appendUInt32(_ value: UInt32, to data: inout Data) {
        var littleEndian = value.littleEndian
        withUnsafeBytes(of: &littleEndian) { bytes in
            data.append(contentsOf: bytes)
        }
    }
}
