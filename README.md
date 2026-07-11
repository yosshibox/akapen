# Akapen (赤ペン)

**A pen-pressure-native, review-focused red-pen (朱入れ) annotation editor for
animation production.** Open an image or a frame sequence, mark it up in red
with genuine tablet pressure, save non-destructively next to the original, and
flow to the next frame.

**アニメ制作のレビュー(朱入れ)に特化した、筆圧ネイティブの軽量注釈エディタ。**
提出された作画に赤で指示を描き込み、元画像を壊さず別名保存し、連番の次カットへ
流れるように移動する。CLIP STUDIO PAINT の手癖がそのまま通じることを重視する。

> **Status: pre-release skeleton (M0).** This repository is currently
> **private**. It will be made public once the maintainer completes OSS launch
> preparation.
> **TODO(maintainer): switch this repository to public when ready** (license,
> NOTICE, third-party attributions and contribution docs are in place; review
> before flipping visibility).

## Scope / スコープ

Akapen does **review markup only** — it is not a drawing app. It does: open
images, draw red (and a few colors) lines / arrows / shapes, erase your own
unsaved strokes, save non-destructively under a new name in a separate
directory, walk frame sequences, and capture native tablet pressure.
It does **not** do: layers, fills, text typesetting, brush materials, filters,
selection/transform, animation drawing, or `.clip` read/write.

**Pen pressure is a hard requirement (MUST).** A build where pressure does not
drive stroke width is not releasable. See the spec §5.

## Architecture / アーキテクチャ

Two layers: a UI-agnostic **Rust core** and thin **native OS shells**.

```
┌──────────────────────┐   ┌──────────────────────────┐
│ mac shell (SwiftUI)  │   │ Windows shell (WinUI 3)  │
│ NSEvent pen input    │   │ WM_POINTER / Wintab      │
│ ImageIO decode       │   │ WIC decode, SwapChainPanel│
└──────────┬───────────┘   └───────────┬──────────────┘
      swift-bridge/UniFFI          C ABI / csbindgen (P/Invoke)
┌──────────▼───────────────────────────▼──────────────┐
│                 Akapen core (Rust)                   │
│  stroke model · coord/view transforms · keymap       │
│  pressure curve · undo/redo · wgpu (Metal/D3D12)     │
│  3-file export (strokes PNG / flat PNG / vector JSON) │
└──────────┬───────────────────────────────────────────┘
   napi-rs (Node addon) / wasm-bindgen (WASM)
┌──────────▼───────────┐
│ VEDA (Electron)      │  ← downstream consumer
└──────────────────────┘
```

The core knows only normalized pointer samples (pressure required), RGBA
bitmaps and key events; it emits screen textures and the VEDA-compatible
3-file export. See `docs/` for the full specification (this repo is the
canonical home of that spec).

### Repository layout

```
crates/akapen-core   drawing engine: stroke model, brush/pressure curve, CPU raster + incremental bake,
                     undo/redo, coordinate transforms, vector JSON schema, 3-file export
crates/akapen-io     I/O: image decode (png/jpg/webp/bmp), _review/ output naming, sequence next/prev
crates/akapen-ffi    C ABI (libakapen) — the surface every language binding wraps (include/akapen.h)
bindings/{swift,dotnet,node,wasm}   binding scaffolds (implemented from M1/M5)
apps/mac             SwiftUI shell (M1): open → draw with pressure → save 3-file set → next/prev
docs/                specification (canonical copy)
testdata/            test vectors ported from the VEDA reference implementation
```

## Build

Requires a Rust toolchain (developed against Rust 1.97; the `image` crate pulls
transitive deps needing edition-2024, so use a recent stable).

```bash
cargo test               # build all crates and run the unit tests (108 as of M1)
cargo build              # build the workspace (produces target/debug/libakapen.a)
```

### mac shell (M1)

```bash
cargo build -p akapen-ffi          # produce the C ABI static lib first
cd apps/mac
swift build                        # build the SwiftUI app + the FFI harness
swift run AkapenApp                # launch the review window
swift run akapen-harness /tmp/out  # headless: draw pressure strokes + write the 3-file export
```

The SwiftUI shell opens an image (⌘O or drag-drop), captures NSEvent tablet
pressure, lets you draw red / erase, zoom (⌘-scroll / pinch), pan (Space-drag),
undo/redo, and saves the 3-file set into `<input folder>/_review/` (⌘S), then
steps through the sequence with ◀ / ▶. The `akapen-harness` executable proves
the Swift↔Rust boundary and the whole draw→bake→save pipeline without a GUI.

### Third-party dependency rationale

- **`png`** (core export) and **`image`** (io decode) are the de-facto pure-Rust
  codecs; pure-Rust default features keep the core cross-compilable for the
  tri-face build discipline (spec §8.2). Both are permissive-licensed.

> **CI:** GitHub Actions runs `cargo build` + `cargo test` + `clippy` on
> macOS / Linux / Windows, and a `mac-shell` job that builds the Rust C ABI,
> compiles the SwiftUI shell, and runs the FFI harness asserting the 3-file
> export — keeping the "tri-face build always on" discipline (spec §9) real.

## Pressure / 筆圧

Every stored point carries raw pressure `p` (0..=1, pre-smoothing). The vector
JSON schema (`veda-annot-1`) rejects points without `p`. Pressure detection is
verified on real WACOM hardware every release (spec §5.6).

## License

Apache-2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE). Third-party
attributions live in [THIRD-PARTY-LICENSES/](THIRD-PARTY-LICENSES/).

CLIP STUDIO PAINT is a registered trademark of CELSYS, Inc. This project is not
affiliated with CELSYS; shortcut-compatibility references are nominative use
only.
