# Unsupported CSS Properties

Catalogue of every CSS property the converter cannot pass through directly to
USS. Each entry says **what authors use it for**, **what the converter does
today** (drop, approximate, or bridge), and **what it would take to support it
better** (USS equivalent, runtime bridge, or HTML-side rewrite).

Keep this list in sync with `DROP_PROPS` in `html2uxml/mappings.py` and the
per-property handlers in `_map_one`.

---

## Dropped or Runtime-Bridged Properties

These properties do not have direct USS equivalents. Some are rewritten to
runtime custom properties; the rest are dropped with warning text:
`unsupported in USS, dropped: <prop>: <value>`.

### `animation` / `animation-*`
- **Used for**: keyframe-driven loops (pulse, ticker, fade-in).
- **Today**: bridged when the animation targets `opacity`, `translate`,
  `scale`, `rotate`, `color`, or `background-color`. The converter emits
  `--odd-animation-*` custom USS and every generated ODD element installs a
  dormant runtime animation binding. There is no root scan.
- **Still unsupported**: layout animations (`width`, `height`, `left`, `top`,
  flex values), SVG path animation, filters, multiple concurrent animations on
  one element, and browser-exact `steps()` / `cubic-bezier(...)` timing.
- **To support better**: extend `Html2UxmlAnimation` property samplers one
  property at a time and keep conversion warnings for properties that would
  force layout every frame.

### `mask`, `mask-type`
- **Used for**: alpha cutouts, gradient fades to transparent.
- **Today**: dropped.
- **To support fully**: requires a stencil/clip pass or shader-based alpha
  mask on the element.

### `mask-image`, `-webkit-mask-image`
- **Used for**: common edge fades on tickers, scroll regions, and HUD panels.
- **Today**: simple two-stop percentage `linear-gradient(...)` masks become
  the package `ODDGamesLinearMask` custom filter. More complex
  `linear-gradient(...)` and `repeating-linear-gradient(...)` masks fall back
  to `--odd-mask-image` and `Html2UxmlPanel` cover fades.
- **Still unsupported**: URL/image masks, radial/conic masks,
  mask-repeat/size/position, compositing semantics, and shader-quality alpha
  clipping.
- **To support fully**: expand shader-backed mask filters and map only
  authored package masks automatically.

### `appearance`
- **Used for**: native form-control reset (`appearance: none`).
- **Today**: dropped (UI Toolkit does not render native widgets, so the reset
  is implicit).
- **To support**: nothing required. The drop is the correct behaviour.

### `float`, `clear`
- **Used for**: legacy text wrapping around images.
- **Today**: dropped.
- **To support**: not feasible in flex-only USS layout. Rewrite layout to flex
  before conversion.

### `box-sizing`
- **Used for**: switching between border-box and content-box.
- **Today**: dropped. USS is always **border-box**.
- **To support**: nothing — USS already matches `box-sizing: border-box`.
  Author should expect width/height to include padding+border.

### `user-select`, `pointer-events`, `touch-action`
- **Used for**: blocking text selection / clicks.
- **Today**: `pointer-events: none` auto-emits `picking-mode="Ignore"` as
  a UXML attribute on the element. `user-select` and `touch-action`
  remain dropped.
- **To support better**: `user-select` has no Unity equivalent — labels
  are never selectable. `touch-action` is a browser scroll hint with no
  USS analogue.

### `perspective`, `perspective-origin`, `transform-style`, `backface-visibility`
- **Used for**: 3D card flips.
- **Today**: dropped.
- **To support**: not feasible; UI Toolkit transforms are 2D only.

### `mix-blend-mode`, `background-blend-mode`, `isolation`
- **Used for**: multiply/screen blending (badges, neon).
- **Today**: dropped.
- **To support**: requires custom shader on the element. Bridge as
  `--odd-blend-mode: <mode>` and pick a Material per mode.

### `contain`, `will-change`
- **Used for**: browser performance hints.
- **Today**: dropped — irrelevant in USS.
- **To support**: nothing needed.

### `outline-offset`
- **Used for**: gap between element and outline.
- **Today**: dropped (`outline` itself is approximated to `border`).
- **To support**: add an absolutely-positioned sibling element styled as the
  outline.

### `list-style`, `border-collapse`, `border-spacing`, `caption-side`,
### `table-layout`
- **Used for**: HTML lists / tables.
- **Today**: dropped (`<ol>`/`<ul>` markers are synthesized by the converter
  instead; tables are flattened to flex containers).
- **To support**: HTML-side rewrite to flex layout.

### `scroll-behavior`, `scroll-snap-type`, `scroll-snap-align`
- **Used for**: smooth scrolling / snapping.
- **Today**: dropped. Overflowed elements promoted to `ScrollView`, but no snap.
- **To support**: subclass `ScrollView` + use `IVisualElementScheduledItem`
  to ease `scrollOffset` to nearest snap position.

