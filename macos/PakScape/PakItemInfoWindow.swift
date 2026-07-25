import AppKit
import SwiftUI

/// Escape closes a Get Info window, the way it dismisses the other panels in the app.
final class PakItemInfoWindow: NSWindow {
    override func cancelOperation(_ sender: Any?) {
        close()
    }
}

/*
 * Opens Get Info in its own window rather than a sheet, so the archive stays usable
 * behind it and several items can be compared side by side. One window per item: a
 * second Get Info on the same file brings the window it already has to the front.
 */
@MainActor
final class PakItemInfoPresenter: NSObject {
    static let shared = PakItemInfoPresenter()

    private var windows: [UUID: NSWindow] = [:]
    private var lastTopLeft: CGPoint?

    func show(info: PakItemInfo, viewModel: PakViewModel) {
        if let existing = windows[info.id] {
            existing.makeKeyAndOrderFront(nil)
            return
        }

        let window = PakItemInfoWindow(
            contentRect: NSRect(x: 0, y: 0, width: 460, height: 320),
            styleMask: [.titled, .closable],
            backing: .buffered,
            defer: false
        )
        window.title = "\(info.name) Info"
        window.isReleasedWhenClosed = false
        window.delegate = self
        /* Archive windows stay out of the Windows menu, so their Get Info windows do too. */
        window.isExcludedFromWindowsMenu = true
        window.contentViewController = NSHostingController(
            rootView: PakItemInfoView(info: info, viewModel: viewModel) { [weak window] in
                window?.close()
            }
        )
        if let content = window.contentViewController?.view {
            content.layoutSubtreeIfNeeded()
            window.setContentSize(content.fittingSize)
        }

        place(window, besides: NSApp.keyWindow ?? NSApp.mainWindow)
        windows[info.id] = window
        window.makeKeyAndOrderFront(nil)
    }

    /// Closes the windows of items the archive no longer holds, as Finder does on a delete.
    func closeWindows(missingFrom root: PakNode) {
        guard !windows.isEmpty else { return }

        var live: Set<UUID> = []
        var pending = [root]
        while let node = pending.popLast() {
            live.insert(node.id)
            pending.append(contentsOf: node.children ?? [])
        }

        for (id, window) in windows where !live.contains(id) {
            window.close()
        }
    }

    private func place(_ window: NSWindow, besides parent: NSWindow?) {
        let screen = parent?.screen ?? NSScreen.main
        guard let visibleFrame = screen?.visibleFrame else { return }

        let size = window.frame.size
        let base = PakItemInfoPlacement.base(
            parentFrame: parent?.frame,
            windowSize: size,
            visibleFrame: visibleFrame
        )
        let topLeft = PakItemInfoPlacement.topLeft(
            base: base,
            previous: lastTopLeft,
            windowSize: size,
            visibleFrame: visibleFrame
        )
        lastTopLeft = topLeft
        window.setFrameTopLeftPoint(NSPoint(x: topLeft.x, y: topLeft.y))
    }
}

extension PakItemInfoPresenter: NSWindowDelegate {
    func windowWillClose(_ notification: Notification) {
        guard let closing = notification.object as? NSWindow else { return }

        windows = windows.filter { $0.value !== closing }
        /* The next window starts the cascade over once the last one has gone. */
        if windows.isEmpty {
            lastTopLeft = nil
        }
    }
}
