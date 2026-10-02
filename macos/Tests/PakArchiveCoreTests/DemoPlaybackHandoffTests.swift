import Foundation
import XCTest
@testable import PakArchiveCore

final class DemoPlaybackHandoffTests: XCTestCase {
    func testArchivePackagesIncludeCurrentMapsFromEveryDocumentFormat() throws {
        for name in ["Untitled.pak", "edited.pk3", "edited.kpf"] {
            let pakFile = PakFile.empty(name: name)
            let maps = PakNode(name: "maps")
            let map = PakNode(name: "custom.bsp")
            map.localData = Data([1])
            maps.children?.append(map)
            let program = PakNode(name: "progs.dat")
            program.localData = Data([2, 3])
            pakFile.root.children?.append(contentsOf: [maps, program])
            map.localData = Data([4, 5, 6])
            let version = pakFile.version

            let packages = try DemoPlaybackHandoff.archivePackages(
                pakFile: pakFile, maps: ["CUSTOM"], maximumBytes: 4096
            )
            let package = try XCTUnwrap(packages.first)
            XCTAssertEqual(packages.count, 1)
            XCTAssertEqual(package.fileName, (name as NSString).deletingPathExtension + ".pk3")
            let archiveURL = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".pk3")
            try package.data.write(to: archiveURL)
            defer { try? FileManager.default.removeItem(at: archiveURL) }
            let loaded = try PakLoader.loadZip(from: archiveURL, name: package.fileName)
            let loadedMap = try XCTUnwrap(loaded.root.children?.first(where: { $0.name == "maps" })?.children?.first)
            XCTAssertEqual(loadedMap.localData, Data([4, 5, 6]))
            XCTAssertEqual(loaded.root.children?.first(where: { $0.name == "progs.dat" })?.localData, Data([2, 3]))
            XCTAssertTrue(pakFile.data.isEmpty)
            XCTAssertEqual(pakFile.version, version)
            XCTAssertTrue(pakFile.root.children?.first === maps)
        }
    }

    func testArchivePackagesSkipUnrelatedMapsAndEnforceTheRemainingBudget() throws {
        let pakFile = PakFile.empty(name: "new.pak")
        let map = PakNode(name: "custom.bsp")
        map.localData = Data([1, 2])
        pakFile.root.children?.append(map)
        XCTAssertTrue(try DemoPlaybackHandoff.archivePackages(
            pakFile: pakFile, maps: ["e1m1"], maximumBytes: 0
        ).isEmpty)
        // Bound both expanded data and ZIP record overhead.
        for limit in [1, 64] {
            XCTAssertThrowsError(try DemoPlaybackHandoff.archivePackages(
                pakFile: pakFile, maps: ["custom"], maximumBytes: limit
            )) { error in
                guard let playbackError = error as? DemoPlaybackError, case .tooLarge = playbackError else {
                    return XCTFail("Expected tooLarge, got \(error)")
                }
            }
        }
    }

    func testVirtualFileNameStripsPathsAndUnsafeCharacters() {
        XCTAssertEqual(DemoPlaybackHandoff.virtualFileName("../../etc/passwd"), "passwd")
        XCTAssertEqual(DemoPlaybackHandoff.virtualFileName("demos/run 1.dem"), "run_1.dem")
        XCTAssertEqual(DemoPlaybackHandoff.virtualFileName("..."), "demo.dem")
        XCTAssertEqual(DemoPlaybackHandoff.virtualFileName(""), "demo.dem")
        XCTAssertEqual(DemoPlaybackHandoff.virtualFileName("e1m3+extra-v2.dem"), "e1m3+extra-v2.dem")
    }

    func testLaunchURLCarriesDemoMetadata() throws {
        let summary = QuakeDemoSummary(
            protocolName: "15",
            segments: [QuakeDemoSegment(map: "e1m3", levelName: "the Necropolis", duration: 74.5)],
            gameDir: "",
            maxClients: 1,
            gameType: 0,
            duration: 74.5,
            frameCount: 975,
            players: [],
            messagesComplete: true,
            truncated: false
        )
        let demo = DemoPlaybackAsset(fileName: "demo1.dem", data: Data([1, 2, 3]))
        let source = try XCTUnwrap(URL(string: "http://127.0.0.1:5555/token/demo1.dem"))

        let url = try DemoPlaybackHandoff.launchURL(
            demo: demo,
            packages: [],
            summary: summary,
            sources: [source]
        )
        let items = try queryItems(of: url)

        XCTAssertTrue(url.absoluteString.hasPrefix(DemoPlaybackHandoff.playerURL))
        XCTAssertEqual(items["source"], source.absoluteString)
        XCTAssertEqual(items["file"], "demo1.dem")
        XCTAssertEqual(items["maps"], "e1m3")
        XCTAssertEqual(items["duration"], "74.50")
        XCTAssertEqual(items["title"], "demo1 — the Necropolis")
        XCTAssertNil(items["packages"])
    }

    func testLaunchURLDescribesArchivePackages() throws {
        let summary = QuakeDemoSummary(
            protocolName: "666",
            segments: [
                QuakeDemoSegment(map: "start", levelName: "Entrance", duration: 10),
                QuakeDemoSegment(map: "e1m1", levelName: "Slipgate", duration: 20),
            ],
            gameDir: "quoth",
            maxClients: 1,
            gameType: 0,
            duration: 30,
            frameCount: 100,
            players: [],
            messagesComplete: true,
            truncated: false
        )
        let demo = DemoPlaybackAsset(fileName: "run.dem", data: Data([1]))
        let archive = DemoPlaybackAsset(fileName: "pak0.pak", data: Data([2]))
        let demoSource = try XCTUnwrap(URL(string: "http://127.0.0.1:5555/a/run.dem"))
        let packageSource = try XCTUnwrap(URL(string: "http://127.0.0.1:5555/b/pak0.pak"))

        let url = try DemoPlaybackHandoff.launchURL(
            demo: demo,
            packages: [archive],
            summary: summary,
            sources: [demoSource, packageSource]
        )
        let items = try queryItems(of: url)

        XCTAssertEqual(items["maps"], "start,e1m1")

        let packagesJSON = try XCTUnwrap(items["packages"])
        let decoded = try JSONSerialization.jsonObject(with: Data(packagesJSON.utf8))
        let packages = try XCTUnwrap(decoded as? [[String: Any]])
        XCTAssertEqual(packages.count, 1)
        XCTAssertEqual(packages[0]["file"] as? String, "pak0.pak")
        XCTAssertEqual(packages[0]["source"] as? String, packageSource.absoluteString)
        XCTAssertEqual(packages[0]["maps"] as? [String], ["start", "e1m1"])
    }

    func testLaunchURLRejectsPayloadsOverTheSessionLimit() {
        let demo = DemoPlaybackAsset(
            fileName: "huge.dem",
            data: Data(repeating: 0, count: 8)
        )
        let server = LoopbackAssetServer()
        defer { server.stop() }

        // Ask for a limit breach without allocating one, by publishing a package that claims
        // the whole budget.
        let oversized = DemoPlaybackAsset(
            fileName: "big.pak",
            data: Data(repeating: 0, count: DemoPlaybackHandoff.maximumSessionBytes)
        )
        XCTAssertThrowsError(
            try DemoPlaybackHandoff.launchURL(
                demo: demo,
                packages: [oversized],
                summary: nil,
                server: server
            )
        )
        XCTAssertFalse(server.isRunning)
    }

    func testServerReturnsPublishedAssetAndNothingElse() throws {
        let server = LoopbackAssetServer()
        defer { server.stop() }

        let payload = Data("demo bytes".utf8)
        let urls = try server.publish(
            assets: [DemoPlaybackAsset(fileName: "run.dem", data: payload)],
            lifetime: 60
        )
        let source = try XCTUnwrap(urls.first)

        let (data, response) = try get(source)
        XCTAssertEqual((response as? HTTPURLResponse)?.statusCode, 200)
        XCTAssertEqual(data, payload)
        XCTAssertEqual(
            (response as? HTTPURLResponse)?.value(forHTTPHeaderField: "Access-Control-Allow-Origin"),
            DemoPlaybackHandoff.playerOrigin
        )

        // Nothing but the published token path is reachable; there is no document root.
        let port = server.boundPort
        let stranger = try XCTUnwrap(URL(string: "http://127.0.0.1:\(port)/run.dem"))
        let (_, missing) = try get(stranger)
        XCTAssertEqual((missing as? HTTPURLResponse)?.statusCode, 404)
    }

    func testStoppingTheServerRevokesPublishedAssets() throws {
        let server = LoopbackAssetServer()
        let urls = try server.publish(
            assets: [DemoPlaybackAsset(fileName: "run.dem", data: Data([9]))],
            lifetime: 60
        )
        let source = try XCTUnwrap(urls.first)
        XCTAssertTrue(server.isRunning)

        server.stop()
        XCTAssertFalse(server.isRunning)

        var request = URLRequest(url: source)
        request.timeoutInterval = 3
        let finished = expectation(description: "request finished")
        var failed = false
        var status = -1
        URLSession.shared.dataTask(with: request) { _, response, error in
            failed = error != nil
            status = (response as? HTTPURLResponse)?.statusCode ?? -1
            finished.fulfill()
        }.resume()
        wait(for: [finished], timeout: 10)

        XCTAssertTrue(failed || status == 404, "a stopped server must not still serve the demo")
    }

    private func queryItems(of url: URL) throws -> [String: String] {
        let components = try XCTUnwrap(URLComponents(url: url, resolvingAgainstBaseURL: false))
        return Dictionary(
            uniqueKeysWithValues: (components.queryItems ?? []).map { ($0.name, $0.value ?? "") }
        )
    }

    private func get(_ url: URL) throws -> (Data, URLResponse) {
        var request = URLRequest(url: url)
        request.timeoutInterval = 5
        var result: (Data, URLResponse)?
        var failure: Error?
        let finished = expectation(description: "request finished")
        URLSession.shared.dataTask(with: request) { data, response, error in
            if let data, let response {
                result = (data, response)
            }
            failure = error
            finished.fulfill()
        }.resume()
        wait(for: [finished], timeout: 15)

        if let failure {
            throw failure
        }
        return try XCTUnwrap(result)
    }
}
