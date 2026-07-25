import AppKit

struct ModelPreviewItem {
    let name: String
    let data: Data
    let fileExtension: String
    let resolver: QuakeModelTextureResolver?
    /// Shown instead of the viewer when a model turns out to be unreadable.
    let fallbackImage: () -> NSImage?
}

/// Presents the interactive model panel. Quick Look cannot host a live view, so
/// models get their own window rather than a rendered still.
@MainActor
final class ModelPreviewPresenter {
    static let shared = ModelPreviewPresenter()

    private var window: NSPanel?
    private var controller: ModelPreviewController?

    var isVisible: Bool {
        window?.isVisible == true
    }

    func show(items: [ModelPreviewItem]) {
        guard !items.isEmpty else { return }

        let targetScreen = (NSApp.keyWindow ?? NSApp.mainWindow)?.screen ?? NSScreen.main
        let controller = ModelPreviewController(items: items) { [weak self] in
            self?.hide()
        }
        self.controller = controller

        let panel = window ?? makeWindow()
        window = panel
        panel.contentViewController = controller
        panel.title = items[0].name
        centerWindow(panel, on: targetScreen)
        panel.makeKeyAndOrderFront(nil)
        panel.makeFirstResponder(controller.renderView)
        NSApp.activate(ignoringOtherApps: true)
    }

    func hide() {
        window?.orderOut(nil)
        window?.contentViewController = nil
        controller = nil
    }

    private func makeWindow() -> NSPanel {
        let panel = NSPanel(
            contentRect: NSRect(x: 0, y: 0, width: 860, height: 640),
            styleMask: [.titled, .closable, .resizable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )

        panel.titleVisibility = .hidden
        panel.titlebarAppearsTransparent = true
        panel.standardWindowButton(.miniaturizeButton)?.isHidden = true
        panel.isReleasedWhenClosed = false
        panel.minSize = NSSize(width: 520, height: 380)
        /* Dragging has to orbit the model, so the panel moves by its title bar only. */
        panel.isMovableByWindowBackground = false
        return panel
    }

    private func centerWindow(_ window: NSWindow, on screen: NSScreen?) {
        guard let screen else {
            window.center()
            return
        }

        let frame = window.frame
        let visibleFrame = screen.visibleFrame
        let origin = NSPoint(
            x: visibleFrame.midX - frame.size.width / 2,
            y: visibleFrame.midY - frame.size.height / 2
        )
        window.setFrame(NSRect(origin: origin, size: frame.size), display: true)
    }
}

/// The panel's chrome: title, navigation between selected models, skin picker,
/// and the render view that owns the camera.
@MainActor
final class ModelPreviewController: NSViewController {
    private let items: [ModelPreviewItem]
    private let onClose: () -> Void
    private var index = 0

    private let titleLabel = NSTextField(labelWithString: "")
    private let subtitleLabel = NSTextField(labelWithString: "")
    private let positionLabel = NSTextField(labelWithString: "")
    private let previousButton = NSButton()
    private let nextButton = NSButton()
    private let skinPicker = NSPopUpButton()
    private let resetButton = NSButton()

    let renderView = ModelRenderView()

