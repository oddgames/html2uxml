# Changelog

All notable changes to this package are documented in this file. The format
follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the
package adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2026-05-26

### Added

- Initial public release of `au.com.oddgames.html2uxml` as a Unity UPM
  package consumable directly from this Git repository root.
- `Html2UxmlHtmlImporter` ScriptedImporter that converts `.html` files in
  `Assets/` into sibling UXML/USS plus image, font, and gradient assets.
- `Html2UxmlPanel` runtime element for bridged CSS rendering: inset and
  multi-layer `box-shadow`, `linear-gradient`, `radial-gradient`, axis-aligned
  `repeating-linear-gradient` patterns, tiled radial dot fields,
  `clip-path: polygon(...)`, and complex linear `mask-image` edge fades.
- Unity 6.3 custom filter assets for simple outer `box-shadow`,
  `filter: drop-shadow(...)`, and two-stop percentage linear masks.
- Support for up to eight inset or multi-layer shadow records on
  `Html2UxmlPanel`.
- Mesh-based gradient rendering with one linear layer plus two radial layers
  that avoids assigning hand-written materials Unity rejects as
  non-UITK-compatible.
- Converter-side CSS variable resolution for bridged gradients, shadows,
  filters, clip paths, and linear mask fades when variables resolve
  statically.
- `ShaderPanel`, `BackdropBlurPanel`, bundled UI shaders, Resources-backed
  material templates, and editor setup for package-provided custom filters.
- ODD subclasses for core UI Toolkit elements (`Html2UxmlElement`,
  `Html2UxmlLabel`, `Html2UxmlButton`, `Html2UxmlScrollView`, fields,
  toggles, dropdowns, foldouts, group containers) plus a shared animation
  ticker for `@keyframes` / `animation` bridge.
- `UXMLController` base MonoBehaviour for generated UI controller wiring.
- `LICENSE.md` (proprietary, ODD Games copyright) and
  `Editor/Plugins/THIRD-PARTY-NOTICES.md` reproducing the MIT licences for
  bundled `AngleSharp` and `ExCSS`. `package.json` declares
  `"license": "SEE LICENSE.md"`.

[0.1.0]: https://github.com/oddgames/html2uxml/releases/tag/v0.1.0
