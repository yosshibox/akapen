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

#ifdef __cplusplus
}
#endif

#endif /* AKAPEN_H */
