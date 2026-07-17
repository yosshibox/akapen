# THIRD-PARTY-LICENSES

This directory collects the license texts and attributions for third-party
components that Akapen depends on or bundles.

Policy (see the specification in `docs/` and `NOTICE`):

- Rust crate dependencies (permissive: MIT / Apache-2.0) — generated inventory
  will be produced with a tool such as `cargo about` / `cargo deny` in CI as the
  dependency set grows.
- Copyleft components that are **bundled** (e.g. libheif for HEIC, ffmpeg for
  video-frame extraction) are kept license-separated from the Apache-2.0 core
  via dynamic linking / separate-process invocation. Their full license texts
  and source-offer information go here when they are added.

At M0 the core (`akapen-core`, `akapen-io`) depends only on permissive Rust
crates; there are no copyleft bundles yet. This file is the designated home so
the placement is fixed before those components land.

- [Fluent UI System Icons](fluentui-system-icons.txt) — MIT (Microsoft)。UIアイコン図形。
