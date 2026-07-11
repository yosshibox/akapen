// The drawing canvas: an AppKit NSView that captures NSEvent tablet pressure
// (spec §5.1) and hosts the composited image. It maps screen points to
// image-native pixels (fit + zoom + pan + rotation) and feeds normalized
// pointer samples to the Rust core via AppState.
//
// Pressure: tablet events carry `NSEvent.pressure`; we only treat
// `.tabletPoint` subtype events as pen pressure so a trackpad Force Touch is
// not mistaken for a pen (spec §5.4 mac note). Mouse drags pass pressure 1.0.
//
// The Metal/wgpu surface (spec §7.1) is a later phase; M1 renders the CPU
// composite to keep the pipeline correct first.

import AkapenKit
import AppKit
import SwiftUI

struct CanvasView: NSViewRepresentable {
    @ObservedObject var state: AppState

    func makeNSView(context: Context) -> CanvasNSView {
        let v = CanvasNSView()
        v.state = state
        return v
    }

    func updateNSView(_ nsView: CanvasNSView, context: Context) {
        nsView.state = state
        // Re-read the composite whenever the revision changes.
        nsView.refresh()
    }
}

final class CanvasNSView: NSView {
    weak var state: AppState?

    // View transform over the image (image-native pixels → view points).
    private var zoom: CGFloat = 1
    private var pan: CGSize = .zero
    private var rotationDeg: CGFloat = 0
    private var fittedOnce = false

    private var cachedImage: NSImage?
    private var lastRevision = -1

    // Space held → pan mode (spec §3 CSP: Space-drag pans).
    private var spaceDown = false

    override var isFlipped: Bool { true } // top-left origin, matches image space
    override var acceptsFirstResponder: Bool { true }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        window?.acceptsMouseMovedEvents = true
    }

    func refresh() {
        guard let state else { return }
        if state.revision != lastRevision {
            cachedImage = state.compositeNSImage()
            lastRevision = state.revision
            fitIfNeeded()
            needsDisplay = true
        }
    }

    private func fitIfNeeded() {
        guard let img = cachedImage, !fittedOnce, bounds.width > 0 else { return }
        let s = min(bounds.width / img.size.width, bounds.height / img.size.height)
        zoom = s > 0 ? s : 1
        pan = .zero
        rotationDeg = 0
        fittedOnce = true
    }

    /// Fit-to-window (Cmd+0 / on new image).
    func fitToWindow() {
        fittedOnce = false
        fitIfNeeded()
        needsDisplay = true
    }

    // MARK: drawing

    override func draw(_ dirtyRect: NSRect) {
        NSColor(white: 0.15, alpha: 1).setFill()
        bounds.fill()
        guard let img = cachedImage else { return }
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }

        ctx.saveGState()
        let center = CGPoint(x: bounds.midX + pan.width, y: bounds.midY + pan.height)
        ctx.translateBy(x: center.x, y: center.y)
        ctx.rotate(by: rotationDeg * .pi / 180)
        ctx.scaleBy(x: zoom, y: zoom)
        let drawRect = CGRect(x: -img.size.width / 2, y: -img.size.height / 2,
                              width: img.size.width, height: img.size.height)
        if let cg = img.cgImage(forProposedRect: nil, context: nil, hints: nil) {
            ctx.draw(cg, in: drawRect)
        }
        ctx.restoreGState()
    }

    // MARK: coordinate mapping (view point → image-native pixel)

    private func imagePoint(from viewPoint: CGPoint) -> CGPoint? {
        guard let img = cachedImage else { return nil }
        let cx = bounds.midX + pan.width
        let cy = bounds.midY + pan.height
        var dx = viewPoint.x - cx
        var dy = viewPoint.y - cy
        // Inverse rotation.
        let rad = -rotationDeg * .pi / 180
        let rx = dx * cos(rad) - dy * sin(rad)
        let ry = dx * sin(rad) + dy * cos(rad)
        dx = rx / zoom
        dy = ry / zoom
        return CGPoint(x: img.size.width / 2 + dx, y: img.size.height / 2 + dy)
    }

    // MARK: input

    private func pressure(from event: NSEvent) -> (Double, AkapenPointerKind) {
        // Only tablet-point events carry genuine pen pressure.
        if event.subtype == .tabletPoint {
            return (Double(event.pressure), .pen)
        }
        return (1.0, .mouse)
    }

    private func send(_ event: NSEvent, phase: AkapenPhase) {
        guard let state, state.engine != nil else { return }
        let vp = convert(event.locationInWindow, from: nil)
        guard let ip = imagePoint(from: vp) else { return }
        let (p, kind) = pressure(from: event)
        state.pointer(imageX: Double(ip.x), imageY: Double(ip.y),
                      pressure: p, kind: kind, phase: phase)
    }

    override func mouseDown(with event: NSEvent) {
        if spaceDown { return } // pan gesture; ignore drawing
        state?.applyToolState()
        send(event, phase: .down)
    }

    override func mouseDragged(with event: NSEvent) {
        if spaceDown {
            pan.width += event.deltaX
            pan.height += event.deltaY
            needsDisplay = true
            return
        }
        send(event, phase: .move)
    }

    override func mouseUp(with event: NSEvent) {
        if spaceDown { return }
        send(event, phase: .up)
    }

    override func scrollWheel(with event: NSEvent) {
        // Ctrl/Cmd + wheel zooms; plain wheel pans (spec §3, B14 lesson).
        if event.modifierFlags.contains(.command) || event.modifierFlags.contains(.control) {
            let factor = 1 + event.scrollingDeltaY * 0.01
            zoom = max(0.02, min(20, zoom * factor))
        } else {
            pan.width += event.scrollingDeltaX
            pan.height += event.scrollingDeltaY
        }
        needsDisplay = true
    }

    override func magnify(with event: NSEvent) {
        zoom = max(0.02, min(20, zoom * (1 + event.magnification)))
        needsDisplay = true
    }

    override func keyDown(with event: NSEvent) {
        switch event.charactersIgnoringModifiers {
        case " ": spaceDown = true
        default: super.keyDown(with: event)
        }
    }

    override func keyUp(with event: NSEvent) {
        if event.charactersIgnoringModifiers == " " { spaceDown = false }
    }

    /// Rotate the canvas (called from the toolbar).
    func rotate(by deg: CGFloat) {
        rotationDeg += deg
        needsDisplay = true
    }
}
