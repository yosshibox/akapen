# Akapen local build helper.
#
# Mirrors the CI job matrix (spec §9 tri-face build discipline) so a developer
# can rehearse locally what CI will do on the same push. Nothing here depends
# on a display, tablet, or GPU adapter — the mac shell target requires
# macOS+Xcode, the .NET target requires the .NET 8 SDK, and the Node target
# requires Node 18+; each is skipped with a friendly message if its toolchain
# is missing.

.PHONY: help all rust mac dotnet node clean

# The default target explains the available faces rather than picking one, so
# `make` on a fresh clone never surprises with a full workspace build.
help:
	@echo "Akapen local build faces (spec §9 tri-face build discipline):"
	@echo "  make rust    - fmt + clippy + build + test the Rust workspace"
	@echo "  make mac     - Rust C ABI + SwiftUI shell + headless FFI harness (macOS only)"
	@echo "  make dotnet  - Rust C ABI + csbindgen + dotnet build (.NET 8 SDK required)"
	@echo "  make node    - napi-rs cdylib build via @napi-rs/cli (Node 18+ required)"
	@echo "  make all     - rust + whichever platform faces this host can build"

# Rust workspace: fmt/clippy/build/test all workspace members. The .NET-side
# bindgen (a bin crate) and the Node-side napi-rs crate are workspace members,
# so a plain break in either fails this target.
rust:
	cargo fmt --all --check
	cargo clippy --workspace --all-targets -- -D warnings
	cargo build --workspace
	cargo test --workspace

# mac face: Rust C ABI static lib, then the SwiftUI shell + harness. Skipped
# on non-macOS hosts. Matches the `mac-shell` CI job.
mac:
	@case "$$(uname -s)" in \
		Darwin) \
			cargo build -p akapen-ffi && \
			(cd apps/mac && swift build) && \
			(cd apps/mac && swift run akapen-harness /tmp/akapen-review) ;; \
		*) \
			echo "make mac: not on macOS ($$(uname -s)); skipping" ;; \
	esac

# .NET face: regenerate the C# P/Invoke bindings via csbindgen, then build the
# class library. Skipped if the .NET SDK is not on PATH (the .NET face is only
# a CI probe at M1 — spec §9 M5 is when the .NET API stabilizes).
dotnet:
	@if ! command -v dotnet >/dev/null 2>&1; then \
		echo "make dotnet: dotnet SDK not found; skipping (CI proves this face on Windows)"; \
	else \
		cargo build -p akapen-ffi && \
		cargo run -p akapen-dotnet-bindgen && \
		dotnet build bindings/dotnet/Akapen.Native/Akapen.Native.csproj -c Release; \
	fi

# Node face: build the napi-rs cdylib into a loadable .node addon. Skipped if
# npm is not on PATH.
node:
	@if ! command -v npm >/dev/null 2>&1; then \
		echo "make node: npm not found; skipping (CI proves this face on Ubuntu)"; \
	else \
		cd bindings/node && npm install --no-fund --no-audit && npm run build; \
	fi

all: rust mac dotnet node

clean:
	cargo clean
	rm -rf bindings/node/node_modules bindings/node/*.node bindings/node/index.js bindings/node/index.d.ts
	rm -rf bindings/dotnet/Akapen.Native/Generated bindings/dotnet/Akapen.Native/bin bindings/dotnet/Akapen.Native/obj
