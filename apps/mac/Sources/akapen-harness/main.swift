// Headless proof harness (spec §9 M1): drives the Rust core through the Swift
// FFI wrapper to draw pressure-varying strokes and write the 3-file export.
//
// This verifies the Swift↔Rust boundary and the whole draw→bake→save pipeline
// on macOS without needing a GUI or a tablet. The interactive pressure
// acceptance gate (spec §5.6) is a separate manual test on real WACOM hardware.
//
// Cross-platform golden check: use the same commands in
// `testdata/golden-export-v1.json` (64x48, two pen strokes with varying
// pressure) in a harness run, then compare the parsed `.strokes.json` schema,
// natural size, stroke/kind count, and every `points[].p` with the Rust and
// Node smoke tests. Also require `.review.png`, `.strokes.png`, and
// `.strokes.json`; do not compare platform UI pixels or reimplement export in
// Swift. Rust's export is the canonical result.
//
// Usage:
//   swift run akapen-harness [outputDir]
// Writes <outputDir>/harness.review.png (flat), .strokes.png and .strokes.json,
// and copies the flat image to /tmp/akapen-m1.png for inspection.

import AkapenKit
import Foundation

let args = CommandLine.arguments

// Frame-navigation micro-benchmark (V1.2 性能調査):
//   swift run -c release akapen-harness bench <folder>
// Measures decode / swapDocument / thumbnail / composite per image.
if args.count > 2, args[1] == "bench" {
    let dir = URL(fileURLWithPath: args[2])
    let exts = Set(["png", "jpg", "jpeg", "webp", "bmp"])
    let urls = ((try? FileManager.default.contentsOfDirectory(
        at: dir, includingPropertiesForKeys: nil)) ?? [])
        .filter { exts.contains($0.pathExtension.lowercased()) }
        .sorted { $0.lastPathComponent < $1.lastPathComponent }
    guard let first = urls.first, let active = AkapenEngine(imagePath: first.path) else {
        print("bench: no images in \(dir.path)")
        exit(1)
    }
    func ms(_ block: () -> Void) -> Double {
        let t0 = DispatchTime.now().uptimeNanoseconds
        block()
        return Double(DispatchTime.now().uptimeNanoseconds - t0) / 1_000_000
    }
    let (w, h) = active.size
    print("bench: \(urls.count) images, first \(w)x\(h)")
    for url in urls.dropFirst() {
        var standby: AkapenEngine?
        let tDecode = ms { standby = AkapenEngine(imagePath: url.path) }
        guard let standby else { continue }
        let tSwap = ms { _ = active.swapDocument(with: standby) }
        let tThumb = ms { _ = active.thumbnail() }
        let tComposite = ms { _ = active.compositeImage() }
        print(String(format: "%@ decode %7.1fms swap %6.1fms thumb %7.1fms composite %7.1fms",
                     url.lastPathComponent, tDecode, tSwap, tThumb, tComposite))
    }
    exit(0)
}

let outDir = args.count > 1 ? args[1] : NSTemporaryDirectory() + "akapen-m1"
try? FileManager.default.createDirectory(atPath: outDir, withIntermediateDirectories: true)

guard let engine = AkapenEngine(width: 640, height: 360) else {
    FileHandle.standardError.write(Data("failed to create engine\n".utf8))
    exit(1)
}

let (w, h) = engine.size
print("engine created: \(w)x\(h)")

// Red pen, a comfortable review size.
engine.setTool(.pen)
engine.setColor(0xFF00_00FF) // opaque red
engine.setSize(14)
engine.setPressureCurve(.normal)

// Draw a wavy "check" stroke whose pressure swells from light to heavy and back
// — this is what a pressure-varying pen stroke looks like to the core.
func stroke(_ pts: [(Double, Double, Double)]) {
    for (i, p) in pts.enumerated() {
        let phase: AkapenPhase = i == 0 ? .down : (i == pts.count - 1 ? .up : .move)
        engine.pointer(x: p.0, y: p.1, pressure: p.2, kind: .pen, phase: phase)
    }
}

var wave: [(Double, Double, Double)] = []
for i in 0...80 {
    let t = Double(i) / 80.0
    let x = 60.0 + t * 520.0
    let y = 180.0 + sin(t * .pi * 3) * 70.0
    // Pressure ramps 0.15 → 1.0 → 0.15.
    let pressure = 0.15 + 0.85 * sin(t * .pi)
    wave.append((x, y, pressure))
}
stroke(wave)

// A second, heavier underline stroke.
engine.setSize(20)
stroke([(80, 300, 1.0), (300, 300, 1.0), (560, 300, 1.0)])

print("pressure stuck warning: \(engine.pressureStuck)")

// Exercise undo/redo so the history path is covered too.
engine.undo()
engine.redo()

