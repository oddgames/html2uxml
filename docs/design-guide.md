# Claude Design CSS Guide

This guide is for Claude Design when creating HTML and CSS for game UI. It is
an authoring guide only: use these CSS patterns, avoid the risky ones, and keep
the source predictable for Unity UI Toolkit.

## Core Rules

- Put visual styling in CSS classes. Do not use inline `style` attributes.
- Reuse classes for repeated visual treatments.
- Use one stable root ID per screen.
- Add stable IDs or clear classes to interactive elements.
- Build layouts with flexbox.
- Use fixed `px` artboard dimensions for game HUD screens.
- Prefer simple CSS that describes the final visual directly.
- Keep the number of fonts, colors, shadows, and one-off classes low.

Good:

```html
<section id="screen-garage" class="screen screen-garage">
  <button id="readyButton" class="button button-primary button-large">
    Ready
  </button>
</section>
```

```css
.screen {
  width: 760px;
  height: 360px;
  display: flex;
  flex-direction: column;
  background: #050608;
}

.button {
  display: flex;
  flex-direction: row;
  align-items: center;
  justify-content: center;
  padding: 8px 14px;
  font-family: "Saira Condensed", "Barlow Condensed", sans-serif;
  font-size: 18px;
  font-weight: 800;
}

.button-primary {
  background: #ffbf13;
  color: #050608;
}
```

Avoid:

```html
<button style="padding: 8px 14px; background: #ffbf13">Ready</button>
```

## Class Naming

Use composable class names:

- Components: `.screen`, `.panel`, `.button`, `.toolbar`, `.card`, `.badge`,
  `.ticker`, `.stat-row`, `.driver-row`, `.icon-button`.
- Variants: `.button-primary`, `.panel-dark`, `.badge-live`,
  `.row-selected`, `.tone-danger`, `.size-large`.
- States: `.is-open`, `.is-selected`, `.is-disabled`, `.is-live`,
  `.is-warning`.
- Shared utilities only when useful: `.row`, `.column`, `.absolute-fill`,
  `.text-uppercase`, `.fill`.

Avoid generated or visual-only names:

- `.style-1`
- `.box-37`
- `.yellow-text-13px`
- `.left-top-title-copy`

If two elements look the same, share a class. If only one token changes, use a
variant class.

## Layout

Use flexbox for all layout.

```css
.row {
  display: flex;
  flex-direction: row;
  align-items: center;
}

.column {
  display: flex;
  flex-direction: column;
}

.spaced-row {
  display: flex;
  flex-direction: row;
  align-items: center;
  gap: 8px;
}
```

Use:

- `display: flex`
- `flex-direction: row | column`
- `align-items`
- `justify-content`
- `gap`, `row-gap`, `column-gap`
- `flex-grow`, `flex-shrink`, `flex-basis`
- `width`, `height`, `min-width`, `min-height`, `max-width`, `max-height`
- `margin`, `padding`

Avoid:

- `display: grid`
- `display: table`
- `display: inline-grid`
- `display: inline-block`
- `float`, `clear`
- multi-column layout
- container queries
- subgrid
- `place-items`, `place-content`, `place-self`

## Positioning

Use normal flex layout first. Use absolute positioning for fixed HUD overlays,
badges, corner controls, and decorative layers.

```css
.screen {
  position: relative;
}

.absolute-fill {
  position: absolute;
  inset: 0;
}

.top-right-toolbar {
  position: absolute;
  top: 12px;
  right: 12px;
  display: flex;
  flex-direction: row;
  gap: 8px;
}
```

Use:

- `position: relative` on the local containing block.
- `position: absolute` for overlays inside that block.
- `top`, `right`, `bottom`, `left`, or `inset`.
- DOM order for layering: later siblings appear above earlier siblings.
- Small `z-index` values only among siblings under the same parent.
- `overflow: hidden` for clipping.

Avoid:

- `position: fixed`
- `position: sticky`
- very large `z-index` values
- relying on nested stacking context behavior from `opacity`, `transform`,
  `filter`, `isolation`, or blend modes
- placing overlays inside deeply clipped or transformed parents

## Sizing and Units

Use:

- `px` for game HUD screens, rows, icons, text, spacing, and precise panels.
- `%` for simple parent-relative sizing.
- `rem` only when the root font size is controlled.
- `aspect-ratio` for fixed-format media, portraits, tiles, and icon boxes.

Avoid:

