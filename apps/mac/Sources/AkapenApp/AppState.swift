// App-level state for the Akapen mac shell. Holds the Rust engine handle, the
// current image + its sibling sequence, and the tool/view state the UI binds
// to. No drawing logic lives here (that is the Rust core); this only marshals
// intent to the engine and exposes the composited image for display.

import AkapenKit
import AppKit
import Foundation
import SwiftUI

@MainActor
final class AppState: ObservableObject {
    @Published var engine: AkapenEngine?
    @Published var currentURL: URL?
    @Published var siblings: [URL] = []
    @Published var tool: AkapenTool = .pen
    @Published var brushSize: Double = 14
    @Published var color: Color = .red
    @Published var pressureCurve: AkapenPressureCurve = .normal
    /// Raised when the core reports a constant-pressure stroke (spec §5.4).
    @Published var pressureWarning = false
    @Published var statusText = "Open an image to begin."

    /// Bumped whenever the composited image changes, so the canvas redraws.
    @Published var revision = 0

    let supportedExts: Set<String> = ["png", "jpg", "jpeg", "webp", "bmp"]

    func open(url: URL) {
        guard let e = AkapenEngine(imagePath: url.path) else {
            statusText = "Could not open \(url.lastPathComponent)."
            return
        }
        engine = e
        currentURL = url
        applyToolState()
        loadSiblings(of: url)
        pressureWarning = false
        let (w, h) = e.size
        statusText = "\(url.lastPathComponent) — \(w)×\(h)"
        revision += 1
    }

    private func loadSiblings(of url: URL) {
        let dir = url.deletingLastPathComponent()
        let items = (try? FileManager.default.contentsOfDirectory(
            at: dir, includingPropertiesForKeys: nil)) ?? []
        siblings = items
            .filter { supportedExts.contains($0.pathExtension.lowercased()) }
            .sorted { naturalLess($0.lastPathComponent, $1.lastPathComponent) }
    }

    func applyToolState() {
        guard let e = engine else { return }
        e.setTool(tool)
        e.setSize(Float(brushSize))
        e.setPressureCurve(pressureCurve)
        e.setColor(packedRGBA(from: color))
    }

    func pointer(imageX: Double, imageY: Double, pressure: Double,
                 kind: AkapenPointerKind, phase: AkapenPhase) {
        guard let e = engine else { return }
        e.pointer(x: imageX, y: imageY, pressure: pressure, kind: kind, phase: phase)
        if phase == .up {
            pressureWarning = e.pressureStuck
        }
        revision += 1
    }

    func undo() { engine?.undo(); revision += 1 }
    func redo() { engine?.redo(); revision += 1 }

    /// Saves the 3-file export into `<input folder>/_review/` (spec §4.3).
    func save() {
        guard let e = engine, let url = currentURL else { return }
        let dir = url.deletingLastPathComponent().appendingPathComponent("_review")
        let stem = url.deletingPathExtension().lastPathComponent
        if e.export(toDir: dir.path, stem: stem) {
            statusText = "Saved review for \(url.lastPathComponent) → _review/"
        } else {
            statusText = "Save failed."
        }
    }

    func step(forward: Bool) {
        guard let url = currentURL,
              let idx = siblings.firstIndex(of: url) else { return }
        let next = forward ? idx + 1 : idx - 1
        guard siblings.indices.contains(next) else {
            statusText = forward ? "Already at the last frame." : "Already at the first frame."
            return
        }
        open(url: siblings[next])
    }

    /// The current composited image as an NSImage for display.
    func compositeNSImage() -> NSImage? {
        guard let e = engine else { return nil }
        let img = e.compositeImage()
        return nsImage(fromRGBA: img.rgba, width: img.width, height: img.height)
    }
}

/// Packs a SwiftUI Color into 0xRRGGBBAA (alpha forced opaque; opacity is a
/// separate stroke property in the core).
func packedRGBA(from color: Color) -> UInt32 {
    let ns = NSColor(color).usingColorSpace(.sRGB) ?? .red
    let r = UInt32((ns.redComponent * 255).rounded())
    let g = UInt32((ns.greenComponent * 255).rounded())
    let b = UInt32((ns.blueComponent * 255).rounded())
    return (r << 24) | (g << 16) | (b << 8) | 0xFF
}

func nsImage(fromRGBA rgba: [UInt8], width: Int, height: Int) -> NSImage? {
    guard width > 0, height > 0, rgba.count == width * height * 4 else { return nil }
    var data = rgba
    let provider = CGDataProvider(data: Data(bytes: &data, count: data.count) as CFData)
    let colorSpace = CGColorSpaceCreateDeviceRGB()
    let bitmapInfo = CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue)
    // Note: our RGBA is straight alpha; for opaque review images the visual is
    // identical, and the flat export path (the source of truth) is correct.
    guard let provider,
          let cg = CGImage(
            width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32,
            bytesPerRow: width * 4, space: colorSpace, bitmapInfo: bitmapInfo,
            provider: provider, decode: nil, shouldInterpolate: true,
            intent: .defaultIntent)
    else { return nil }
    return NSImage(cgImage: cg, size: NSSize(width: width, height: height))
}

/// Human/natural filename ordering so `c2 < c10`.
func naturalLess(_ a: String, _ b: String) -> Bool {
    a.compare(b, options: .numeric) == .orderedAscending
}