// ── Palm rejection (spec §5.2) ──
// Drive the core gate through the Swift↔Rust boundary and assert the routing
// contract, then confirm a *touch*-kind stroke sent to the engine never adds
// ink. The real liquid-tablet behavior is a §5.6 device-gate check, deferred.
func expect(_ got: AkapenRouting, _ want: AkapenRouting, _ what: String) {
    if got != want {
        FileHandle.standardError.write(Data("palm gate FAIL: \(what) = \(got), want \(want)\n".utf8))
        exit(3)
    }
}
var gate = PalmGate()
expect(gate.route(kind: .touch, phase: .down, nowMs: 0), .navigate, "touch alone → pan")
expect(gate.route(kind: .pen, phase: .down, nowMs: 10), .draw, "pen down draws")
expect(gate.route(kind: .touch, phase: .down, nowMs: 11), .ignore, "palm during pen")
expect(gate.route(kind: .pen, phase: .up, nowMs: 20), .draw, "pen up draws")     // lock → 520
expect(gate.route(kind: .touch, phase: .down, nowMs: 400), .ignore, "palm in lock")
expect(gate.route(kind: .touch, phase: .down, nowMs: 520), .navigate, "touch after lock")
expect(gate.route(kind: .mouse, phase: .down, nowMs: 600), .draw, "mouse always draws")
print("palm gate: routing contract OK")

// A touch-kind stroke must not add ink (core ignores Touch samples). Send one
// and confirm the exported stroke count is unchanged (still the 2 pen strokes).
engine.pointer(x: 100, y: 100, pressure: 1.0, kind: .touch, phase: .down)
engine.pointer(x: 200, y: 150, pressure: 1.0, kind: .touch, phase: .move)
engine.pointer(x: 300, y: 100, pressure: 1.0, kind: .touch, phase: .up)

do {
    try engine.export(toDir: outDir, stem: "harness")
} catch {
    FileHandle.standardError.write(Data("export failed: \(error)\n".utf8))
    exit(2)
}

// ── §4.7: 出力先接尾辞のカスタマイズ ──
// 追加の 3 点セットを別サフィックスで出し、ファイル名にちゃんと反映されるかを
// Swift↔Rust の境界越しに一度だけ確認する。GUI の Settings は headless では
// 触れないので、ここで最小限の境界チェックを兼ねる。
engine.setOutputNaming(flatSuffix: "akapen", strokesSuffix: "ink")
do {
    try engine.export(toDir: outDir, stem: "harness-custom")
} catch {
    FileHandle.standardError.write(Data("custom-naming export failed: \(error)\n".utf8))
    exit(6)
}
let customFlat = outDir + "/harness-custom.akapen.png"
let customStrokesPng = outDir + "/harness-custom.ink.png"
let customStrokesJSON = outDir + "/harness-custom.ink.json"
for p in [customFlat, customStrokesPng, customStrokesJSON] where !FileManager.default.fileExists(atPath: p) {
    FileHandle.standardError.write(Data("custom-naming export missing: \(p)\n".utf8))
    exit(7)
}
print("custom naming: 3-file set with (akapen/ink) suffixes OK")
// Reset back to the default suffix pair by passing nil, and make sure the
// pre-§4.7 layout (`review` / `strokes`) is restored.
engine.setOutputNaming(flatSuffix: nil, strokesSuffix: nil)
do {
    try engine.export(toDir: outDir, stem: "harness-reset")
} catch {
    FileHandle.standardError.write(Data("reset export failed: \(error)\n".utf8))
    exit(8)
}
if !FileManager.default.fileExists(atPath: outDir + "/harness-reset.review.png") {
    FileHandle.standardError.write(Data("reset export missing default suffix\n".utf8))
    exit(9)
}
print("custom naming: nil reset restored default suffixes OK")

// Verify no touch stroke leaked into the export (spec §5.2 / acceptance #5).
let strokesJSON = outDir + "/harness.strokes.json"
if let data = FileManager.default.contents(atPath: strokesJSON),
   let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
   let strokes = obj["strokes"] as? [[String: Any]] {
    if strokes.count != 2 {
        FileHandle.standardError.write(
            Data("touch leaked into ink: \(strokes.count) strokes, want 2\n".utf8))
        exit(4)
    }
    print("touch rejection: export has \(strokes.count) strokes (no touch ink) OK")
} else {
    FileHandle.standardError.write(Data("could not read \(strokesJSON)\n".utf8))
    exit(5)
}

let flat = outDir + "/harness.review.png"
print("exported 3-file set to \(outDir)")

// Copy the flat image to the well-known inspection path.
let inspect = "/tmp/akapen-m1.png"
try? FileManager.default.removeItem(atPath: inspect)
try? FileManager.default.copyItem(atPath: flat, toPath: inspect)
print("flat image copied to \(inspect)")
print("OK")
