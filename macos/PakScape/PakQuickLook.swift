import AppKit
import QuickLookUI

final class PakQuickLookItem: NSObject, QLPreviewItem {
    /// Rendered at a multiple of the canvas size so the map stays sharp when it
    /// is zoomed in.
    private static let previewPixelScale = 4

    private(set) var previewItemURL: URL?
    let previewItemTitle: String?
    let cleanupURL: URL
    let bspLevelData: Data?
    let viewSkybox: (() -> Void)?

    private var renderedOptions: BspLevelPreviewOptions?
    private var renderedImage: NSImage?
    private var cachedAvailableMarkers: BspLevelPreviewOptions?

    init(
        url: URL,
        title: String,
        cleanupURL: URL,
        bspLevelData: Data? = nil,
        viewSkybox: (() -> Void)? = nil
    ) {
        self.previewItemURL = url
        self.previewItemTitle = title
        self.cleanupURL = cleanupURL
        self.bspLevelData = bspLevelData
        self.viewSkybox = viewSkybox
    }

    /// The marker groups this level places items for. Options outside this set
    /// are no-ops, so the preview does not offer them.
    var availableMarkers: BspLevelPreviewOptions {
        if let cachedAvailableMarkers {
            return cachedAvailableMarkers
        }

        guard let bspLevelData else { return .geometryOnly }
        let available = BspLevelPreviewRenderer.availableMapMarkers(data: bspLevelData)
        cachedAvailableMarkers = available
        return available
    }

    func bspLevelImage(options: BspLevelPreviewOptions) -> NSImage? {
        if renderedOptions == options, let renderedImage {
            return renderedImage
        }

        guard let bspLevelData,
              let image = BspLevelPreviewRenderer.renderImage(
                data: bspLevelData,
                options: options,
                pixelScale: Self.previewPixelScale
              ) else {
            return nil
        }

        renderedOptions = options
        renderedImage = image
        return image
    }

    /// Releases the cached full resolution render once another item is shown.
    func discardRenderedImage() {
        renderedOptions = nil
        renderedImage = nil
    }

    func updateBspPreview(options: BspLevelPreviewOptions) -> NSImage? {
        guard let image = bspLevelImage(options: options),
              let tiff = image.tiffRepresentation,
              let representation = NSBitmapImageRep(data: tiff),
              let png = representation.representation(using: .png, properties: [:]) else {
            return nil
        }

        let destination = cleanupURL.appendingPathComponent(
            "preview-\(UUID().uuidString).png"
        )
        do {
            try png.write(to: destination, options: .atomic)
            previewItemURL = destination
            return image
        } catch {
            return nil
        }
    }
}

final class PakQuickLook: NSObject, QLPreviewPanelDataSource, QLPreviewPanelDelegate {
    static let shared = PakQuickLook()

    /// Title bar, marker controls and the margins around the map.
    private static let levelPanelChromeHeight: CGFloat = 134
    private static let maximumLevelPanelSide: CGFloat = 1_200

