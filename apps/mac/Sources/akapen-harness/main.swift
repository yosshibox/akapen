// Headless proof harness (spec §9 M1): drives the Rust core through the Swift
// FFI wrapper to draw pressure-varying strokes and write the 3-file export.
//
// This verifies the Swift↔Rust boundary and the whole draw→bake→save pipeline
// on macOS without needing a GUI or a tablet. The interactive pressure
// acceptance gate (spec §5.6) is a separate manual test on real WACOM hardware.
//
// Usage:
//   swift run akapen-harness [outputDir]
// Writes <outputDir>/harness.review.png (flat), .strokes.png and .strokes.json,
// and copies the flat image to /tmp/akapen-m1.png for inspection.

import AkapenKit
import Foundation

let args = CommandLine.arguments
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

let ok = engine.export(toDir: outDir, stem: "harness")
guard ok else {
    FileHandle.standardError.write(Data("export failed\n".utf8))
    exit(2)
}

let flat = outDir + "/harness.review.png"
print("exported 3-file set to \(outDir)")

// Copy the flat image to the well-known inspection path.
let inspect = "/tmp/akapen-m1.png"
try? FileManager.default.removeItem(atPath: inspect)
try? FileManager.default.copyItem(atPath: flat, toPath: inspect)
print("flat image copied to \(inspect)")
print("OK")