- `calc()` with mixed units
- `min()`, `max()`, `clamp()`
- `dvh`, `dvw`, `lvh`, `lvw`
- physical units like `pt`, `cm`, `mm`, `in`, `pc`
- layouts that only work because everything is tied to viewport units

## Typography

Use a small, sourceable font set.

- Prefer Google Fonts / open-source families.
- Use supplied `.ttf` or `.otf` files for brand or game fonts.
- Use common Windows system fonts only when the target machine is controlled.
- Use one primary UI family and at most one accent family per screen.
- Prefer real italic font files over synthetic italic when available.

```css
.hud-title {
  font-family: "Saira Condensed", "Barlow Condensed", sans-serif;
  font-size: 24px;
  font-weight: 900;
  font-style: italic;
  letter-spacing: 1.2px;
  line-height: 28px;
  text-transform: uppercase;
  color: #ffffff;
  text-shadow: 2px 2px 0 #000000;
}
```

Use:

- `font-family`
- `font-size`
- numeric `font-weight` values such as `400`, `700`, `800`, `900`
- `font-style: normal | italic`
- `letter-spacing`
- `word-spacing`
- `line-height` in `px`
- `text-align`
- `text-transform: uppercase`
- `text-decoration: underline`
- `white-space: nowrap | normal | pre | pre-wrap`
- `text-overflow: ellipsis` on fixed-width labels
- `text-shadow`
- `-webkit-text-stroke` for strong display outlines
- `-unity-text-outline-width` and `-unity-text-outline-color` for Unity text
  outlines

Avoid:

- many font families in one screen
- proprietary fonts unless the font files are supplied
- variable font axes other than weight
- browser-only font feature tuning
- paragraph text tricks such as hyphenation and advanced wrapping
- depending on fallback fonts to define the design

## Text Patterns

For compact HUD labels:

```css
.hud-label {
  height: 18px;
  line-height: 18px;
  font-family: "Saira Condensed", sans-serif;
  font-size: 12px;
  font-weight: 800;
  font-style: italic;
  letter-spacing: 1.5px;
  text-transform: uppercase;
  white-space: nowrap;
}
```

For outlined or shadowed display numbers:

```css
.result-time {
  font-family: "Saira Condensed", sans-serif;
  font-size: 28px;
  font-weight: 900;
  font-style: italic;
  color: #ffffff;
  text-shadow: 2px 2px 0 #000000;
  -webkit-text-stroke: 1px #000000;
}
```

For truncating player names:

```css
.driver-name {
  width: 112px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
```

## Color

Use:

- hex colors: `#ffbf13`, `#050608`
- `rgb(...)`, `rgba(...)`
- `hsl(...)`, `hsla(...)`
- alpha only where transparency is intended

Avoid:

- `oklch()`, `lab()`, `lch()`
- `color-mix()`
- CSS variables for every color unless the palette is reused heavily
- parent `opacity` when children overlap other UI

Prefer alpha in the actual color:

```css
.muted-text {
  color: rgba(255, 255, 255, 0.55);
}
```

Avoid this for containers with child content:

```css
.muted-panel {
  opacity: 0.55;
}
```

## Backgrounds and Gradients

Use CSS backgrounds for panels, lighting, overlays, and simple texture.

```css
.panel-dark {
  background:
    linear-gradient(180deg, rgba(255, 255, 255, 0.08), rgba(0, 0, 0, 0.00)),
    linear-gradient(90deg, #070b12 0%, #111820 55%, #050608 100%);
}
```

Use:

- `background-color`
- `background-image: url(...)`
- `background-size: cover | contain | 100% 100% | <px> <px>`
- `background-position`
- `background-repeat`
- `linear-gradient(...)`
- `radial-gradient(...)`
- `repeating-linear-gradient(...)`

For scanlines:

```css
.scanline-panel {
  background:
    repeating-linear-gradient(
      180deg,
      rgba(255, 255, 255, 0.04) 0,
      rgba(255, 255, 255, 0.04) 1px,
      rgba(0, 0, 0, 0.00) 1px,
      rgba(0, 0, 0, 0.00) 4px
    ),
    #080a0d;
}
```

For subtle dot grain:

```css
.dot-grain {
  background:
    radial-gradient(circle, rgba(255, 255, 255, 0.06) 0 1px, transparent 1px)
      0 0 / 8px 8px,
    #07090d;
}
```

Avoid:

- `conic-gradient(...)`
- `background-blend-mode`
- `mix-blend-mode`
- animated gradients
- gradients that rely on many tiny fractional stops

