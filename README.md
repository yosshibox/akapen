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
crates/akapen-core   drawing core: stroke model, coordinate transforms, vector JSON schema
crates/akapen-io     I/O helpers: sequence detection, output naming, path normalization (scaffold)
bindings/{swift,dotnet,node,wasm}   binding scaffolds (implemented from M1/M5)
apps/mac             SwiftUI shell scaffold (implemented in M1)
docs/                specification (canonical copy)
testdata/            test vectors ported from the VEDA reference implementation
```

## Build

Requires a Rust toolchain (developed against cargo 1.83).

```bash
cargo test      # build the core + I/O crates and run all unit tests
cargo build     # build the workspace
```

> **CI note:** at the time this skeleton was authored the workspace was built
> and tested locally (`cargo test`, all green). The GitHub Actions workflow
> runs `cargo build` + `cargo test` on macOS / Linux / Windows and holds
> placeholder jobs for the three binding faces (Swift / .NET / Node) so the
> "tri-face build always on" discipline (spec §9) is in place from M0.

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
