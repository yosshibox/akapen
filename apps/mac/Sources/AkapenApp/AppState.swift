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
    @Published var color: Color = AkapenPalette.defaultColor.color
    /// 右側パレットで選択中のスウォッチ(ハイライト表示用)。ColorPicker などで
    /// パレット外の色を選んだ場合は nil になる。
    @Published var selectedColorHex: String? = AkapenPalette.defaultColor.hex
    @Published var pressureCurve: AkapenPressureCurve = .normal
    /// Raised when the core reports a constant-pressure stroke (spec §5.4).
    @Published var pressureWarning = false
    @Published var statusText = "Open an image to begin."

    /// このフレームに未保存の描き込み(完了ストローク)があるか。フレーム切替時の
    /// 自動保存(§4.5)の判定に使う。C ABI はストローク数を公開しないため、シェル側で
    /// ストローク完了(pointer .up)を数えて追跡する。open / save でクリアされる。
    private(set) var hasUnsavedStrokes = false

    /// Bumped whenever the composited image changes, so the canvas redraws.
    @Published var revision = 0

    /// Whether to attempt the GPU (wgpu/Metal) render path (spec §7.4-6,
    /// Phase e). Default true; disabled by the `AKAPEN_NO_GPU` environment
    /// variable or a `--no-gpu` launch argument, for A/B testing and as an
    /// escape hatch. When attach fails at runtime, the canvas falls back to the
    /// CPU composite path regardless of this flag.
    let useGPU: Bool = {
        if ProcessInfo.processInfo.arguments.contains("--no-gpu") { return false }
        if let v = ProcessInfo.processInfo.environment["AKAPEN_NO_GPU"], !v.isEmpty, v != "0" {
            return false
        }
        return true
    }()

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
        hasUnsavedStrokes = false
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
            hasUnsavedStrokes = true // 1ストローク完了 = このフレームは要保存
        }
        revision += 1
    }

    func undo() { engine?.undo(); revision += 1 }
    func redo() { engine?.redo(); revision += 1 }

    /// 右側パレットのスウォッチをタップしたときの選択(spec 2026-07-11)。
    func selectColor(hex: String) {
        color = Color(hex: hex)
        selectedColorHex = hex
        applyToolState()
    }

    /// ツールバーの ColorPicker などパレット外から任意色が選ばれたときの選択。
    /// パレットのハイライトは外す。
    func setArbitraryColor(_ c: Color) {
        color = c
        selectedColorHex = nil
        applyToolState()
    }

    /// Saves the 3-file export into `<input folder>/_review/` (spec §4.3).
    @discardableResult
    func save() -> Bool {
        guard let e = engine, let url = currentURL else { return false }
        let dir = url.deletingLastPathComponent().appendingPathComponent("_review")
        let stem = url.deletingPathExtension().lastPathComponent
        if e.export(toDir: dir.path, stem: stem) {
            hasUnsavedStrokes = false
            statusText = "Saved review for \(url.lastPathComponent) → _review/"
            return true
        } else {
            statusText = "Save failed."
            return false
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
        // フレームを切り替えるときは、描き込みがあれば自動保存してから移動する
        // (§4.5: 確認ダイアログではなく自動保存。非ダーティ時は空ファイルを作らない)。
        if hasUnsavedStrokes {
            save() // 既存の _review/ 命名・衝突回避(-2,-3)経路をそのまま使う
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
