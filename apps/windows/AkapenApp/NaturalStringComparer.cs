// Natural (human) filename ordering for frame-sibling sorting (spec §4.5 —
// M2-F frame stepping). Plain lexicographic ordering would put
// "frame_10.png" before "frame_2.png"; natural ordering treats the embedded
// number as a unit so the sequence reads frame_1, frame_2, ..., frame_10 —
// the order a user already expects from Explorer. Mirrors the mac shell's
// `naturalLess` (apps/mac/Sources/AkapenApp/AppState.swift), which wraps
// Foundation's `String.compare(_:options:.numeric)`; .NET has no built-in
// numeric-aware string comparer, so this wraps Shlwapi's StrCmpLogicalW — the
// same routine Windows Explorer itself uses to order a folder's file listing,
// which additionally keeps the stepping order consistent with what the user
// already sees when browsing the same folder outside Akapen.

using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AkapenApp;

internal sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? string.Empty, y ?? string.Empty);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int StrCmpLogicalW(string psz1, string psz2);
}
