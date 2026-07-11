# bindings

Language bindings that wrap the Rust core's C ABI (spec §7.2 / §8.2). All are
**scaffolds at M0** — present so the tri-face build discipline (spec §9) has a
home from day one. They are implemented in later milestones:

- `swift/`  — swift-bridge / UniFFI wrapper for the mac SwiftUI shell (M1).
- `dotnet/` — csbindgen-generated C# bindings for the WinUI 3 shell (M2).
- `node/`   — napi-rs Node native addon `@akapen/core-node` for VEDA (M5).
- `wasm/`   — wasm-bindgen `@akapen/core-wasm` for pure-logic reuse (M5).

The compatibility contract these expose is the 3-file export + `VectorDoc`
schema (`veda-annot-1`, per-point pressure required).
