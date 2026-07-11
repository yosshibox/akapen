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
        v.useGPU = state.useGPU
        return v
    }

    func updateNSView(_ nsView: CanvasNSView, context: Context) {
        nsView.state = state
        nsView.useGPU = state.useGPU
        // Re-read the composite (CPU) or redraw the frame (GPU) whenever the
        // revision changes.
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

    // CPU path cache (unchanged): the composited NSImage drawn in draw(_:).
    private var cachedImage: NSImage?
    // Natural (image-native) size, tracked separately from `cachedImage` so the
    // GPU path — which never builds the CPU composite — can still map pointer
    // coordinates and fit-to-window. Equals `cachedImage.size` on the CPU path.
    private var imageSize: CGSize?
    private var lastRevision = -1

    // MARK: GPU path (spec §7.4-6, Phase e). Inert unless `useGPU` and attach
    // succeeds. When active, a MetalHostView child renders over this view; the
    // CPU draw(_:) path below is left completely untouched as the fallback.
    var useGPU = false
    private var metalHost: MetalHostView?
    private var gpuActive = false
    private weak var attachedEngine: AkapenEngine?
    private var lastPhysicalSize: CGSize = .zero

    // Space held → pan mode (spec §3 CSP: Space-drag pans).
    private var spaceDown = false

    override var isFlipped: Bool { true } // top-left origin, matches image space
    override var acceptsFirstResponder: Bool { true }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        window?.acceptsMouseMovedEvents = true
        if window == nil {
            teardownGPU()
        } else {
            ensureGPU()
            renderGPU()
        }
    }

    func refresh() {
        guard let state else { return }
        guard state.revision != lastRevision else { return }
        lastRevision = state.revision

        if let e = state.engine {
            let (w, h) = e.size
            imageSize = CGSize(width: w, height: h)
        } else {
            imageSize = nil
        }

        ensureGPU()
        if gpuActive {
            // GPU path: no CPU composite needed; render the frame directly.
            fitIfNeeded()
            renderGPU()
        } else {
            // CPU fallback (unchanged).
            cachedImage = state.compositeNSImage()
            fitIfNeeded()
            needsDisplay = true
        }
    }

    private func fitIfNeeded() {
        guard let sz = imageSize, !fittedOnce, bounds.width > 0 else { return }
        let s = min(bounds.width / sz.width, bounds.height / sz.height)
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
        renderGPU()
    }

    // MARK: GPU lifecycle

    /// Attaches (or re-attaches, when the engine changed) the GPU surface. A
    /// no-op when GPU is disabled, no engine/window/bounds are available yet,
    /// or the surface is already attached to the current engine. On attach
    /// failure this leaves `gpuActive == false`, so the CPU path takes over.
    private func ensureGPU() {
        guard useGPU, let engine = state?.engine, window != nil,
              bounds.width > 1, bounds.height > 1 else { return }
        if gpuActive, attachedEngine === engine { return }
        // Engine changed (new image opened) — drop the old surface first.
        if gpuActive { teardownGPU() }

        let host = MetalHostView(frame: bounds)
        host.autoresizingMask = [.width, .height]
        addSubview(host)
        host.wantsLayer = true
        host.layoutSubtreeIfNeeded()
        let scale = window?.backingScaleFactor ?? 2.0
        host.layer?.contentsScale = scale

        let physW = UInt32(max(1, bounds.width * scale))
        let physH = UInt32(max(1, bounds.height * scale))
        let ok = engine.attachRender(
            nsView: host.nsViewPointer, width: physW, height: physH, scale: Float(scale))
        if ok {
            metalHost = host
            gpuActive = true
            attachedEngine = engine
            lastPhysicalSize = CGSize(width: CGFloat(physW), height: CGFloat(physH))
        } else {
            host.removeFromSuperview()
            gpuActive = false
            attachedEngine = nil
        }
    }

    private func teardownGPU() {
        if gpuActive {
            // Detach the engine we attached to (weak: nil if it already freed
            // itself, in which case its GPU surface was released on free).
            attachedEngine?.detachRender()
            metalHost?.removeFromSuperview()
            metalHost = nil
            gpuActive = false
            lastPhysicalSize = .zero
        }
        attachedEngine = nil
    }

    /// Reconfigures the swapchain when the physical (backing-scaled) size
    /// changed, then redraws.
    private func updateGPUSurfaceSizeIfNeeded() {
        guard gpuActive, let engine = state?.engine else { return }
        let scale = window?.backingScaleFactor ?? 2.0
        metalHost?.layer?.contentsScale = scale
        let physW = UInt32(max(1, bounds.width * scale))
        let physH = UInt32(max(1, bounds.height * scale))
        let sz = CGSize(width: CGFloat(physW), height: CGFloat(physH))
        guard sz != lastPhysicalSize else { return }
        engine.resizeRender(width: physW, height: physH, scale: Float(scale))
        lastPhysicalSize = sz
        renderGPU()
    }

    /// Draws one GPU frame using the current zoom/pan/rotation, converted to
    /// the core's physical-pixel view transform. No-op unless GPU is active.
    private func renderGPU() {
        guard gpuActive, let engine = state?.engine, imageSize != nil else { return }
        let scale = window?.backingScaleFactor ?? (metalHost?.layer?.contentsScale ?? 2.0)
        // Image center maps to (bounds center + pan) in points; ×scale → pixels.
        let cx = (bounds.midX + pan.width) * scale
        let cy = (bounds.midY + pan.height) * scale
        let view = AkapenEngine.ViewTransform(
            centerX: Float(cx),
            centerY: Float(cy),
            scale: Float(zoom * scale),
            rotationDeg: Float(rotationDeg))
        engine.renderFrame(view)
    }

    override func setFrameSize(_ newSize: NSSize) {
        super.setFrameSize(newSize)
        ensureGPU()
        updateGPUSurfaceSizeIfNeeded()
    }

    override func viewDidChangeBackingProperties() {
        super.viewDidChangeBackingProperties()
        updateGPUSurfaceSizeIfNeeded()
    }

    override func viewWillMove(toWindow newWindow: NSWindow?) {
        super.viewWillMove(toWindow: newWindow)
        if newWindow == nil {
            teardownGPU()
        }
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
        // Uses `imageSize` (populated from the engine) rather than the CPU
        // `cachedImage`, so pointer mapping works identically on the GPU path
        // (which never builds the CPU composite). On the CPU path the two are
        // always equal, so this is behavior-preserving.
        guard let sz = imageSize else { return nil }
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
        return CGPoint(x: sz.width / 2 + dx, y: sz.height / 2 + dy)
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
            renderGPU()
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
        renderGPU()
    }

    override func magnify(with event: NSEvent) {
        zoom = max(0.02, min(20, zoom * (1 + event.magnification)))
        needsDisplay = true
        renderGPU()
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
        renderGPU()
    }
}

/// A thin layer-backed child view that hosts the wgpu-managed `CAMetalLayer`
/// (spec §7.4-6, Phase e). It is added over `CanvasNSView` only while the GPU
/// path is active, keeping `CanvasNSView` itself an ordinary layer-backed view
/// whose CPU `draw(_:)` fallback is never disturbed. Do NOT override
/// `makeBackingLayer` here: `raw-window-metal` (inside libakapen) sets
/// `wantsLayer` and inserts/manages its own `CAMetalLayer` sublayer on attach.
/// It is transparent to hit-testing so pointer events still reach the canvas.
final class MetalHostView: NSView {
    override var isFlipped: Bool { true }

    /// Pointer events must fall through to the underlying `CanvasNSView`.
    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    /// The `NSView*` to hand to `akapen_render_attach` (wgpu's AppKit surface
    /// path expects the view pointer, not a layer).
    var nsViewPointer: UnsafeMutableRawPointer {
        Unmanaged.passUnretained(self).toOpaque()
    }
}
