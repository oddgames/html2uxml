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

## Element Coverage

| HTML tag(s)                                                        | UXML element                       | Notes |
|--------------------------------------------------------------------|------------------------------------|-------|
| `div`, `section`, `article`, `header`, `footer`, `main`, `nav`, `aside`, `form`, `dialog`, `menu`, `figure` | `ui:VisualElement`        | Generic flex container. |
| `span`, `p`, `label`, `small`, `strong`, `em`, `b`, `i`, `u`, `code`, `pre`, `h1`-`h6`, `legend`, `time`, `output`, `figcaption`, `dt`, `dd`, `caption` | `ui:Label` | Text container. `<u>`, `<strong>`, etc. become inline rich-text. |
| `a`                                                                | `ui:Button`                        | `href` preserved as a UXML attribute. |
| `button`                                                           | `odd:Html2UxmlButton`              | Carries `button-type`, `form-name`, `form-value`. |
| `img`                                                              | `odd:Html2UxmlImage`               | Sprite via `background-image` on a generated class. |
| `input[type=text\|email\|search\|url\|tel\|password\|date\|...\|color]` | `odd:Html2UxmlTextField`     | `password`, `placeholder`, validation attrs survive. |
| `input[type=number]`                                               | `odd:Html2UxmlFloatField`          | `min` / `max` / `step`. |
| `input[type=range]`                                                | `ui:Slider`                        | `low-value` / `high-value` / `step`. |
| `input[type=checkbox]`                                             | `ui:Toggle`                        | `checked` -> `value="true"`. |
| `input[type=radio]`                                                | `ui:RadioButton`                   | `name` -> `html-name` for grouping. |
| `input[type=button\|submit\|reset\|file]`                          | `odd:Html2UxmlButton`              | |
| `select`                                                           | `odd:Html2UxmlDropdownField`       | `<option>` text -> `choices`. |
| `textarea`                                                         | `odd:Html2UxmlTextField`           | `multiline="true"`. |
| `fieldset`                                                         | `ui:GroupBox`                      | `disabled` -> `enabled="false"` (cascades). |
| `details` / `summary`                                              | `odd:Html2UxmlFoldout`             | `<summary>` text hoisted to `text=`. |
| `progress`, `meter`                                                | `odd:Html2UxmlProgressBar` / `odd:Html2UxmlMeter` | `value`, `low-value`, `high-value`. |
| `ul`, `ol`, `li`, `dl`                                             | `ui:VisualElement`                 | Markers are synthesized; no native list-style. |
| `table`, `tr`, `thead`/`tbody`/`tfoot`, `th`, `td`                 | `odd:Html2UxmlTable*`              | Flattened to flex. `colspan`/`rowspan` preserved. |
| `video`, `audio`                                                   | `odd:Html2UxmlVideo` / `Html2UxmlAudio` | `src`, `controls`, `autoplay`, `loop`, `muted`, `poster`. |
| `canvas`                                                           | `odd:Html2UxmlCanvas`              | `width`/`height` -> `canvas-width`/`canvas-height`. |
| `embed`, `object`, `math`                                          | `odd:Html2UxmlEmbed`               | Placeholder; `src` / `data` / `type` preserved. |
| `br`, `hr`                                                         | `ui:VisualElement` with `class="br"` / `class="hr"` | Styled by UA defaults. |
| `svg`, `iframe`, `noscript`, `script`, `template`                  | placeholder `ui:VisualElement`     | Children dropped. |

## Supported CSS Properties

### Layout

| Property | Notes |
|---|---|
| `display: none\|flex\|inline-flex\|block\|inline\|inline-block\|list-item` | Everything except `none` becomes `flex`. `block` / `list-item` -> column; `inline` / `inline-flex` -> row. `grid` / `table` approximated as flex with a warning. |
| `flex-direction`, `flex-wrap` | Pass through. |
| `flex`, `flex-grow`, `flex-shrink`, `flex-basis` | Shorthand split. |
| `align-items`, `align-self`, `align-content` | Pass through. |
| `justify-content`, `justify-self` | Pass through. `text-align` is mirrored to `justify-content` on flex parents that don't declare one. |
| `gap`, `row-gap`, `column-gap` | Baked into per-child margins at convert time; original property dropped. Promoted elements also receive `--odd-row-gap` / `--odd-column-gap` when runtime help is needed. |
| `position: relative\|absolute\|static` | `static` collapses to `relative`. `fixed` / `sticky` warn and become `absolute`. |
| `top`, `right`, `bottom`, `left`, `inset` | `inset` shorthand expands to all four. |
| `width`, `height`, `min-width`, `min-height`, `max-width`, `max-height` | Pass through. `auto` is preserved on the dimension props. |
| `margin`, `padding` (and `-top`/`-right`/`-bottom`/`-left`) | Pass through. |
| `overflow: hidden\|visible` | `auto` / `scroll` / `clip` warn and become `hidden`; the converter promotes scrollable subtrees to `ScrollView` separately. `overflow-x` / `overflow-y` are unsupported. |
| `aspect-ratio` | Pass through. |
| `visibility` | Pass through. |
| `pointer-events: none` | Emitted as `picking-mode="Ignore"` on the UXML element. Other values dropped. |

