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

    /// Saves the 3-file export into the resolved output directory (spec §4.3).
    /// 出力先とファイル名の接尾辞は §4.7 の設定に従う(既定は
    /// `<入力フォルダ>/_review/` と `review` / `strokes`)。設定側の入力検証を
    /// すり抜けた無効値も、ここと C ABI 側 `sanitize_suffix` で二段構えに
    /// フォールバックする。
    @discardableResult
    func save() -> Bool {
        guard let e = engine, let url = currentURL else { return false }
        let (dir, fallbackWarning) = resolveOutputDir(input: url)
        let naming = resolveOutputNaming()
        let stem = url.deletingPathExtension().lastPathComponent
        // 命名の接尾辞は engine に持たせる(§4.7 の setter パターン)。ここで
        // 毎回上書きしておけば、設定変更が次の save から確実に反映される。
        e.setOutputNaming(flatSuffix: naming.flat, strokesSuffix: naming.strokes)
        do {
            try e.export(toDir: dir.path, stem: stem)
            hasUnsavedStrokes = false
            // フォールバックが起きたときは、成功メッセージがそれを上書きして
            // 消してしまわないよう同じ statusText に警告を合流させる(Codex
            // レビュー指摘: 警告が save 成功で見えなくなっていた)。
            var message = "Saved review for \(url.lastPathComponent) → \(dir.path)/"
            if let fallbackWarning {
                message += " (\(fallbackWarning))"
            }
            statusText = message
            return true
        } catch {
            statusText = "Save failed for \(url.lastPathComponent): "
                + "\(saveFailureCause(error)). Frame not changed."
            return false
        }
    }

    /// §4.7 の出力先モードに従って書き出しフォルダを決める。設定が無効(空・
    /// パス区切りや `.`/`..` を含むサブフォルダ名、存在しない固定パス、など)
    /// なら `<入力フォルダ>/_review/` にフォールバックする。フォールバックが
    /// 起きた場合は、その理由を第2要素で返す(呼び出し側の statusText に
    /// 合流させ、成功メッセージで警告が消えないようにするため)。
    private func resolveOutputDir(input: URL) -> (dir: URL, fallbackWarning: String?) {
        let defaults = UserDefaults.standard
        let modeRaw = defaults.string(forKey: AkapenSettingsKey.dirMode)
            ?? AkapenSettingsDefault.dirMode.rawValue
        let mode = AkapenOutputDirMode(rawValue: modeRaw) ?? AkapenSettingsDefault.dirMode

        switch mode {
        case .besideInput:
            let raw = defaults.string(forKey: AkapenSettingsKey.subfolderName)
                ?? AkapenSettingsDefault.subfolderName
            if isValidSubfolder(raw) {
                let name = raw.trimmingCharacters(in: .whitespacesAndNewlines)
                return (input.deletingLastPathComponent().appendingPathComponent(name), nil)
            }
            return (
                input.deletingLastPathComponent()
                    .appendingPathComponent(AkapenSettingsDefault.subfolderName),
                "設定のサブフォルダ名が無効なので \(AkapenSettingsDefault.subfolderName)/ にフォールバック"
            )

        case .fixedAbsolute:
            let raw = defaults.string(forKey: AkapenSettingsKey.fixedDir)
                ?? AkapenSettingsDefault.fixedDir
            if isValidFixedDir(raw) {
                return (URL(fileURLWithPath: raw.trimmingCharacters(in: .whitespacesAndNewlines)), nil)
            }
            return (
                input.deletingLastPathComponent()
                    .appendingPathComponent(AkapenSettingsDefault.subfolderName),
                "設定の固定パスが無効なので \(AkapenSettingsDefault.subfolderName)/ にフォールバック"
            )
        }
    }

    /// §4.7 の接尾辞設定を読み取る。無効な値はフィールドごとに default に落とす
    /// (C ABI 側でも同じ二段目チェックが走る)。
    private func resolveOutputNaming() -> (flat: String, strokes: String) {
        let defaults = UserDefaults.standard
        let flatRaw = defaults.string(forKey: AkapenSettingsKey.flatSuffix)
            ?? AkapenSettingsDefault.flatSuffix
        let strokesRaw = defaults.string(forKey: AkapenSettingsKey.strokesSuffix)
            ?? AkapenSettingsDefault.strokesSuffix
        let flat = isValidSuffix(flatRaw)
            ? flatRaw.trimmingCharacters(in: .whitespacesAndNewlines)
            : AkapenSettingsDefault.flatSuffix
        let strokes = isValidSuffix(strokesRaw)
            ? strokesRaw.trimmingCharacters(in: .whitespacesAndNewlines)
            : AkapenSettingsDefault.strokesSuffix
        return (flat, strokes)
    }

    /// Maps an export failure to a short, human-readable cause for the status bar
    /// (kept in sync with `AkapenExportError` / the C ABI codes).
    private func saveFailureCause(_ error: Error) -> String {
        switch error {
        case AkapenExportError.createDirFailed: return "couldn't create _review folder"
        case AkapenExportError.encodeFailed: return "couldn't encode annotation data"
        case AkapenExportError.writeFailed: return "couldn't write review files"
        case AkapenExportError.invalidArgs: return "invalid save request"
        case AkapenExportError.unknown(let code): return "unknown error (code \(code))"
        default: return "unexpected error"
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
        // 保存に失敗した場合は現フレームを維持する(データ損失防止。statusText は
        // save() が失敗理由を設定済み)。
        if hasUnsavedStrokes {
            guard save() else { return } // 既存の _review/ 命名・衝突回避(-2,-3)経路をそのまま使う
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
