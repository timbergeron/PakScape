import Foundation
import XCTest
@testable import PakArchiveCore

final class PakArchiveCoreTests: XCTestCase {
    func testSkyboxFaceSetFindsAllSixFacesFromAnyFace() {
        let suffixes = ["rt", "bk", "lf", "ft", "up", "dn"]
        let nodes = suffixes.map { PakNode(name: "storm_\($0).tga", entry: PakEntry(name: "", offset: 0, length: 1)) }

        let set = PakSkyboxFaceSet(selected: nodes[2], siblings: nodes)

        XCTAssertEqual(set?.name, "storm")
        XCTAssertEqual(
            set?.sceneKitFaceNodes.map(\.name),
            ["storm_ft.tga", "storm_bk.tga", "storm_up.tga", "storm_dn.tga", "storm_rt.tga", "storm_lf.tga"]
        )
    }

    func testSkyboxFaceSetRequiresACompleteSet() {
        let nodes = ["fog_rt.png", "fog_bk.png", "fog_lf.png", "fog_ft.png", "fog_up.png"].map {
            PakNode(name: $0, entry: PakEntry(name: "", offset: 0, length: 1))
        }

        XCTAssertNil(PakSkyboxFaceSet(selected: nodes[0], siblings: nodes))
    }

    func testDetailsColumnOmitsPreviewMetadataPrefixes() {
        let data = Data([
            137, 80, 78, 71, 13, 10, 26, 10,
            0, 0, 0, 13, 73, 72, 68, 82,
            0, 0, 1, 64, 0, 0, 0, 200, 8, 6, 0, 0, 0,
        ])

        let column = PakFormatInspector.detailsColumnSummary(
            fileName: "shot.png",
            data: data,
            fileSize: data.count
        )

        XCTAssertEqual(column, "320 × 200 pixels  •  Bit Depth: 8-bit")
    }

    func testFormatInspectorReadsClassicAndRemasterSavegameHeaders() {
        let classic = Dictionary(uniqueKeysWithValues: PakFormatInspector.details(
            fileName: "s0.sav",
            data: savegame(version: 5, comment: "The_Slipgate_Complex_kills:__3/__9"),
            fileSize: 512
        ).map { ($0.label, $0.value) })

        XCTAssertEqual(classic["Format"], "Quake savegame")
        XCTAssertEqual(classic["Description"], "The Slipgate Complex kills:  3/  9")
        XCTAssertEqual(classic["Map"], "e1m1")
        XCTAssertEqual(classic["Skill"], "Hard")
        XCTAssertEqual(classic["Duration"], "1:36")
        XCTAssertEqual(
            PakFormatInspector.detailsColumnSummary(
                fileName: "s0.sav",
                data: savegame(version: 5, comment: "The_Slipgate_Complex"),
                fileSize: 512
            ),
            "Map: e1m1  •  Skill: Hard"
        )

        let remaster = Dictionary(uniqueKeysWithValues: PakFormatInspector.details(
            fileName: "s1.sav",
            data: savegame(version: 6, comment: "Dimension_of_the_Machine", gameDirectory: "mg1"),
            fileSize: 512
        ).map { ($0.label, $0.value) })

        XCTAssertEqual(remaster["Format"], "Quake remaster savegame")
        XCTAssertEqual(remaster["Mod"], "mg1")
    }

    func testFormatInspectorRejectsTruncatedSavegameHeader() {
        let data = Data("5\nnot enough lines\n".utf8)
        let details = PakFormatInspector.details(
            fileName: "broken.sav",
            data: data,
            fileSize: data.count
        )

        XCTAssertFalse(details.contains { $0.label == "Format" })
        XCTAssertEqual(
            PakFormatInspector.detailsColumnSummary(
                fileName: "broken.sav",
                data: data,
                fileSize: data.count
            ),
            ""
        )
    }

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

