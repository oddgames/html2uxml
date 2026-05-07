# Unsupported CSS Properties

Catalogue of every CSS property the converter cannot pass through directly to
USS. Each entry says **what authors use it for**, **what the converter does
today** (drop, approximate, or bridge), and **what it would take to support it
better** (USS equivalent, runtime bridge, or HTML-side rewrite).

Keep this list in sync with `DROP_PROPS` in `html2uxml/mappings.py` and the
per-property handlers in `_map_one`.

---

## Hard-dropped properties (`DROP_PROPS`)

These are silently rewritten to nothing. Warning text:
`unsupported in USS, dropped: <prop>: <value>`.

### `animation` / `animation-*`
- **Used for**: keyframe-driven loops (pulse, ticker, fade-in).
- **Today**: dropped. `@keyframes` rules also stripped.
- **To support**: write a runtime bridge that registers a keyframe parser,
  schedules a `IVisualElementScheduledItem`, and tweens style each tick. Map a
  subset (`opacity`, `translate`, `scale`, `rotate`) and warn on the rest.
  Cheaper alternative: detect the animated property + duration and emit a
  `transition` instead so static toggles work.

### `backdrop-filter`
- **Used for**: frosted-glass panels behind modals.
- **Today**: dropped (no GPU shader available in USS).
- **To support**: ship a `BlurBox` element using `RenderTexture` + a Gaussian
  shader. Bridge `--gg-backdrop-blur: <px>` and read it in C#.

### `mask`, `mask-image`, `mask-type`
- **Used for**: alpha cutouts, gradient fades to transparent.
- **Today**: dropped.
- **To support**: requires a stencil/clip pass. Stretch goal: bridge
  `mask-image: linear-gradient(...)` to a custom shader on the element.

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
  `--gg-blend-mode: <mode>` and pick a Material per mode.

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
- **Today**: dropped. USS only has `white-space: normal | nowrap | pre`.
- **To support**: nothing in current USS.

### `line-height`
- **Used for**: vertical text rhythm.
- **Today**: `line-height: <px>` auto-mapped to `-unity-paragraph-spacing`.
  Unitless multipliers (`1.5`) drop with a warning since the converter
  can't resolve the parent font-size at mapping time.
- **To support better**: when authoring with `line-height: 1.5`-style
  multipliers, the converter would need to walk the resolver and emit a
  font-size-relative paragraph-spacing per element. Not implemented.

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

### `overflow`
- `auto` / `scroll` -> the converter promotes the element to `ScrollView`.
  When that promotion is inhibited (e.g. inside Label-only elements), the prop
  falls through to `overflow: hidden` with a warning.

### `cursor`
- Mapped via `CURSOR_MAP` to Unity's fixed cursor set. Unmapped values dropped.
- To extend: ship custom `Cursor` assets + a `--gg-cursor: <name>` bridge.

### `outline`
- Approximated to `border` on all four sides. Difference: `outline` doesn't
  occupy layout space; `border` does. Warned.

### `white-space`
- `pre-wrap`, `pre-line`, `break-spaces` collapsed to `pre` with a warning.

### `text-align: justify`
- Falls back to `middle-left`. USS has no justified text.

---

## Bridged via `BridgeBox` (custom props)

These promote the element to `<gg:BridgeBox>` and stash a `--gg-*` custom
property the runtime kit reads.

| CSS                          | Custom prop(s)                                     | Notes |
|------------------------------|----------------------------------------------------|-------|
| `box-shadow` (single)        | `--gg-shadow-offset-x/-y/-blur/-color`             | Multi-shadow / inset dropped. |
| `filter: drop-shadow(...)`   | same as box-shadow                                  | Other filter functions dropped. |
| `background: linear-gradient`| `--gg-gradient` (string)                           | Painted as 32-stripe approximation. Non-axis-aligned angles fall back to a flat tint. |
| `clip-path: polygon(...)`    | `--gg-clip-polygon` (string)                       | Inverse-fill mask; needs a solid background to look right. |
| `font-family`                | `--gg-font-family` (string)                        | `--download-fonts` resolves to `-unity-font-definition` at convert time. |
| `gap` / `row-gap` / `column-gap` | `--gg-row-gap`, `--gg-column-gap`              | BridgeBox applies margin to direct children based on `flex-direction`. Reverse-direction ordering not yet handled. |

---

## Element-level features

### `<details>` / `<summary>`
- Converted to `ui:Foldout` with the `<summary>` text hoisted to `text=`.

### `<select>` / `<option>`
- Converted to `ui:DropdownField` with `choices=` set from option text. Values
  are dropped (Unity dropdowns key by index).

### `<progress>` / `<meter>`
- Converted to `ui:ProgressBar` with `value` / `high-value` / `low-value`.

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
   "Bridged via BridgeBox" table.
3. If you reclaim a property (drop -> approximate, or approximate -> bridged),
   move the entry rather than duplicating.