## Borders, Radius, Shadows, Emboss

Use borders and shadows for bevels, embossing, button chrome, and inset panel
depth.

```css
.embossed-panel {
  border: 1px solid rgba(255, 255, 255, 0.18);
  background: linear-gradient(180deg, #222832 0%, #11151b 100%);
  box-shadow:
    inset 0 1px 0 rgba(255, 255, 255, 0.18),
    inset 0 -2px 0 rgba(0, 0, 0, 0.55),
    0 2px 4px rgba(0, 0, 0, 0.45);
}
```

Use:

- `border`
- `border-color`
- `border-width`
- `border-radius`
- `box-shadow`
- `inset` shadows

Avoid:

- dozens of layered shadows on many repeated rows
- very large blur shadows on scrolling lists
- border styles other than `solid`
- `outline-offset` as layout

## Slanted Shapes

Use `clip-path: polygon(...)` for slanted panels and motorsport-style chrome.

```css
.slanted-panel {
  clip-path: polygon(6% 0, 100% 0, 94% 100%, 0 100%);
}
```

For a slanted result row:

```css
.result-row {
  display: flex;
  flex-direction: row;
  align-items: center;
  height: 28px;
  background: linear-gradient(90deg, #110404 0%, #050608 60%, #030405 100%);
  clip-path: polygon(3% 0, 100% 0, 97% 100%, 0 100%);
}
```

Use:

- `clip-path: polygon(...)`
- simple 3 to 8 point polygons
- a normal rectangular parent when text or hit area must stay predictable

Avoid:

- `clip-path: path(...)`
- CSS masks for complex shapes
- `shape-outside`
- relying on clipped children to create layout

## Images and SVG

Use raster images for complex art.

- Logos, portraits, game art, and textured badges should be PNG/WebP/JPG.
- Use transparent PNG for irregular logos.
- Use SVG only for simple static icons and flat vector marks.
- Inline SVGs should include `viewBox`, `width`, and `height`.
- Keep SVGs self-contained.

Avoid in SVG:

- external references
- `<use>` references to external sprites
- filters
- masks
- animation
- embedded scripts
- complex text inside SVG

For icon buttons:

```css
.icon-button {
  width: 32px;
  height: 32px;
  display: flex;
  flex-direction: row;
  align-items: center;
  justify-content: center;
  border: 1px solid rgba(255, 255, 255, 0.22);
  background: rgba(0, 0, 0, 0.18);
}

.icon {
  width: 20px;
  height: 20px;
  flex-shrink: 0;
}
```

## Filters and Blur

Use simple filters sparingly.

Use:

- `filter: blur(<px>)` for isolated decorative layers
- `filter: grayscale(...)`
- `filter: invert(...)`
- `filter: opacity(...)`
- `filter: sepia(...)`
- `filter: hue-rotate(...)`
- `filter: contrast(...)`

Avoid:

- `backdrop-filter`
- `-webkit-backdrop-filter`
- `filter: drop-shadow(...)` on repeated elements
- heavy blur on large moving regions
- chaining many filters on the same element

For frosted glass, fake the look with a translucent background and subtle
highlight instead of live backdrop blur:

```css
.frosted-panel {
  background:
    linear-gradient(180deg, rgba(255, 255, 255, 0.14), rgba(255, 255, 255, 0.04)),
    rgba(20, 24, 30, 0.72);
  border: 1px solid rgba(255, 255, 255, 0.18);
}
```

## Masks and Fades

Prefer explicit gradient overlays for fades.

```css
.bottom-fade {
  position: absolute;
  left: 0;
  right: 0;
  bottom: 0;
  height: 48px;
  pointer-events: none;
  background: linear-gradient(180deg, rgba(0, 0, 0, 0.00), #000000);
}
```

Avoid:

- complex CSS masks
- image masks
- nested mask stacks
- masks used as the primary way to draw shapes

## Animation and State

Use state classes for UI changes.

```css
.menu {
  opacity: 0;
}

.menu.is-open {
  opacity: 1;
}

.row.is-selected {
  background: #ffbf13;
  color: #050608;
}
```

Use:

- `.is-open`
- `.is-selected`
- `.is-disabled`
- `.is-live`
- `.is-warning`
- simple `:hover`, `:focus`, `:active`, `:disabled`
- `transition-property`
- `transition-duration`
- `transition-timing-function`
- short `@keyframes` for `opacity`, `translate`, `scale`, `rotate`,
  `color`, or `background-color`

Avoid:

