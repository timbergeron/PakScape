import AppKit
import QuickLookUI

final class PakQuickLook: NSObject, QLPreviewPanelDataSource, QLPreviewPanelDelegate {
    static let shared = PakQuickLook()

    private var urls: [URL] = []

    func toggle(urls: [URL]) {
        guard let panel = QLPreviewPanel.shared() else { return }

        if panel.isVisible {
            if self.urls == urls {
                hide()
            } else {
                update(urls: urls)
            }
        } else {
            show(urls: urls)
        }
    }

    func show(urls: [URL]) {
        update(urls: urls)
        QLPreviewPanel.shared()?.makeKeyAndOrderFront(nil)
    }

    func hide() {
        QLPreviewPanel.shared()?.orderOut(nil)
    }

    private func update(urls: [URL]) {
        self.urls = urls
        guard let panel = QLPreviewPanel.shared() else { return }
        panel.dataSource = self
        panel.delegate = self
        panel.reloadData()
        panel.currentPreviewItemIndex = 0
    }

    func numberOfPreviewItems(in panel: QLPreviewPanel!) -> Int {
        urls.count
    }

    func previewPanel(_ panel: QLPreviewPanel!, previewItemAt index: Int) -> QLPreviewItem! {
        urls[index] as NSURL
    }
}
