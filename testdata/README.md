# testdata

Test vectors ported from the VEDA reference implementation
(`lib/annotate-*.js` and `test/annotate-*.test.js`), which the spec treats as
the **algorithm specification** (spec §7.3).

At M0 the ported vectors are embedded directly in the Rust unit tests:

- Coordinate transform (`clientToCanvasPoint`) — ported into
  `crates/akapen-core/src/coord.rs` tests (zoom / pan / rotation / DPR),
  numerically matching `test/annotate-geometry.test.js`.

As more reference modules are ported (keymap, view, smoothing, path
normalization), shared vector fixtures that both JS and Rust can read will be
collected here.
