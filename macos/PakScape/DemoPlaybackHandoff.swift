import Foundation
import Network
import Security

/// One file the player is allowed to fetch: the demo itself, or an archive holding its maps.
struct DemoPlaybackAsset: Equatable {
    let fileName: String
    let data: Data
}

enum DemoPlaybackError: LocalizedError {
    case unavailablePort(reason: String?)
    case tooLarge(limit: Int)

    var errorDescription: String? {
        switch self {
        case let .unavailablePort(reason):
            let base = "PakScape could not open a local port to hand the demo to your browser."
            guard let reason, !reason.isEmpty else { return base }
            return "\(base)\n\n\(reason)"
        case let .tooLarge(limit):
            return "This demo and its archive are larger than the \(limit / (1_024 * 1_024)) MB playback limit."
        }
    }
}

/// Hands a demo to the q1tools web player instead of embedding a game engine.
///
/// The demo never leaves the machine. PakScape serves it from a loopback socket, and the
/// page — which the browser downloads from the player's own origin — fetches it back from
/// `127.0.0.1`. Only paths registered for a handoff are served, so there is no document
/// root to traverse, and every session expires on its own.
enum DemoPlaybackHandoff {
    /// The published player. Kept as one constant so pinning a fork is a one-line change.
    static let playerURL = "https://q1tools.github.io/demo/play/"

    /// Total bytes one handoff may publish, covering the demo and any archive with its maps.
    static let maximumSessionBytes = 256 * 1_024 * 1_024

    /// Long enough to restart playback, short enough that a forgotten window stops listening.
    static let sessionLifetime: TimeInterval = 15 * 60

    /// The origin allowed to read the served assets, derived from ``playerURL``.
    static var playerOrigin: String {
        guard let components = URLComponents(string: playerURL),
              let scheme = components.scheme,
              let host = components.host else { return "null" }
        if let port = components.port {
            return "\(scheme)://\(host):\(port)"
        }
        return "\(scheme)://\(host)"
    }

    /// Publishes `demo` and any `packages`, then returns the URL to open in a browser.
    static func launchURL(
        demo: DemoPlaybackAsset,
        packages: [DemoPlaybackAsset] = [],
        summary: QuakeDemoSummary? = nil,
        server: LoopbackAssetServer = .shared
    ) throws -> URL {
        let assets = [demo] + packages
        let total = assets.reduce(0) { $0 + $1.data.count }
        guard total <= maximumSessionBytes else {
            throw DemoPlaybackError.tooLarge(limit: maximumSessionBytes)
        }

        let sources = try server.publish(assets: assets, lifetime: sessionLifetime)
        return try launchURL(
            demo: demo,
            packages: packages,
            summary: summary,
            sources: sources
        )
    }

    /// The URL half of the handoff, separated so it can be checked without a socket.
    static func launchURL(
        demo: DemoPlaybackAsset,
        packages: [DemoPlaybackAsset],
        summary: QuakeDemoSummary?,
        sources: [URL]
    ) throws -> URL {
        guard sources.count == packages.count + 1 else {
            throw DemoPlaybackError.unavailablePort(reason: nil)
        }

        let maps = summary.map(orderedMaps) ?? []
        var items = [
            URLQueryItem(name: "source", value: sources[0].absoluteString),
            URLQueryItem(name: "file", value: virtualFileName(demo.fileName)),
        ]

        if let title = playerTitle(demo.fileName, summary) {
            items.append(URLQueryItem(name: "title", value: title))
        }
        if !maps.isEmpty {
            items.append(URLQueryItem(name: "maps", value: maps.joined(separator: ",")))
        }
        if let summary, summary.duration > 0 {
            items.append(URLQueryItem(name: "duration", value: String(format: "%.2f", summary.duration)))
        }
        if !packages.isEmpty {
            let descriptors: [[String: Any]] = zip(packages, sources.dropFirst()).map { package, url in
                [
                    "file": virtualFileName(package.fileName),
                    "source": url.absoluteString,
                    "maps": maps,
                ]
            }
            if let json = try? JSONSerialization.data(withJSONObject: descriptors),
               let text = String(data: json, encoding: .utf8) {
                items.append(URLQueryItem(name: "packages", value: text))
            }
        }

        guard var components = URLComponents(string: playerURL) else {
            throw DemoPlaybackError.unavailablePort(reason: nil)
        }
        components.queryItems = items
        guard let url = components.url else {
            throw DemoPlaybackError.unavailablePort(reason: nil)
        }
        return url
    }