    init(items: [ModelPreviewItem], onClose: @escaping () -> Void) {
        self.items = items
        self.onClose = onClose
        super.init(nibName: nil, bundle: nil)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    override func loadView() {
        let container = NSView(frame: NSRect(x: 0, y: 0, width: 860, height: 640))

        titleLabel.font = .systemFont(ofSize: 19, weight: .semibold)
        titleLabel.lineBreakMode = .byTruncatingMiddle
        subtitleLabel.font = .systemFont(ofSize: 12)
        subtitleLabel.textColor = .secondaryLabelColor
        subtitleLabel.lineBreakMode = .byTruncatingTail

        let header = NSStackView(views: [titleLabel, subtitleLabel])
        header.orientation = .vertical
        header.alignment = .leading
        header.spacing = 3
        /* Stack views only hug their content weakly, so without this the header
           stretches and the model is left with a sliver of the window. */
        header.setHuggingPriority(.required, for: .vertical)

        configure(button: previousButton, title: "←", tooltip: "Previous item")
        previousButton.target = self
        previousButton.action = #selector(showPrevious)
        configure(button: nextButton, title: "→", tooltip: "Next item")
        nextButton.target = self
        nextButton.action = #selector(showNext)

        skinPicker.translatesAutoresizingMaskIntoConstraints = false
        skinPicker.target = self
        skinPicker.action = #selector(skinChanged)
        skinPicker.toolTip = "Skin"

        resetButton.translatesAutoresizingMaskIntoConstraints = false
        resetButton.bezelStyle = .rounded
        resetButton.title = "Reset view"
        resetButton.target = self
        resetButton.action = #selector(resetView)
        resetButton.toolTip = "Frame the model again (R)"

        positionLabel.font = .systemFont(ofSize: 12)
        positionLabel.textColor = .secondaryLabelColor

        let closeHint = NSTextField(labelWithString: "Space or Esc to close")
        closeHint.font = .systemFont(ofSize: 12)
        closeHint.textColor = .secondaryLabelColor

        let navigation = NSStackView(views: [previousButton, nextButton])
        navigation.orientation = .horizontal
        navigation.spacing = 4

        let footer = NSStackView(views: [
            navigation, skinPicker, resetButton, positionLabel, closeHint,
        ])
        footer.orientation = .horizontal
        footer.spacing = 10
        footer.setHuggingPriority(.defaultLow, for: .horizontal)
        footer.setHuggingPriority(.required, for: .vertical)
        footer.setCustomSpacing(18, after: resetButton)

        let surface = NSView()
        surface.translatesAutoresizingMaskIntoConstraints = false
        surface.wantsLayer = true
        surface.layer?.cornerRadius = 10
        surface.layer?.masksToBounds = true
        surface.layer?.borderWidth = 1
        surface.layer?.borderColor = NSColor.separatorColor.cgColor

        renderView.translatesAutoresizingMaskIntoConstraints = false
        surface.addSubview(renderView)

        for view in [header, surface, footer] {
            view.translatesAutoresizingMaskIntoConstraints = false
            container.addSubview(view)
        }

        NSLayoutConstraint.activate([
            renderView.leadingAnchor.constraint(equalTo: surface.leadingAnchor),
            renderView.trailingAnchor.constraint(equalTo: surface.trailingAnchor),
            renderView.topAnchor.constraint(equalTo: surface.topAnchor),
            renderView.bottomAnchor.constraint(equalTo: surface.bottomAnchor),

            header.leadingAnchor.constraint(equalTo: container.leadingAnchor, constant: 20),
            header.trailingAnchor.constraint(equalTo: container.trailingAnchor, constant: -20),
            header.topAnchor.constraint(equalTo: container.topAnchor, constant: 30),

            surface.leadingAnchor.constraint(equalTo: container.leadingAnchor, constant: 20),
            surface.trailingAnchor.constraint(equalTo: container.trailingAnchor, constant: -20),
            surface.topAnchor.constraint(equalTo: header.bottomAnchor, constant: 12),

            footer.leadingAnchor.constraint(equalTo: container.leadingAnchor, constant: 20),
            footer.trailingAnchor.constraint(lessThanOrEqualTo: container.trailingAnchor, constant: -20),
            footer.topAnchor.constraint(equalTo: surface.bottomAnchor, constant: 12),
            footer.bottomAnchor.constraint(equalTo: container.bottomAnchor, constant: -18),
        ])

        view = container
    }

    override func viewDidLoad() {
        super.viewDidLoad()
        showCurrentItem()
    }

    private func configure(button: NSButton, title: String, tooltip: String) {
        button.translatesAutoresizingMaskIntoConstraints = false
        button.bezelStyle = .rounded
        button.title = title
        button.toolTip = tooltip
    }

    private func showCurrentItem() {
        let item = items[index]
        titleLabel.stringValue = item.name
        positionLabel.stringValue = items.count == 1
            ? ""
            : "\(index + 1) of \(items.count)"
        previousButton.isEnabled = items.count > 1
        nextButton.isEnabled = items.count > 1
        view.window?.title = item.name

        do {
            let viewer = try QuakeModelViewer(
                data: item.data,
                fileExtension: item.fileExtension,
                resolver: item.resolver
            )
            renderView.attach(viewer: viewer)
            subtitleLabel.stringValue = viewer.statusLine

            skinPicker.removeAllItems()
            if viewer.skinCount > 1 {
                for skin in 0 ..< viewer.skinCount {
                    skinPicker.addItem(withTitle: "Skin \(skin + 1)")
                }
                skinPicker.selectItem(at: 0)
                skinPicker.isHidden = false
            } else {
                skinPicker.isHidden = true
            }
            resetButton.isEnabled = true
        } catch {
            /* Truncated models still have a skin worth showing, as on the other editions. */
            renderView.attach(viewer: nil, fallbackImage: item.fallbackImage())
            subtitleLabel.stringValue = error.localizedDescription
            skinPicker.isHidden = true
            resetButton.isEnabled = false
        }
    }

    private func move(by delta: Int) {
        guard items.count > 1 else { return }
        index = (index + delta + items.count) % items.count
        showCurrentItem()
        view.window?.makeFirstResponder(renderView)
    }

    @objc private func showPrevious() {
        move(by: -1)
    }

    @objc private func showNext() {
        move(by: 1)
    }

    @objc private func resetView() {
        renderView.viewer?.reset()
    }

    @objc private func skinChanged() {
        guard let viewer = renderView.viewer, skinPicker.indexOfSelectedItem >= 0 else { return }
        if viewer.selectSkin(skinPicker.indexOfSelectedItem) {
            subtitleLabel.stringValue = viewer.statusLine
            renderView.requestRedraw()
        }
    }

    /// Called by the render view, which is the first responder while the panel is up.
    func handleKey(_ event: NSEvent) -> Bool {
        let modifiers = event.modifierFlags.intersection([.command, .option, .control])
        guard modifiers.isEmpty else { return false }

        switch event.charactersIgnoringModifiers {
        case " ", "\u{1b}":
            onClose()
            return true
        case "r", "R":
            renderView.viewer?.reset()
            return true
        case "+", "=":
            renderView.viewer?.nudge(.zoomIn)
            return true
        case "-", "_":
            renderView.viewer?.nudge(.zoomOut)
            return true
        default:
            break
        }

        switch Int(event.keyCode) {
        case 123: renderView.viewer?.nudge(.left)
        case 124: renderView.viewer?.nudge(.right)
        case 126: renderView.viewer?.nudge(.up)
        case 125: renderView.viewer?.nudge(.down)
        case 121: move(by: 1)     // page down
        case 116: move(by: -1)    // page up
        default: return false
        }
        return true
    }
}

/// Overlays that sit on the model but must not swallow a drag.
private final class PassThroughView: NSView {
    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    override var mouseDownCanMoveWindow: Bool { false }
}

/// Draws the software-rendered frames and turns pointer input into camera moves.
@MainActor
final class ModelRenderView: NSView {
    private(set) var viewer: QuakeModelViewer?
    private var context: CGContext?
    private var image: CGImage?
    private var timer: Timer?
    private var lastFrame = Date()
    private var isPanning = false
    private let hintLabel = NSTextField(labelWithString: "Drag to orbit • Scroll to zoom • R to reset")
    private let hintBackground = PassThroughView()
    private let fallbackImageView = NSImageView()

