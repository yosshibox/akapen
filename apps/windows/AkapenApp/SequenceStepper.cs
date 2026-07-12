// Sequence-token frame stepping (spec §4.5 連番次前) — Codex Ch.10 review,
// Medium2. StepFrame (MainWindow.xaml.cs) previously always picked the
// natural-sort-adjacent sibling (`idx ± 1`), which drifts from §4.5: the spec
// wants the trailing-digit-run of the current filename incremented/
// decremented (padding preserved) FIRST, falling back to natural-sort
// adjacency only when that target doesn't exist. Without the token step, a
// non-frame file sitting between two numbered frames (e.g. `c001.png`,
// `note.png`, `c002.png`) makes Next land on `note.png` instead of `c002.png`.
//
// This is a line-for-line translation of the core's
// crates/akapen-io/src/sequence.rs (`next_sequence_name` / `prev_sequence_name`
// live in crates/akapen-io/src/lib.rs, `neighbor` in sequence.rs itself) — NOT
// a call through FFI. Routing this through akapen_open_image's FFI surface
// would mean extending csbindgen's generated bindings, both akapen.h headers,
// and the header-parity CI check for a single shell's frame-stepping UI —
// out of scope for an M2-F fix. Kept as a static helper (not part of
// MainWindow) so the algorithm can be read and sanity-checked on its own,
// independent of the shell's engine/UI state.
//
// Drift risk: this hand-translation can fall out of sync with the Rust
// original if `sequence.rs::neighbor` changes. Worth reconsidering an FFI
// export (or a shared golden-test fixture) if a third shell ever needs the
// same logic — left as a note for whoever picks that up, per the core
// comment above.

using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace AkapenApp;

internal static class SequenceStepper
{
    /// <summary>
    /// Resolves the Prev/Next neighbor of <paramref name="current"/> within
    /// <paramref name="siblings"/> (already natural-sorted, containing full
    /// paths — mirrors <c>_siblings</c> in MainWindow). Prefers the
    /// sequence-token target (trailing digit run ± 1, zero-padding
    /// preserved, same extension) when a matching file exists in the list;
    /// otherwise falls back to the natural-sort-adjacent entry. Returns null
    /// at the ends (no more frames in that direction) — mirrors
    /// sequence.rs::neighbor's <c>Option&lt;PathBuf&gt;</c> return exactly,
    /// including returning null when <paramref name="current"/> itself isn't
    /// found in <paramref name="siblings"/> and no token target exists.
    /// </summary>
    public static string? Neighbor(string current, IReadOnlyList<string> siblings, bool forward)
    {
        string stem = Path.GetFileNameWithoutExtension(current);
        string ext = Path.GetExtension(current);

        // 1) Sequence-token target with the same extension, if present. Uses
        // case-sensitive Ordinal comparison to match core's `sequence.rs:53`
        // (`current.file_stem() == token && current.extension() == ext`) exactly
        // — Windows filesystems are usually case-insensitive so this is
        // typically transparent, but on a case-sensitive directory (Dev Drive
        // ReFS, WSL mount, network share) the two shells must agree.
        string? token = forward ? NextSequenceName(stem) : PrevSequenceName(stem);
        if (token is not null)
        {
            foreach (string candidate in siblings)
            {
                if (string.Equals(Path.GetFileNameWithoutExtension(candidate), token, StringComparison.Ordinal)
                    && string.Equals(Path.GetExtension(candidate), ext, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
        }

        // 2) Fallback: adjacent entry in the natural-sorted listing.
        int idx = -1;
        for (int i = 0; i < siblings.Count; i++)
        {
            if (string.Equals(siblings[i], current, StringComparison.Ordinal))
            {
                idx = i;
                break;
            }
        }
        if (idx < 0) return null;

        int neighborIdx = forward ? idx + 1 : idx - 1;
        if (neighborIdx < 0 || neighborIdx >= siblings.Count) return null;
        return siblings[neighborIdx];
    }

    /// <summary>
    /// Increments the trailing digit run of a filename stem by one,
    /// preserving zero padding and any prefix/suffix around it (spec §4.5).
    /// Returns null when there is no digit run anywhere in the stem.
    /// Translated from sequence.rs's `next_sequence_name` (lib.rs).
    /// </summary>
    public static string? NextSequenceName(string stem) => AdjustSequenceName(stem, +1);

    /// <summary>
    /// Decrements the trailing digit run of a filename stem by one,
    /// preserving zero padding and any prefix/suffix around it (spec §4.5).
    /// Returns null when there is no digit run, or the run's value is
    /// already 0 (no previous frame). Translated from sequence.rs's
    /// `prev_sequence_name` (lib.rs).
    /// </summary>
    public static string? PrevSequenceName(string stem) => AdjustSequenceName(stem, -1);

    private static string? AdjustSequenceName(string stem, int delta)
    {
        // Find the last run of ASCII digits, walking back from the end past
        // any non-digit suffix first (e.g. "c001b" has digit run "001" with
        // suffix "b") — exact mirror of the Rust byte-scan.
        int end = stem.Length;
        while (end > 0 && !char.IsAsciiDigit(stem[end - 1])) end--;
        if (end == 0) return null;

        int start = end;
        while (start > 0 && char.IsAsciiDigit(stem[start - 1])) start--;

        string prefix = stem[..start];
        string digits = stem[start..end];
        string suffix = stem[end..];
        int width = digits.Length;

        // BigInteger (not int/long) to tolerate arbitrarily long digit runs,
        // matching the core's u128 parse.
        if (!BigInteger.TryParse(digits, out BigInteger value)) return null;
        BigInteger next = value + delta;
        if (next < BigInteger.Zero) return null; // prev at 0: no previous frame

        string numStr = next.ToString();
        if (numStr.Length < width) numStr = numStr.PadLeft(width, '0');
        return prefix + numStr + suffix;
    }
}
