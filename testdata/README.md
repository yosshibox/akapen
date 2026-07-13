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

## Shared golden export

`golden-export-v1.json` is the small cross-platform export contract. It uses a
64x48 blank canvas and six portable `pointer` commands to create two
pressure-varying pen strokes. The `expected` object fixes the `veda-annot-1`
schema, natural size, stroke/kind count, and every `points[].p`; its artifact
names fix the three-file contract.

Rust is the export authority: Windows, Mac, and Node should send these same
commands and compare the parsed VectorDoc and the presence of all three
artifacts. PNG pixels are deliberately not duplicated in this fixture.