- keyframes that change layout (`width`, `height`, `left`, `top`, margins,
  padding, flex values)
- animating filters, masks, clip paths, shadows, or background images
- multiple simultaneous animations on the same element
- infinite animation on large lists or full-screen panels
- visual state that only exists in JavaScript
- transitions between different unit types

## Pointer Events

Use `pointer-events: none` only for decorative layers that should never
receive input.

```css
.chrome-highlight {
  pointer-events: none;
}
```

Interactive controls should use real elements:

- `button` for actions
- `input` for text or toggles
- `select` for option lists
- `textarea` for multi-line text

Avoid using plain decorative `div` elements as the only interactive target
unless a stable ID and role are provided.

## Selectors

Keep selectors simple and class-based.

Use:

- `.component`
- `.component .child`
- `.component > .child`
- `.component.variant`
- `.component.is-selected`
- selector lists such as `.button, .tab`
- pseudo-classes for direct state: `:hover`, `:focus`, `:active`, `:disabled`

Avoid:

- `:has(...)`
- `::part`
- `::slotted`
- deep `nth-child(...)` selectors as core styling
- sibling selectors for dynamic UI behavior
- framework-generated selectors
- `@media`, `@container`, `@supports`, `@layer`, `@scope`
- deep chains such as `body > div:nth-child(2) > div > div`

## Common Patterns

Ready prompt with a round dot:

```css
.ready-cta {
  display: flex;
  flex-direction: row;
  align-items: center;
  justify-content: center;
  gap: 10px;
  font-family: "Saira Condensed", sans-serif;
  font-size: 12px;
  font-weight: 800;
  font-style: italic;
  letter-spacing: 2px;
  text-transform: uppercase;
  color: #dca40d;
  text-shadow: 1px 1px 0 #000000;
}

.ready-cta-dot {
  width: 6px;
  height: 6px;
  flex-shrink: 0;
  border-radius: 999px;
  background: #dca40d;
}
```

Broadcast ticker:

```css
.ticker {
  height: 26px;
  display: flex;
  flex-direction: row;
  align-items: center;
  gap: 12px;
  padding: 0 14px;
  background: #050608;
  border-top: 2px solid #ffbf13;
}

.ticker-label {
  font-family: "Saira Condensed", sans-serif;
  font-size: 16px;
  font-weight: 900;
  font-style: italic;
  letter-spacing: 1px;
  text-transform: uppercase;
  color: #ffbf13;
}
```

Toolbar icon rail:

```css
.toolbar {
  display: flex;
  flex-direction: row;
  align-items: center;
  gap: 12px;
  padding: 8px 12px;
  background: rgba(25, 30, 38, 0.96);
}

.toolbar-icon {
  width: 24px;
  height: 24px;
  flex-shrink: 0;
}
```

## Hard-No List

Avoid these in design source:

- Inline styles.
- Inline JavaScript event handlers.
- Grid, table layout, floats, and multi-column layout.
- `position: fixed` and `position: sticky`.
- layout-changing keyframes and large infinite animations.
- `backdrop-filter` and `-webkit-backdrop-filter`.
- `mix-blend-mode` and `background-blend-mode`.
- `conic-gradient`.
- `clip-path` shapes other than `polygon(...)`.
- General masks beyond simple edge fades.
- `canvas`, `video`, `audio`, `iframe`, `embed`.
- Web Components and shadow DOM.
- `@media`, `@container`, `@supports`, `@layer`, `@scope`.
- `:has(...)`, `::part`, `::slotted`.
- `calc()` with mixed units, `min()`, `max()`, `clamp()`.
- Modern color functions such as `oklch()`, `lab()`, `lch()`, `color-mix()`.
- Variable font axes beyond weight.
- Browser-only font features.
- CSS that depends on framework runtime state to look correct.

## Self-Check

Before handing off a design, verify:

- No inline styles.
- Shared visuals use shared classes.
- Every flex container declares `flex-direction`.
- No grid, table, or float layout.
- Fonts are open source, supplied, or known system fonts.
- Each screen has one root ID.
- Interactive elements have stable IDs or classes.
- Keyframe animations only touch opacity, translate, scale, rotate, color, or background color.
- SVGs have `viewBox`, `width`, and `height`.
- Complex logos and portraits are raster images.
- Slanted panels use `clip-path: polygon(...)`.
- Emboss and bevel use `box-shadow` and inset shadows.
- Frosted glass is represented as translucent chrome, not backdrop blur.
- Selectors are simple and class-based.