    /// The player builds its own virtual paths from these names, so anything that could
    /// climb out of its filesystem is replaced rather than escaped.
    static func virtualFileName(_ name: String) -> String {
        let base = (name as NSString).lastPathComponent
        let mapped = base.map { character -> Character in
            character.isASCII && (character.isLetter || character.isNumber || "._-+".contains(character))
                ? character
                : "_"
        }
        let cleaned = String(mapped).trimmingCharacters(in: CharacterSet(charactersIn: "."))
        return cleaned.isEmpty ? "demo.dem" : cleaned
    }

    private static func orderedMaps(_ summary: QuakeDemoSummary) -> [String] {
        var maps: [String] = []
        var seen = Set<String>()
        for segment in summary.segments where !segment.map.isEmpty {
            if seen.insert(segment.map.lowercased()).inserted {
                maps.append(segment.map)
            }
        }
        return maps
    }

    private static func playerTitle(_ fileName: String, _ summary: QuakeDemoSummary?) -> String? {
        let base = ((fileName as NSString).lastPathComponent as NSString).deletingPathExtension
        guard let levelName = summary?.segments.first(where: { !$0.levelName.isEmpty })?.levelName
        else {
            return base.isEmpty ? nil : base
        }
        return base.isEmpty ? levelName : "\(base) — \(levelName)"
    }
}

/// A minimal loopback HTTP server that only ever answers with assets registered for a
/// handoff. It has no document root, so there is nothing to traverse into.
final class LoopbackAssetServer {
    static let shared = LoopbackAssetServer()

    private struct Entry {
        let data: Data
        let expires: Date
    }

    private let lock = NSLock()
    private let netQueue = DispatchQueue(label: "com.pakscape.demo-handoff", qos: .userInitiated)
    private var listener: NWListener?
    private var entries: [String: Entry] = [:]
    private var port: UInt16 = 0

    var isRunning: Bool {
        lock.lock()
        defer { lock.unlock() }
        return listener != nil
    }

    /// The port currently listening, or 0. Exposed for tests and diagnostics.
    var boundPort: UInt16 {
        lock.lock()
        defer { lock.unlock() }
        return port
    }

    /// Registers `assets` under unguessable paths and returns their loopback URLs.
    func publish(assets: [DemoPlaybackAsset], lifetime: TimeInterval) throws -> [URL] {
        lock.lock()
        purgeExpiredLocked()
        let alreadyRunning = listener != nil
        lock.unlock()

        if !alreadyRunning {
            try start()
        }

        lock.lock()
        defer { lock.unlock() }
        guard port != 0 else { throw DemoPlaybackError.unavailablePort(reason: nil) }

        let expires = Date().addingTimeInterval(lifetime)
        var urls: [URL] = []
        for asset in assets {
            let path = "/\(Self.randomToken())/\(DemoPlaybackHandoff.virtualFileName(asset.fileName))"
            guard let url = URL(string: "http://127.0.0.1:\(port)\(path)") else {
                throw DemoPlaybackError.unavailablePort(reason: nil)
            }
            entries[path] = Entry(data: asset.data, expires: expires)
            urls.append(url)
        }
        return urls
    }

    /// Drops every published asset and stops listening. Safe to call when idle.
    func stop() {
        lock.lock()
        let stopping = listener
        listener = nil
        entries.removeAll()
        port = 0
        lock.unlock()
        stopping?.cancel()
    }

    private func start() throws {
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = NWEndpoint.hostPort(host: .ipv4(.loopback), port: .any)
        parameters.allowLocalEndpointReuse = true

        let listener: NWListener
        do {
            listener = try NWListener(using: parameters)
        } catch {
            throw DemoPlaybackError.unavailablePort(reason: error.localizedDescription)
        }

        // The sandbox denies binding without com.apple.security.network.server, so the
        // failure reason is worth carrying back to the alert rather than swallowing.
        let ready = DispatchSemaphore(value: 0)
        let failure = NSLock()
        var failureReason: String?
        listener.stateUpdateHandler = { state in
            switch state {
            case .ready, .cancelled:
                ready.signal()
            case let .failed(error), let .waiting(error):
                failure.lock()
                failureReason = error.localizedDescription
                failure.unlock()
                ready.signal()
            default:
                break
            }
        }
        listener.newConnectionHandler = { [weak self] connection in
            self?.accept(connection)
        }
        listener.start(queue: netQueue)

        let signalled = ready.wait(timeout: .now() + 5) == .success
        failure.lock()
        let reason = failureReason
        failure.unlock()

        guard signalled, reason == nil,
              let resolved = listener.port?.rawValue, resolved != 0 else {
            listener.cancel()
            throw DemoPlaybackError.unavailablePort(reason: reason)
        }

        lock.lock()
        self.listener = listener
        port = resolved
        lock.unlock()
    }