### `writing-mode`, `direction`
- **Used for**: vertical / RTL text.
- **Today**: dropped (USS is LTR-only).
- **To support**: not feasible without custom text rendering.

### `text-decoration`, `text-transform`
- **Used for**: underline / capitalize.
- **Today**: **consumed by the converter** before `_map_one` is called —
  underline becomes `<u>...</u>` rich-text, transform mutates the label text.
  The `DROP_PROPS` entry just suppresses a redundant warning.
- **To support**: already bridged at converter level.

### `text-indent`, `vertical-align`
- **Used for**: paragraph indent / inline alignment.
- **Today**: dropped.
- **To support**: `text-indent` could become `padding-left` on Label.
  `vertical-align` for inline-block is meaningless in flex.

### `word-break`, `overflow-wrap`, `hyphens`
- **Used for**: hyphenation, breaking long tokens.
- **Today**: dropped. Unity 6.3 USS only has
  `white-space: normal | nowrap | pre | pre-wrap`.
- **To support**: nothing in current USS.

### `line-height`
- **Used for**: vertical text rhythm.
- **Today**: `line-height: <px>` auto-mapped to `-unity-paragraph-spacing`.
  Unitless multipliers (`1`, `1.5`) still report as approximations because USS
  has no true line-height property. The converter does use resolved unitless
  values to add tight, centered generated line boxes for safe single-line HUD
  labels.
- **Still unsupported**: multi-line browser line-height rhythm. Author explicit
  pixel line boxes or split dense HUD labels into fixed-height rows when exact
  vertical rhythm matters.

---

## Approximated properties

### `display`
- Most values become `flex`. `display: none` -> `display: none`.
- Warning: `display: <v> approximated as flex`.
- Already correct given USS is flex-only.

### `position`
- `static`, `relative`, `fixed` -> `absolute`. Warns.
- USS only knows `relative` / `absolute`. `fixed` becomes `absolute` + author
  must position against the root manually.

### `z-index`
- Consumed by the converter as a static sibling paint-order sort. Higher
  numeric z-index siblings are emitted later in UXML, which makes Unity paint
  them on top. Nothing is emitted to USS.
- Limits: only compares siblings under the same parent. Browser stacking
  contexts from transform/opacity/filter/isolation are not reproduced.

### `opacity`
- Direct element opacity maps to USS `opacity`.
- CSS parent opacity is browser group compositing. UI Toolkit does not give the
  converter an equivalent USS-only offscreen subtree compositing primitive.
- Current approximation: when a low-opacity parent contains absolutely
  positioned painted overlay children, the converter removes parent opacity,
  applies that opacity to normal children, and leaves the overlay opaque so it
  still occludes lower siblings. This is designed for badges, chips, and HUD
  overlays where bleed-through is more visibly wrong than a slightly stronger
  overlay color.
- Warning:
  `CSS opacity group approximated for positioned overlay children; parent opacity was pushed to non-overlay children`.

### `backdrop-filter` / `-webkit-backdrop-filter`
- `blur`, `grayscale`, `invert`, `opacity`, `sepia`, `tint`, `hue-rotate`,
  `contrast`, and custom `filter("...")` are approximated by emitting a normal
  Unity `filter` declaration.
- `brightness()` and `saturate()` map to the package-provided
  `ODDGamesColorAdjust` custom filter asset.
- Difference from CSS: Unity filters process the element subtree. They do
  **not** sample pixels behind the element, so exact browser frosted glass is
  not reproduced.
- To support exact backdrop blur: use the package `BackdropBlurPanel` with a
  camera/UI `RenderTexture` source and the bundled Gaussian blur shader. For
  multiple live blur panels, use one `BackdropCaptureSource` and set
  `source-id` on each panel so the capture/blur work is shared.

### `transform`
- `translate`, `translateX`, `translateY`, `translate3d(x, y, z)`,
  `rotate`, `rotateZ`, `scale`, `scaleX`, and `scaleY` map to Unity's
  individual `translate`, `rotate`, and `scale` properties.
- `translate3d` is flattened to 2D; the z component is ignored.
- `matrix`, `matrix3d`, `skew`, `skewX`, `skewY`, `translateZ`, `rotateX`,
  `rotateY`, and `perspective` are dropped or ignored because UI Toolkit
  transforms are 2D.

### `overflow`
- `auto` / `scroll` -> the converter promotes the element to `ScrollView`.
  When that promotion is inhibited (e.g. inside Label-only elements), the prop
  falls through to `overflow: hidden` with a warning.

### `cursor`
- Mapped via `CURSOR_MAP` to Unity's fixed cursor set. Unmapped values dropped.
- To extend: ship custom `Cursor` assets + a `--odd-cursor: <name>` bridge.

### `outline`
- Approximated to `border` on all four sides. Difference: `outline` doesn't
  occupy layout space; `border` does. CSS pixel widths use the same
  converter hairline scaling as normal borders. Warned.

### `white-space`
- `normal`, `nowrap`, `pre`, and `pre-wrap` pass through. `pre-line` and
  `break-spaces` approximate to `pre-wrap`.

