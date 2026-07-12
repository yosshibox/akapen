# bindings

Language bindings that wrap the Rust core's C ABI (spec §7.2 / §8.2).

At M1, the tri-face build discipline (spec §9 "M1 併走条件") is enforced by CI:

- `swift/`  — swift-bridge / UniFFI wrapper for the mac SwiftUI shell (M1
  proper lands here; the current mac shell in `../apps/mac` links the hand-
  written C ABI directly via a `CAkapen` module).
- `dotnet/` — csbindgen-generated C# P/Invoke bindings + a `net8.0` class
  library. Built in CI on Windows on every push (spec §9 M2 lands the shell).
- `node/`   — napi-rs Node native addon. Built in CI on Ubuntu on every push
  (spec §9 M5 lands the public npm package `@akapen/core-node`).
- `wasm/`   — wasm-bindgen `@akapen/core-wasm` for pure-logic reuse (M5).

The three CI faces (mac / dotnet / node) all link against the same C ABI in
`crates/akapen-ffi`, so an ABI-breaking change fails at least one face on the
same push that lands it. This is what "M1 併走条件" in the spec means.

The hand-written C header `crates/akapen-ffi/include/akapen.h` is the single
source of truth; a byte-identical copy lives at
`apps/mac/Sources/CAkapen/include/akapen.h` for SwiftPM to consume, and
`scripts/check-header-parity.sh` (run as its own CI job) enforces the two stay
in lockstep. The .NET (csbindgen) and Node (napi-rs) faces read the Rust
source directly, so they need no header parity of their own.

The eventual compatibility contract these expose is the 3-file export +
`VectorDoc` schema (`veda-annot-1`, per-point pressure required).
