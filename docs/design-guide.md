# Design Contract for html2uxml

Hand this document to whoever (human or AI) authors the HTML+CSS so the
converter produces clean Unity UXML+USS. Every convention here is something
the converter actively recognises. Code that follows the contract round-trips
losslessly; code that drifts gets dropped or approximated.

---

## 1. Layout

**Use flex only.** No `display: grid`, no `float`, no `display: table`,
no `position: static` reliance. USS is flex-only; everything else is
approximated.

- Always declare `display: flex` *and* `flex-direction: row | column`
  explicitly. (CSS defaults to row, USS defaults to column — implicit row
  is auto-backfilled but brittle.)
- Use `gap` / `row-gap` / `column-gap` (bridged via runtime kit, applied as
  child margin).
- For sizing, prefer `px`, `%`, `vw`, `vh`, `rem`. Avoid `calc()` mixing
  units, avoid `min()` / `max()` / `clamp()`.
- `position: absolute` is honoured; `position: fixed` is approximated as
  absolute (use the document root as the offset parent yourself).
- `box-sizing` is always **border-box** in USS — write to that assumption.

## 2. Typography

- Fonts must be available on Google Fonts; the converter downloads TTF/OTF
  files via the Google Fonts CSS API. System fonts (`Courier New`,
  `Helvetica`, etc.) won't bundle.
- Stick to:
  `font-size`, `font-weight`, `font-style: italic`, `letter-spacing`,
  `text-align`, `color`, `text-transform`, `text-decoration: underline`,
  `white-space`.
- Avoid `line-height` (no USS equivalent), `text-indent`, `word-break`,
  `hyphens`, `vertical-align`.

## 3. Color, Background, Borders

- Solid backgrounds, `linear-gradient(...)` (single direction), and
  `repeating-linear-gradient(...)` are all supported. Other gradient
  functions are dropped.
- `box-shadow`: single shadow only. Multi-shadow lists and `inset` shadows
  drop.
- `filter: drop-shadow(...)` is bridged to box-shadow. No other filter
  functions.
- `border: <width> solid <color>` is fully supported; dashed/dotted/double
  styles are flattened to solid.
- `border-radius` (all four corners) is supported.
- `clip-path: polygon(...)` is bridged. Other shapes (circle, inset, path,
  url) drop.
- `outline` becomes a real `border` (so it occupies layout space).

## 4. Images & Media

- `<img src="...">`: relative paths or full http(s) URLs. Path resolution
  is relative to the input HTML file (or the page URL).
- Inline `<svg>...</svg>` is preserved verbatim and written to
  `Assets/UI/Images/svg-N.svg`. Authoring rules:
  - Always set `width` and `height` (or a `viewBox`) on the root `<svg>`.
  - Don't reference external assets from inside the SVG (no `<image href>`,
    no `<use href>`).
  - Animations / scripts inside the SVG are stripped at import time.
- `background-image: url(...)` works for raster images (PNG/JPG/WebP).
- No `<canvas>`, `<video>`, `<audio>`, `<iframe>`, `<embed>`. They become
  empty placeholder elements.

## 5. Animation Contract

USS has no `@keyframes`; the bridge kit can run a tween scheduler if the
keyframes follow this shape:

- **Properties allowed in keyframes**:
  `opacity`, `transform: translateX/Y(...)`, `transform: scale(...)`,
  `transform: rotate(...)`, `background-position`.
- **Timing**: `linear`, `ease`, `ease-in`, `ease-out`, `ease-in-out`.
  No `cubic-bezier()`, no `steps()`.
- **Iteration**: `infinite` or an integer count. Direction `normal`,
  `reverse`, `alternate`. Fill modes are ignored.
- **Keyframe shape**: percentage stops (`0%`, `50%`, `100%`) only. Don't
  use `from` / `to` shorthand (still parses but be explicit).

Example the converter is happy with:

```css
@keyframes pulse {
  0%, 100% { opacity: 1; }
  50%      { opacity: 0.3; }
}
.dot { animation: pulse 1.6s ease-in-out infinite; }
```

Anything outside this subset becomes a static element on import.

## 6. Transitions

USS supports `transition` (`property duration timing delay`) on the same
property set animation supports. Use them for hover/focus/active states.
The triggering pseudo-classes (`:hover`, `:focus`, `:active`,
`:disabled`, `:checked`) are honoured.

## 7. Interactivity Contract