    /* The renderer runs on the CPU, so the buffer is capped and stretched to fit. */
    private let maximumRenderPixels = 1_400_000

    override init(frame frameRect: NSRect) {
        super.init(frame: frameRect)
        setUpHint()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    /*
     * The view stays in AppKit's default bottom-up space: the renderer emits
     * top-down rows, which is what Core Graphics expects, and flipping the view
     * would draw every frame upside down.
     */
    override var acceptsFirstResponder: Bool { true }

    /* Dragging orbits the camera, so it must not start a window move. */
    override var mouseDownCanMoveWindow: Bool { false }

    private func setUpHint() {
        fallbackImageView.translatesAutoresizingMaskIntoConstraints = false
        fallbackImageView.imageScaling = .scaleProportionallyUpOrDown
        fallbackImageView.isHidden = true
        addSubview(fallbackImageView)

        hintBackground.translatesAutoresizingMaskIntoConstraints = false
        hintBackground.wantsLayer = true
        hintBackground.layer?.cornerRadius = 13
        hintBackground.layer?.backgroundColor = NSColor.black.withAlphaComponent(0.55).cgColor
        addSubview(hintBackground)

        hintLabel.translatesAutoresizingMaskIntoConstraints = false
        hintLabel.font = .systemFont(ofSize: 12)
        hintLabel.textColor = .white
        hintBackground.addSubview(hintLabel)

        NSLayoutConstraint.activate([
            hintLabel.leadingAnchor.constraint(equalTo: hintBackground.leadingAnchor, constant: 12),
            hintLabel.trailingAnchor.constraint(equalTo: hintBackground.trailingAnchor, constant: -12),
            hintLabel.topAnchor.constraint(equalTo: hintBackground.topAnchor, constant: 6),
            hintLabel.bottomAnchor.constraint(equalTo: hintBackground.bottomAnchor, constant: -6),
            hintBackground.centerXAnchor.constraint(equalTo: centerXAnchor),
            hintBackground.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -18),

            fallbackImageView.leadingAnchor.constraint(equalTo: leadingAnchor, constant: 16),
            fallbackImageView.trailingAnchor.constraint(equalTo: trailingAnchor, constant: -16),
            fallbackImageView.topAnchor.constraint(equalTo: topAnchor, constant: 16),
            fallbackImageView.bottomAnchor.constraint(equalTo: bottomAnchor, constant: -16),
        ])
    }

    func attach(viewer: QuakeModelViewer?, fallbackImage: NSImage? = nil) {
        self.viewer = viewer
        context = nil
        image = nil
        viewer?.setDarkBackground(effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua)
        hintBackground.isHidden = viewer == nil
        fallbackImageView.image = fallbackImage
        fallbackImageView.isHidden = viewer != nil || fallbackImage == nil
        needsDisplay = true
        startTimer()
        requestRedraw()
    }

    func requestRedraw() {
        renderFrame()
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        startTimer()
    }