    func testLoaderIgnoresZeroByteDirectoryMarker() throws {
        let data = makePak(path: "textures/empty/", payload: Data())

        let loaded = try PakLoader.load(data: data, name: "directory-marker.pak")

        XCTAssertTrue(loaded.entries.isEmpty)
        XCTAssertTrue(loaded.root.children?.isEmpty == true)
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

    func testFormatInspectorReadsQuakeModelMetadata() {
        var data = Data("IDPO".utf8)
        appendInt32(6, to: &data) // version
        data.append(Data(repeating: 0, count: 40))
        appendInt32(2, to: &data) // skins
        appendInt32(320, to: &data) // skin width
        appendInt32(200, to: &data) // skin height
        appendInt32(512, to: &data) // vertices
        appendInt32(640, to: &data) // triangles
        appendInt32(10, to: &data) // frames

        let details = Dictionary(uniqueKeysWithValues: PakFormatInspector.details(
            fileName: "player.mdl",
            data: data,
            fileSize: data.count
        ).map { ($0.label, $0.value) })

        XCTAssertEqual(details["Format"], "Quake alias model")
        XCTAssertEqual(details["Version"], "6")
        XCTAssertEqual(details["Skin Size"], "320 × 200 pixels")
        XCTAssertEqual(details["Skins"], "2")
        XCTAssertEqual(details["Vertices"], "512")
        XCTAssertEqual(details["Triangles"], "640")
        XCTAssertEqual(details["Frames"], "10")
    }

    func testFormatInspectorReadsQuakeCProgramMetadata() {
        var data = Data()
        appendInt32(6, to: &data)      // version
        appendInt32(5927, to: &data)   // progdefs CRC
        appendInt32(60, to: &data)     // statement offset
        appendInt32(20940, to: &data)  // statements
        appendInt32(0, to: &data)      // global definition offset
        appendInt32(4287, to: &data)   // global definitions
        appendInt32(0, to: &data)      // field definition offset
        appendInt32(218, to: &data)    // field definitions
        appendInt32(0, to: &data)      // function offset
        appendInt32(2091, to: &data)   // functions
        appendInt32(60, to: &data)     // string offset
        appendInt32(88336, to: &data)  // string bytes
        appendInt32(0, to: &data)      // global offset
        appendInt32(11471, to: &data)  // globals
        appendInt32(195, to: &data)    // entity fields

        let details = Dictionary(uniqueKeysWithValues: PakFormatInspector.details(
            fileName: "progs.dat",
            data: data,
            fileSize: data.count
        ).map { ($0.label, $0.value) })

        XCTAssertEqual(details["Format"], "Compiled QuakeC program")
        XCTAssertEqual(details["Version"], "6")
        XCTAssertEqual(details["Progdefs CRC"], "5927")
        XCTAssertEqual(details["Functions"], "2,091")
        XCTAssertEqual(details["Entity Fields"], "195")
        XCTAssertEqual(details["String Data"], "88,336 bytes")
        XCTAssertTrue(details["Purpose"]?.contains("compiled QuakeC program") == true)

        /* The column stays a stat rather than the sentence. */
        XCTAssertEqual(
            PakFormatInspector.summary(fileName: "progs.dat", data: data, fileSize: data.count),
            "Functions: 2,091  •  Entity Fields: 195"
        )
    }

    func testFormatInspectorReadsDosTextScreen() {
        let headline = "QUAKE: The Doomed Dimension by id Software"
        var data = Data(repeating: 0, count: 80 * 25 * 2)
        for (index, character) in headline.utf8.enumerated() {
            data[index * 2] = character
            data[index * 2 + 1] = 0x4f  // colour attribute
        }

        let details = Dictionary(uniqueKeysWithValues: PakFormatInspector.details(
            fileName: "end1.bin",
            data: data,
            fileSize: data.count
        ).map { ($0.label, $0.value) })

        XCTAssertEqual(details["Format"], "DOS text-mode screen")
        XCTAssertEqual(details["Screen Size"], "80 × 25 characters")
        XCTAssertEqual(details["Description"], headline)
        XCTAssertTrue(details["Purpose"]?.contains("shareware") == true)
    }

    func testFormatInspectorDescribesWellKnownQuakeFiles() {
        let script = Data("exec default.cfg\nexec config.cfg\n".utf8)
        let startup = Dictionary(uniqueKeysWithValues: PakFormatInspector.details(
            fileName: "quake.rc",
            data: script,
            fileSize: script.count
        ).map { ($0.label, $0.value) })

        XCTAssertEqual(startup["Format"], "Quake console script")
        XCTAssertTrue(startup["Purpose"]?.contains("startup script") == true)

        /* A name PakScape does not know still gets its extension's description. */
        let other = Dictionary(uniqueKeysWithValues: PakFormatInspector.details(
            fileName: "sv_main.qc",
            data: Data("void() main = {};\n".utf8),
            fileSize: 18
        ).map { ($0.label, $0.value) })
        XCTAssertTrue(other["Purpose"]?.contains("QuakeC source") == true)

        /* A file with nothing known about it keeps saying nothing. */
        XCTAssertEqual(
            PakFormatInspector.details(
                fileName: "unknown.xyz",
                data: Data([1, 2, 3, 4]),
                fileSize: 4
            ),
            []
        )
    }

    func testGetInfoWindowsCascadeFromTheArchiveWindow() {
        let visibleFrame = CGRect(x: 0, y: 0, width: 1600, height: 1000)
        let archiveWindow = CGRect(x: 200, y: 300, width: 900, height: 600)
        let size = CGSize(width: 460, height: 320)

        let base = PakItemInfoPlacement.base(
            parentFrame: archiveWindow,
            windowSize: size,
            visibleFrame: visibleFrame
        )
        XCTAssertEqual(base, CGPoint(x: 232, y: 868))

        let first = PakItemInfoPlacement.topLeft(
            base: base,
            previous: nil,
            windowSize: size,
            visibleFrame: visibleFrame
        )
        XCTAssertEqual(first, base)

        let second = PakItemInfoPlacement.topLeft(
            base: base,
            previous: first,
            windowSize: size,
            visibleFrame: visibleFrame
        )
        XCTAssertEqual(second, CGPoint(x: 256, y: 844))
    }

    func testGetInfoCascadeStartsOverWhenItRunsOffScreen() {
        let visibleFrame = CGRect(x: 0, y: 0, width: 1600, height: 1000)
        let size = CGSize(width: 460, height: 320)
        let base = CGPoint(x: 100, y: 900)

        /* One more step would put the bottom of the window below the screen. */
        let placed = PakItemInfoPlacement.topLeft(
            base: base,
            previous: CGPoint(x: 100, y: 321),
            windowSize: size,
            visibleFrame: visibleFrame
        )
        XCTAssertEqual(placed, base)
    }

    func testGetInfoWindowStaysOnScreenBesideAnArchiveWindowAtTheEdge() {
        let visibleFrame = CGRect(x: 0, y: 0, width: 1200, height: 800)
        let size = CGSize(width: 460, height: 320)

        let base = PakItemInfoPlacement.base(
            parentFrame: CGRect(x: 1100, y: 700, width: 900, height: 600),
            windowSize: size,
            visibleFrame: visibleFrame
        )
        XCTAssertEqual(base.x, visibleFrame.maxX - size.width)
        XCTAssertEqual(base.y, visibleFrame.maxY)
        XCTAssertTrue(base.y - size.height >= visibleFrame.minY)
    }

    func testGetInfoWindowCentersWithoutAnArchiveWindow() {
        let visibleFrame = CGRect(x: 0, y: 0, width: 1200, height: 800)
        let size = CGSize(width: 460, height: 320)

        XCTAssertEqual(
            PakItemInfoPlacement.base(
                parentFrame: nil,
                windowSize: size,
                visibleFrame: visibleFrame
            ),
            CGPoint(x: 370, y: 560)
        )
    }

    func testFormatInspectorRejectsTruncatedKnownFormat() {
        XCTAssertEqual(
            PakFormatInspector.details(
                fileName: "broken.mdl",
                data: Data("IDPO".utf8),
                fileSize: 4
            ),
            []
        )
    }

    func testFormatInspectorReportsTextEncodingAndLines() {
        let data = Data("first\nsecond\nthird".utf8)
        let details = Dictionary(uniqueKeysWithValues: PakFormatInspector.details(
            fileName: "autoexec.cfg",
            data: data,
            fileSize: data.count
        ).map { ($0.label, $0.value) })

        XCTAssertEqual(details["Format"], "Quake configuration")
        XCTAssertEqual(details["Encoding"], "UTF-8")
        XCTAssertEqual(details["Lines"], "3")
    }

    func testFormatInspectorReadsBsp2AndQuake64Headers() {
        var bsp2 = Data(repeating: 0, count: 320)
        bsp2.replaceSubrange(0 ..< 4, with: Data("BSP2".utf8))
        let entities = Data(#"{"classname" "worldspawn" "message" "Modern Map"}"#.utf8)
        writeInt32(124, at: 4, to: &bsp2)
        writeInt32(entities.count, at: 8, to: &bsp2)
        bsp2.replaceSubrange(124 ..< 124 + entities.count, with: entities)
        writeInt32(200, at: 4 + 3 * 8, to: &bsp2)
        writeInt32(24, at: 4 + 3 * 8 + 4, to: &bsp2)
        writeInt32(224, at: 4 + 7 * 8, to: &bsp2)
        writeInt32(56, at: 4 + 7 * 8 + 4, to: &bsp2)

        let details = inspected("modern.bsp", bsp2)
        XCTAssertEqual(details["Format"], "Quake BSP2 level")
        XCTAssertEqual(details["Description"], "Modern Map")
        XCTAssertEqual(details["Vertices"], "2")
        XCTAssertEqual(details["Faces"], "2")

        var quake64 = Data(repeating: 0, count: 124)
        writeInt32(23, at: 0, to: &quake64)
        XCTAssertEqual(inspected("q64.bsp", quake64)["Format"], "Quake 64 BSP level")
    }

    func testFormatInspectorReadsMd3AndMd5Models() {
        var md3 = Data(repeating: 0, count: 216)
        md3.replaceSubrange(0 ..< 4, with: Data("IDP3".utf8))
        writeInt32(15, at: 4, to: &md3)
        md3.replaceSubrange(8 ..< 12, with: Data("ogre".utf8))
        writeInt32(3, at: 76, to: &md3)
        writeInt32(2, at: 80, to: &md3)
        writeInt32(1, at: 84, to: &md3)
        writeInt32(108, at: 100, to: &md3)
        md3.replaceSubrange(108 ..< 112, with: Data("IDP3".utf8))
        writeInt32(2, at: 108 + 76, to: &md3)
        writeInt32(24, at: 108 + 80, to: &md3)
        writeInt32(12, at: 108 + 84, to: &md3)
        writeInt32(108, at: 108 + 104, to: &md3)

        let md3Details = inspected("ogre.md3", md3)
        XCTAssertEqual(md3Details["Frames"], "3")
        XCTAssertEqual(md3Details["Surfaces"], "1")
        XCTAssertEqual(md3Details["Triangles"], "12")

        let mesh = Data("""
        MD5Version 10
        numJoints 4
        numMeshes 2
        numverts 12
        numtris 6
        numverts 8
        numtris 4
        """.utf8)
        let meshDetails = inspected("ogre.md5mesh", mesh)
        XCTAssertEqual(meshDetails["Meshes"], "2")
        XCTAssertEqual(meshDetails["Vertices"], "20")
        XCTAssertEqual(meshDetails["Triangles"], "10")

        let animation = Data("""
        MD5Version 10
        numFrames 48
        numJoints 4
        frameRate 24
        numAnimatedComponents 16
        """.utf8)
        let animationDetails = inspected("ogre.md5anim", animation)
        XCTAssertEqual(animationDetails["Duration"], "0:02")
        XCTAssertEqual(animationDetails["Frame Rate"], "24 fps")
    }

    func testFormatInspectorReadsQuakeSidecarsAndAddedTextFormats() {
        var lit = Data("QLIT".utf8)
        appendInt32(1, to: &lit)
        lit.append(Data(repeating: 0x7f, count: 30))
        XCTAssertEqual(inspected("e1m1.lit", lit)["Samples"], "10")

        var vis = Data(repeating: 0, count: 32)
        vis.replaceSubrange(0 ..< 8, with: Data("e1m1.bsp".utf8))
        appendInt32(8, to: &vis)
        vis.append(Data(repeating: 1, count: 8))
        let visDetails = inspected("e1m1.vis", vis)
        XCTAssertEqual(visDetails["Maps"], "e1m1.bsp")
        XCTAssertEqual(visDetails["Visibility Data"], "8 bytes")

        let nav = Data("NAV2".utf8) + Data(repeating: 0, count: 12)
        XCTAssertEqual(inspected("e1m1.nav", nav)["Version"], "NAV2")

        let fgd = Data("// entity definitions\n@PointClass\n".utf8)
        let fgdDetails = inspected("quake.fgd", fgd)
        XCTAssertEqual(fgdDetails["Format"], "Game definition")
        XCTAssertEqual(fgdDetails["Lines"], "2")
        XCTAssertNotNil(fgdDetails["Purpose"])
    }

    func testFormatInspectorReadsDdsAndModernAudioHeaders() {
        var dds = Data(repeating: 0, count: 148)
        dds.replaceSubrange(0 ..< 4, with: Data("DDS ".utf8))
        writeInt32(124, at: 4, to: &dds)
        writeInt32(128, at: 12, to: &dds)
        writeInt32(256, at: 16, to: &dds)
        writeInt32(8, at: 28, to: &dds)
        writeInt32(32, at: 76, to: &dds)
        dds.replaceSubrange(84 ..< 88, with: Data("DX10".utf8))
        writeInt32(98, at: 128, to: &dds)
        let ddsDetails = inspected("wall.dds", dds)
        XCTAssertEqual(ddsDetails["Dimensions"], "256 × 128 pixels")
        XCTAssertEqual(ddsDetails["Mipmaps"], "8")
        XCTAssertEqual(ddsDetails["Compression"], "DX10 (DXGI format 98)")

        var flac = Data(repeating: 0, count: 42)
        flac.replaceSubrange(0 ..< 4, with: Data("fLaC".utf8))
        flac[4] = 0
        flac[7] = 34
        let streamInfo = UInt64(44_100) << 44 |
            UInt64(1) << 41 |
            UInt64(15) << 36 |
            UInt64(441_000)
        writeUInt64BE(streamInfo, at: 18, to: &flac)
        let flacDetails = inspected("track.flac", flac)
        XCTAssertEqual(flacDetails["Channels"], "Stereo")
        XCTAssertEqual(flacDetails["Bit Depth"], "16-bit")
        XCTAssertEqual(flacDetails["Duration"], "0:10")

        let ogg = makeVorbis(sampleRate: 48_000, channels: 2, samples: 480_000)
        let oggDetails = inspected("track.ogg", ogg)
        XCTAssertEqual(oggDetails["Format"], "Ogg Vorbis audio")
        XCTAssertEqual(oggDetails["Sample Rate"], "48,000 Hz")
        XCTAssertEqual(oggDetails["Duration"], "0:10")

        let opusDetails = inspected("track.opus", makeOpus(samples: 480_312))
        XCTAssertEqual(opusDetails["Format"], "Ogg Opus audio")
        XCTAssertEqual(opusDetails["Sample Rate"], "48,000 Hz")
        XCTAssertEqual(opusDetails["Duration"], "0:10")
    }

    func testFormatInspectorReadsTrackerAndUmxHeaders() {
        var xm = Data(repeating: 0, count: 80)
        xm.replaceSubrange(0 ..< 17, with: Data("Extended Module: ".utf8))
        xm.replaceSubrange(17 ..< 21, with: Data("Song".utf8))
        xm[37] = 0x1a
        writeUInt16(0x0104, at: 58, to: &xm)
        writeUInt16(4, at: 64, to: &xm)
        writeUInt16(8, at: 68, to: &xm)
        writeUInt16(3, at: 70, to: &xm)
        writeUInt16(5, at: 72, to: &xm)
        writeUInt16(125, at: 78, to: &xm)
        XCTAssertEqual(inspected("song.xm", xm)["Channels"], "8")

        var s3m = Data(repeating: 255, count: 96)
        s3m.replaceSubrange(0 ..< 4, with: Data("Song".utf8))
        s3m.replaceSubrange(44 ..< 48, with: Data("SCRM".utf8))
        writeUInt16(4, at: 32, to: &s3m)
        writeUInt16(2, at: 34, to: &s3m)
        writeUInt16(3, at: 36, to: &s3m)
        s3m[50] = 125
        s3m[64] = 0
        s3m[65] = 1
        XCTAssertEqual(inspected("song.s3m", s3m)["Channels"], "2")

        var it = Data(repeating: 255, count: 128)
        it.replaceSubrange(0 ..< 4, with: Data("IMPM".utf8))
        it.replaceSubrange(4 ..< 8, with: Data("Song".utf8))
        writeUInt16(4, at: 32, to: &it)
        writeUInt16(2, at: 34, to: &it)
        writeUInt16(6, at: 36, to: &it)
        writeUInt16(3, at: 38, to: &it)
        writeUInt16(0x0214, at: 40, to: &it)
        it[51] = 125
        it[64] = 32
        XCTAssertEqual(inspected("song.it", it)["Samples"], "6")

        var mod = Data(repeating: 0, count: 1_084)
        mod.replaceSubrange(0 ..< 4, with: Data("Song".utf8))
        mod[950] = 2
        mod[952] = 0
        mod[953] = 3
        mod.replaceSubrange(1_080 ..< 1_084, with: Data("M.K.".utf8))
        XCTAssertEqual(inspected("song.mod", mod)["Patterns"], "4")

        var umx = Data(repeating: 0, count: 36)
        writeUInt32(0x9e2a83c1, at: 0, to: &umx)
        writeUInt16(69, at: 4, to: &umx)
        writeInt32(12, at: 12, to: &umx)
        writeInt32(1, at: 20, to: &umx)
        XCTAssertEqual(inspected("song.umx", umx)["Format"], "Unreal music package")
    }

    func testFormatInspectorFindsMapNamesInDemo() {
        let data = Data(
            "noise maps/b_shell0.bsp more maps/e1m3.bsp duplicate maps/E1M3.bsp".utf8
        )

        XCTAssertEqual(
            PakFormatInspector.summary(fileName: "run.dem", data: data, fileSize: data.count),
            "Map: e1m3"
        )
    }

    func testFormatInspectorReadsDemoServerInfoAndScores() {
        var signon = Data()
        appendServerInfo(
            to: &signon,
            protocolVersion: 15,
            maxClients: 4,
            gameType: 1,
            levelName: "the Slipgate Complex",
            models: ["maps/e1m3.bsp", "progs/player.mdl"],
            sounds: ["weapons/r_exp3.wav"]
        )
        appendPlayer(to: &signon, slot: 0, name: "alice", frags: 12, colors: 0x44)
        appendPlayer(to: &signon, slot: 1, name: "bob", frags: 7, colors: 0x33)
        appendTime(to: &signon, 0)

        var closing = Data()
        appendTime(to: &closing, 95.5)
        closing.append(14) // svc_updatefrags
        closing.append(1)
        appendUInt16(20, to: &closing)

        let data = demo(frames: [signon, closing])
        let details = Dictionary(uniqueKeysWithValues: PakFormatInspector.details(
            fileName: "duel.dem",
            data: data,
            fileSize: data.count
        ).map { ($0.label, $0.value) })

        XCTAssertEqual(details["Format"], "Quake demo")
        XCTAssertEqual(details["Map"], "e1m3")
        XCTAssertEqual(details["Level"], "the Slipgate Complex")
        XCTAssertEqual(details["Duration"], "1:36")
        XCTAssertEqual(details["Mode"], "Deathmatch")
        XCTAssertEqual(details["Players"], "alice, bob")
        XCTAssertEqual(details["Scores"], "bob 20, alice 12")
        XCTAssertEqual(details["Protocol"], "15")
        XCTAssertEqual(
            PakFormatInspector.summary(fileName: "duel.dem", data: data, fileSize: data.count),
            "Map: e1m3  •  Duration: 1:36"
        )
        XCTAssertEqual(
            PakFormatInspector.detailsColumnSummary(
                fileName: "duel.dem",
                data: data,
                fileSize: data.count
            ),
            "Map: e1m3  •  1:36"
        )
    }

    func testFormatInspectorReportsSinglePlayerDemoMode() {
        var signon = Data()
        appendServerInfo(
            to: &signon,
            protocolVersion: 666,
            maxClients: 1,
            gameType: 0,
            levelName: "the Slipgate Complex",
            models: ["maps/e1m1.bsp"],
            sounds: []
        )
        appendPlayer(to: &signon, slot: 0, name: "player", frags: 0, colors: 0x00)
        appendTime(to: &signon, 1.25)

        let data = demo(frames: [signon])
        let details = Dictionary(uniqueKeysWithValues: PakFormatInspector.details(
            fileName: "run.dem",
            data: data,
            fileSize: data.count
        ).map { ($0.label, $0.value) })

        XCTAssertEqual(details["Map"], "e1m1")
        XCTAssertEqual(details["Mode"], "Single player")
        XCTAssertEqual(details["Player"], "player")
        XCTAssertEqual(details["Protocol"], "666")
        XCTAssertNil(details["Scores"])
    }

    /// A frame this parser cannot decode must not cost the timings the frame walk already has.
    func testFormatInspectorKeepsDemoTimingAcrossUnreadableFrames() {
        var signon = Data()
        appendServerInfo(
            to: &signon,
            protocolVersion: 15,
            maxClients: 2,
            gameType: 1,
            levelName: "",
            models: ["maps/dm4.bsp"],
            sounds: []
        )
        appendTime(to: &signon, 0)

        var unreadable = Data()
        appendTime(to: &unreadable, 30)
        unreadable.append(58) // svc_csqcentities, deliberately unsupported

        var later = Data()
        appendTime(to: &later, 61)

        let data = demo(frames: [signon, unreadable, later])
        let details = Dictionary(uniqueKeysWithValues: PakFormatInspector.details(
            fileName: "dm.dem",
            data: data,
            fileSize: data.count
        ).map { ($0.label, $0.value) })

        XCTAssertEqual(details["Map"], "dm4")
        XCTAssertEqual(details["Duration"], "At least 1:01")
    }

    /// Quake parks -99 in a slot whose player left, so those names are not scores.
    func testFormatInspectorExcludesVacatedSlotsFromDemoScores() {
        var signon = Data()
        appendServerInfo(
            to: &signon,
            protocolVersion: 15,
            maxClients: 16,
            gameType: 1,
            levelName: "",
            models: ["maps/ctf2m8.bsp"],
            sounds: []
        )
        appendPlayer(to: &signon, slot: 0, name: "sa", frags: 3, colors: 0x44)
        appendPlayer(to: &signon, slot: 1, name: "lilbro", frags: 1, colors: 0x33)
        appendPlayer(to: &signon, slot: 2, name: "departed", frags: -99, colors: 0x00)
        appendTime(to: &signon, 0)

        let data = demo(frames: [signon])
        let details = Dictionary(uniqueKeysWithValues: PakFormatInspector.details(
            fileName: "ctf.dem",
            data: data,
            fileSize: data.count
        ).map { ($0.label, $0.value) })

        XCTAssertEqual(details["Players"], "sa, lilbro, departed")
        XCTAssertEqual(details["Scores"], "sa 3, lilbro 1")
    }

    func testFormatInspectorFallsBackToTextScanForUnparsableDemo() {
        var data = Data("-1\n".utf8)
        data.append(Data("noise maps/e1m5.bsp".utf8))

        XCTAssertEqual(
            PakFormatInspector.summary(fileName: "broken.dem", data: data, fileSize: data.count),
            "Map: e1m5"
        )
    }

    func testFormatInspectorReadsBspWorldspawnDescription() {
        let entities = Data(#"{"classname" "worldspawn" "message" "The Slipgate Complex"}"#.utf8)
        var data = Data()
        appendInt32(29, to: &data)
        appendInt32(124, to: &data)
        appendInt32(entities.count, to: &data)
        data.append(Data(repeating: 0, count: 112))
        data.append(entities)

        let details = Dictionary(uniqueKeysWithValues: PakFormatInspector.details(
            fileName: "e1m1.bsp",
            data: data,
            fileSize: data.count
        ).map { ($0.label, $0.value) })

        XCTAssertEqual(details["Description"], "The Slipgate Complex")
        XCTAssertEqual(
            PakFormatInspector.summary(fileName: "e1m1.bsp", data: data, fileSize: data.count),
            "Description: The Slipgate Complex"
        )
        XCTAssertEqual(
            PakFormatInspector.detailsColumnSummary(
                fileName: "e1m1.bsp",
                data: data,
                fileSize: data.count
            ),
            "The Slipgate Complex"
        )
    }

    func testFormatInspectorReadsBspDescriptionPastOrdinaryInspectionLimit() {
        let entities = Data(#"{"classname" "worldspawn" "message" "The Wind Tunnels"}"#.utf8)
        let entityOffset = PakFormatInspector.maximumInspectionBytes + 128
        var data = Data()
        appendInt32(29, to: &data)
        appendInt32(entityOffset, to: &data)
        appendInt32(entities.count, to: &data)
        data.append(Data(repeating: 0, count: entityOffset - data.count))
        data.append(entities)

        let inspected = data.prefix(PakFormatInspector.inspectionByteLimit(for: "e3m5.bsp"))
        XCTAssertEqual(
            PakFormatInspector.summary(
                fileName: "e3m5.bsp",
                data: Data(inspected),
                fileSize: data.count
            ),
            "Description: The Wind Tunnels"
        )
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

    func testArchiveSearchMatchesInspectedMetadata() {
        let root = PakNode(name: "/")
        let demo = PakNode(name: "speedrun.dem")
        demo.localData = Data("noise maps/e1m3.bsp".utf8)

        let entities = Data(#"{"classname" "worldspawn" "message" "The Slipgate Complex"}"#.utf8)
        let bsp = PakNode(name: "level.bsp")
        var bspData = Data()
        appendInt32(29, to: &bspData)
        appendInt32(124, to: &bspData)
        appendInt32(entities.count, to: &bspData)
        bspData.append(Data(repeating: 0, count: 112))
        bspData.append(entities)
        bsp.localData = bspData
        root.children?.append(contentsOf: [demo, bsp])

        let metadata: (PakNode) -> String = { node in
            PakFormatInspector.searchableText(
                fileName: node.name,
                data: node.localData,
                fileSize: node.fileSize
            )
        }

        XCTAssertEqual(
            PakArchiveSearch.search(root: root, query: "e1m3", metadataText: metadata).map(\.node),
            [demo]
        )
        XCTAssertEqual(
            PakArchiveSearch.search(
                root: root,
                query: "Slipgate Complex",
                metadataText: metadata
            ).map(\.node),
            [bsp]
        )
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

    private func savegame(
        version: Int,
        comment: String,
        gameDirectory: String? = nil
    ) -> Data {
        var lines = [String(version)]
        if version == 6 {
            lines.append(gameDirectory ?? "id1")
        }
        lines.append(comment)
        lines.append(contentsOf: Array(repeating: "0", count: 16))
        lines.append("2")
        lines.append("e1m1")
        lines.append("95.5")
        lines.append("{}")
        return Data(lines.joined(separator: "\r\n").utf8)
    }

    private func inspected(_ name: String, _ data: Data) -> [String: String] {
        Dictionary(uniqueKeysWithValues: PakFormatInspector.details(
            fileName: name,
            data: data,
            fileSize: data.count
        ).map { ($0.label, $0.value) })
    }

    private func makeVorbis(sampleRate: UInt32, channels: UInt8, samples: UInt64) -> Data {
        var packet = Data([1])
        packet.append(Data("vorbis".utf8))
        appendUInt32(0, to: &packet)
        packet.append(channels)
        appendUInt32(sampleRate, to: &packet)
        packet.append(Data(repeating: 0, count: 14))

        var data = Data(repeating: 0, count: 28)
        data.replaceSubrange(0 ..< 4, with: Data("OggS".utf8))
        data[5] = 2
        data[26] = 1
        data[27] = UInt8(packet.count)
        data.append(packet)

        var finalPage = Data(repeating: 0, count: 28)
        finalPage.replaceSubrange(0 ..< 4, with: Data("OggS".utf8))
        writeUInt64(samples, at: 6, to: &finalPage)
        finalPage[26] = 1
        data.append(finalPage)
        return data
    }

    private func makeOpus(samples: UInt64) -> Data {
        var packet = Data("OpusHead".utf8)
        packet.append(1)
        packet.append(2)
        appendUInt16(312, to: &packet)
        appendUInt32(44_100, to: &packet)
        appendUInt16(0, to: &packet)
        packet.append(0)

        var data = Data(repeating: 0, count: 28)
        data.replaceSubrange(0 ..< 4, with: Data("OggS".utf8))
        data[5] = 2
        data[26] = 1
        data[27] = UInt8(packet.count)
        data.append(packet)

        var finalPage = Data(repeating: 0, count: 28)
        finalPage.replaceSubrange(0 ..< 4, with: Data("OggS".utf8))
        writeUInt64(samples, at: 6, to: &finalPage)
        finalPage[26] = 1
        data.append(finalPage)
        return data
    }

    private func writeUInt16(_ value: UInt16, at offset: Int, to data: inout Data) {
        var bytes = Data()
        appendUInt16(value, to: &bytes)
        data.replaceSubrange(offset ..< offset + 2, with: bytes)
    }

    private func writeInt32(_ value: Int, at offset: Int, to data: inout Data) {
        var bytes = Data()
        appendInt32(value, to: &bytes)
        data.replaceSubrange(offset ..< offset + 4, with: bytes)
    }

    private func writeUInt32(_ value: UInt32, at offset: Int, to data: inout Data) {
        var bytes = Data()
        appendUInt32(value, to: &bytes)
        data.replaceSubrange(offset ..< offset + 4, with: bytes)
    }

    private func writeUInt64(_ value: UInt64, at offset: Int, to data: inout Data) {
        var littleEndian = value.littleEndian
        let bytes = withUnsafeBytes(of: &littleEndian) { Data($0) }
        data.replaceSubrange(offset ..< offset + 8, with: bytes)
    }

    private func writeUInt64BE(_ value: UInt64, at offset: Int, to data: inout Data) {
        var bigEndian = value.bigEndian
        let bytes = withUnsafeBytes(of: &bigEndian) { Data($0) }
        data.replaceSubrange(offset ..< offset + 8, with: bytes)
    }

    /// Wraps message payloads in the length-prefixed frames a recording is made of.
    private func demo(frames: [Data]) -> Data {
        var data = Data("-1\n".utf8)
        for frame in frames {
            appendInt32(frame.count, to: &data)
            for _ in 0 ..< 3 {
                appendFloat(0, to: &data)
            }
            data.append(frame)
        }
        return data
    }

    private func appendServerInfo(
        to data: inout Data,
        protocolVersion: Int,
        maxClients: UInt8,
        gameType: UInt8,
        levelName: String,
        models: [String],
        sounds: [String]
    ) {
        data.append(11) // svc_serverinfo
        appendInt32(protocolVersion, to: &data)
        data.append(maxClients)
        data.append(gameType)
        appendCString(levelName, to: &data)
        for model in models {
            appendCString(model, to: &data)
        }
        data.append(0)
        for sound in sounds {
            appendCString(sound, to: &data)
        }
        data.append(0)
    }

    private func appendPlayer(
        to data: inout Data,
        slot: UInt8,
        name: String,
        frags: Int16,
        colors: UInt8
    ) {
        data.append(13) // svc_updatename
        data.append(slot)
        appendCString(name, to: &data)
        data.append(14) // svc_updatefrags
        data.append(slot)
        appendUInt16(UInt16(bitPattern: frags), to: &data)
        data.append(17) // svc_updatecolors
        data.append(slot)
        data.append(colors)
    }

    private func appendTime(to data: inout Data, _ seconds: Float) {
        data.append(7) // svc_time
        appendFloat(seconds, to: &data)
    }

    private func appendCString(_ value: String, to data: inout Data) {
        data.append(contentsOf: Array(value.utf8))
        data.append(0)
    }

    private func appendFloat(_ value: Float, to data: inout Data) {
        appendUInt32(value.bitPattern, to: &data)
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