The converter sees static DOM only — no JS execution. To make buttons
behave at runtime, encode behaviour with these declarative attributes
that the bridge kit recognises:

| Attribute                     | Purpose |
|-------------------------------|---------|
| `data-toggle-class="<class>"` | On click, toggles `<class>` on `this`. |
| `data-toggle-target="<sel>"`  | Combined with `data-toggle-class`, targets a sibling/descendant by CSS selector instead of `this`. |
| `data-show-target="<sel>"`    | On click, sets target's `display: flex` (and hides others sharing the same `data-show-group`). |
| `data-show-group="<id>"`      | Mutually-exclusive show group (radio-button-style tabs). |
| `data-checked`                | Initial state for elements that participate in toggling. |
| `aria-expanded="true|false"`  | Honoured for `<details>` and disclosure widgets. |

For anything more bespoke (forms, fetches, derived state) leave a
placeholder `data-action="<name>"` attribute and write the C# handler
yourself; the bridge kit dispatches a UnityEvent named `<name>` on click.

Avoid: arbitrary `onclick="..."` strings, React state, jQuery toggles.
The converter will not parse them.

## 8. Components Worth Using

Each maps to a real Unity control:

| HTML                              | Unity control      |
|-----------------------------------|--------------------|
| `<button>`                        | `ui:Button`        |
| `<input type="text|email|...">`   | `ui:TextField`     |
| `<input type="number">`           | `ui:IntegerField`  |
| `<input type="checkbox">`         | `ui:Toggle`        |
| `<input type="range">`            | `ui:Slider`        |
| `<input type="color">`            | `ui:ColorField`    |
| `<select><option>`                | `ui:DropdownField` |
| `<textarea>`                      | `ui:TextField` (multiline) |
| `<details><summary>`              | `ui:Foldout`       |
| `<progress>` / `<meter>`          | `ui:ProgressBar`   |

Use the right tag and you get a Unity control with the right styling
hooks for free.

## 9. Pseudo-elements

- `::before` and `::after` are materialized as Label children with the
  rule's `content` text + the rest of the rule's styling.
- CSS counters, attr() in content, and `::marker` aren't supported.

## 10. Selectors

Stick to:

- Type, class, id selectors.
- Descendant, child (` > `), and adjacent-sibling (` + `) combinators.
- `:hover`, `:focus`, `:active`, `:disabled`, `:checked`,
  `:first-child`, `:last-child`, `:nth-child(2n)` — supported.
- `:not(.x)` — supported.

Skip: attribute selectors (`[type="text"]`), `::part`, `::slotted`,
container queries, `@layer`, `@scope`, `@media` (the converter strips
them).

## 11. File Layout the Converter Expects

```
your-design/
  index.html          # entry
  styles.css          # optional external sheet
  images/
    icon.png
  fonts/              # optional, Google Fonts handled separately
```

- One root HTML file passed to `html2uxml`.
- External CSS files referenced via `<link rel="stylesheet">` are
  fetched (URL inputs) or read from disk (file inputs).
- Image paths must resolve relative to the HTML file.

## 12. Hard-No List (Avoid Entirely)

Features in this list are **silently dropped** and have no bridge in
flight. If the design depends on any of them, redesign the affected
piece before handing it off — the converter will not warn loudly enough
to save you.

### Layout
- `display: grid`, `display: table`, `display: inline-grid`, `display: inline-block`
- `float`, `clear`
- `column-count` / multi-column layout
- `position: sticky`, `position: fixed` (the latter is silently re-aliased)
- `aspect-ratio` (use explicit `width` + `height` instead)
- `place-items`, `place-content`, `place-self` (grid-only shorthands)
- Container queries (`@container`)
- CSS subgrid

### Visual effects
- `backdrop-filter` (frosted glass)
- `mask`, `mask-image`, `-webkit-mask`
- `mix-blend-mode`, `background-blend-mode`
- `filter` other than `drop-shadow(...)` (no blur, hue-rotate, saturate, etc.)
- `clip-path` shapes other than `polygon(...)` (no `circle()`, `ellipse()`,
  `inset()`, `path()`, `url(#mask)`)
- Multi-shadow `box-shadow` lists
- `inset` / inner shadows
- Conic and radial gradients
- CSS variables that resolve to gradient/shadow strings (only literal
  declarations are bridged)
- `transform-style: preserve-3d`, `perspective`, `backface-visibility`
- 3D transforms (`rotateX`, `rotateY`, `rotateZ` other than `rotate`,
  `translateZ`, `matrix3d`)

