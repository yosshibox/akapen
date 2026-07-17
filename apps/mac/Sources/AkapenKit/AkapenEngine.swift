// Idiomatic Swift wrapper around the Akapen C ABI (spec §7.2).
//
// Owns the opaque engine handle and turns the C surface into a small, safe
// Swift API the SwiftUI shell drives. No business logic lives here — it only
// marshals values across the boundary.

import CAkapen
import Foundation

public enum AkapenTool: Int32, Hashable {
    case pen = 0
    case eraser = 1
}

public enum AkapenPointerKind: Int32 {
    case pen = 0
    case touch = 1
    case mouse = 2
}

public enum AkapenPhase: Int32 {
    case down = 0
    case move = 1
    case up = 2
}

public enum AkapenPressureCurve: Int32, Hashable {
    case normal = 0
    case soft = 1
    case hard = 2
}

/// Where a classified pointer event should go (palm rejection, spec §5.2).
public enum AkapenRouting: Int32 {
    /// Feed the drawing engine (pen, or a mouse fallback).
    case draw = 0
    /// Use for canvas pan/pinch, not drawing (a deliberate touch, no pen).
    case navigate = 1
    /// Drop entirely — a palm touch during pen contact or the pen lock.
    case ignore = 2
}

/// Palm-rejection gate (spec §5.2): the small state machine the Rust core owns,
/// wrapped so the shell can ask each classified pointer event whether it should
/// reach the drawing path. Engine-independent (no handle) — the same shape as
/// the core key map. Hold one per canvas; it carries the pen-priority lock.
public struct PalmGate {
    private var state = AkapenPalmState(pen_down: 0, lock_active: 0, lock_until_ms: 0)

    public init() {}

    /// Routes one classified pointer event, updating the pen-priority lock.
    /// `nowMs` is a monotonic timestamp in milliseconds (e.g.
    /// `Int64(event.timestamp * 1000)`). Touches during pen contact or within
    /// the lock come back as `.ignore`; a deliberate touch as `.navigate`; pen
    /// and mouse as `.draw`.
    public mutating func route(
        kind: AkapenPointerKind, phase: AkapenPhase, nowMs: Int64
    ) -> AkapenRouting {
        let code = withUnsafeMutablePointer(to: &state) {
            akapen_palm_route($0, kind.rawValue, phase.rawValue, nowMs)
        }
        return AkapenRouting(rawValue: code) ?? .draw
    }
}

/// Failure kinds from the 3-file export. Each maps to a distinct non-zero
/// return code of `akapen_export_to_dir` (crates/akapen-ffi/src/lib.rs); the
/// UI turns these into a human-readable cause (spec §4.5, data-loss guard).
public enum AkapenExportError: Error {
    case invalidArgs // rc 1: null handle / bad path
    case createDirFailed // rc 2: could not create the output (_review) folder
    case encodeFailed // rc 3: could not serialize the vector annotation JSON
    case writeFailed // rc 4: could not write one of the export files
    case unknown(Int32) // any other non-zero code
}

/// Calls `body` with a NUL-terminated UTF-8 C string for `s`, or with a NULL
/// pointer when `s == nil`. Used by the setter wrappers whose C ABI accepts
/// NULL as "reset that argument to default" (e.g. `akapen_set_output_naming`).
func withOptionalCString<R>(
    _ s: String?, _ body: (UnsafePointer<CChar>?) -> R
) -> R {
    if let s = s {
        return s.withCString { body($0) }
    }
    return body(nil)
}

/// A decoded RGBA frame the UI can render.
public struct AkapenImage {
    public let width: Int
    public let height: Int
    public let rgba: [UInt8]
}

public final class AkapenEngine {
    private let handle: OpaquePointer

    /// Opens an image file (png/jpg/webp/bmp). Returns nil on decode failure.
    public init?(imagePath: String) {
        guard let h = imagePath.withCString({ akapen_open_image($0) }) else {
            return nil
        }
        handle = h
    }