    private func startTimer() {
        timer?.invalidate()
        timer = nil
        guard viewer != nil, window != nil else { return }

        lastFrame = Date()
        let timer = Timer(timeInterval: 1.0 / 60.0, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.step()
            }
        }
        RunLoop.main.add(timer, forMode: .common)
        self.timer = timer
    }

    private func step() {
        guard let viewer, window != nil else { return }

        let now = Date()
        let elapsed = now.timeIntervalSince(lastFrame)
        lastFrame = now

        if viewer.advance(elapsed) {
            renderFrame()
        }

        let showsHint = viewer.showsInteractionPrompt
        if hintBackground.isHidden != !showsHint {
            hintBackground.isHidden = !showsHint
        }
    }

    private func renderFrame() {
        guard let viewer, bounds.width > 1, bounds.height > 1 else { return }

        let scale = window?.backingScaleFactor ?? 2
        var width = max(16, Int((bounds.width * scale).rounded()))
        var height = max(16, Int((bounds.height * scale).rounded()))
        let total = width * height
        if total > maximumRenderPixels {
            let reduction = (Double(maximumRenderPixels) / Double(total)).squareRoot()
            width = max(16, Int(Double(width) * reduction))
            height = max(16, Int(Double(height) * reduction))
        }

        if context?.width != width || context?.height != height {
            context = CGContext(
                data: nil,
                width: width,
                height: height,
                bitsPerComponent: 8,
                bytesPerRow: width * 4,
                space: CGColorSpaceCreateDeviceRGB(),
                bitmapInfo: CGImageAlphaInfo.noneSkipFirst.rawValue
                    | CGBitmapInfo.byteOrder32Little.rawValue
            )
        }

        guard let context, let buffer = context.data else { return }
        guard viewer.render(
            into: buffer,
            width: width,
            height: height,
            stride: context.bytesPerRow
        ) else {
            return
        }

        image = context.makeImage()
        needsDisplay = true
    }

    override func layout() {
        super.layout()
        renderFrame()
    }

    override func viewDidChangeEffectiveAppearance() {
        super.viewDidChangeEffectiveAppearance()
        viewer?.setDarkBackground(
            effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
        )
        renderFrame()
    }

    override func draw(_ dirtyRect: NSRect) {
        guard let image, let context = NSGraphicsContext.current?.cgContext else {
            NSColor.windowBackgroundColor.setFill()
            dirtyRect.fill()
            return
        }

        context.interpolationQuality = .high
        context.draw(image, in: bounds)
    }

    /// Input arrives in points; the camera works in rendered pixels.
    private var inputScale: CGFloat {
        guard bounds.width > 0, let context else { return 1 }
        return CGFloat(context.width) / bounds.width
    }

    override func mouseDown(with event: NSEvent) {
        window?.makeFirstResponder(self)
        guard viewer != nil else { return }

        if event.clickCount == 2 {
            viewer?.reset()
            return
        }

        isPanning = event.modifierFlags.contains(.shift)
        viewer?.beginInteraction()
    }

    override func mouseDragged(with event: NSEvent) {
        guard let viewer else { return }
        let dx = event.deltaX * inputScale
        let dy = event.deltaY * inputScale
        if isPanning {
            viewer.pan(dx: dx, dy: dy)
        } else {
            viewer.orbit(dx: dx, dy: dy)
        }
    }

    override func mouseUp(with event: NSEvent) {
        isPanning = false
        viewer?.endInteraction()
    }

    override func rightMouseDown(with event: NSEvent) {
        viewer?.beginInteraction()
    }

    override func rightMouseDragged(with event: NSEvent) {
        viewer?.pan(dx: event.deltaX * inputScale, dy: event.deltaY * inputScale)
    }

    override func rightMouseUp(with event: NSEvent) {
        viewer?.endInteraction()
    }

    override func scrollWheel(with event: NSEvent) {
        guard let viewer else { return }
        let steps = event.hasPreciseScrollingDeltas
            ? event.scrollingDeltaY / 24.0
            : event.scrollingDeltaY / 3.0
        viewer.zoom(steps: steps)
    }

    override func magnify(with event: NSEvent) {
        guard let viewer else { return }
        if event.phase == .began {
            viewer.beginInteraction()
        }
        viewer.zoom(steps: event.magnification * 6.0)
        if event.phase == .ended || event.phase == .cancelled {
            viewer.endInteraction()
        }
    }

    override func keyDown(with event: NSEvent) {
        if let controller = enclosingController, controller.handleKey(event) {
            return
        }
        super.keyDown(with: event)
    }

    private var enclosingController: ModelPreviewController? {
        var responder: NSResponder? = nextResponder
        while let current = responder {
            if let controller = current as? ModelPreviewController {
                return controller
            }
            responder = current.nextResponder
        }
        return nil
    }
}
