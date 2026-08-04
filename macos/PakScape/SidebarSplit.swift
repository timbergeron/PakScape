import AppKit
import SwiftUI

/// Layout state for the folder sidebar's seam.
///
/// This lives outside `ContentView` on purpose. While you drag the seam its width
/// changes every frame, and any view whose body reads that width is rebuilt just as
/// often — with the width as plain `@State` on `ContentView` that meant re-sorting
/// the folder and rebuilding the whole file table sixty times a second, which is
/// what made dragging feel heavy. Keeping it here lets only the two small views
/// that actually need the width observe it.
@Observable
final class SidebarSplitState {
    static let minWidth: CGFloat = 200
    static let maxWidth: CGFloat = 420
    static let defaultWidth: CGFloat = 280
    /// Row chrome ahead of a folder label: the list's own inset, the disclosure
    /// triangle, the folder icon, and the gap before the text.
    static let rowChromeWidth: CGFloat = 54
    static let rowIndentPerLevel: CGFloat = 16
    static let rowTrailingInset: CGFloat = 16
    /// Released below this the sidebar shuts; released between this and
    /// `reopenWidth` it springs back out, so it never rests unusably narrow.
    static let collapseThreshold: CGFloat = 120
    static let reopenWidth: CGFloat = 220
    /// Width of the invisible strip you can grab, centered on the seam.
    static let seamHitWidth: CGFloat = 16
    static let minContentWidth: CGFloat = 260
    /// Where AppKit already starts the navigation area, just past the window buttons.
    static let toolbarNavigationInset: CGFloat = 78
    /// Pulls the controls' capsule back to line its leading edge up with the content
    /// column's own leading padding, flush with the list and its column headers.
    static let toolbarAlignmentNudge: CGFloat = 12
    /// Below this the spacer would be zero-width, and an empty item still claims a
    /// slot — so there is nothing to gain by adding one.
    static var toolbarSpacerThreshold: CGFloat { toolbarNavigationInset + toolbarAlignmentNudge }
    static let settleAnimation: Animation = .easeOut(duration: 0.16)
    static let coordinateSpace = "PakScapeSidebarSplit"

    /// Changes on every frame of a drag. Read it only from the seam and the column.
    var width: CGFloat = SidebarSplitState.defaultWidth
    var isHovered = false
    var isDragging = false
    /// Flips only when the sidebar actually opens or shuts, so the toolbar can
    /// follow it without paying for the drag.
    private(set) var isCollapsed = false
    /// Whether the sidebar is wide enough for the toolbar spacer to be worth adding.
    private(set) var needsToolbarSpacer = false

    /// How far the leading toolbar items have to be pushed to clear the sidebar.
    var toolbarSpacerWidth: CGFloat {
        max(0, width - Self.toolbarSpacerThreshold)
    }

    /// Widest the sidebar may be dragged before the content pane would be squeezed.
    private var maxDraggableWidth: CGFloat {
        max(0, containerWidth - Self.minContentWidth)
    }

    var canNarrowSidebar: Bool { width > 0 }
    var canWidenSidebar: Bool { width < maxDraggableWidth }

    private var lastExpandedWidth: CGFloat = SidebarSplitState.defaultWidth
    private var grabOffset: CGFloat = 0
    /// Measured by the layout, read only while clamping a drag — no view watches it.
    @ObservationIgnored var containerWidth: CGFloat = 0

    func beginDrag(startX: CGFloat) {
        isDragging = true
        // Where along the grip it was grabbed, so the seam keeps that spot under
        // the pointer instead of jumping to center itself on the first frame.
        grabOffset = startX - width
    }

    func drag(toX locationX: CGFloat) {
        width = clamped(locationX - grabOffset)

        refreshToolbarState()
    }

    /// Tracks the toolbar's two switches mid-drag. Both are assigned only on their
    /// transition: writing them every frame would invalidate everything watching
    /// them, which is the rebuild storm this split was factored out to avoid.
    private func refreshToolbarState() {
        let collapsed = width < 1
        if collapsed != isCollapsed {
            isCollapsed = collapsed
        }

        let needsSpacer = width > Self.toolbarSpacerThreshold
        if needsSpacer != needsToolbarSpacer {
            needsToolbarSpacer = needsSpacer
        }
    }

    /// Nothing snaps until the drag is released; this is where it lands.
    func endDrag(wasTap: Bool) {
        isDragging = false

        guard !wasTap else {
            if isCollapsed {
                settle(to: max(Self.reopenWidth, lastExpandedWidth))
            }
            return
        }

        if width < Self.collapseThreshold {
            settle(to: 0)
        } else if width < Self.reopenWidth {
            settle(to: Self.reopenWidth)
        } else {
            lastExpandedWidth = width
            refreshToolbarState()
        }
    }

    /// Sizes the sidebar to the folder names it is about to show, so an archive of
    /// short names doesn't open behind a half-empty column. Rows are the ones visible
    /// before anything is expanded — the root and its top-level folders — paired with
    /// their depth. Clamped, so a deeply nested name can't run away with the window.
    func fitWidth(toRows rows: [(name: String, depth: Int)]) {
        guard !rows.isEmpty else { return }

        let font = NSFont.systemFont(ofSize: NSFont.systemFontSize)
        let widest = rows.reduce(CGFloat.zero) { widest, row in
            let text = (row.name as NSString).size(withAttributes: [.font: font]).width
            let row = Self.rowChromeWidth
                + (CGFloat(row.depth) * Self.rowIndentPerLevel)
                + text
                + Self.rowTrailingInset
            return max(widest, row)
        }

        width = min(max(widest.rounded(.up), Self.minWidth), Self.maxWidth)
        lastExpandedWidth = width
        refreshToolbarState()
    }

