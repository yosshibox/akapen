// COM interop for handing a WinUI 3 SwapChainPanel through the C ABI to
// akapen-render (spec §7.4-6, and see crates/akapen-render/src/surface.rs's
// SurfaceKind::SwapChainPanel docs). A SwapChainPanel has no HWND; wgpu's
// dx12 backend consumes it as an ISwapChainPanelNative* raw COM pointer via
// `SurfaceTargetUnsafe::SwapChainPanel(*mut c_void)`.
//
// What the shell has to do:
//   1. QueryInterface the SwapChainPanel's underlying WinRT IUnknown for the
//      ISwapChainPanelNative interface (IID 63aad0b8-7c24-40ff-85a8-640d944cc325,
//      declared in `microsoft.ui.xaml.media.dxinterop.h`).
//   2. Hand that raw pointer to akapen_render_attach as
//      AkapenSurfaceDesc.handle. akapen-render then hangs onto it for the
//      lifetime of the surface (via wgpu-hal's own retain in
//      CreateSwapChainForComposition / SetSwapChain).
//   3. Release the QI'd pointer once (and only once) after
//      akapen_render_detach, so the ISwapChainPanelNative* reference count
//      returns to what it was before this shell touched it.
//
// The IID and the interface's single vtable slot (`SetSwapChain`) are the
// contract wgpu depends on; if Microsoft ever revs them we would fail the
// QI, not silently corrupt state. The interface itself is intentionally
// *not* used from managed code — we only need the raw pointer for wgpu.
// SetSwapChain is declared for completeness / future diagnostics.

using System;
using System.Runtime.InteropServices;

namespace AkapenApp.Interop;

/// <summary>
/// Managed declaration of ISwapChainPanelNative, the COM interface a WinUI 3
/// SwapChainPanel exposes to native DirectX code. Kept here for the IID and
/// for reference; the actual attach path retrieves the raw pointer through
/// <see cref="SwapChainPanelNativeInterop.QueryInterfacePointer"/> and passes
/// it into the C ABI unchanged.
/// </summary>
[ComImport]
[Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISwapChainPanelNative
{
    /// <summary>
    /// Vtable slot 3 (after IUnknown's 0/1/2). wgpu's dx12 backend calls this
    /// from `Surface::configure`; the shell itself never invokes it.
    /// </summary>
    [PreserveSig]
    int SetSwapChain(IntPtr swapChain);
}

/// <summary>
/// Small helper wrapping the QueryInterface / Release pair. Static so callers
/// can hold the raw pointer as an <see cref="IntPtr"/> field instead of a
/// finalizable wrapper: the raw pointer is what wgpu retains.
/// </summary>
internal static class SwapChainPanelNativeInterop
{
    // ISwapChainPanelNative IID — mirrors the ComImport attribute above so
    // both consumers use the same constant. Declared here (not derived via
    // typeof(...).GUID) so a future refactor that removes the interface
    // declaration doesn't silently break the QI at runtime.
    private static readonly Guid IID_ISwapChainPanelNative =
        new("63aad0b8-7c24-40ff-85a8-640d944cc325");

    /// <summary>
    /// QueryInterface the SwapChainPanel's underlying WinRT IUnknown for
    /// ISwapChainPanelNative and return the AddRef'd raw pointer. Returns
    /// <see cref="IntPtr.Zero"/> if the QI fails (which would mean the
    /// runtime SwapChainPanel does not implement the interface — a WindowsAppSDK
    /// version mismatch or a broken WinUI install, not a normal condition).
    ///
    /// Ownership: the returned pointer carries an AddRef; the caller must
    /// pass it to <see cref="Release"/> exactly once after detaching the
    /// wgpu surface. Do *not* Release before detach — wgpu retains this
    /// pointer for as long as the surface stays attached.
    /// </summary>
    /// <param name="panel">
    /// A WinUI 3 SwapChainPanel. Must be non-null and already added to a live
    /// visual tree (Loaded event has fired) so its underlying IInspectable
    /// exists.
    /// </param>
    public static IntPtr QueryInterfacePointer(
        Microsoft.UI.Xaml.Controls.SwapChainPanel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);

        // Marshal.GetIUnknownForObject on a C#/WinRT projected type routes
        // through CsWinRT's ICustomQueryInterface implementation, which
        // returns the underlying WinRT IUnknown (AddRef'd). This is the
        // path Win2D and other WindowsAppSDK samples take for exactly this
        // scenario. If for any reason the projection doesn't route through
        // ICustomQueryInterface (a broken CsWinRT install or a future WinRT
        // shape change), we fall through to a null return and the shell's
        // "CPU fallback only" status text kicks in — no silent corruption.
        IntPtr unknown = Marshal.GetIUnknownForObject(panel);
        if (unknown == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        try
        {
            // Query for the raw ISwapChainPanelNative COM interface. Success
            // gives us a fresh AddRef; we transfer ownership out of this
            // method via the returned IntPtr.
            Guid iid = IID_ISwapChainPanelNative;
            int hr = Marshal.QueryInterface(unknown, ref iid, out IntPtr panelNative);
            return hr == 0 ? panelNative : IntPtr.Zero;
        }
        finally
        {
            // Balance the GetIUnknownForObject AddRef; this only releases the
            // IUnknown reference, not the ISwapChainPanelNative one we are
            // returning.
            Marshal.Release(unknown);
        }
    }

    /// <summary>
    /// Releases one reference on the raw pointer returned by
    /// <see cref="QueryInterfacePointer"/>. Safe on <see cref="IntPtr.Zero"/>
    /// (no-op). Callers null out their local field after calling so a stray
    /// double-release becomes a no-op instead of undefined behavior.
    /// </summary>
    public static void Release(IntPtr panelNative)
    {
        if (panelNative != IntPtr.Zero)
        {
            Marshal.Release(panelNative);
        }
    }
}
