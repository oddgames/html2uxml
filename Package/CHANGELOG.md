# Changelog

## Unreleased

- Renamed the runtime visual bridge element to `Html2UxmlPanel`.
- Added Html2UxmlPanel approximation for linear `mask-image` /
  `-webkit-mask-image` fades, intended for panel, ticker, and scroll-edge
  fades. General CSS masking remains unsupported.
- Added Html2UxmlPanel support for up to eight outer/inset shadow layers.
- Added automatic CSS gradient rendering in `Html2UxmlPanel`, with one linear
  layer, two radial layers, explicit radial size parsing, and mesh-based
  drawing that avoids assigning non-UITK-compatible materials.
- Improved radial-gradient fidelity for Chrome-normalized `radial-gradient(at
  x y, ...)` values and softened default ellipse washes to match CSS
  farthest-corner behavior more closely.
- Added automatic runtime rendering for axis-aligned
  `repeating-linear-gradient(...)` patterns and tiled radial dot fields that
  use `radial-gradient(...)` plus explicit `background-size`.
- Clipped inset shadow/highlight bands to `clip-path: polygon(...)` shapes so
  slanted panels no longer draw rectangular edge lines outside the polygon.
- Added converter-side CSS variable resolution for bridged gradients,
  shadows, filters, clip paths, and linear mask fades when variables resolve
  statically.
- Added `ShaderPanel`, `BackdropBlurPanel`, bundled UI shaders, Resources-based
  material templates, and editor setup for package-provided custom filters.
- Removed the hard package dependency on Vector Graphics; projects that need
  SVG import can add Unity's SVG support explicitly.

## 0.1.0

- Initial `html2uxml` runtime package.
- Added `Html2UxmlPanel` for bridged CSS rendering.
- Added `UXMLController` for generated UI controller wiring.
- Added Vector Graphics dependency for converted SVG assets.
- Added Html2UxmlPanel support for radial gradients and inset shadow emboss.
- Added converter support for static sibling `z-index`, native USS blur
  filters, `backdrop-filter` blur approximation, and 2D flattening for
  `translate3d` / axis scale transforms.
