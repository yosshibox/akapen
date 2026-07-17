// App-level state for the Akapen mac shell. Holds the Rust engine handle, the
// current image + its sibling sequence, and the tool/view state the UI binds
// to. No drawing logic lives here (that is the Rust core); this only marshals
// intent to the engine and exposes the composited image for display.

import AkapenKit
import AkapenUIContract
import AppKit
import Foundation
import SwiftUI

enum CanvasCommand: Equatable {
    case draw
    case pan
    case zoomIn
    case zoomOut
    case rotateLeft
    case rotateRight
    case fit
    case actualSize
    /// Navigator click/drag (V1.2): center the view on this image pixel.
    case centerOn(Double, Double)
}

/// Portable UI command IDs shared by the Mac dock and future Windows/VEDA
/// adapters. The Mac shell currently maps these to SwiftUI actions/popovers.
enum AkapenStyleCommand: String, CaseIterable {
    case brushSize = "style.size"
    case color = "style.color"
    case pressure = "style.pressure"
    case opacity = "style.opacity"
}

@MainActor
final class AppState: ObservableObject {
    @Published private(set) var workspacePhase: WorkspacePhase = .empty
    @Published var engine: AkapenEngine?
    @Published var currentURL: URL?
    @Published var siblings: [URL] = []
    @Published var tool: AkapenTool = .pen
    /// V1.2 (Windows V1.1 parity): 矢印ツール(操作なし)。true の間はポインタ
    /// 入力を描画に流さない。シェル専用状態で、コアの Tool とは独立。
    @Published var arrowMode = false
    @Published var brushSize: Double = 14
    @Published var color: Color = AkapenPalette.defaultColor.color
    /// 右側パレットで選択中のスウォッチ(ハイライト表示用)。ColorPicker などで
    /// パレット外の色を選んだ場合は nil になる。
    @Published var selectedColorHex: String? = AkapenPalette.defaultColor.hex
    @Published var pressureCurve: AkapenPressureCurve = .normal
    /// The core currently renders strokes opaque. Keep the portable value in
    /// shell state so a future opacity API can be wired without changing the
    /// dock contract; the control remains disabled until then.
    @Published var opacity: Double = 1
    /// Raised when the core reports a constant-pressure stroke (spec §5.4).
    @Published var pressureWarning = false
    @Published var statusText = "Open an image to begin."
    @Published private(set) var canUndo = false
    @Published private(set) var canRedo = false
    @Published var canvasCommand: CanvasCommand?

    /// このフレームに未保存の描き込み(完了ストローク)があるか。フレーム切替時の
    /// 自動保存(§4.5)の判定に使う。C ABI はストローク数を公開しないため、シェル側で
    /// ストローク完了(pointer .up)を数えて追跡する。open / save でクリアされる。
    private(set) var hasUnsavedStrokes = false

    /// Bumped whenever the composited image changes, so the canvas redraws.
    @Published var revision = 0

    // ── Navigator (V1.2) ─────────────────────────────────────────────────
    // The canvas view reports its live transform here so the navigator can
    // draw the viewport polygon; the thumbnail is re-read from the core only
    // on content changes (open / stroke end / undo / redo), never per frame.
    @Published var viewZoom: Double = 1
    @Published var viewPanX: Double = 0
    @Published var viewPanY: Double = 0
    @Published var viewRotationDeg: Double = 0
    @Published var canvasSize: CGSize = .zero
    @Published var navThumbnail: NSImage?
    /// Natural size of the loaded image (navigator mapping).
    @Published var imageSize: CGSize = .zero