    private func purgeExpiredLocked() {
        let now = Date()
        entries = entries.filter { $0.value.expires > now }
    }

    private func entry(for path: String) -> Entry? {
        lock.lock()
        defer { lock.unlock() }
        guard let entry = entries[path], entry.expires > Date() else { return nil }
        return entry
    }

    private func accept(_ connection: NWConnection) {
        connection.start(queue: netQueue)
        receive(connection, buffer: Data())
    }

    private func receive(_ connection: NWConnection, buffer: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 8 * 1_024) {
            [weak self] chunk, _, isComplete, error in
            guard let self, error == nil else {
                connection.cancel()
                return
            }

            var accumulated = buffer
            if let chunk {
                accumulated.append(chunk)
            }

            if let headerEnd = Self.headerTerminator(in: accumulated) {
                self.respond(connection, to: accumulated.prefix(headerEnd))
                return
            }
            // A request line plus headers is all this server ever needs to read.
            if accumulated.count > 16 * 1_024 || isComplete {
                connection.cancel()
                return
            }
            self.receive(connection, buffer: accumulated)
        }
    }

    private func respond(_ connection: NWConnection, to header: Data) {
        guard let text = String(data: header, encoding: .isoLatin1),
              let requestLine = text.split(whereSeparator: \.isNewline).first else {
            send(connection, status: "400 Bad Request", body: nil)
            return
        }

        let fields = requestLine.split(separator: " ")
        guard fields.count >= 2 else {
            send(connection, status: "400 Bad Request", body: nil)
            return
        }

        let method = fields[0].uppercased()
        let target = String(fields[1])
        let path = target.split(separator: "?", maxSplits: 1).first.map(String.init) ?? target

        switch method {
        case "OPTIONS":
            send(connection, status: "204 No Content", body: nil)
        case "GET", "HEAD":
            guard let entry = entry(for: path) else {
                send(connection, status: "404 Not Found", body: nil)
                return
            }
            send(
                connection,
                status: "200 OK",
                body: method == "HEAD" ? nil : entry.data,
                contentType: "application/octet-stream",
                contentLength: entry.data.count
            )
        default:
            send(connection, status: "405 Method Not Allowed", body: nil)
        }
    }

    private func send(
        _ connection: NWConnection,
        status: String,
        body: Data?,
        contentType: String? = nil,
        contentLength: Int? = nil
    ) {
        var header = "HTTP/1.1 \(status)\r\n"
        header += "Access-Control-Allow-Origin: \(DemoPlaybackHandoff.playerOrigin)\r\n"
        header += "Access-Control-Allow-Methods: GET, HEAD, OPTIONS\r\n"
        header += "Cache-Control: no-store\r\n"
        header += "X-Content-Type-Options: nosniff\r\n"
        if let contentType {
            header += "Content-Type: \(contentType)\r\n"
        }
        header += "Content-Length: \(contentLength ?? body?.count ?? 0)\r\n"
        header += "Connection: close\r\n\r\n"

        var response = Data(header.utf8)
        if let body {
            response.append(body)
        }
        connection.send(content: response, completion: .contentProcessed { _ in
            connection.cancel()
        })
    }

    private static func headerTerminator(in data: Data) -> Int? {
        guard let range = data.range(of: Data("\r\n\r\n".utf8)) else { return nil }
        return range.upperBound - data.startIndex
    }

    private static func randomToken() -> String {
        var bytes = [UInt8](repeating: 0, count: 16)
        if SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes) != errSecSuccess {
            bytes = (0 ..< 16).map { _ in UInt8.random(in: UInt8.min ... UInt8.max) }
        }
        return bytes.map { String(format: "%02x", $0) }.joined()
    }
}
