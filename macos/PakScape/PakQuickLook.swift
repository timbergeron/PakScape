import AppKit
import QuickLookUI

final class PakQuickLookItem: NSObject, QLPreviewItem {
    private(set) var previewItemURL: URL?
    let previewItemTitle: String?
    let cleanupURL: URL
    let bspLevelData: Data?

    init(
        url: URL,
        title: String,
        cleanupURL: URL,
        bspLevelData: Data? = nil
    ) {
        self.previewItemURL = url
        self.previewItemTitle = title
        self.cleanupURL = cleanupURL
        self.bspLevelData = bspLevelData
    }

    func updateBspPreview(options: BspLevelPreviewOptions) -> Bool {
        guard let bspLevelData,
              let image = BspLevelPreviewRenderer.renderImage(
                data: bspLevelData,
                options: options
              ),
              let tiff = image.tiffRepresentation,
              let representation = NSBitmapImageRep(data: tiff),
              let png = representation.representation(using: .png, properties: [:]) else {
            return false
        }

        let destination = cleanupURL.appendingPathComponent(
            "preview-\(UUID().uuidString).png"
        )
        do {
            try png.write(to: destination, options: .atomic)
            previewItemURL = destination
            return true
        } catch {
            return false
        }
    }
}

final class PakQuickLook: NSObject, QLPreviewPanelDataSource, QLPreviewPanelDelegate {
    static let shared = PakQuickLook()

    private var items: [PakQuickLookItem] = []
    private var panelKeyMonitor: Any?
    private var currentItemObservation: NSKeyValueObservation?
    private weak var previewControls: NSView?
    private weak var controlsLabel: NSView?
    private var markerButtons: [NSButton] = []

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
        installPreviewControls(in: panel)
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
        currentItemObservation = nil
        previewControls?.removeFromSuperview()
        controlsLabel = nil
        markerButtons = []
        stopMonitoringCloseKeys()
        cleanUpCurrentItems()
    }

    private func installPreviewControls(in panel: QLPreviewPanel) {
        currentItemObservation = nil
        previewControls?.removeFromSuperview()
        markerButtons = [
            markerButton("Armor"),
            markerButton("Megahealth"),
            markerButton("Powerups"),
            markerButton("Weapons"),
            markerButton("Flags"),
        ]

        let label = NSTextField(labelWithString: "Show:")
        label.textColor = .labelColor
        let stack = NSStackView(views: [label] + markerButtons)
        stack.orientation = .horizontal
        stack.alignment = .centerY
        stack.spacing = 10

        let background = NSVisualEffectView()
        background.material = .hudWindow
        background.blendingMode = .withinWindow
        background.state = .active
        background.wantsLayer = true
        background.layer?.cornerRadius = 7
        background.translatesAutoresizingMaskIntoConstraints = false
        stack.translatesAutoresizingMaskIntoConstraints = false
        background.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: background.leadingAnchor, constant: 12),
            stack.trailingAnchor.constraint(equalTo: background.trailingAnchor, constant: -12),
            stack.topAnchor.constraint(equalTo: background.topAnchor, constant: 8),
            stack.bottomAnchor.constraint(equalTo: background.bottomAnchor, constant: -8),
        ])

        guard let contentView = panel.contentView else { return }
        contentView.addSubview(background)
        NSLayoutConstraint.activate([
            background.centerXAnchor.constraint(equalTo: contentView.centerXAnchor),
            background.bottomAnchor.constraint(equalTo: contentView.bottomAnchor, constant: -16),
        ])
        previewControls = background
        controlsLabel = label
        updatePreviewControls(for: panel)
        currentItemObservation = panel.observe(
            \.currentPreviewItemIndex,
            options: [.new]
        ) { [weak self, weak panel] _, _ in
            guard let self, let panel else { return }
            DispatchQueue.main.async {
                self.updatePreviewControls(for: panel)
            }
        }
    }

    private func markerButton(_ title: String) -> NSButton {
        let button = NSButton(checkboxWithTitle: title, target: self, action: #selector(markerOptionChanged(_:)))
        button.state = .off
        return button
    }

    private func updatePreviewControls(for panel: QLPreviewPanel) {
        let index = panel.currentPreviewItemIndex
        let item = items.indices.contains(index) ? items[index] : nil
        let showsMarkers = item?.bspLevelData != nil
        controlsLabel?.isHidden = !showsMarkers
        markerButtons.forEach { $0.isHidden = !showsMarkers }
        previewControls?.isHidden = !showsMarkers
    }

    @objc private func markerOptionChanged(_ sender: NSButton) {
        guard let panel = QLPreviewPanel.shared() else { return }
        let index = panel.currentPreviewItemIndex
        guard items.indices.contains(index) else { return }
        let options = BspLevelPreviewOptions(
            showArmors: markerButtons[0].state == .on,
            showMegaHealth: markerButtons[1].state == .on,
            showPowerups: markerButtons[2].state == .on,
            showMajorWeapons: markerButtons[3].state == .on,
            showFlags: markerButtons[4].state == .on
        )
        guard items[index].updateBspPreview(options: options) else { return }
        panel.reloadData()
        panel.currentPreviewItemIndex = index
    }
}