    private var items: [PakQuickLookItem] = []
    private var panelKeyMonitor: Any?
    private var currentItemObservation: NSKeyValueObservation?
    private weak var previewControls: NSView?
    private weak var controlsLabel: NSView?
    private var markerButtons: [NSButton] = []
    private weak var skyboxButton: NSButton?
    private weak var mapView: BspLevelMapView?
    private weak var displayedMapItem: PakQuickLookItem?
    private weak var cachedPreviewArea: NSView?
    private var hasExpandedLevelPanel = false

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
        expandPanelForLevelPreview(panel)
    }

    /// Quick Look opens at a size meant for documents, which leaves a level map
    /// smaller than it needs to be. The first level preview of the session gets
    /// a taller panel; it is never shrunk, so a size the user picked is kept.
    private func expandPanelForLevelPreview(_ panel: QLPreviewPanel) {
        guard !hasExpandedLevelPanel,
              items.contains(where: { $0.bspLevelData != nil }),
              let visible = (panel.screen ?? NSScreen.main)?.visibleFrame else { return }
        hasExpandedLevelPanel = true

        let height = min(Self.maximumLevelPanelSide, visible.height * 0.95)
        let width = min(
            Self.maximumLevelPanelSide,
            visible.width * 0.9,
            // The map is square, so only enough width to match its height pays off.
            max(panel.frame.width, height - Self.levelPanelChromeHeight)
        )
        let size = NSSize(
            width: max(width, panel.frame.width),
            height: max(height, panel.frame.height)
        )
        guard size != panel.frame.size else { return }

        panel.setFrame(
            NSRect(
                x: visible.midX - (size.width / 2),
                y: visible.midY - (size.height / 2),
                width: size.width,
                height: size.height
            ),
            display: true
        )
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
        mapView?.removeFromSuperview()
        controlsLabel = nil
        markerButtons = []
        skyboxButton = nil
        mapView = nil
        displayedMapItem = nil
        restoreQuickLookPreviewArea()
        stopMonitoringCloseKeys()
        cleanUpCurrentItems()
    }

    private func installPreviewControls(in panel: QLPreviewPanel) {
        currentItemObservation = nil
        previewControls?.removeFromSuperview()
        mapView?.removeFromSuperview()
        mapView = nil
        displayedMapItem = nil
        restoreQuickLookPreviewArea()
        markerButtons = [
            markerButton("Armor"),
            markerButton("Megahealth"),
            markerButton("Powerups"),
            markerButton("Weapons"),
            markerButton("Flags"),
        ]

        let label = NSTextField(labelWithString: "Show:")
        label.textColor = .labelColor
        let viewSkybox = NSButton(
            title: "View Skybox",
            target: self,
            action: #selector(viewSkyboxFromQuickLook(_:))
        )
        viewSkybox.bezelStyle = .rounded
        let stack = NSStackView(views: [label] + markerButtons + [viewSkybox])
        stack.orientation = .horizontal
        stack.alignment = .centerY
        stack.spacing = 12

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

        // Sits below the controls so the map can be laid out clear of them, and
        // in the panel's content area so the Quick Look toolbar stays visible.
        let map = BspLevelMapView()
        map.translatesAutoresizingMaskIntoConstraints = false
        map.controlsView = background
        map.coverageProvider = { [weak self, weak panel] () -> NSView? in
            guard let self, let panel else { return nil }
            return self.quickLookPreviewArea(in: panel)
        }
        map.isHidden = true
        contentView.addSubview(map)
        NSLayoutConstraint.activate([
            map.leadingAnchor.constraint(equalTo: contentView.leadingAnchor),
            map.trailingAnchor.constraint(equalTo: contentView.trailingAnchor),
            map.topAnchor.constraint(equalTo: contentView.topAnchor),
            map.bottomAnchor.constraint(equalTo: contentView.bottomAnchor),
        ])

        contentView.addSubview(background)
        NSLayoutConstraint.activate([
            background.centerXAnchor.constraint(equalTo: contentView.centerXAnchor),
            background.bottomAnchor.constraint(equalTo: contentView.bottomAnchor, constant: -16),
        ])
        previewControls = background
        controlsLabel = label
        skyboxButton = viewSkybox
        mapView = map
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

    /// Quick Look lays its preview out below the panel's title bar. The map is
    /// drawn over that area only, so the title bar controls stay usable.
    private func quickLookPreviewArea(in panel: QLPreviewPanel) -> NSView? {
        if let cachedPreviewArea, cachedPreviewArea.window === panel {
            return cachedPreviewArea
        }
        guard let contentView = panel.contentView else { return nil }

        let match = firstSubview(in: contentView) { view in
            let name = String(describing: type(of: view))
            return name.hasPrefix("QL") && name.hasSuffix("PreviewView")
        }
        cachedPreviewArea = match
        return match
    }

    private func restoreQuickLookPreviewArea() {
        cachedPreviewArea?.isHidden = false
        cachedPreviewArea = nil
    }

    private func firstSubview(in view: NSView, matching matches: (NSView) -> Bool) -> NSView? {
        for subview in view.subviews {
            if matches(subview) {
                return subview
            }
            if let match = firstSubview(in: subview, matching: matches) {
                return match
            }
        }
        return nil
    }

    private func markerButton(_ title: String) -> NSButton {
        let button = NSButton(checkboxWithTitle: title, target: self, action: #selector(markerOptionChanged(_:)))
        button.state = .off
        return button
    }

    private func updatePreviewControls(for panel: QLPreviewPanel) {
        let index = panel.currentPreviewItemIndex
        let item = items.indices.contains(index) ? items[index] : nil
        let showsMap = item?.bspLevelData != nil
        let availableMarkers = item?.availableMarkers ?? .geometryOnly
        let showsMarkers = availableMarkers != .geometryOnly
        let showsSkybox = item?.viewSkybox != nil
        controlsLabel?.isHidden = !showsMarkers
        for (button, category) in zip(markerButtons, BspLevelPreviewOptions.markerCategories) {
            button.isHidden = !availableMarkers[keyPath: category]
        }
        skyboxButton?.isHidden = !showsSkybox
        previewControls?.isHidden = !showsMarkers && !showsSkybox

        guard let mapView else { return }
        let mapImage = showsMap ? item?.bspLevelImage(options: markerOptions()) : nil
        // Only start over at the fit scale when a different level is shown; a
        // reload of the same level keeps whatever the user zoomed to.
        let isNewLevel = mapImage == nil || item !== displayedMapItem
        if item !== displayedMapItem {
            displayedMapItem?.discardRenderedImage()
        }
        displayedMapItem = mapImage == nil ? nil : item
        mapView.isHidden = mapImage == nil
        mapView.setImage(mapImage, resettingZoom: isNewLevel)
        // Quick Look would otherwise draw the same map stretched to fill the
        // panel behind this view.
        quickLookPreviewArea(in: panel)?.isHidden = mapImage != nil
    }

    private func markerOptions() -> BspLevelPreviewOptions {
        var options = BspLevelPreviewOptions.geometryOnly
        for (button, category) in zip(markerButtons, BspLevelPreviewOptions.markerCategories)
        where button.state == .on {
            options[keyPath: category] = true
        }
        return options
    }

    @objc private func markerOptionChanged(_ sender: NSButton) {
        guard let panel = QLPreviewPanel.shared() else { return }
        let index = panel.currentPreviewItemIndex
        guard items.indices.contains(index) else { return }
        guard let image = items[index].updateBspPreview(options: markerOptions()) else { return }
        // Keep the current zoom so toggling markers does not jump the view.
        mapView?.setImage(image, resettingZoom: false)
        panel.reloadData()
        panel.currentPreviewItemIndex = index
    }

    @objc private func viewSkyboxFromQuickLook(_ sender: NSButton) {
        guard let panel = QLPreviewPanel.shared() else { return }
        let index = panel.currentPreviewItemIndex
        guard items.indices.contains(index), let action = items[index].viewSkybox else { return }
        action()
    }
}