### Typography
- `line-height` (use `font-size` + parent `padding` instead)
- `text-shadow` with multiple shadows
- `writing-mode`, `direction: rtl`
- `font-variant-*`, `font-feature-settings`
- `text-stroke`, `-webkit-text-stroke`
- `hyphens`, `word-break`, `overflow-wrap`
- Variable-font axes beyond weight (`wdth`, `slnt`, `opsz`, custom axes)
- `@font-face` with non-Google sources

### Color & input
- `color-mix()`, `oklch()`, `lab()`, `lch()`, `hwb()`, relative-color syntax
- Wide-gamut color spaces (`@media (color-gamut: p3)`)
- `::selection` styling
- `caret-color`
- System color keywords (`Canvas`, `LinkText`, etc.)

### Animation
- `cubic-bezier()` and `steps()` timing functions
- `@keyframes` driving anything outside the whitelisted property set
  (`opacity`, `translate`, `scale`, `rotate`, `background-position`)
- CSS scroll-driven animations (`animation-timeline`, `view-timeline`)
- `@scope`, `@layer`, `@property` registrations

### Interactivity / scripting
- Inline `onclick`, `onchange`, `oninput`, `onsubmit` handlers
- React, Vue, Svelte, Alpine, htmx — anything that materialises DOM at
  runtime. The converter sees only the static markup; runtime-rendered
  components produce empty UXML. Render to static HTML first.
- `<form>` submission semantics (no network in Unity).
- `contenteditable`
- `<dialog>` open/close behaviour (renders, but `showModal()` doesn't fire).
- Web Components (`<my-element>` custom elements + shadow DOM).
- IFrames, embeds, web-views.

### Media
- `<canvas>`, `<video>`, `<audio>`
- Background `url()` referencing a CSS sprite (the entire sheet is loaded
  but `background-position` cropping is approximate at best).
- Lazy-loaded images (`loading="lazy"` is ignored — everything is loaded
  on import).
- Picture sources / `srcset` (only the `src` attribute is read).

### HTML structure
- `<table>`, `<thead>`, `<tbody>`, `<tr>`, `<td>` — flatten to flex
  containers in the source.
- `<map>` / `<area>` image hotspots
- `<noscript>`, `<template>` (stripped)
- `<slot>` and shadow-root placeholders

### Selectors / at-rules
- `@media` queries (everything collapses to the default resolution).
- `@supports`, `@import`
- `[attr~=value]`, `[attr^=value]`, `[attr$=value]`, `[attr*=value]`
- `:has(...)` (relational pseudo-class)
- `::part`, `::slotted`, `::backdrop`
- `:where()`, `:is()` with non-trivial argument lists (parsed but
  precedence may shift)

### Units
- `calc()` mixing length with percentage / viewport units
- `min()`, `max()`, `clamp()`
- Container query units (`cqw`, `cqh`, `cqi`, `cqb`)
- `lvh`, `lvw`, `dvh`, `dvw` — dynamic viewport units
- `q`, `cm`, `mm`, `in`, `pc`, `pt` (use `px`)

### Workflow gotchas
- HTML pages that require a build step (`<script type="module">` with
  imports). Pre-bundle to a single static file.
- HTML pages that depend on browser DevTools-only features (`@scroll-timeline`,
  `:state()`).
- Designs sized to a specific viewport using `vh/vw` only — supply
  fixed `px` widths/heights so the UXML survives at any Unity panel
  resolution.

If a design needs something on this list, treat it as a **redesign
trigger**, not a converter bug. The closer the source sticks to the
contract, the less hand-cleanup the Unity output needs.

## 13. Quick Self-Check

Before handing a design to the converter, run through:

- [ ] No `display: grid` anywhere.
- [ ] Every flex container declares `flex-direction` explicitly.
- [ ] All fonts come from Google Fonts.
- [ ] All animations use only opacity/transform/background-position.
- [ ] Interactive buttons use `data-toggle-*` / `data-show-*` not JS.
- [ ] SVGs have `width`+`height` or `viewBox`.
- [ ] No `<canvas>`, `<iframe>`, `<video>`, `<audio>`.
- [ ] No `calc()` mixing units.
- [ ] No `position: fixed` reliance (treated as absolute).

If every box checks, conversion is high-fidelity. If a box fails, expect
the matching feature to drop or approximate per `docs/unsupported.md`.
