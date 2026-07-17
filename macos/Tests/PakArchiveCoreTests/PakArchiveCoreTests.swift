import Foundation
import XCTest
@testable import PakArchiveCore

final class PakArchiveCoreTests: XCTestCase {
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
