import AppKit
import SceneKit

struct SkyboxPreviewItem {
    let name: String
    /// SceneKit order: +X, -X, +Y, -Y, +Z, -Z.
    let images: [NSImage]
}

final class SkyboxPreviewPresenter: NSObject, NSWindowDelegate {
    static let shared = SkyboxPreviewPresenter()

    private var windowController: NSWindowController?

    var isVisible: Bool {
        windowController?.window?.isVisible == true
    }

    func show(item: SkyboxPreviewItem) {
        hide()

        let content = SkyboxPreviewController(item: item)
        let window = NSWindow(contentViewController: content)
        window.title = "\(item.name) — Skybox Preview"
        window.setContentSize(NSSize(width: 900, height: 620))
        window.minSize = NSSize(width: 560, height: 400)
        window.styleMask = [.titled, .closable, .miniaturizable, .resizable]
        window.isReleasedWhenClosed = false
        window.delegate = self
        window.center()

        let controller = NSWindowController(window: window)
        windowController = controller
        controller.showWindow(nil)
        window.makeKeyAndOrderFront(nil)
    }

    func hide() {
        windowController?.close()
        windowController = nil
    }

    func windowWillClose(_ notification: Notification) {
        windowController = nil
    }
}

final class SkyboxPreviewController: NSViewController {
    private let item: SkyboxPreviewItem
    private let sceneView = SkyboxSceneView()

    init(item: SkyboxPreviewItem) {
        self.item = item
        super.init(nibName: nil, bundle: nil)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    override func loadView() {
        let container = NSView()
        container.wantsLayer = true
        container.layer?.backgroundColor = NSColor.windowBackgroundColor.cgColor

        let title = NSTextField(labelWithString: item.name)
        title.font = .systemFont(ofSize: 18, weight: .semibold)
        title.lineBreakMode = .byTruncatingMiddle

        let subtitle = NSTextField(labelWithString: "Drag to look around • Scroll to zoom")
        subtitle.font = .systemFont(ofSize: 12)
        subtitle.textColor = .secondaryLabelColor

        let labels = NSStackView(views: [title, subtitle])
        labels.orientation = .vertical
        labels.alignment = .leading
        labels.spacing = 3

        let resetButton = NSButton(title: "Reset View", target: self, action: #selector(resetView))
        resetButton.bezelStyle = .rounded

        let closeHint = NSTextField(labelWithString: "Space or Esc to close")
        closeHint.font = .systemFont(ofSize: 12)
        closeHint.textColor = .secondaryLabelColor

        let header = NSStackView(views: [labels, NSView(), resetButton])
        header.orientation = .horizontal
        header.alignment = .centerY
        header.spacing = 12

        sceneView.configure(images: item.images)
        sceneView.wantsLayer = true
        sceneView.layer?.cornerRadius = 10
        sceneView.layer?.masksToBounds = true
        sceneView.layer?.borderWidth = 1
        sceneView.layer?.borderColor = NSColor.separatorColor.cgColor

        for child in [header, sceneView, closeHint] {
            child.translatesAutoresizingMaskIntoConstraints = false
            container.addSubview(child)
        }

        NSLayoutConstraint.activate([
            header.leadingAnchor.constraint(equalTo: container.leadingAnchor, constant: 20),
            header.trailingAnchor.constraint(equalTo: container.trailingAnchor, constant: -20),
            header.topAnchor.constraint(equalTo: container.topAnchor, constant: 20),

            sceneView.leadingAnchor.constraint(equalTo: container.leadingAnchor, constant: 20),
            sceneView.trailingAnchor.constraint(equalTo: container.trailingAnchor, constant: -20),
            sceneView.topAnchor.constraint(equalTo: header.bottomAnchor, constant: 12),

            closeHint.leadingAnchor.constraint(equalTo: container.leadingAnchor, constant: 20),
            closeHint.topAnchor.constraint(equalTo: sceneView.bottomAnchor, constant: 10),
            closeHint.bottomAnchor.constraint(equalTo: container.bottomAnchor, constant: -16),
        ])

        view = container
    }

    override func viewDidAppear() {
        super.viewDidAppear()
        view.window?.makeFirstResponder(sceneView)
    }

    @objc private func resetView() {
        sceneView.resetView()
        view.window?.makeFirstResponder(sceneView)
    }
}

private final class SkyboxSceneView: SCNView {
    private let cameraNode = SCNNode()
    private var lastDragLocation: NSPoint?
    private var yaw = -Float.pi / 2
    private var pitch: Float = 0

    override var acceptsFirstResponder: Bool { true }

    func configure(images: [NSImage]) {
        let scene = SCNScene()
        scene.background.contents = images

        cameraNode.camera = SCNCamera()
        cameraNode.camera?.fieldOfView = 70
        cameraNode.camera?.zNear = 0.01
        cameraNode.position = SCNVector3Zero
        scene.rootNode.addChildNode(cameraNode)

        self.scene = scene
        pointOfView = cameraNode
        backgroundColor = .black
        rendersContinuously = false
        antialiasingMode = .multisampling4X
        resetView()
    }

    func resetView() {
        yaw = -Float.pi / 2
        pitch = 0
        cameraNode.camera?.fieldOfView = 70
        updateCamera()
    }

    override func mouseDown(with event: NSEvent) {
        lastDragLocation = convert(event.locationInWindow, from: nil)
    }

    override func mouseDragged(with event: NSEvent) {
        let location = convert(event.locationInWindow, from: nil)
        guard let previous = lastDragLocation else {
            lastDragLocation = location
            return
        }

        yaw -= Float(location.x - previous.x) * 0.005
        pitch += Float(location.y - previous.y) * 0.005
        pitch = min(Float.pi / 2 - 0.01, max(-Float.pi / 2 + 0.01, pitch))
        lastDragLocation = location
        updateCamera()
    }

    override func mouseUp(with event: NSEvent) {
        lastDragLocation = nil
    }

    override func scrollWheel(with event: NSEvent) {
        guard let camera = cameraNode.camera else { return }
        camera.fieldOfView = min(100, max(30, camera.fieldOfView + event.scrollingDeltaY * 0.35))
        setNeedsDisplay(bounds)
    }

    override func keyDown(with event: NSEvent) {
        let modifiers = event.modifierFlags.intersection([.command, .option, .control, .shift])
        if modifiers.isEmpty,
           event.charactersIgnoringModifiers == " " || event.charactersIgnoringModifiers == "\u{1b}" {
            window?.performClose(nil)
            return
        }
        super.keyDown(with: event)
    }

    private func updateCamera() {
        cameraNode.eulerAngles = SCNVector3(pitch, yaw, 0)
        setNeedsDisplay(bounds)
    }
}
