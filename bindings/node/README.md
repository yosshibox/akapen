# bindings/node

Node face of the tri-face build discipline (spec §9). This is a **VEDA adapter
preview** with a small, synchronous `AkapenSession` API over the same C ABI used
by Mac and Windows, plus compatibility probe functions.

## Layout

```
bindings/node/
├─ Cargo.toml            # cdylib crate wrapping akapen-ffi via napi-derive
├─ build.rs              # napi_build::setup()
├─ src/lib.rs            # AkapenSession plus the backwards-compatible probes
├─ package.json          # @napi-rs/cli devDep, `npm run build`
└─ index.node            # emitted by the build (gitignored)
```

The Rust crate `akapen-node` is a member of the workspace at the repo root so
`cargo build` from anywhere picks up ABI-breaking changes in `akapen-ffi` on
the same push.

## Build locally

```
cd bindings/node
npm install
npm run build     # emits akapen-core-node.<triple>.node in this dir
npm run smoke     # loads the local platform artifact, if present
```

Or without the CLI:

```
cargo build -p akapen-node --release
```

The napi build is what CI runs on Ubuntu; the plain cargo build works too but
does not emit the platform-tagged `.node` filename Node's loader expects.

CI does not stop at "it built": `npm run smoke` requires the produced `.node`
file, drives varying pointer pressure through `AkapenSession`, and verifies the
three-file export. A wrong DLL name, missing native dependency, or ABI symbol
that resolves at build time but fails to link at load time fails the same push.

## JavaScript preview API

```js
const { AkapenSession } = require("./index.node");
const session = new AkapenSession(1920, 1080); // or: AkapenSession.fromImage(path)
session.setTool(0);             // Pen; 1 is Eraser
session.setColor(0xff3366ff);   // packed 0xRRGGBBAA
session.setSize(6);
session.pointer(10, 20, 1, 0, 0); // x, y, pressure, kind, phase: Down
session.pointer(40, 50, 0.8, 0, 2); // Up
if (session.canUndo) session.undo();
session.exportToDir("./_review", "frame-001");
```

`width`, `height`, `canUndo`, and `canRedo` are read-only properties. Pointer
kind is `0=Pen, 1=Touch, 2=Mouse`; phase is `0=Down, 1=Move, 2=Up`.
Invalid inputs return a JavaScript exception; undo/redo return whether history
was available.

The shared smoke input is [`../../testdata/golden-export-v1.json`](../../testdata/golden-export-v1.json).
The Rust unit test in `src/lib.rs` sends its portable pointer commands through
`AkapenSession`, checks undo/redo, then checks the same three files and parsed
VectorDoc contract used by Mac and Windows. This keeps Rust export as the
authority; JavaScript must not reimplement PNG or VectorDoc serialization.

The unit test links the binding's Rust `AkapenSession` directly. The dynamic
smoke loads the local platform `.node` directly, exercises varying pointer
pressure, and verifies all three export files. It skips clearly when no
matching artifact exists. A native `.node`
load is a separate CI/environment check: it requires `npm install` and the
platform napi build to have produced the matching file, so this repository
does not claim that `cargo test` alone proves Node's dynamic loader path.

## Portable command and VectorDoc contract

Windows, Mac, and VEDA should all drive the same portable command shape:
`pointer(x, y, pressure, kind, phase)`, history/style commands, and
`exportToDir(dir, stem)`. Export delegates to Rust's existing
`akapen_export_to_dir`; JavaScript does not recreate PNG or JSON contents.
The result remains the VEDA three-file set: transparent strokes PNG, flat
composite PNG, and `veda-annot-1` VectorDoc JSON with pressure-bearing
`points[].p`, mapped by VEDA to `file`, `flatFile`, and `vectorFile`.

## Not yet in scope

- npm publish.
- VEDA integration.
