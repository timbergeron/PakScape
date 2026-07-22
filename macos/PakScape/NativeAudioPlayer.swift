import Foundation

enum NativeAudioPlayerError: LocalizedError {
    case unavailable(String)
    case playbackFailed
    case seekFailed

    var errorDescription: String? {
        switch self {
        case .unavailable(let message):
            return message
        case .playbackFailed:
            return "The system audio output is unavailable."
        case .seekFailed:
            return "The audio decoder could not seek in this file."
        }
    }
}

final class NativeAudioPlayer {
    private var handle: OpaquePointer?

    init(data: Data, fileExtension: String) throws {
        var errorBuffer = [CChar](repeating: 0, count: 512)
        handle = errorBuffer.withUnsafeMutableBufferPointer { errorBytes in
            data.withUnsafeBytes { bytes in
                fileExtension.withCString { extensionBytes in
                    pka_player_create(
                        bytes.baseAddress,
                        bytes.count,
                        extensionBytes,
                        errorBytes.baseAddress,
                        errorBytes.count
                    )
                }
            }
        }

        guard handle != nil else {
            let message = String(cString: errorBuffer)
            throw NativeAudioPlayerError.unavailable(
                message.isEmpty ? "The audio decoder could not read this file." : message
            )
        }
    }

    deinit {
        pka_player_destroy(handle)
    }

    var duration: Double {
        pka_player_duration(handle)
    }

    var currentTime: Double {
        pka_player_position(handle)
    }

    var isPlaying: Bool {
        pka_player_is_playing(handle) != 0
    }

    var isFinished: Bool {
        pka_player_is_finished(handle) != 0
    }

    func play() throws {
        guard pka_player_play(handle) == 0 else {
            throw NativeAudioPlayerError.playbackFailed
        }
    }

    func pause() {
        _ = pka_player_pause(handle)
    }

    func seek(to seconds: Double) throws {
        guard pka_player_seek(handle, seconds) == 0 else {
            throw NativeAudioPlayerError.seekFailed
        }
    }
}
