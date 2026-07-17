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
import AkapenUIContract
import AppKit
import CAkapen
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
        if let command = state.canvasCommand {
            nsView.perform(command)
            state.canvasCommand = nil
        }
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

    // Space held → pan mode (spec §3 CSP: Space-drag pans). With Shift also
    // held, a Space drag rotates the canvas instead (spec §3.1).
    private var spaceDown = false
    private var panTool = false

    // Palm rejection (spec §5.2): the core state machine that keeps a resting
    // hand off the ink path. Every classified pointer event is routed through it
    // before it can draw; pen down/move/up here arm the pen-priority lock that
    // later rejects a trailing palm touch.
    private var palmGate = PalmGate()

    override var isFlipped: Bool { true } // top-left origin, matches image space
    override var acceptsFirstResponder: Bool { true }

    // Focus-independent key dispatch (spec §3 「モードスコープのキー
    // ディスパッチ(フォーカス非依存)」): a local monitor sees key events for
    // this window regardless of which control currently has focus, so
    // shortcuts work right after launch/open without clicking the canvas
    // first (V1.2 fix — the responder-chain-only path silently dropped every
    // key until the canvas had focus). Text fields / IME are still respected
    // via resolveAction's guards; the brush fader keeps its arrow keys by
    // being checked as first responder before dispatch.
    private var keyMonitor: Any?

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        // 回転時に描画がステータスバーへはみ出さないよう明示クリップ
        // (macOS 14 以降 clipsToBounds の既定が false)。
        clipsToBounds = true
        window?.acceptsMouseMovedEvents = true
        // Receive *direct* touches (finger/palm on a touch display) so palm
        // rejection can classify and reject them (spec §5.2). Indirect
        // (trackpad) touches are left to the existing magnify/scroll gestures.
        allowedTouchTypes = [.direct]
        if window == nil {
            teardownGPU()
            if let keyMonitor { NSEvent.removeMonitor(keyMonitor) }
            keyMonitor = nil
        } else {
            ensureGPU()
            renderGPU()
            if keyMonitor == nil {
                keyMonitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .keyUp]) {
                    [weak self] event in
                    guard let self, event.window === self.window else { return event }
                    return self.handleMonitoredKey(event) ? nil : event
                }
            }
        }
    }

    /// Returns true when the event was consumed. Mirrors keyDown/keyUp but
    /// runs before the responder chain (focus-independent).
    private func handleMonitoredKey(_ event: NSEvent) -> Bool {
        // Let focused text inputs and the brush fader keep their keys.
        if let responder = window?.firstResponder,
           responder is NSText || responder is NSTextView {
            return false
        }
        if let responder = window?.firstResponder as? NSView,
           responder !== self, responder.acceptsFirstResponder,
           !(responder is CanvasNSView) {
            // e.g. the brush fader (arrow keys adjust the size while focused).
            return false
        }
        if event.type == .keyUp {
            if event.charactersIgnoringModifiers == " " { spaceDown = false }
            return false
        }
        // Plain Space = momentary pan hold (not a discrete action).
        if event.charactersIgnoringModifiers == " ",
           !event.modifierFlags.contains(.command),
           !event.modifierFlags.contains(.option) {
            spaceDown = true
            return true
        }
        if let action = resolveAction(for: event), dispatch(action) {
            return true
        }
        return false
    }

    private var sizedWindowOnce = false

    func refresh() {
        guard let state else { return }
        guard state.revision != lastRevision else { return }
        lastRevision = state.revision

        if let e = state.engine {
            let (w, h) = e.size
            let newSize = CGSize(width: w, height: h)
            // 新しい文書(サイズ変化)は必ずフィットし直す(Windows の
            // OpenImageFile → FitView の写像)。
            if imageSize != newSize { fittedOnce = false }
            imageSize = newSize
            sizeWindowToImageIfNeeded()
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

    /// 最初の画像を開いたとき、ウィンドウを画像に合わせた作業サイズにする
    /// (Windows 起動時の「画像原寸ベースのクライアントサイズ」の写像。画面の
    /// 85% を上限にフィット)。以後のリサイズはユーザーの意思を尊重する。
    private func sizeWindowToImageIfNeeded() {
        guard !sizedWindowOnce, let window, let sz = imageSize,
              let screen = window.screen ?? NSScreen.main else { return }
        sizedWindowOnce = true
        let avail = screen.visibleFrame
        let dockW = CGFloat(AkapenUIMetrics.dockWidth) + 1
        let statusH = CGFloat(AkapenUIMetrics.statusHeight) + 1
        let maxCanvasW = avail.width * 0.85 - dockW
        let maxCanvasH = avail.height * 0.85 - statusH
        let scale = min(1, min(maxCanvasW / sz.width, maxCanvasH / sz.height))
        let content = NSSize(
            width: max(960, sz.width * scale + dockW),
            height: max(720, sz.height * scale + statusH))
        window.setContentSize(content)
        window.center()
    }

    private func fitIfNeeded() {
        guard let sz = imageSize, !fittedOnce, bounds.width > 0 else { return }
        let s = min(bounds.width / sz.width, bounds.height / sz.height)
        zoom = s > 0 ? s : 1
        pan = .zero
        rotationDeg = 0
        fittedOnce = true
        syncViewState()
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
        // Windows 正本(ResizeContract.RefitsImageOnEveryWindowResize):
        // ウィンドウのサイズ変更中も画像を連続的にフィットさせる。
        if imageSize != nil {
            fittedOnce = false
            fitIfNeeded()
            needsDisplay = true
            renderGPU()
        }
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

    /// Classifies a mouse-path NSEvent into (pressure, kind). Only tablet-point
    /// events carry genuine pen pressure; everything else on this path is a
    /// mouse (a trackpad Force Touch must NOT be taken for a pen — spec §5.4).
    /// Genuine touches never arrive here; they come through `touchesBegan`… and
    /// are classified as `.touch` (spec §5.2).
    private func classify(_ event: NSEvent) -> (Double, AkapenPointerKind) {
        if event.subtype == .tabletPoint {
            // V1.2: the settings toggle (input.pressure, default on) flattens
            // pen pressure to a fixed 1.0 — constant line width — matching the
            // Windows V1.1.1 semantics.
            let pressureEnabled = UserDefaults.standard.object(
                forKey: AkapenSettingsKey.pressureEnabled) as? Bool ?? true
            return (pressureEnabled ? Double(event.pressure) : 1.0, .pen)
        }
        return (1.0, .mouse)
    }

    /// NSEvent.timestamp is seconds since boot (monotonic); the core palm gate
    /// wants monotonic milliseconds.
    private func nowMs(_ event: NSEvent) -> Int64 {
        Int64(event.timestamp * 1000)
    }

    private func send(_ event: NSEvent, phase: AkapenPhase) {
        guard let state, state.engine != nil else { return }
        if state.arrowMode { return } // 矢印ツール(操作なし): 描画に流さない

        let (p, kind) = classify(event)
        // Palm rejection (spec §5.2): consult the core gate before drawing. Pen
        // and mouse always resolve to `.draw`; the pen's down/move/up here is
        // what arms the pen-priority lock that later rejects a trailing palm
        // touch (handled in touchesBegan/… below, which never draws).
        guard palmGate.route(kind: kind, phase: phase, nowMs: nowMs(event)) == .draw else { return }
        let vp = convert(event.locationInWindow, from: nil)
        guard let ip = imagePoint(from: vp) else { return }
        state.pointer(imageX: Double(ip.x), imageY: Double(ip.y),
                      pressure: p, kind: kind, phase: phase)
    }

    // MARK: touch input (palm rejection, spec §5.2)
    //
    // Direct touches (a finger or resting palm on a touch display) are
    // classified explicitly as `.touch` and run through the same core gate. In
    // M1 they never reach the ink path: a palm during pen contact / the pen lock
    // routes to `.ignore`, and a deliberate touch to `.navigate` (canvas
    // pan/pinch). Touch-driven pan/pinch and the real liquid-tablet behavior are
    // deferred to the §5.6 device gate; here we only guarantee touches never
    // draw and keep the lock state exercised.
    override func touchesBegan(with event: NSEvent) { gateTouches(event, phase: .down) }
    override func touchesMoved(with event: NSEvent) { gateTouches(event, phase: .move) }
    override func touchesEnded(with event: NSEvent) { gateTouches(event, phase: .up) }
    override func touchesCancelled(with event: NSEvent) { gateTouches(event, phase: .up) }

    private func gateTouches(_ event: NSEvent, phase: AkapenPhase) {
        // Explicitly classified as touch — deliberately NOT sent to
        // state.pointer (the drawing path). We still route it so the gate is
        // exercised and, once touch pan/pinch lands, the pen-priority lock is
        // already in place. `.navigate` (deliberate touch) will drive canvas
        // pan/pinch at the device gate; `.ignore` (palm) stays dropped.
        _ = palmGate.route(kind: .touch, phase: phase, nowMs: nowMs(event))
    }

    override func mouseDown(with event: NSEvent) {
        // Claim first responder so keyDown (shortcuts, Space-pan) reaches this
        // view even if focus was elsewhere (e.g. a toolbar text field). Without
        // this, a click here would draw but leave keyboard focus stuck on the
        // previous responder and shortcuts would silently stop firing.
        window?.makeFirstResponder(self)
        if spaceDown || panTool { return } // pan gesture; ignore drawing
        state?.applyToolState()
        send(event, phase: .down)
    }

    override func mouseDragged(with event: NSEvent) {
        if spaceDown || panTool {
            // Shift+Space drag rotates; plain Space drag pans (spec §3.1).
            if event.modifierFlags.contains(.shift) {
                rotate(by: event.deltaX * 0.5)
            } else {
                pan.width += event.deltaX
                pan.height += event.deltaY
                needsDisplay = true
                renderGPU()
            }
            return
        }
        send(event, phase: .move)
    }

    override func mouseUp(with event: NSEvent) {
        if spaceDown || panTool { return }
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
        // Plain Space is a momentary-pan hold (not a discrete action): tracked
        // here, not routed through the core keymap. (Shift+Space drag rotates —
        // decided at drag time in mouseDragged.)
        if event.charactersIgnoringModifiers == " ",
           !event.modifierFlags.contains(.command),
           !event.modifierFlags.contains(.option) {
            spaceDown = true
            return
        }
        // Every other key goes through the core keymap (spec §3).
        if let action = resolveAction(for: event), dispatch(action) {
            return
        }
        super.keyDown(with: event)
    }

    override func keyUp(with event: NSEvent) {
        if event.charactersIgnoringModifiers == " " { spaceDown = false }
    }

    /// Rotate the canvas (called from the toolbar or a rotation key).
    func rotate(by deg: CGFloat) {
        rotationDeg += deg
        needsDisplay = true
        renderGPU()
        syncViewState()
    }

    func perform(_ command: CanvasCommand) {
        switch command {
        case .draw: panTool = false
        case .pan: panTool = true
        case .zoomIn: zoomBy(1.25)
        case .zoomOut: zoomBy(1.0 / 1.25)
        case .rotateLeft: rotate(by: -15)
        case .rotateRight: rotate(by: 15)
        case .fit: fitToWindow()
        case .actualSize: setActualSize()
        case .centerOn(let ix, let iy):
            // Navigator click/drag (V1.2): pan so the image pixel lands at
            // the canvas center (shared NavigatorMath contract).
            guard let sz = imageSize else { break }
            let p = NavigatorMath.panToCenter(
                onImageX: ix, imageY: iy,
                imageW: Double(sz.width), imageH: Double(sz.height),
                zoom: Double(zoom), rotationDeg: Double(rotationDeg))
            pan = CGSize(width: p.x, height: p.y)
            needsDisplay = true
            renderGPU()
        }
        syncViewState()
    }

    /// Publishes the live view transform to AppState for the navigator (V1.2).
    /// Cheap (a handful of Doubles) and deferred inside AppState, so calling
    /// it after every transform mutation is fine.
    private func syncViewState() {
        state?.reportViewTransform(
            zoom: Double(zoom), panX: Double(pan.width), panY: Double(pan.height),
            rotationDeg: Double(rotationDeg), canvasSize: bounds.size)
    }

    // MARK: keymap (spec §3)

    /// Whether the shell is currently editing text or composing with an IME, in
    /// which case the core keymap must not steal keys (B15 / B21 lessons). The
    /// M1 shell has no in-canvas text fields, but the toolbar ColorPicker and a
    /// future text tool do, so we honor the first responder's field editor and
    /// its marked-text (IME composition) state.
    private func textInputGuards() -> (composing: Bool, editing: Bool) {
        guard let responder = window?.firstResponder else { return (false, false) }
        // A field editor (NSTextView backing a text field) means text editing.
        let editing = responder is NSText || responder is NSTextView
        var composing = false
        if let tv = responder as? NSTextView {
            composing = tv.hasMarkedText()
        }
        return (composing, editing)
    }

    /// Maps an NSEvent to the corresponding physical-key code the C ABI expects.
    /// Uses hardware `keyCode` so it is keyboard-layout independent (JIS/US),
    /// which is what recovers `[` `]` `-` `^` when the produced character differs
    /// by layout or is swallowed by an IME (spec §3, cross-platform rule 5).
    private func physicalCode(for event: NSEvent) -> Int32 {
        // macOS ANSI/JIS virtual key codes (Carbon kVK_*). Stable across layouts.
        switch event.keyCode {
        case 35: return Int32(AKAPEN_PK_P)
        case 14: return Int32(AKAPEN_PK_E)
        case 32: return Int32(AKAPEN_PK_U)
        case 0: return Int32(AKAPEN_PK_A)
        case 15: return Int32(AKAPEN_PK_R)
        case 31: return Int32(AKAPEN_PK_O)
        case 17: return Int32(AKAPEN_PK_T)
        case 34: return Int32(AKAPEN_PK_I)
        case 7: return Int32(AKAPEN_PK_X)
        case 8: return Int32(AKAPEN_PK_C)
        case 6: return Int32(AKAPEN_PK_Z)
        case 16: return Int32(AKAPEN_PK_Y)
        case 29: return Int32(AKAPEN_PK_DIGIT0) // top-row 0
        case 49: return Int32(AKAPEN_PK_SPACE)
        case 33: return Int32(AKAPEN_PK_BRACKET_LEFT)
        case 30: return Int32(AKAPEN_PK_BRACKET_RIGHT)
        case 27: return Int32(AKAPEN_PK_MINUS)
        // keyCode 24 is JIS '^' *and* US '=' at the same physical position.
        // Do NOT map it to AKAPEN_PK_CARET here: JIS '^' already resolves via
        // the character-first path (produced char is '^'), and if this were
        // mapped physically too, a US keyboard producing '=' at that position
        // (which has no character-level rotate meaning) would incorrectly
        // resolve to RotateRight through the physical fallback. Leaving it as
        // AKAPEN_PK_OTHER means: JIS '^' still rotates (character match), US
        // '=' does nothing (spec §3 — no physical CARET fallback needed).
        case 116: return Int32(AKAPEN_PK_PAGE_UP)
        case 121: return Int32(AKAPEN_PK_PAGE_DOWN)
        // V1.2 (Windows V1.1 の写像): Photoshop プリセットのキーと矢印。
        case 11: return Int32(AKAPEN_PK_B)
        case 18: return Int32(AKAPEN_PK_DIGIT1) // top-row 1
        case 123: return Int32(AKAPEN_PK_ARROW_LEFT)
        case 124: return Int32(AKAPEN_PK_ARROW_RIGHT)
        case 126: return Int32(AKAPEN_PK_ARROW_UP)
        case 125: return Int32(AKAPEN_PK_ARROW_DOWN)
        default: return Int32(AKAPEN_PK_OTHER)
        }
    }

    /// Runs the event through the core key map, returning the resolved action
    /// code (or nil for AKAPEN_ACT_NONE).
    private func resolveAction(for event: NSEvent) -> Int32? {
        let (composing, editing) = textInputGuards()
        // `primary` = platform accelerator: Command on macOS (cross-platform
        // rule 1). The character is taken ignoring Cmd/Option so US layouts see
        // the base character.
        let scalar = event.charactersIgnoringModifiers?.unicodeScalars.first?.value ?? 0
        let flags = event.modifierFlags
        // V1.2: resolve against the user-selected keymap preset (default
        // Photoshop, spec §10-14). The preset code mirrors AKAPEN_KEYMAP_*.
        let preset = AkapenKeymapPreset.from(
            raw: UserDefaults.standard.string(forKey: AkapenSettingsKey.keymapPreset))
        let code = akapen_resolve_key_preset(
            preset.code,
            scalar,
            physicalCode(for: event),
            flags.contains(.command) ? 1 : 0,
            flags.contains(.shift) ? 1 : 0,
            flags.contains(.option) ? 1 : 0,
            composing ? 1 : 0,
            editing ? 1 : 0)
        return code == Int32(AKAPEN_ACT_NONE) ? nil : code
    }

    /// Applies a resolved action. Returns true if it was handled (so keyDown
    /// stops propagation), false to let the event fall through to the menu/OS.
    private func dispatch(_ action: Int32) -> Bool {
        guard let state, state.engine != nil else { return false }
        switch action {
        case Int32(AKAPEN_ACT_TOOL_PEN):
            state.arrowMode = false
            state.tool = .pen
            state.applyToolState()
        case Int32(AKAPEN_ACT_TOOL_ERASER):
            state.arrowMode = false
            state.tool = .eraser
            state.applyToolState()
        case Int32(AKAPEN_ACT_TOOL_ARROW):
            state.arrowMode = true
        case Int32(AKAPEN_ACT_UNDO):
            state.undo()
        case Int32(AKAPEN_ACT_REDO):
            state.redo()
        case Int32(AKAPEN_ACT_ZOOM_IN):
            zoomBy(1.25)
        case Int32(AKAPEN_ACT_ZOOM_OUT):
            zoomBy(1.0 / 1.25)
        case Int32(AKAPEN_ACT_FIT):
            fitToWindow()
        case Int32(AKAPEN_ACT_ACTUAL_SIZE):
            setActualSize()
        case Int32(AKAPEN_ACT_ROTATE_LEFT):
            rotate(by: -15)
        case Int32(AKAPEN_ACT_ROTATE_RIGHT):
            rotate(by: 15)
        case Int32(AKAPEN_ACT_BRUSH_SMALLER):
            adjustBrush(-1)
        case Int32(AKAPEN_ACT_BRUSH_LARGER):
            adjustBrush(1)
        case Int32(AKAPEN_ACT_NEXT_FRAME):
            state.step(forward: true)
        case Int32(AKAPEN_ACT_PREV_FRAME):
            state.step(forward: false)
        // M3 tools/colors (mapped by the core but not acted on at M1): report
        // as unhandled so the key is not silently eaten.
        default:
            return false
        }
        return true
    }

    /// Zooms about the view center, matching the scroll-wheel clamp.
    private func zoomBy(_ factor: CGFloat) {
        zoom = max(0.02, min(20, zoom * factor))
        needsDisplay = true
        renderGPU()
        syncViewState()
    }

    /// 100% view (1 image pixel : 1 view point), centered.
    private func setActualSize() {
        zoom = 1
        pan = .zero
        needsDisplay = true
        renderGPU()
        syncViewState()
    }

    private func adjustBrush(_ delta: Double) {
        guard let state else { return }
        state.brushSize = max(1, min(50, state.brushSize + delta))
        state.applyToolState()
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
