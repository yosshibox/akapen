/*
 * Akapen C ABI (spec §7.2).
 *
 * Hand-written to mirror crates/akapen-ffi/src/lib.rs. The core is UI-agnostic;
 * this header is the minimal-common-denominator surface every language binding
 * wraps (mac SwiftUI now; .NET / Node later).
 *
 * Threading: an AkapenEngine handle is not thread-safe; drive it from one
 * thread. Strings are borrowed UTF-8, NUL-terminated. Colors are packed
 * 0xRRGGBBAA.
 */
#ifndef AKAPEN_H
#define AKAPEN_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct AkapenEngine AkapenEngine;

/* Tool codes (akapen_set_tool). */
enum { AKAPEN_TOOL_PEN = 0, AKAPEN_TOOL_ERASER = 1 };
/* Pointer kind (akapen_pointer). */
enum { AKAPEN_KIND_PEN = 0, AKAPEN_KIND_TOUCH = 1, AKAPEN_KIND_MOUSE = 2 };
/* Pointer phase (akapen_pointer). */
enum { AKAPEN_PHASE_DOWN = 0, AKAPEN_PHASE_MOVE = 1, AKAPEN_PHASE_UP = 2 };
/* Pressure curve (akapen_set_pressure_curve). */
enum { AKAPEN_CURVE_NORMAL = 0, AKAPEN_CURVE_SOFT = 1, AKAPEN_CURVE_HARD = 2 };

/* Lifecycle. akapen_open_image returns NULL on decode failure. */
AkapenEngine *akapen_open_image(const char *path);
AkapenEngine *akapen_new(uint32_t width, uint32_t height);
void akapen_free(AkapenEngine *engine);
void akapen_size(AkapenEngine *engine, uint32_t *w, uint32_t *h);

/* Tool / style. */
void akapen_set_tool(AkapenEngine *engine, int tool);
void akapen_set_color(AkapenEngine *engine, uint32_t rgba);
void akapen_set_size(AkapenEngine *engine, float px);
void akapen_set_pressure_curve(AkapenEngine *engine, int curve);

/* Input. pressure is 0..1 (mouse passes 1.0). */
void akapen_pointer(AkapenEngine *engine, double x, double y, double pressure,
                    int kind, int phase);

/* History. */
void akapen_undo(AkapenEngine *engine);
void akapen_redo(AkapenEngine *engine);

/* Returns 1 if the last pen stroke had no pressure variation (spec §5.4). */
int akapen_pressure_stuck(AkapenEngine *engine);

/*
 * Composites the display image (RGBA8, width*height*4 bytes) into out.
 * Returns the number of bytes required; if out_len is too small nothing is
 * written (call once with out=NULL to size the buffer).
 */
size_t akapen_composite_rgba(AkapenEngine *engine, uint8_t *out, size_t out_len);

/*
 * Writes the 3-file export into dir using stem as the base name (collision-free
 * naming). Returns 0 on success, non-zero on failure.
 */
int akapen_export_to_dir(AkapenEngine *engine, const char *dir, const char *stem);

/* ──────────────────────────────────────────────────────────────────────────
 * Key mapping (spec §3).
 *
 * akapen_resolve_key is a pure, engine-independent function: the shell passes a
 * described key-down event and gets back a stable action code (AKAPEN_ACT_*).
 * Keeping the shortcut table in the core means every platform shares one map.
 *
 *   ch           Unicode scalar produced ignoring the primary/alt modifiers
 *                (mac charactersIgnoringModifiers), or 0 for none.
 *   physical     physical-key code (AKAPEN_PK_*), 0 for unknown/other.
 *   primary      1 while the primary accelerator is held (Cmd on mac, Ctrl on
 *                Windows/Linux); shift/alt likewise.
 *   composing    1 while an IME composition is active  (do not steal keys; B21).
 *   text_editing 1 while focus is in a text control    (do not steal keys; B15).
 *
 * Returns an AKAPEN_ACT_* code, or AKAPEN_ACT_NONE (0) to leave the key alone.
 * ────────────────────────────────────────────────────────────────────────── */

/* Physical-key codes for akapen_resolve_key `physical`. */
enum {
    AKAPEN_PK_OTHER = 0,
    AKAPEN_PK_P = 1, AKAPEN_PK_E = 2, AKAPEN_PK_U = 3, AKAPEN_PK_A = 4,
    AKAPEN_PK_R = 5, AKAPEN_PK_O = 6, AKAPEN_PK_T = 7, AKAPEN_PK_I = 8,
    AKAPEN_PK_X = 9, AKAPEN_PK_C = 10, AKAPEN_PK_Z = 11, AKAPEN_PK_Y = 12,
    AKAPEN_PK_DIGIT0 = 13, AKAPEN_PK_SPACE = 14,
    AKAPEN_PK_BRACKET_LEFT = 15, AKAPEN_PK_BRACKET_RIGHT = 16,
    AKAPEN_PK_MINUS = 17, AKAPEN_PK_CARET = 18,
    AKAPEN_PK_PAGE_UP = 19, AKAPEN_PK_PAGE_DOWN = 20
};

