#!/usr/bin/env bash
# Parity check for the two hand-written copies of the Akapen C ABI header.
#
# The canonical header lives at `crates/akapen-ffi/include/akapen.h` (referenced
# by the akapen-ffi crate and by any C consumer of the produced dylib), and is
# duplicated at `apps/mac/Sources/CAkapen/include/akapen.h` (read by SwiftPM
# when building the mac shell). SwiftPM discourages targets from resolving
# header paths outside the Swift package root, so keeping a physical copy
# inside `apps/mac` is simpler than a modulemap escape — but any lone edit to
# one copy would silently drift the mac shell's declared C ABI from the actual
# one produced by the Rust cdylib. This script fails CI on that drift so a
# lone edit is caught on the same push.
#
# Usage: `bash scripts/check-header-parity.sh` from anywhere in the repo.

set -euo pipefail

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
canonical="$repo_root/crates/akapen-ffi/include/akapen.h"
mirror="$repo_root/apps/mac/Sources/CAkapen/include/akapen.h"

for f in "$canonical" "$mirror"; do
    if [ ! -f "$f" ]; then
        echo "check-header-parity: missing file: $f" >&2
        exit 2
    fi
done

if ! diff -u "$canonical" "$mirror"; then
    echo >&2
    echo "check-header-parity: the two akapen.h copies have drifted." >&2
    echo "  canonical: $canonical" >&2
    echo "  mirror:    $mirror" >&2
    echo "Edit both in the same patch, or run:" >&2
    echo "  cp \"$canonical\" \"$mirror\"" >&2
    exit 1
fi

echo "check-header-parity: OK (both copies of akapen.h are byte-identical)"
