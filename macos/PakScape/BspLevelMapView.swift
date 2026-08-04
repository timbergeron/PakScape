import AppKit

/// Displays a rendered BSP level map inside the Quick Look panel.
///
/// Quick Look scales its own image preview to fill the panel, which pushes the
/// map underneath the floating marker controls. This view draws the map itself
/// so it can start at a fit-to-window scale that clears the controls, and so it
/// can be zoomed with the scroll wheel (or a trackpad pinch) and panned by
/// dragging once it no longer fits.
final class BspLevelMapView: NSView {
    static let fitZoom: CGFloat = 1
    private static let maximumZoom: CGFloat = 8
    private static let lineScrollZoomRate: CGFloat = 0.12
    private static let preciseScrollZoomRate: CGFloat = 0.006
    private static let controlsClearance: CGFloat = 12
    private static let fallbackEdgeInset: CGFloat = 5
    private static let fallbackTitleBarInset: CGFloat = 38

    /// Margins kept between the map and the edges of the preview area.
    var contentInsets = NSEdgeInsets(top: 20, left: 20, bottom: 20, right: 20) {
        didSet { needsDisplay = true }
    }

    /// Floating controls the map should never be drawn underneath.
    weak var controlsView: NSView?

    /// Supplies Quick Look's own preview area, which the panel hides while a
    /// map is shown. The map takes over exactly that region: everything else in
    /// the panel — the title bar with its close, share and open buttons — keeps
    /// drawing and receiving clicks.
    var coverageProvider: (() -> NSView?)?

    private(set) var image: NSImage?
    private var zoom = BspLevelMapView.fitZoom
    private var panOffset = CGPoint.zero
    private var dragAnchor: (location: CGPoint, offset: CGPoint)?

    override func hitTest(_ point: NSPoint) -> NSView? {
        guard let superview, image != nil else { return nil }
        let local = convert(point, from: superview)
        guard coverageRect().contains(local) else { return nil }
        return super.hitTest(point)
    }

    func setImage(_ image: NSImage?, resettingZoom: Bool) {
        self.image = image
        if resettingZoom {
            resetZoom()
        } else {
            needsDisplay = true
        }
    }

    func resetZoom() {
        zoom = Self.fitZoom
        panOffset = .zero
        needsDisplay = true
    }

    override func layout() {
        super.layout()
        needsDisplay = true
    }

    override func draw(_ dirtyRect: NSRect) {
        let (coverage, isFallback) = coverageArea()
        if isFallback {
            // Quick Look's preview area could not be found, so it is still
            // showing its own stretched copy of the map underneath.
            NSColor.windowBackgroundColor.setFill()
            coverage.intersection(dirtyRect).fill()
        }

        // Otherwise nothing is painted around the map: the rendered image
        // brings its own background and outline, and the panel's background
        // shows through everywhere else.
        guard let image, let rect = mapRect(for: image) else { return }
        NSGraphicsContext.current?.saveGraphicsState()
        defer { NSGraphicsContext.current?.restoreGraphicsState() }
        coverage.clip()
        NSGraphicsContext.current?.imageInterpolation = .high
        image.draw(in: rect)
    }

    override func scrollWheel(with event: NSEvent) {
        guard image != nil else {
            super.scrollWheel(with: event)
            return
        }

        let rate = event.hasPreciseScrollingDeltas
            ? Self.preciseScrollZoomRate
            : Self.lineScrollZoomRate
        let step = event.scrollingDeltaY * rate
        guard step != 0,
              applyZoom(factor: exp(step), around: convert(event.locationInWindow, from: nil)) else {
            // Already at the limit, so let Quick Look handle the gesture.
            super.scrollWheel(with: event)
            return
        }
    }

    override func magnify(with event: NSEvent) {
        guard image != nil else {
            super.magnify(with: event)
            return
        }

        _ = applyZoom(
            factor: 1 + event.magnification,
            around: convert(event.locationInWindow, from: nil)
        )
    }

    override func mouseDown(with event: NSEvent) {
        guard let image else {
            super.mouseDown(with: event)
            return
        }

        if event.clickCount >= 2 {
            dragAnchor = nil
            resetZoom()
            return
        }

        guard canPan(showing: image) else {
            // The whole map is visible, so keep the panel's usual behaviour of
            // being dragged around by its body.
            dragAnchor = nil
            window?.performDrag(with: event)
            return
        }

        dragAnchor = (convert(event.locationInWindow, from: nil), panOffset)
    }