    /// Blank white canvas of the given size.
    public init?(width: Int, height: Int) {
        guard width > 0, height > 0,
              let h = akapen_new(UInt32(width), UInt32(height))
        else { return nil }
        handle = h
    }

    deinit {
        akapen_free(handle)
    }

    public var size: (width: Int, height: Int) {
        var w: UInt32 = 0
        var h: UInt32 = 0
        akapen_size(handle, &w, &h)
        return (Int(w), Int(h))
    }

    public func setTool(_ tool: AkapenTool) {
        akapen_set_tool(handle, Int32(tool.rawValue))
    }

    /// Packed 0xRRGGBBAA.
    public func setColor(_ rgba: UInt32) {
        akapen_set_color(handle, rgba)
    }

    public func setSize(_ px: Float) {
        akapen_set_size(handle, px)
    }

    public func setPressureCurve(_ curve: AkapenPressureCurve) {
        akapen_set_pressure_curve(handle, Int32(curve.rawValue))
    }

    /// Feeds one normalized pointer sample (image-native pixel coords).
    public func pointer(x: Double, y: Double, pressure: Double,
                        kind: AkapenPointerKind, phase: AkapenPhase) {
        akapen_pointer(handle, x, y, pressure,
                       Int32(kind.rawValue), Int32(phase.rawValue))
    }

    public func undo() { akapen_undo(handle) }
    public func redo() { akapen_redo(handle) }

    /// Whether the core has a committed stroke available for undo.
    /// The C ABI is null-safe; this wrapper owns a valid handle, so no optional
    /// Rust/C state leaks into the SwiftUI layer.
    public var canUndo: Bool {
        akapen_can_undo(handle) != 0
    }

    /// Whether the core has an undone stroke available for redo.
    public var canRedo: Bool {
        akapen_can_redo(handle) != 0
    }

    /// True if the last pen stroke carried no pressure variation (spec §5.4).
    public var pressureStuck: Bool {
        akapen_pressure_stuck(handle) != 0
    }

    /// The composited display image (background + strokes + in-progress stroke).
    public func compositeImage() -> AkapenImage {
        let (w, h) = size
        let needed = akapen_composite_rgba(handle, nil, 0)
        var buf = [UInt8](repeating: 0, count: needed)
        buf.withUnsafeMutableBufferPointer { p in
            _ = akapen_composite_rgba(handle, p.baseAddress, p.count)
        }
        return AkapenImage(width: w, height: h, rgba: buf)
    }

    /// Navigator thumbnail (V1.2): the display composite downscaled by the
    /// core to fit `maxW` × `maxH` (aspect preserved, never upscaled). Returns
    /// nil when no image is loaded. Same size-probe convention as
    /// `compositeImage`, but the reduction happens on the Rust side so the
    /// shell never copies the full-resolution composite.
    public func thumbnail(maxW: UInt32 = 256, maxH: UInt32 = 256) -> AkapenImage? {
        var w: UInt32 = 0
        var h: UInt32 = 0
        let needed = akapen_thumbnail_rgba(handle, maxW, maxH, nil, 0, &w, &h)
        guard needed > 0, w > 0, h > 0 else { return nil }
        var buf = [UInt8](repeating: 0, count: needed)
        let written = buf.withUnsafeMutableBufferPointer { p in
            akapen_thumbnail_rgba(handle, maxW, maxH, p.baseAddress, p.count, &w, &h)
        }
        guard written == needed else { return nil }
        return AkapenImage(width: Int(w), height: Int(h), rgba: buf)
    }

