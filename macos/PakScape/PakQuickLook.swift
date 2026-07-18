import AppKit
import QuickLookUI

final class PakQuickLookItem: NSObject, QLPreviewItem {
    let previewItemURL: URL?
    let previewItemTitle: String?
    let cleanupURL: URL

    init(url: URL, title: String, cleanupURL: URL) {
        self.previewItemURL = url
        self.previewItemTitle = title
        self.cleanupURL = cleanupURL
    }
}

final class PakQuickLook: NSObject, QLPreviewPanelDataSource, QLPreviewPanelDelegate {
    static let shared = PakQuickLook()

    private var items: [PakQuickLookItem] = []
    private var panelKeyMonitor: Any?

    var isVisible: Bool {
        QLPreviewPanel.shared()?.isVisible == true
    }

    func show(items: [PakQuickLookItem]) {
        guard !items.isEmpty else { return }
        guard let panel = QLPreviewPanel.shared() else {
            stopMonitoringCloseKeys()
            cleanUpCurrentItems()
            cleanUp(items)
            return
        }

        cleanUpCurrentItems()
        self.items = items
        panel.dataSource = self
        panel.delegate = self
        panel.reloadData()
        panel.currentPreviewItemIndex = 0
        monitorCloseKeys(for: panel)
        panel.makeKeyAndOrderFront(nil)
    }

    func hide() {
        guard let panel = QLPreviewPanel.shared() else {
            stopMonitoringCloseKeys()
            cleanUpCurrentItems()
            return
        }

        panel.orderOut(nil)
        releasePreviewResources(from: panel)
    }

    func numberOfPreviewItems(in panel: QLPreviewPanel!) -> Int {
        items.count
    }

    func previewPanel(_ panel: QLPreviewPanel!, previewItemAt index: Int) -> QLPreviewItem! {
        guard items.indices.contains(index) else { return nil }
        return items[index]
    }

    func windowWillClose(_ notification: Notification) {
        guard let panel = notification.object as? QLPreviewPanel else { return }
        releasePreviewResources(from: panel)
    }

    private func cleanUpCurrentItems() {
        let oldItems = items
        items = []
        cleanUp(oldItems)
    }

    private func cleanUp(_ items: [PakQuickLookItem]) {
        let cleanupURLs = Set(items.map(\.cleanupURL))
        for url in cleanupURLs {
            try? FileManager.default.removeItem(at: url)
        }
    }

    private func monitorCloseKeys(for panel: QLPreviewPanel) {
        stopMonitoringCloseKeys()
        panelKeyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self, weak panel] event in
            guard let self, let panel, panel.isVisible, event.window === panel else {
                return event
            }

            let modifiers = event.modifierFlags.intersection([.command, .option, .control, .shift])
            let characters = event.charactersIgnoringModifiers
            guard modifiers.isEmpty, characters == " " || characters == "\u{1b}" else {
                return event
            }

            self.hide()
            return nil
        }
    }

    private func stopMonitoringCloseKeys() {
        guard let panelKeyMonitor else { return }
        NSEvent.removeMonitor(panelKeyMonitor)
        self.panelKeyMonitor = nil
    }

    private func releasePreviewResources(from panel: QLPreviewPanel) {
        panel.dataSource = nil
        panel.delegate = nil
        stopMonitoringCloseKeys()
        cleanUpCurrentItems()
    }
}