USS is **always border-box**. Width/height includes padding and border; `box-sizing` is dropped silently.

### Visual

| Property | Notes |
|---|---|
| `background-color`, `color`, `opacity` | Pass through. Parent opacity over absolutely-positioned overlay children is rebalanced onto siblings to avoid bleed-through. |
| `background-image: url(...)` | Pass through. Multiple comma layers are flattened (single image only). |
| `background: <gradient>` | `linear-gradient`, `repeating-linear-gradient`, `radial-gradient`, `repeating-radial-gradient` route through `Html2UxmlPanel` custom props. Up to 1 linear + 1 repeating-linear + 2 radial layers. `conic-gradient` is **not** bridged. |
| `background-position`, `background-size`, `background-repeat` | Pass through (single layer; comma-separated lists are collapsed to the first token). `cover`, `contain`, `100% 100%`, explicit px sizes all work. |
| `border` (shorthand), `border-top` / `-right` / `-bottom` / `-left` | Split into width + color. Style is always solid in USS. |
| `border-width`, `border-color`, `border-radius` (and per-corner variants) | Pass through. Oversized corner radii are clamped to the element box. |
| `border-image`, `border-image-source`, `border-image-slice` | Become `background-image` + `-unity-slice-{top,right,bottom,left}` 9-slice. |
| `outline` | Approximated as `border` (occupies layout). `outline-offset` dropped. |
| `box-shadow` | One outer shadow with no spread becomes the `ODDGamesBoxShadow` custom filter. Multi-layer, inset, and spread shadows route through `--odd-box-shadows` (up to 8) on `Html2UxmlPanel`. |
| `clip-path: polygon(...)` | Bridged via `--odd-clip-polygon`. Other shapes (`circle`, `path`, `inset`) drop with a warning. |
| `filter` | `blur`, `grayscale`, `invert`, `opacity`, `sepia`, `hue-rotate`, `contrast` are native. `drop-shadow` routes to `--odd-box-shadows`. `brightness` / `saturate` map to the `ODDGamesColorAdjust` custom filter (currently disabled by flag — drop with warning). |
| `backdrop-filter`, `-webkit-backdrop-filter` | Approximated as a normal `filter` on the element subtree (does not sample the backdrop). For a true frosted look use the package `BackdropBlurPanel`. |
| `mask-image`, `-webkit-mask-image` | Two-stop percentage `linear-gradient(...)` masks become `ODDGamesLinearMask`. More complex `linear-gradient` / `repeating-linear-gradient` masks fall back to `--odd-mask-image`. URL/radial/conic masks dropped. |
| `transform`, `translate`, `rotate`, `scale`, `transform-origin` | See [Transform functions](#transform-functions) below. |
| `cursor` | Mapped via the USS keyword set (`arrow`, `link`, `text`, `pan`, `resize-vertical`, `resize-horizontal`, `zoom`). Unmapped keywords dropped. `url(...)` / `resource(...)` pass through. |
| `object-fit` | Maps to `-unity-background-scale-mode` (`fill`->`stretch-to-fill`, `cover`->`scale-and-crop`, `contain`/`scale-down`->`scale-to-fit`). |

### Typography

| Property | Notes |
|---|---|
| `color` | Pass through. |
| `font-size`, `letter-spacing`, `word-spacing` | Pass through. |
| `font-family` | First family in the list emitted as `--odd-font-family`. Generic families (`serif`, `sans-serif`, etc.) dropped. CLI resolves the marker to `-unity-font-definition` from Google Fonts / supplied / installed TTF/OTF unless `--no-download-fonts` is set. |
| `font-weight` | Numeric value preserved as `--odd-font-weight`. `>=600`, `bold`, `bolder` -> `-unity-font-style: bold` (and combine with italic if both). |
| `font-style: italic\|oblique` | Folded into `-unity-font-style: italic` / `bold-and-italic`. |
| `font` (shorthand) | Only `font-size` is extracted; full shorthand warns. |
| `text-align` | Mapped to `-unity-text-align` (`left`->`middle-left`, etc.). On flex parents without `justify-content` the converter mirrors it to `flex-start`/`center`/`flex-end`. `justify` falls back to `middle-left` + `space-between` justification. |
| `line-height` | `<px>` -> `-unity-paragraph-spacing`. Unitless / em / % falls back to 0 with a warning. |
| `text-shadow` | Pass through. |
| `text-decoration` (`underline`) | Consumed by the converter, emitted as `<u>...</u>` rich-text on the Label. |
| `text-transform` (`uppercase` / `lowercase` / `capitalize`) | Consumed by the converter, mutates the Label text. |
| `text-overflow: clip\|ellipsis` | Pass through. |
| `white-space: normal\|nowrap\|pre\|pre-wrap` | Pass through. `pre-line`, `break-spaces` -> `pre-wrap`. |
| `-webkit-text-stroke`, `-webkit-text-stroke-width`, `-webkit-text-stroke-color` | Mapped to `-unity-text-outline-width` / `-unity-text-outline-color`. |
| `-unity-*` props | Pass through verbatim (e.g. `-unity-font-style`, `-unity-text-outline-color`). |

### Animation

| Property | Notes |
|---|---|
| `animation`, `animation-name`, `animation-duration`, `animation-delay`, `animation-timing-function`, `animation-iteration-count`, `animation-direction`, `animation-fill-mode`, `animation-play-state` | Bridged via `--odd-animation-*` custom properties read by the runtime `Html2UxmlAnimation` binding. |
| `@keyframes <name> { ... }` | Encoded into `--odd-animation-keyframes`. |
| `transition`, `transition-property`, `transition-duration`, `transition-delay`, `transition-timing-function` | Pass through (USS native transitions). |

The runtime animation sampler only ticks these properties:

- `opacity`
- `translate`
- `rotate`
- `scale`
- `color`
- `background-color`

Authoring layout-changing keyframes (`width`, `height`, `left`, flex values), filter / mask / clip-path animation, or multiple concurrent animations on one element does not work. Author one stable transform animation per element.

### Selectors

The selector rewriter accepts:

- Tag (`div`), id (`#main`), class (`.button`).
- Compound (`.button.is-primary`), descendant (` `), child (`>`).
- Selector lists (`.a, .b`) — emitted as **per-selector rules** (Unity 6000.4.x silently drops every rule in a sheet that uses comma-grouped selectors).
- Pseudo-classes that map to UI Toolkit runtime states: `:hover`, `:active`, `:focus`, `:disabled`, `:enabled`, `:checked`, `:root`, `:selected`, `:inactive`.

Rejected (rule dropped from USS, warning emitted):

- Sibling combinators `+` and `~`.
- Attribute selectors `[type="text"]`, `[disabled]`.
- Functional / structural pseudos `:nth-child(...)`, `:not(...)`, `:has(...)`, `::part`, `::slotted`.

Pseudo-elements:

- `::before` and `::after` are **materialized at convert time** as a synthetic leading / trailing `ui:Label` child carrying the `content` text and the rule's other declarations. They are not real CSS pseudo-elements at runtime, so CSS counters are not supported.

### CSS Values & Functions

**Colors:** hex (`#ffbf13`, `#ffbf13aa`), `rgb()`, `rgba()`, `hsl()`, `hsla()`, modern slash syntax (`rgb(255 191 19 / 0.5)`), and the standard CSS named colour set (`transparent`, `black`, `white`, `red`, `gold`, `crimson`, `lavender`, etc.). Modern colour functions `oklch()`, `lab()`, `lch()`, `color-mix()` are not supported.

**Gradient functions** (in `background` / `background-image`):

| Function | Status |
|---|---|
| `linear-gradient(...)` | Bridged via `--odd-gradient`. Any angle. Multi-stop. |
| `repeating-linear-gradient(...)` | Bridged via `--odd-repeating-linear-gradient`. One layer. Best for axis-aligned scanlines / stripes. |
| `radial-gradient(...)` | Bridged via `--odd-radial-gradient` (+ `-2`). Up to two layers. Explicit `circle/ellipse <size> at x y` accepted. Tiled small radials (`radial-gradient(circle, ... 1px, transparent 1px) 0 0 / 8px 8px`) are detected as dot grids. |
| `repeating-radial-gradient(...)` | Bridged as a single radial-gradient layer. |
| `conic-gradient(...)` | **Not bridged.** Drops with a warning. |

**Filter functions** (in `filter:`):

| Function | Status |
|---|---|
| `blur()` | Native. |
| `grayscale()` | Native. |
| `invert()` | Native. |
| `opacity()` | Native. |
| `sepia()` | Native. |
| `hue-rotate()` | Native. |
| `contrast()` | Native. |
| `drop-shadow()` | Routed to `--odd-box-shadows` on `Html2UxmlPanel` (up to 8 layered drop-shadows). |
| `brightness()`, `saturate()` | Map to the `ODDGamesColorAdjust` package custom filter — currently behind a disabled flag, so they drop with a warning. |
| `tint()`, `filter("...")` | Pass through (Unity 6.3 native). |

### Transform functions

| Function | Status |
|---|---|
| `translate(x[, y])`, `translateX()`, `translateY()` | Mapped to `translate: <x> <y>`. Percentages allowed. |
| `translate3d(x, y, z)` | Flattened to 2D; z ignored, warning emitted. |
| `rotate(<angle>)`, `rotateZ()` | Mapped to `rotate`. |
| `scale(<n>)`, `scale(x, y)`, `scaleX()`, `scaleY()` | Mapped to `scale`. |
| `matrix(a, b, c, d, e, f)` | Decomposed to translate / rotate / scale when the matrix is conformal. Skewed / reflected matrices warn and may partial-apply. |
| `rotateX()`, `rotateY()`, `translateZ()`, `skew()`, `skewX()`, `skewY()`, `perspective()`, `matrix3d()` | Dropped with a warning. UI Toolkit transforms are 2D only. |

### Lengths and Units

- `px` is always safe and exact.
- `%` works on the same properties browsers allow it on (width/height/translate/etc.).
- `em` and `rem` are converted to `px` at parse time using the resolved `font-size` cascade. Compound expressions like `calc(1em + 4px)` are not resolved.
- `calc()`, `min()`, `max()`, `clamp()` mostly drop or pass through unparsed — author concrete pixel sizes instead.
- Viewport units (`vh`, `vw`, `dvh`, `dvw`) are not supported.
- Physical units (`pt`, `cm`, `mm`, `in`, `pc`) are not supported.

## Unsupported / Dropped CSS

`DROP_PROPS` (warning, no USS emitted):

`mask`, `mask-type`, `appearance`, `float`, `clear`, `box-sizing`, `user-select`, `perspective`, `perspective-origin`, `transform-style`, `backface-visibility`, `mix-blend-mode`, `background-blend-mode`, `isolation`, `contain`, `will-change`, `outline-offset`, `list-style`, `table-layout`, `border-spacing`, `caption-side`, `scroll-behavior`, `scroll-snap-type`, `scroll-snap-align`, `touch-action`, `writing-mode`, `direction`, `text-indent`, `vertical-align`, `word-break`, `overflow-wrap`, `hyphens`.

(`text-decoration` and `text-transform` are also in `DROP_PROPS` but only because the converter has already consumed them as rich-text / text-mutation — they're not silently lost.)

Other things the converter cannot represent: logical-property variants (`block-size`, `inline-size`, `border-block-*`, `padding-inline-*`, `margin-block-*`), `@media`, `@container`, `@supports`, `@layer`, `@scope`, container queries, CSS Grid, multi-column layout, `place-items` / `place-content` / `place-self`.

CSS values filtered before mapping: `unset`, `initial`, `inherit`, `revert`, `revert-layer` (silent drop on every property except where they're explicit defaults).

See `docs/unsupported.md` for the full catalogue with remediation guidance for each gap.

## Chrome (UA) Defaults

The converter prepends a tiny user-agent stylesheet before the author CSS so the standard HTML semantic tags read correctly. Author rules win on equal specificity.

| Selector | Default emitted |
|---|---|
| `mark` | yellow background, black text |
| `small` | `font-size: 0.83em` |
| `a` | blue (`#0000ee`), `-unity-text-decoration: underline` |
| `h1` - `h6` | descending `font-size` (`2em` -> `0.67em`), `margin-bottom` |
| `p`, `blockquote`, `ul`, `ol` | `margin-bottom: 1em` |
| `pre`, `pre code` | `white-space: pre`, `margin-bottom: 1em` (transparent code block bg) |
| `progress`, `meter` | 160x14 px chrome with light grey fill, 2px grey border, 4px radius |
| `hr` | 1px grey divider with 8px vertical margin |

If you don't want a default, override it explicitly in your stylesheet — there is no way to disable the UA sheet.

## Runtime Bridge Custom Properties

The runtime panel (`Html2UxmlPanel` and friends) reads a stable set of `--odd-*` custom properties. The converter writes them automatically when you use the equivalent CSS, but you can author them directly when you want explicit control:

| Custom prop | Mirrors |
|---|---|
| `--odd-gradient` | `background: linear-gradient(...)` |
| `--odd-radial-gradient`, `--odd-radial-gradient-2` | `background: radial-gradient(...)` (1-2 layers) |
| `--odd-repeating-linear-gradient` | `background: repeating-linear-gradient(...)` |
| `--odd-tiled-radial-gradient` + `--odd-background-pattern-size` | small dot-grid `radial-gradient(...)` paired with `background-size` |
| `--odd-box-shadows` (+ legacy `--odd-shadow-*`, `--odd-inner-shadow-*`) | `box-shadow` (multi-layer, inset, drop-shadow filter) |
| `--odd-mask-image` | `mask-image: linear-gradient(...)` (complex fades) |
| `--odd-clip-polygon` | `clip-path: polygon(...)` |
| `--odd-row-gap`, `--odd-column-gap` | `gap` / `row-gap` / `column-gap` on promoted panels |
| `--odd-animation-name`, `--odd-animation-duration-ms`, `--odd-animation-delay-ms`, `--odd-animation-timing`, `--odd-animation-iteration-count`, `--odd-animation-direction`, `--odd-animation-fill-mode`, `--odd-animation-play-state`, `--odd-animation-keyframes` | `animation` shorthand + `@keyframes` |
| `--odd-font-family`, `--odd-font-weight` | `font-family` (first family) and numeric `font-weight` |
| `--odd-background-color` | Set by the converter when `--odd-clip-polygon` would otherwise clip the normal `background-color` |

`var(...)` references inside bridged values are resolved at convert time when the variable is in the cascade. Runtime USS variable changes / cyclic vars / unresolved fallbacks fall through unmapped.

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

CSS default `display: flex` is row direction; USS default is column. The converter backfills `flex-direction: row` when you omit it.

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

`z-index` is consumed at convert time as a static sibling paint-order sort (higher `z-index` siblings emit later in UXML so Unity paints them on top). Browser stacking contexts from `transform` / `opacity` / `filter` / `isolation` are not reproduced.

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

The converter reads the **first family** in the `font-family` list and tries to ship the matching TTF/OTF (Google Fonts download, supplied file, or installed local/system font). Use generic fallbacks (`sans-serif`, `serif`) only as the trailing token.

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

Prefer alpha in the actual color rather than parent `opacity`:

```css
.muted-text {
  color: rgba(255, 255, 255, 0.55);
}
```

Avoid this for containers with child content — group opacity on a parent paints overlays through their backdrop:

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

USS only renders solid borders — `dashed` / `dotted` / `double` are not supported.

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

`clip-path: path(...)`, `clip-path: circle(...)`, and `shape-outside` are not supported.

## Images and SVG

Use raster images for complex art.

- Logos, portraits, game art, and textured badges should be PNG/WebP/JPG.
- Use transparent PNG for irregular logos.
- Use SVG only for simple static icons and flat vector marks.
- Inline SVGs should include `viewBox`, `width`, and `height`.
- Keep SVGs self-contained.

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

For frosted glass, fake the look with a translucent background and subtle
highlight rather than `backdrop-filter` (which only blurs the element's own
subtree, not the pixels behind it):

```css
.frosted-panel {
  background:
    linear-gradient(180deg, rgba(255, 255, 255, 0.14), rgba(255, 255, 255, 0.04)),
    rgba(20, 24, 30, 0.72);
  border: 1px solid rgba(255, 255, 255, 0.18);
}
```

True backdrop blur requires the package `BackdropBlurPanel` + a render texture source.

## Masks and Fades

Two-stop percentage `mask-image: linear-gradient(...)` fades are bridged. For more
complex fades, prefer an explicit gradient overlay element:

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

Keyframes can only target the runtime sampler's allow-list: `opacity`, `translate`, `rotate`, `scale`, `color`, `background-color`. Layout-changing properties (`width`, `height`, `left`, margins, padding, flex) and effects (`filter`, `clip-path`, `box-shadow`, `background-image`) cannot be animated.

## Pointer Events

Use `pointer-events: none` only for decorative layers that should never
receive input. The converter emits `picking-mode="Ignore"` on the UXML element.

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

## Selectors

Keep selectors simple and class-based. Selector lists are accepted but emitted
as **per-selector rules** (Unity 6000.4.x silently drops every rule in a sheet
that uses comma-grouped selectors).

```css
.button { ... }
.button .label { ... }
.button > .icon { ... }
.button.is-primary { ... }
.button:hover { ... }
.button:disabled { ... }
```

Do not use sibling combinators (`+`, `~`), attribute selectors (`[type="text"]`),
`:has()`, `:nth-child()`, `::part`, `::slotted`, or `@media` / `@container` /
`@supports` / `@layer` / `@scope`. They are dropped with a warning at convert
time. `::before` / `::after` are accepted but materialized as synthetic Label
children, not real CSS pseudo-elements.

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
- `backdrop-filter` for true frosted glass (it only blurs the element subtree).
- `mix-blend-mode` and `background-blend-mode`.
- `conic-gradient`.
- `clip-path` shapes other than `polygon(...)`.
- General masks beyond simple two-stop edge fades.
- `canvas`, `video`, `audio`, `iframe`, `embed`.
- Web Components and shadow DOM.
- `@media`, `@container`, `@supports`, `@layer`, `@scope`.
- `:has(...)`, `::part`, `::slotted`, attribute selectors, sibling combinators.
- `calc()` with mixed units, `min()`, `max()`, `clamp()`.
- Modern color functions such as `oklch()`, `lab()`, `lch()`, `color-mix()`.
- Variable font axes beyond weight.
- Browser-only font features.
- CSS that depends on framework runtime state to look correct.

## File Structure For Multi-Screen Bundles

Each screen in its own HTML file. No mega-file with tabs or in-page state swapping. Keeps screens diffable, assets reusable, dead rules visible.

HTML rules:

- Body contains only the root mount node and `<script>` tags. No inline `<style>` blocks. No inline script blobs.
- Head links the shared stylesheet first, then any screen-specific stylesheet (cascade order matters).
- Aligned attributes on `<link>` and `<script>` tags. Pretty-formatted markup.
- No embedded assets. Fonts, images, icons live as separate files referenced by URL. Never base64 / data-URI inside the HTML.

CSS rules:

- One shared sheet (e.g. `mt-ui.css`) holds tokens, base resets, shared components (buttons, pills, cards, chat bubbles), and the region layout grid. Every screen links it.
- A per-screen sheet contains only what differs from the shared baseline. A screen with no overrides gets no own sheet.
- Keyframes, body resets, and layout primitives belong in a `base.css`, not duplicated per screen.
- One blank line between rule blocks. Properties grouped: layout -> box -> typography -> color -> effects.

Dead-rule discipline:

- After any structural change, grep every class and id used across all screen files. Anything unreferenced gets deleted, not commented out.
- Drop empty selectors (`.foo::before { }` with no content). Drop stylesheets nothing links to.
- Drop wrapper divs whose only purpose was to host now-removed CSS.

## Semantic IDs

Every label, button, and field gets an `id`. The id identifies the element's role, not its visible text. Visible copy changes per event / locale / state; the role does not.

Good:

```html
<span id="bracketEventName" class="event-name">Monster Truck Drag Racing</span>
<span id="spectatorTimeCode" class="time-code">04:35</span>
<button id="standingsEntry1Truck" class="row truck-row">Grave Digger</button>
```

Avoid:

```html
<span id="mtdRacing">Monster Truck Drag Racing</span>
<span id="mins0435">04:35</span>
<button id="graveDigger">Grave Digger</button>
```

Rules:

- camelCase ids.
- When the same role exists on multiple screens, prefix by screen scope: `bracket*`, `spectator*`, `standings*`. Avoids per-screen drift and keeps the role obvious.
- Ids identify the instance; classes identify the style. Do not encode style state in the id.
- Every interactive control (button, input, select, textarea) has an id. Static labels next to interactive controls get an id when downstream code needs to read or animate them.

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
- Each screen lives in its own HTML file. Shared sheet linked first, per-screen sheet last.
- No embedded fonts, images, or inline `<style>` / `<script>` blobs.
- Controls land in their conventional 3x3 region cell (Close TopRight, primary CTA Center or BottomRight, etc.).
- Every interactive element has a role-based camelCase id, not a text-based one.