    /// Called by the canvas whenever zoom/pan/rotation/bounds change.
    func reportViewTransform(zoom: Double, panX: Double, panY: Double,
                             rotationDeg: Double, canvasSize: CGSize) {
        // Deferred: the canvas reports from within SwiftUI's update pass
        // (updateNSView → refresh), where publishing directly is not allowed.
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            if self.viewZoom != zoom { self.viewZoom = zoom }
            if self.viewPanX != panX { self.viewPanX = panX }
            if self.viewPanY != panY { self.viewPanY = panY }
            if self.viewRotationDeg != rotationDeg { self.viewRotationDeg = rotationDeg }
            if self.canvasSize != canvasSize { self.canvasSize = canvasSize }
        }
    }

    /// Re-reads the navigator thumbnail from the core (content changes only).
    func refreshNavigatorThumbnail() {
        guard let e = engine, let thumb = e.thumbnail() else {
            navThumbnail = nil
            return
        }
        navThumbnail = nsImage(fromRGBA: thumb.rgba, width: thumb.width, height: thumb.height)
    }

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

    // ── Frame-navigation cache (V1.2, Windows §4.5 の写像) ────────────────
    // Decoded documents keyed by URL. Two sources: preloaded neighbors and
    // documents swapped out by akapen_swap_document (which then preserve
    // per-image strokes/undo across back-and-forth navigation). Decode runs
    // off the main actor; the GPU surface never re-attaches on navigation.
    private var documentCache: [URL: AkapenEngine] = [:]
    private var cacheOrder: [URL] = []
    private var preloadInFlight: Set<URL> = []
    private var dirtyDocs: Set<URL> = []
    private let preloadRadius = 12
    private let cacheLimit = 30

    func open(url: URL) {
        if let cached = takeCached(url) {
            activate(document: cached, url: url)
            return
        }
        workspacePhase = .loading
        Task.detached(priority: .userInitiated) { [weak self] in
            let decoded = AkapenEngine(imagePath: url.path)
            await MainActor.run { self?.finishOpen(url: url, decoded: decoded) }
        }
    }

    func openFolder(url: URL) {
        let items = (try? FileManager.default.contentsOfDirectory(
            at: url, includingPropertiesForKeys: nil)) ?? []
        guard let first = items
            .filter({ supportedExts.contains($0.pathExtension.lowercased()) })
            .sorted(by: { naturalLess($0.lastPathComponent, $1.lastPathComponent) })
            .first
        else {
            statusText = "このフォルダに対応画像がありません。"
            workspacePhase = engine == nil ? .empty : .loaded
            return
        }
        open(url: first)
    }

    func openDroppedURL(_ url: URL) {
        var isDirectory: ObjCBool = false
        if FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory), isDirectory.boolValue {
            openFolder(url: url)
        } else {
            open(url: url)
        }
    }

    private func finishOpen(url: URL, decoded: AkapenEngine?) {
        guard let e = decoded else {
            statusText = "Could not open \(url.lastPathComponent)."
            workspacePhase = engine == nil ? .empty : .loaded
            return
        }
        activate(document: e, url: url)
    }

    /// Makes `document` the displayed document. When an engine (and its GPU
    /// surface) already exists, the documents are swapped in place —
    /// navigation never tears down the swapchain — and the previous document
    /// goes into the cache with its strokes/undo intact (spec §4.5).
    private func activate(document: AkapenEngine, url: URL) {
        if let active = engine, active !== document {
            if active.swapDocument(with: document) {
                if let prev = currentURL { storeCache(prev, document) }
            } else {
                engine = document // fallback: full replace (GPU re-attach)
            }
        } else if engine == nil {
            engine = document
        }
        guard let e = engine else { return }
        currentURL = url
        applyToolState()
        loadSiblings(of: url)
        pressureWarning = false
        hasUnsavedStrokes = dirtyDocs.contains(url)
        refreshHistory()
        let (w, h) = e.size
        statusText = "\(url.lastPathComponent) — \(w)×\(h)"
        workspacePhase = .loaded
        imageSize = CGSize(width: w, height: h)
        refreshNavigatorThumbnail()
        revision += 1
        preloadNeighbors(of: url)
    }

    private func takeCached(_ url: URL) -> AkapenEngine? {
        guard let cached = documentCache.removeValue(forKey: url) else { return nil }
        cacheOrder.removeAll { $0 == url }
        return cached
    }

    private func storeCache(_ url: URL, _ document: AkapenEngine) {
        if documentCache[url] == nil { cacheOrder.append(url) }
        documentCache[url] = document
        // Evict beyond the cap, oldest first, but never a document that still
        // holds unsaved strokes (Windows parity: session documents survive).
        while cacheOrder.count > cacheLimit {
            guard let victim = cacheOrder.first(where: { !dirtyDocs.contains($0) }) else { break }
            cacheOrder.removeAll { $0 == victim }
            documentCache.removeValue(forKey: victim)
        }
    }

    /// Decodes ±preloadRadius siblings in the background (Windows §4.5:
    /// 前後の先読みで切替を体感ゼロにする).
    private func preloadNeighbors(of url: URL) {
        guard let idx = siblings.firstIndex(of: url) else { return }
        var targets: [URL] = []
        for offset in 1...preloadRadius {
            for candidate in [idx + offset, idx - offset]
            where siblings.indices.contains(candidate) {
                targets.append(siblings[candidate])
            }
        }
        for target in targets
        where documentCache[target] == nil && !preloadInFlight.contains(target) {
            preloadInFlight.insert(target)
            Task.detached(priority: .utility) { [weak self] in
                let decoded = AkapenEngine(imagePath: target.path)
                await MainActor.run {
                    guard let self else { return }
                    self.preloadInFlight.remove(target)
                    if let decoded, self.documentCache[target] == nil, self.currentURL != target {
                        self.storeCache(target, decoded)
                    }
                }
            }
        }
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
            if let url = currentURL { dirtyDocs.insert(url) }
            refreshHistory()
            refreshNavigatorThumbnail()
        }
        revision += 1
    }

    func undo() {
        guard let e = engine, e.canUndo else { return }
        e.undo()
        refreshHistory()
        refreshNavigatorThumbnail()
        revision += 1
    }

    func redo() {
        guard let e = engine, e.canRedo else { return }
        e.redo()
        refreshHistory()
        refreshNavigatorThumbnail()
        revision += 1
    }

    func requestCanvas(_ command: CanvasCommand) {
        canvasCommand = command
    }

    private func refreshHistory() {
        canUndo = engine?.canUndo ?? false
        canRedo = engine?.canRedo ?? false
    }

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

    /// Hex is the portable color editing contract. The current C ABI accepts
    /// RGB and forces alpha opaque, so an optional alpha suffix is displayed
    /// but safely ignored by `packedRGBA` until the core exposes it.
    func setColorHex(_ hex: String) {
        let normalized = hex.trimmingCharacters(in: .whitespacesAndNewlines)
        guard Color.isValidHex(normalized) else { return }
        color = Color(hex: normalized)
        selectedColorHex = normalized.hasPrefix("#") ? normalized.uppercased() : "#" + normalized.uppercased()
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
            if let url = currentURL { dirtyDocs.remove(url) }
            refreshHistory()
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
