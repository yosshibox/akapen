# Akapen UI icons

`akapen-ui-icons.svg` is the canonical, cross-platform icon source.

- `viewBox="0 0 24 24"`
- `stroke-width="1.75"`
- round line caps and joins
- no text glyphs and no product-specific tracing
- `currentColor` for normal, hover, selected, focus, and disabled states

Renderers must rasterize from vector paths at the active DPI. The Windows MVP
loads the same path vocabulary through `SvgIconCatalog`; Mac and VEDA can use
the sprite directly or generate platform-native assets from it.