    override func mouseDragged(with event: NSEvent) {
        guard let dragAnchor else {
            super.mouseDragged(with: event)
            return
        }

        let location = convert(event.locationInWindow, from: nil)
        panOffset = CGPoint(
            x: dragAnchor.offset.x + (location.x - dragAnchor.location.x),
            y: dragAnchor.offset.y + (location.y - dragAnchor.location.y)
        )
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        dragAnchor = nil
    }

    /// Returns whether the zoom actually changed.
    @discardableResult
    private func applyZoom(factor: CGFloat, around point: CGPoint) -> Bool {
        guard let image,
              factor > 0,
              let currentRect = mapRect(for: image),
              currentRect.width > 0,
              currentRect.height > 0 else { return false }

        let updatedZoom = min(max(zoom * factor, Self.fitZoom), Self.maximumZoom)
        guard updatedZoom != zoom else { return false }

        // Keep whatever sits under the pointer anchored while the scale changes.
        let growth = updatedZoom / zoom
        let anchorX = (point.x - currentRect.minX) * growth
        let anchorY = (point.y - currentRect.minY) * growth
        let updatedRect = CGRect(
            x: point.x - anchorX,
            y: point.y - anchorY,
            width: currentRect.width * growth,
            height: currentRect.height * growth
        )

        zoom = updatedZoom
        let area = availableRect()
        panOffset = CGPoint(
            x: updatedRect.midX - area.midX,
            y: updatedRect.midY - area.midY
        )
        needsDisplay = true
        return true
    }

    private func canPan(showing image: NSImage) -> Bool {
        guard let rect = mapRect(for: image) else { return false }
        let area = availableRect()
        return (rect.width - area.width) > 0.5 || (rect.height - area.height) > 0.5
    }

    private func coverageRect() -> CGRect {
        coverageArea().rect
    }

    /// Region of the panel the map is responsible for drawing, and whether it
    /// had to be guessed because Quick Look's preview area was not found.
    private func coverageArea() -> (rect: CGRect, isFallback: Bool) {
        if let area = coverageProvider?(), area.superview != nil {
            let rect = convert(area.bounds, from: area).intersection(bounds)
            if rect.width > 1, rect.height > 1 {
                return (rect, false)
            }
        }

        // Keep clear of the strip the panel's title bar occupies.
        let rect = CGRect(
            x: bounds.minX + Self.fallbackEdgeInset,
            y: bounds.minY + Self.fallbackEdgeInset,
            width: max(1, bounds.width - (Self.fallbackEdgeInset * 2)),
            height: max(1, bounds.height - Self.fallbackEdgeInset - Self.fallbackTitleBarInset)
        )
        return (rect, true)
    }

    /// Area the map may occupy: the covered region minus its margins and minus
    /// anything the floating controls sit on top of.
    private func availableRect() -> CGRect {
        let coverage = coverageRect()
        var bottomInset = contentInsets.bottom
        if let controlsView, !controlsView.isHidden, controlsView.superview != nil {
            let controlsFrame = convert(controlsView.bounds, from: controlsView)
            bottomInset = max(
                bottomInset,
                controlsFrame.maxY - coverage.minY + Self.controlsClearance
            )
        }

        let horizontal = contentInsets.left + contentInsets.right
        let vertical = contentInsets.top + bottomInset
        return CGRect(
            x: coverage.minX + contentInsets.left,
            y: coverage.minY + bottomInset,
            width: max(1, coverage.width - horizontal),
            height: max(1, coverage.height - vertical)
        )
    }

    private func mapRect(for image: NSImage) -> CGRect? {
        let imageSize = image.size
        guard imageSize.width > 0, imageSize.height > 0 else { return nil }

        let area = availableRect()
        let fit = min(area.width / imageSize.width, area.height / imageSize.height)
        let size = CGSize(
            width: imageSize.width * fit * zoom,
            height: imageSize.height * fit * zoom
        )
        // Normalise the stored offset so drags and zooms always continue from
        // the position that is actually on screen.
        panOffset = clampedOffset(panOffset, for: size, in: area)
        return CGRect(
            x: area.midX - (size.width / 2) + panOffset.x,
            y: area.midY - (size.height / 2) + panOffset.y,
            width: size.width,
            height: size.height
        )
    }

    private func clampedOffset(_ offset: CGPoint, for size: CGSize, in area: CGRect) -> CGPoint {
        let limitX = max(0, (size.width - area.width) / 2)
        let limitY = max(0, (size.height - area.height) / 2)
        return CGPoint(
            x: min(max(offset.x, -limitX), limitX),
            y: min(max(offset.y, -limitY), limitY)
        )
    }
}