### `text-align: justify`
- Falls back to `middle-left`. USS has no justified text.

---

## Bridged via package custom filters

These stay on normal UI Toolkit elements and emit a Unity `filter(...)` call
that references an asset in `Packages/au.com.oddgames.html2uxml/Runtime/Filters`.

| CSS | Filter asset | Notes |
|-----|--------------|-------|
| one-layer outer `box-shadow` | `ODDGamesBoxShadow` | Blurs the element subtree alpha and composites the shadow behind it. |
| `filter: drop-shadow(...)` | `ODDGamesBoxShadow` | Chained with Unity-native filters in the same `filter:` declaration. |
| `filter: brightness(...)`, `filter: saturate(...)` | `ODDGamesColorAdjust` | Fills Unity 6.3 built-in filter gaps. |
| simple two-stop percentage linear masks | `ODDGamesLinearMask` | Shader alpha mask over the element subtree. |

---

## Bridged via `Html2UxmlPanel` (custom props)

These promote the element to `<odd:Html2UxmlPanel>` and stash a `--odd-*` custom
property the runtime package reads.

| CSS                          | Custom prop(s)                                     | Notes |
|------------------------------|----------------------------------------------------|-------|
| `box-shadow` (inset or multi-layer) | `--odd-box-shadows` plus legacy `--odd-shadow-*` / `--odd-inner-shadow-*` fallback props | Up to eight supported records. Used for glow stacks, bevel, and emboss. Simple one-layer outer shadows use the `ODDGamesBoxShadow` custom filter instead. |
| `background: linear-gradient`| `--odd-gradient` (string)                           | Automatic mesh renderer. Non-axis-aligned angles are supported. |
| `background: radial-gradient`| `--odd-radial-gradient`, `--odd-radial-gradient-2` (strings) | Up to two radial layers plus one linear layer. Explicit `circle/ellipse <size> at x y` forms are parsed. |
| `background: repeating-linear-gradient(...)` | `--odd-repeating-linear-gradient` | Axis-aligned stripe and scanline patterns are painted as repeated pattern content. |
| tiled `radial-gradient(...)` dot grids | `--odd-tiled-radial-gradient`, `--odd-background-pattern-size` | Detected when a small radial dot gradient is paired with explicit `background-size`. |
| complex `mask-image: linear-gradient(...)` / `-webkit-mask-image: linear-gradient(...)` | `--odd-mask-image` | Html2UxmlPanel approximation for edge fades only; simple two-stop percentage masks use `ODDGamesLinearMask`. |
| `clip-path: polygon(...)`    | `--odd-clip-polygon` (string)                       | Polygon-shaped background/gradient rendering with inverse-fill fallback for uncovered edges. |
| `gap` / `row-gap` / `column-gap` | generated child margins; `--odd-row-gap`, `--odd-column-gap` when runtime help is still needed | Static flex containers bake gaps into direct-child margins. Converted buttons use `Html2UxmlButton`; elements already promoted to `Html2UxmlPanel` can still apply runtime gap based on `flex-direction`, including reverse directions. |

`font-family` emits `--odd-font-family` as a converter-side marker, but it
does **not** promote the element to `Html2UxmlPanel`. The CLI resolves that
marker to `-unity-font-definition` by default when Google Fonts, supplied
files, or installed local/system fonts provide a matching TTF/OTF. Use
`--no-download-fonts` to leave the marker unresolved.

### CSS variable resolution for bridged values

- **Today**: static `var(...)` values are resolved before bridge parsing for
  gradients, shadows, filters, polygon clips, and linear mask fades when the
  custom property is available in the conversion cascade.
- **Limits**: this is converter-time substitution only. Runtime USS custom
  property changes, unresolved fallbacks, cyclic variables, and variables that
  expand to unsupported syntax still drop or approximate according to the
  underlying feature.

---

## Element-level features

### `<details>` / `<summary>`
- Converted to `odd:Html2UxmlFoldout` with the `<summary>` text hoisted to `text=`.

### `<select>` / `<option>`
- Converted to `odd:Html2UxmlDropdownField` with `choices=` set from option text. Values
  are dropped (Unity dropdowns key by index).

### `<progress>` / `<meter>`
- Converted to `odd:Html2UxmlProgressBar` with `value` / `high-value` / `low-value`.

### `<img>`
- Synthesized into a per-element class with `background-image: url(...)` so
  authors can swap in a Unity asset reference.

### `::before` / `::after`
- Materialized as a leading / trailing Label child carrying the `content` text
  + the rule's other declarations. CSS counters not supported.

---

## Extending the catalogue

When you add or change a mapping in `mappings.py`:

1. Update the relevant section in this doc (or add a new one).
2. If the property is fully bridged with a custom prop, add it to the
   "Bridged via Html2UxmlPanel" table.
3. If you reclaim a property (drop -> approximate, or approximate -> bridged),
   move the entry rather than duplicating.