    func toggle() {
        if isCollapsed {
            settle(to: max(Self.reopenWidth, lastExpandedWidth))
        } else {
            lastExpandedWidth = max(Self.reopenWidth, width)
            settle(to: 0)
        }
    }

    private func settle(to target: CGFloat) {
        if target >= 1 {
            lastExpandedWidth = target
        }
        withAnimation(Self.settleAnimation) {
            width = target
        }
        refreshToolbarState()
    }

    private func clamped(_ width: CGFloat) -> CGFloat {
        min(max(width, 0), max(0, containerWidth - Self.minContentWidth))
    }
}

@available(macOS 15.0, *)
private func seamResizeDirections(canNarrow: Bool, canWiden: Bool) -> HorizontalDirection.Set {
    var directions: HorizontalDirection.Set = []
    if canNarrow {
        directions.insert(.leading)
    }
    if canWiden {
        directions.insert(.trailing)
    }
    return directions
}

private extension View {
    /// The system column-resize pointer, showing arrows only for the directions the
    /// seam can still travel — both ways mid-range, one way at either end.
    @ViewBuilder
    func columnResizePointer(canNarrow: Bool, canWiden: Bool) -> some View {
        if #available(macOS 15.0, *) {
            self.pointerStyle(
                .columnResize(directions: seamResizeDirections(canNarrow: canNarrow, canWiden: canWiden))
            )
        } else {
            self
        }
    }
}

/// Holds the sidebar at the seam's width. Only this view rebuilds as you drag.
struct SidebarColumn<Content: View>: View {
    let split: SidebarSplitState
    @ViewBuilder let content: Content

    var body: some View {
        content
            .frame(width: split.width, alignment: .leading)
            .clipped()
    }
}

/// Both column surfaces as one layer running the full height of the window,
/// titlebar included. AppKit gives Finder this two-tone titlebar for free through
/// `NSSplitViewController`'s sidebar behaviour; a hand-rolled split has to paint it.
/// Drawn as a single background rather than one per pane so the two colours meet
/// at exactly the seam, above and below the toolbar alike.
struct SidebarSplitSurfaces: View {
    let split: SidebarSplitState
    let sidebarColor: NSColor

    var body: some View {
        HStack(spacing: 0) {
            Color(nsColor: sidebarColor)
                .frame(width: split.width)
            Color(nsColor: .windowBackgroundColor)
        }
        .ignoresSafeArea(.container, edges: .top)
    }
}

/// An empty leading toolbar item as wide as the sidebar, so the real controls are
/// laid out over the content column. `NavigationSplitView` would have split the
/// toolbar at the sidebar for us; a hand-rolled split gets one continuous bar.
///
/// Deliberately a spacer item rather than padding or an offset on the controls
/// themselves: padding grows the controls' own item so its capsule stretches back
/// across the seam, and an offset moves the drawing without moving the frame, which
/// strands the capsule and runs the contents into the trailing items. Only real
/// layout width moves an item without those artifacts — and the window title, which
/// AppKit places after the leading items, comes along with it.
struct SidebarToolbarSpacer: View {
    let split: SidebarSplitState

    var body: some View {
        Color.clear
            .frame(width: split.toolbarSpacerWidth, height: 1)
    }
}

/// The grip: an invisible full-height strip centered on the seam, showing a capsule
/// on hover and resizing the sidebar live as you drag it.
struct SidebarSeamHandle: View {
    let split: SidebarSplitState

    var body: some View {
        Capsule()
            .fill(Color.secondary.opacity(0.7))
            .frame(width: 8, height: 44)
            .frame(width: SidebarSplitState.seamHitWidth)
            .frame(maxHeight: .infinity)
            .opacity(split.isHovered || split.isDragging ? 1 : 0)
            .contentShape(Rectangle())
            // Sits just past the seam rather than straddling it, so the grip reads as
            // part of the content column instead of splitting the difference.
            .offset(x: split.width)
            .onHover { isHovered in
                split.isHovered = isHovered
            }
            .gesture(
                // Measured against the fixed split, never the grip's own space: the
                // grip rides the seam, so a translation-based drag would chase itself.
                DragGesture(
                    minimumDistance: 0,
                    coordinateSpace: .named(SidebarSplitState.coordinateSpace)
                )
                .onChanged { value in
                    if !split.isDragging {
                        split.beginDrag(startX: value.startLocation.x)
                    }
                    split.drag(toX: value.location.x)
                }
                .onEnded { value in
                    split.endDrag(wasTap: value.location == value.startLocation)
                }
            )
            .columnResizePointer(canNarrow: split.canNarrowSidebar, canWiden: split.canWidenSidebar)
            .help(split.isCollapsed ? "Drag or click to show folders" : "Drag to resize folders")
            .accessibilityLabel(split.isCollapsed ? "Show folders" : "Resize folders")
    }
}