    /// Sets the artifact-name suffixes (spec §4.7). Passing `nil` for either
    /// argument resets *only* that field to the built-in default (`review` /
    /// `strokes`). The C ABI additionally rejects suffixes that contain a path
    /// separator or a dot; the SwiftUI settings panel does the same up front,
    /// so callers here can pass through user input directly.
    public func setOutputNaming(flatSuffix: String?, strokesSuffix: String?) {
        // `withOptionalCString` lets us pass NULL for a nil argument (matching
        // the C ABI's "reset that field to default" semantics) without needing
        // a separate branch.
        withOptionalCString(flatSuffix) { flat in
            withOptionalCString(strokesSuffix) { strokes in
                akapen_set_output_naming(handle, flat, strokes)
            }
        }
    }

    /// Writes the 3-file export into `dir` using `stem` as the base name.
    /// Throws `AkapenExportError` on failure so callers can surface the cause
    /// (the C ABI returns a distinct non-zero code per failure kind).
    public func export(toDir dir: String, stem: String) throws {
        let rc = dir.withCString { cdir in
            stem.withCString { cstem in
                akapen_export_to_dir(handle, cdir, cstem)
            }
        }
        // Codes mirror `akapen_export_to_dir` in crates/akapen-ffi/src/lib.rs.
        switch rc {
        case 0: return
        case 1: throw AkapenExportError.invalidArgs
        case 2: throw AkapenExportError.createDirFailed
        case 3: throw AkapenExportError.encodeFailed
        case 4: throw AkapenExportError.writeFailed
        default: throw AkapenExportError.unknown(rc)
        }
    }

    // MARK: - GPU surface path (spec §7.4-6, Phase e)
    //
    // Optional and additive: the CPU composite path above works with or without
    // a GPU surface attached. All of these must be called from the same (main)
    // thread that attached the surface, since attaching wires a CAMetalLayer
    // into the given NSView.

    /// The on-screen view transform for one GPU frame, in physical pixels.
    /// `scale` is the shell's zoom multiplied by the backing scale (uniform);
    /// `center` is the displayed image center in physical pixels.
    public struct ViewTransform {
        public var centerX: Float
        public var centerY: Float
        public var scale: Float
        public var rotationDeg: Float
        public init(centerX: Float, centerY: Float, scale: Float, rotationDeg: Float) {
            self.centerX = centerX
            self.centerY = centerY
            self.scale = scale
            self.rotationDeg = rotationDeg
        }
    }

    /// Attaches a GPU render surface backed by the given `NSView` pointer
    /// (`AKAPEN_SURFACE_METAL_LAYER`: the pointer must be the `NSView*`, not a
    /// layer — wgpu inserts and manages its own `CAMetalLayer` sublayer).
    /// Returns `true` on success; on `false` the caller must keep using the CPU
    /// composite path. `width`/`height` are physical pixels.
    @discardableResult
    public func attachRender(
        nsView: UnsafeMutableRawPointer, width: UInt32, height: UInt32, scale: Float
    ) -> Bool {
        var desc = AkapenSurfaceDesc(
            kind: Int32(AKAPEN_SURFACE_METAL_LAYER),
            handle: nsView,
            display: nil,
            width: width,
            height: height,
            scale_factor: scale)
        return withUnsafePointer(to: &desc) { akapen_render_attach(handle, $0) } == 0
    }

    /// Re-configures the attached surface for a new physical pixel size /
    /// backing scale. No-op if nothing is attached.
    public func resizeRender(width: UInt32, height: UInt32, scale: Float) {
        akapen_render_resize(handle, width, height, scale)
    }

    /// Draws and presents one GPU frame through `view`. No-op if not attached.
    public func renderFrame(_ view: ViewTransform) {
        let vt = AkapenViewTransform(
            center_x: view.centerX,
            center_y: view.centerY,
            scale: view.scale,
            rotation_deg: view.rotationDeg)
        akapen_render_frame(handle, vt)
    }

    /// Detaches and tears down the GPU surface. Safe when nothing is attached.
    public func detachRender() {
        akapen_render_detach(handle)
    }

    /// True while a GPU surface is attached (GPU path active).
    public var renderAvailable: Bool {
        akapen_render_available(handle) != 0
    }
}
