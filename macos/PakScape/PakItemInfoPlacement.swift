import CoreGraphics
import Foundation

/*
 * Where each Get Info window lands. Finder opens these offset from the window the
 * item was selected in and steps every further one down and to the right, so a stack
 * of them stays readable instead of hiding one another.
 */
enum PakItemInfoPlacement {
    /// How far the first window sits inside the top-left corner of its archive window.
    static let firstOffset = CGSize(width: 32, height: 32)

    /// How far each further window steps down and to the right of the last one.
    static let cascadeStep = CGSize(width: 24, height: 24)

    /// The corner the first window of a cascade uses, in screen coordinates.
    static func base(
        parentFrame: CGRect?,
        windowSize: CGSize,
        visibleFrame: CGRect
    ) -> CGPoint {
        guard let parentFrame else {
            /* With no archive window to sit beside, the screen is all there is to go on. */
            return clamped(
                CGPoint(
                    x: visibleFrame.midX - windowSize.width / 2,
                    y: visibleFrame.midY + windowSize.height / 2
                ),
                windowSize: windowSize,
                visibleFrame: visibleFrame
            )
        }

        return clamped(
            CGPoint(
                x: parentFrame.minX + firstOffset.width,
                y: parentFrame.maxY - firstOffset.height
            ),
            windowSize: windowSize,
            visibleFrame: visibleFrame
        )
    }

    /// The corner the next window uses, starting the cascade over once it runs off screen.
    static func topLeft(
        base: CGPoint,
        previous: CGPoint?,
        windowSize: CGSize,
        visibleFrame: CGRect
    ) -> CGPoint {
        guard let previous else { return base }

        let cascaded = CGPoint(x: previous.x + cascadeStep.width, y: previous.y - cascadeStep.height)
        return fits(cascaded, windowSize: windowSize, visibleFrame: visibleFrame) ? cascaded : base
    }

    private static func frame(topLeft: CGPoint, windowSize: CGSize) -> CGRect {
        CGRect(
            x: topLeft.x,
            y: topLeft.y - windowSize.height,
            width: windowSize.width,
            height: windowSize.height
        )
    }

    private static func fits(_ topLeft: CGPoint, windowSize: CGSize, visibleFrame: CGRect) -> Bool {
        visibleFrame.contains(frame(topLeft: topLeft, windowSize: windowSize))
    }

    /// Nudges a corner back until the whole window is on screen, as far as it can.
    private static func clamped(
        _ topLeft: CGPoint,
        windowSize: CGSize,
        visibleFrame: CGRect
    ) -> CGPoint {
        var point = topLeft
        let rect = frame(topLeft: point, windowSize: windowSize)

        if rect.maxX > visibleFrame.maxX {
            point.x = visibleFrame.maxX - windowSize.width
        }
        if point.x < visibleFrame.minX {
            point.x = visibleFrame.minX
        }
        if rect.minY < visibleFrame.minY {
            point.y = visibleFrame.minY + windowSize.height
        }
        if point.y > visibleFrame.maxY {
            point.y = visibleFrame.maxY
        }
        return point
    }
}
