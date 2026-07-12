# bindings/node

Node face of the tri-face build discipline (spec §9). This is a **CI-only
scaffold at M1** — the real npm package (`@akapen/core-node`, spec §9 M5) will
land when VEDA re-imports the core. Here we only prove that the same C ABI the
mac SwiftUI shell links against can be bound from Node via napi-rs.

## Layout

```
bindings/node/
├─ Cargo.toml            # cdylib crate wrapping akapen-ffi via napi-derive
├─ build.rs              # napi_build::setup()
├─ src/lib.rs            # 4 tiny wrappers (version / ping / palm_route / open_bogus_returns_null)
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
```

Or without the CLI:

```
cargo build -p akapen-node --release
```

The napi build is what CI runs on Ubuntu; the plain cargo build works too but
does not emit the platform-tagged `.node` filename Node's loader expects.

CI does not stop at "it built": a follow-up step `require`s the produced
`.node` file and calls all four wrappers (`node -e "..."`), so a wrong DLL
name, a missing native dependency, or an ABI symbol that resolves at build
time but fails to link at load time all fail the same push, not silently.

## Not yet in scope (spec §9 M5)

- Stable JavaScript API surface (the four functions here are a smoke probe).
- npm publish.
- VEDA integration.