/* Action codes returned by akapen_resolve_key. 0 = no action. Tool codes
 * 1..7 match akapen_set_tool numbering. */
enum {
    AKAPEN_ACT_NONE = 0,
    AKAPEN_ACT_TOOL_PEN = 1, AKAPEN_ACT_TOOL_ERASER = 2, AKAPEN_ACT_TOOL_LINE = 3,
    AKAPEN_ACT_TOOL_ARROW = 4, AKAPEN_ACT_TOOL_RECT = 5, AKAPEN_ACT_TOOL_ELLIPSE = 6,
    AKAPEN_ACT_TOOL_TEXT = 7,
    AKAPEN_ACT_UNDO = 10, AKAPEN_ACT_REDO = 11,
    AKAPEN_ACT_ZOOM_IN = 12, AKAPEN_ACT_ZOOM_OUT = 13,
    AKAPEN_ACT_FIT = 14, AKAPEN_ACT_ACTUAL_SIZE = 15,
    AKAPEN_ACT_ROTATE_LEFT = 16, AKAPEN_ACT_ROTATE_RIGHT = 17,
    AKAPEN_ACT_BRUSH_SMALLER = 18, AKAPEN_ACT_BRUSH_LARGER = 19,
    AKAPEN_ACT_NEXT_FRAME = 20, AKAPEN_ACT_PREV_FRAME = 21,
    AKAPEN_ACT_SWAP_COLOR = 22, AKAPEN_ACT_EYEDROPPER = 23,
    AKAPEN_ACT_TRANSPARENT_COLOR = 24
};

int32_t akapen_resolve_key(uint32_t ch, int32_t physical,
                           int primary, int shift, int alt,
                           int composing, int text_editing);

/* ──────────────────────────────────────────────────────────────────────────
 * Phase e: GPU surface path (spec §7.4-6 "GPU 描画(wgpu: Metal/D3D12)").
 *
 * Optional and additive: the CPU composite path (akapen_composite_rgba /
 * akapen_export_to_dir) works identically whether or not a surface is
 * attached. If akapen_render_attach fails, keep using the CPU path.
 *
 * Threading: all of the calls below (and the handle itself) must be driven
 * from a single thread — on mac, the main thread, since attaching wires a
 * CAMetalLayer into the given NSView.
 * ────────────────────────────────────────────────────────────────────────── */

/* Surface kind for AkapenSurfaceDesc.kind. */
enum {
    AKAPEN_SURFACE_METAL_LAYER = 0,     /* mac/iOS: handle is the NSView (not the layer) */
    AKAPEN_SURFACE_HWND = 1,            /* Windows Win32 window handle */
    AKAPEN_SURFACE_SWAPCHAIN_PANEL = 2  /* WinUI 3 SwapChainPanel (not yet wired; attach returns 4) */
};

/*
 * A native drawing surface. `kind` (one of AKAPEN_SURFACE_*) selects how
 * `handle` is interpreted. `width`/`height` are the surface size in physical
 * pixels; `scale_factor` is the backing-store scale (e.g. 2.0 on Retina).
 */
typedef struct AkapenSurfaceDesc {
    int32_t kind;
    void *handle;        /* native view/window handle (meaning depends on kind) */
    void *display;       /* native display/connection handle, or NULL */
    uint32_t width;
    uint32_t height;
    float scale_factor;
} AkapenSurfaceDesc;

/*
 * The on-screen view transform for a rendered frame, in physical pixels.
 * `center_x`/`center_y` are the displayed image center; `scale` is the
 * shell's zoom multiplied by the backing scale (uniform); `rotation_deg` is
 * clockwise degrees. The image's own size is taken from the attached surface,
 * not from this struct.
 */
typedef struct AkapenViewTransform {
    float center_x;
    float center_y;
    float scale;
    float rotation_deg;
} AkapenViewTransform;

/*
 * Attaches a GPU render surface to the engine, seeding it with the current
 * background and committed strokes. Returns 0 on success; non-zero means the
 * caller should fall back to the CPU path:
 *   1 = null engine, 2 = null desc, 3 = unknown kind,
 *   4 = surface/adapter/device bring-up failed (e.g. no GPU, or SwapChainPanel).
 * `desc->handle` must stay valid (and used only from this thread) for as long
 * as the surface remains attached.
 */
int akapen_render_attach(AkapenEngine *engine, const AkapenSurfaceDesc *desc);

/* Re-configures the attached surface for a new physical pixel size / backing
 * scale (e.g. on window resize or a screen change). No-op if not attached. */
void akapen_render_resize(AkapenEngine *engine, uint32_t width, uint32_t height, float scale);

/* Draws and presents one frame through `view` (bakes any pending committed-
 * stroke changes first). No-op if not attached. */
void akapen_render_frame(AkapenEngine *engine, AkapenViewTransform view);

/* Detaches and tears down the GPU surface. Safe when nothing is attached; the
 * engine keeps working on the CPU path afterward. */
void akapen_render_detach(AkapenEngine *engine);

/* Returns 1 if a GPU surface is currently attached (GPU path active), else 0. */
int akapen_render_available(AkapenEngine *engine);

#ifdef __cplusplus
}
#endif

#endif /* AKAPEN_H */
