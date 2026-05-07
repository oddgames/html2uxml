# Design Contract for html2uxml

Hand this document to whoever (human or AI) authors the HTML+CSS so the
converter produces clean Unity UXML+USS. Every convention here is something
the converter actively recognises. Code that follows the contract round-trips
losslessly; code that drifts gets dropped or approximated.

**Target runtime**: Unity 6.4 (6000.4) or newer. The bridge kit uses the
`[UxmlElement]` source generator (Unity 2023.2+) and depends on
`com.unity.vectorgraphics` for SVG asset import. Older Unity versions
are outside this contract.

This guide tracks the Unity 6.4 UI Toolkit manual for USS properties, USS
data types, selectors, transitions, and filters. If a generated file targets
Unity 6.4, prefer native USS first and use the bridge only for gaps Unity
still does not cover.

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
- `aspect-ratio: <ratio>` is supported natively in Unity 6 USS — use it
  for any element whose width should track its height.

### Z-ordering

USS does **not** support `z-index`. Stacking is determined entirely by
tree order: later siblings paint on top of earlier ones. To put one
element in front of another:

1. Make sure the visually-on-top element appears **after** its peer in
   the DOM, or
2. Lift it into a higher-up container that paints later.

Don't author with `z-index: 999` and expect it to survive — the value is
dropped at conversion. Plan stacking by source order.

## 2. Typography

- Fonts must be available on Google Fonts; the converter downloads TTF/OTF
  files via the Google Fonts CSS API. System fonts (`Courier New`,
  `Helvetica`, etc.) won't bundle.
- Stick to:
  `font-size`, `font-weight`, `font-style: italic`, `letter-spacing`,
  `text-align`, `color`, `text-transform`, `text-decoration: underline`,
  `white-space`.
- `line-height: <px>` is auto-mapped to `-unity-paragraph-spacing`
  (Unity's nearest equivalent). Unitless multipliers (`line-height: 1.5`)
  drop with a warning — author with explicit pixel values for fidelity.
- `-webkit-text-stroke: <width> <color>` (and the long-hand
  `-webkit-text-stroke-width` / `-webkit-text-stroke-color`) auto-map to
  `-unity-text-outline-width` / `-unity-text-outline-color`. Use this for
  outlined text instead of stacked text-shadows.
- Avoid `text-indent`, `word-break`, `hyphens`, `vertical-align`.

## 3. Color, Background, Borders

### Supported color formats

- Hex: `#fff`, `#ffffff`, `#ffffff80` (8-digit alpha is honoured).
- `rgb(r, g, b)` and `rgba(r, g, b, a)`.
- `hsl(h, s%, l%)` and `hsla(h, s%, l%, a)` are accepted by the
  converter and normalized to Unity-supported `rgb(...)` / `rgba(...)`.
- Named CSS keywords (`red`, `transparent`, `cornflowerblue`, etc.).
- `currentColor` is **not** supported — restate the color literally.
- Modern color spaces (`oklch`, `lab`, `lch`, `hwb`, `color-mix()`,
  relative-color syntax) are not parsed; see the Hard-No list.

### Other rules

- Solid backgrounds, `background-image: url(...)`,
  `background-position`, `background-position-x/y`, `background-repeat`,
  and `background-size` pass through to native Unity 6.4 USS.
- `linear-gradient(...)` and `repeating-linear-gradient(...)` are bridged.
  Other gradient functions are dropped.
- `box-shadow`: single shadow only. Multi-shadow lists and `inset` shadows
  drop.
- Unity 6.4 native filters pass through:
  `blur`, `grayscale`, `invert`, `opacity`, `sepia`, `tint`,
  `hue-rotate`, `contrast`, and custom `filter("...")` assets.
- `filter: drop-shadow(...)` is still bridged to box-shadow because
  Unity 6.4 USS explicitly does not support the CSS drop-shadow filter.
- `border: <width> solid <color>` is fully supported; dashed/dotted/double
  styles are flattened to solid.
- `border-radius` (all four corners) is supported.
- `clip-path: polygon(...)` is bridged. Other shapes (circle, inset, path,
  url) drop.
- `outline` becomes a real `border` (so it occupies layout space).
- `border-image: url(...) <slice>` (and the longhands `border-image-source`,
  `border-image-slice`) auto-map to `background-image` plus
  `-unity-slice-top/-right/-bottom/-left`. Use this for stretchy 9-slice
  frames; the source asset must be set up for 9-slice in Unity.

## 4. Images & Media

- `<img src="...">`: relative paths or full http(s) URLs. Path resolution
  is relative to the input HTML file (or the page URL).
- Inline `<svg>...</svg>` is preserved verbatim and written to
  `Assets/UI/Images/svg-N.svg`. Authoring rules:
  - Always set `width` and `height` (or a `viewBox`) on the root `<svg>`.
  - Don't reference external assets from inside the SVG (no `<image href>`,
    no `<use href>`).
  - Animations / scripts inside the SVG are stripped at import time.
  - **Unity Vector Graphics package limits**: no `<text>` (rasterise to
    paths), no `<filter>`, no per-pixel masks, no embedded raster images,
    no SVG `<animate>` tags. Stick to paths, basic shapes, and gradient
    fills.
  - Multiply-tint a single SVG via `-unity-background-image-tint-color`
    if you need it in different palettes.
- `background-image: url(...)` works for raster images (PNG/JPG/WebP).
- No `<canvas>`, `<video>`, `<audio>`, `<iframe>`, `<embed>`. They become
  empty placeholder elements.

## 5. Animation Contract

The converter does **not** preserve CSS `@keyframes` or `animation-*`.
Those at-rules are stripped and the `animation` declaration is dropped.
For Unity 6.4, author motion as USS transitions or as C# behavior.

Use USS transitions when the motion is tied to a style change:

```css
.tile {
  transition-property: translate, opacity, filter;
  transition-duration: 160ms;
  transition-timing-function: ease-out-cubic;
  translate: 0px 0px;
  opacity: 1;
}
.tile:hover {
  translate: 0px -4px;
  opacity: 0.85;
  filter: blur(1px);
}
```

Keep start and end units identical. For example, transition from
`translate: 0px 0px` to `translate: 12px 0px`, not from `0` to `12%`.
Unity 6.4 transitions can be triggered by pseudo-classes, C# class
toggles, or direct style changes.

## 5b. Unity-Specific Properties Worth Authoring Against

USS exposes `-unity-*` properties that have no CSS analogue. They don't
appear in plain CSS authoring but the converter **passes them through**
verbatim if the source CSS uses them, so designers can opt in:

- `-unity-text-outline-width` + `-unity-text-outline-color` — true text
  stroke. Use instead of stacked text-shadows.
- `-unity-paragraph-spacing: <px>` — replacement for `line-height`.
  Spacing between paragraphs (or text wraps) in pixels.
- `-unity-background-image-tint-color: <color>` — multiply the element's
  background image (or SVG) by a color. Lets one icon asset render in
  many palettes.
- `-unity-slice-left/-top/-right/-bottom` + `-unity-slice-scale` —
  9-slice scaling for stretchy frames. Use for resizable button
  backgrounds and cards. Source PNG must be set up for 9-slice.
- `-unity-font-definition: url("Assets/UI/Fonts/Foo.ttf")` — direct font
  reference. The converter writes this automatically when
  `--download-fonts` is used; you can hand-author for non-Google fonts.
- `-unity-overflow-clip-box: padding-box | content-box` — choose where
  `overflow: hidden` clips. Defaults to padding-box.

These are the right answer when a CSS feature looks tantalisingly close
but isn't quite supported (line-height, icon coloring, multi-shadow text).

## 6. Transitions

USS supports `transition` (`property duration timing delay`) and the
converter passes transition longhands/shorthand through. Use transitions
for hover/focus/active states and for C# class toggles.

Unity 6.4 supports named timing functions such as `linear`, `ease`,
`ease-in`, `ease-out`, `ease-in-out`, and the sine/cubic/circ/elastic/
back/bounce families (`ease-out-cubic`, `ease-in-out-sine`, etc.).
It does not support CSS `cubic-bezier()` or `steps()` syntax.

## 7. Interactivity Contract

The converter sees static DOM only. It does not execute JavaScript and it
does not currently forward arbitrary `data-*` attributes into UXML.

Author runtime behavior as C# that queries stable `name` and `class`
hooks from the generated UXML:

- Use `id="..."` for elements you need to query with `root.Q<T>("id")`.
- Use classes for visual states (`.open`, `.selected`, `.danger`) and have
  C# add/remove those classes with `AddToClassList`, `RemoveFromClassList`,
  or `ToggleInClassList`.
- Use native controls where possible: `Button`, `Toggle`, `RadioButton`,
  `DropdownField`, `TextField`, `Slider`, `Foldout`, `ProgressBar`.

Avoid: arbitrary `onclick="..."` strings, React state, jQuery toggles,
generic `data-toggle-*` assumptions, and runtime-rendered DOM. Render to
static HTML first, then bind behavior in Unity C#.

**Pointer-blocking**: `pointer-events: none` (inline, class rule, or
`<style>`) is auto-emitted as `picking-mode="Ignore"` on the UXML
element. Use it for decorative overlays you don't want to swallow
clicks.

## 7b. Controller Export Contract

A controller exporter should generate one C# controller per converted UI:

```
MyScreen.uxml
MyScreen.uss
MyScreenController.cs
MyScreenController.Custom.cs   # optional, hand-authored partial
```

The generated file is disposable and may be overwritten. Any hand-written
logic goes in a separate partial class file that the exporter never touches.
This matches Unity UI Toolkit's normal runtime shape: bind against a
`UIDocument.rootVisualElement`, query elements with UQuery (`Q<T>`), and
register callbacks in C#.

The `au.com.oddgames.html2uxml` Unity package provides
`ODDGames.Html2Uxml.UXMLController`; generated controllers should derive
from it. That base class already owns UIDocument resolution, delayed wire
retries, callback tracking, reverse-order unwire, transient asset cleanup,
and editor warnings for missing query targets. Generated controllers should
not define `Start()` or `OnDestroy()` in that setup.

The exporter must read the original HTML and scripts before conversion,
because `<script>` tags are stripped and `data-*` attributes are not
currently emitted into UXML. The generated controller should bind to the
UXML by `id`, which becomes the Unity `name` attribute.

### Authoring rules for controller-friendly HTML

- Put a stable `id` on every interactive element and every target:
  `id="menuButton"`, `id="menu"`, `id="settingsPanel"`.
- Put the root controller name on the main wrapper:
  `data-h2u-controller="GarageMenu"`.
- Prefer `data-h2u-*` actions over inline JavaScript. They are easier and
  safer to lower into C# than arbitrary code.
- Keep state visual, not structural. Toggle classes like `.open`,
  `.selected`, `.disabled`, and let USS handle transitions.
- Keep targets explicit. Use `#id` selectors in `data-h2u-target`; avoid
  relative selectors such as `.parent .child:nth-child(2)`.
- If the action cannot be represented declaratively, mark it as custom and
  let the generated controller create a partial method stub.

Example:

```html
<div id="garageRoot" data-h2u-controller="GarageMenu">
  <button id="menuButton"
          data-h2u-on="click"
          data-h2u-action="toggle-class"
          data-h2u-target="#menu"
          data-h2u-class="open">
    Menu
  </button>

  <button id="buyButton"
          data-h2u-on="click"
          data-h2u-action="custom"
          data-h2u-method="BuyUpgrade">
    Buy
  </button>

  <div id="menu" class="menu"></div>
</div>
```

### Supported declarative actions

| Action | Required attributes | Generated C# behavior |
|--------|---------------------|-----------------------|
| `toggle-class` | `data-h2u-target`, `data-h2u-class` | `target.ToggleInClassList(className)` |
| `add-class` | `data-h2u-target`, `data-h2u-class` | `target.AddToClassList(className)` |
| `remove-class` | `data-h2u-target`, `data-h2u-class` | `target.RemoveFromClassList(className)` |
| `show` | `data-h2u-target` | `target.style.display = DisplayStyle.Flex` |
| `hide` | `data-h2u-target` | `target.style.display = DisplayStyle.None` |
| `show-panel` | `data-h2u-target`, `data-h2u-group` | Hide peers in the same group, then show the target |
| `set-text` | `data-h2u-target`, `data-h2u-value` | Set `Label.text` or `Button.text` |
| `set-value` | `data-h2u-target`, `data-h2u-value` | Set a supported field value with `SetValueWithoutNotify` |
| `custom` | `data-h2u-method` | Call a generated partial method stub |

For `show-panel`, every panel in the group should declare
`data-h2u-group="<group>"`. The exporter can build the group from those
source attributes, even though they do not survive into UXML.

### Event mapping

| HTML event | Unity binding |
|------------|---------------|
| `click` on `<button>` | `WireClick(button, Handler)` |
| `click` on other elements | `WireClickable(element, Handler)` |
| `change` on fields/toggles/sliders/dropdowns | `WireValueChanged(field, Handler)` |
| `input` on text fields | `WireValueChanged(textField, Handler)` |
| `submit` | Generate a custom partial method; Unity has no HTML form submission |

Generated controllers should cache element references in `WireUI`, register
callbacks only through the base `Wire*` helpers, and return `false` until
required elements are present. Do not call `root.Q(...)` inside every click
handler.

Recommended generated shape:

```csharp
using UnityEngine;
using UnityEngine.UIElements;
using ODDGames.Html2Uxml;

public partial class GarageMenuController : UXMLController
{
    [SerializeField] VisualTreeAsset visualTree;

    Button menuButton;
    VisualElement menu;
    Button buyButton;

    protected override void ConfigureDocument(UIDocument doc)
    {
        if (visualTree != null)
            doc.visualTreeAsset = visualTree;
    }

    protected override bool WireUI(VisualElement root)
    {
        menuButton = root.Q<Button>("menuButton");
        menu = root.Q<VisualElement>("menu");
        buyButton = root.Q<Button>("buyButton");

        if (menuButton == null || menu == null || buyButton == null)
            return false;

        WireClick(menuButton, OnMenuButtonClicked);
        WireClick(buyButton, OnBuyButtonClicked);
        return true;
    }

    protected override void OnWired()
    {
        OnBound();
    }

    private void OnMenuButtonClicked()
    {
        menu.ToggleInClassList("open");
    }

    private void OnBuyButtonClicked()
    {
        BuyUpgrade();
    }

    partial void OnBound();
    partial void BuyUpgrade();
}
```

If a target project intentionally does not install
`au.com.oddgames.html2uxml`, use the same shape with
`MonoBehaviour.OnEnable` / `OnDisable`, cache UQuery results once, register
callbacks at enable time, and unregister every callback manually. The
package base-controller path is preferred because it avoids duplicate
lifecycle and cleanup code in every generated file.

Hand-authored extension file:

```csharp
public partial class GarageMenuController
{
    partial void BuyUpgrade()
    {
        // Game-specific behavior lives here.
    }
}
```

### JavaScript recovery rules

When importing an existing UI, JavaScript recovery should be best-effort and
AST-based. Do not use regular expressions and do not execute the script.
Only lower simple, local DOM actions:

- `document.getElementById("id")` and `document.querySelector("#id")`.
- `element.addEventListener("click", handler)`.
- `classList.add(...)`, `classList.remove(...)`, `classList.toggle(...)`.
- `style.display = "none"` and `style.display = "flex"`.
- Assigning simple `.textContent`, `.innerText`, `.value`, and `.checked`.

Everything else becomes a generated partial method with a TODO:
network calls, timers, storage, canvas, dynamic DOM creation, framework
state, complex conditionals, loops over live DOM collections, and animation
code. The exporter should preserve the UI and isolate missing behavior
behind partial hooks rather than producing fragile C#.

## 8. Components Worth Using

Each maps to a real Unity control:

| HTML                              | Unity control      |
|-----------------------------------|--------------------|
| `<button>`                        | `ui:Button`        |
| `<input type="text|email|...">`   | `ui:TextField`     |
| `<input type="number">`           | `ui:FloatField`    |
| `<input type="checkbox">`         | `ui:Toggle`        |
| `<input type="range">`            | `ui:Slider`        |
| `<input type="color">`            | `ui:ColorField`    |
| `<select><option>`                | `ui:DropdownField` |
| `<textarea>`                      | `ui:TextField` (multiline) |
| `<details><summary>`              | `ui:Foldout`       |
| `<progress>` / `<meter>`          | `ui:ProgressBar`   |

Use the right tag and you get a Unity control with the right styling
hooks for free.

## 9. Accessibility & Tooltip Hooks

The converter forwards a small set of HTML attributes onto Unity controls:

| HTML attribute     | Unity result |
|--------------------|--------------|
| `title="..."`      | `tooltip="..."` on any element. |
| `alt="..."` (on `<img>`) | Falls back to `tooltip` if `title` is absent. |
| `id="..."`         | `name="..."` on the UXML element so it's queryable via `Q<T>("id")`. |

**Not forwarded today**: `disabled`, `placeholder`, `aria-label`,
`aria-describedby`, `role`, `tabindex`, `lang`, and arbitrary `data-*`
attributes. Use `title` for hover text and C# setup for enabled state,
placeholder behavior, focus order, and accessibility metadata.

## 10. Pseudo-elements

- `::before` and `::after` are materialized as Label children with the
  rule's `content` text + the rest of the rule's styling.
- CSS counters, attr() in content, and `::marker` aren't supported.

## 11. Selectors

Selectors have two paths:

- **Emitted verbatim to USS**: type, class, id, universal selectors,
  descendant combinators, child (` > `) combinators, selector lists, and
  Unity state pseudo-classes (`:hover`, `:focus`, `:active`,
  `:inactive`, `:disabled`, `:enabled`, `:checked`, `:root`).
- **Statically hoisted by the converter**: attribute selectors,
  adjacent/general sibling combinators (`+`, `~`), `:first-child`,
  `:last-child`, `:nth-child(...)`, `:nth-last-child(...)`, and
  `:not(...)`. The declarations are copied onto matching elements as
  generated `.h2u-N` classes because Unity 6.4 USS does not parse those
  selectors.

Prefer the verbatim set for maintainability. Hoisted selectors are useful
when importing existing HTML, but they become static snapshots: if C# later
moves elements around, the generated `.h2u-N` classes do not recompute.
Do not combine hoisted selectors with runtime pseudo-classes such as
`:hover` or `:focus`; the converter drops those mixed selectors rather
than baking a stateful rule into an always-on class.

Skip: `::part`, `::slotted`, container queries, `@layer`, `@scope`,
`@media` (the converter strips them).

## 11b. Screen Labeling for `--selector`

The CLI's `--selector` flag extracts a single screen subtree from a
multi-screen design canvas. To make a screen reliably extractable, label
its root element with **both** an `id` and the convention class
`screen`. The id is what the converter targets; the class is for human
readability inside the design canvas.

```html
<section id="login" class="screen" aria-label="Login screen">
  <!-- screen contents -->
</section>

<section id="lobby" class="screen" aria-label="Lobby screen">
  <!-- screen contents -->
</section>

<section id="match-summary" class="screen" aria-label="Match summary">
  <!-- screen contents -->
</section>
```

Conversion rules:

- **id naming**: lowercase-kebab, no spaces, stable. Becomes `name="..."`
  on the UXML root, so it must round-trip cleanly to a Unity-side
  `Q<VisualElement>("login")` lookup.
- **One id per screen**, project-wide unique. Reusing an id across
  screens makes `--selector "#dup"` ambiguous (the converter takes the
  first match).
- **Wrap with a single root element**. `--selector` extracts one subtree;
  if your screen is a fragment of siblings, wrap them in a containing
  `<section id="...">` first.
- **Avoid relying on positional selectors** (`body > div:nth-child(2)`).
  They break the moment the design canvas reorders.

Then the per-screen export commands look like:

```bash
html2uxml design-canvas.html --selector "#login"        --name Login        -o out/
html2uxml design-canvas.html --selector "#lobby"        --name Lobby        -o out/
html2uxml design-canvas.html --selector "#match-summary" --name MatchSummary -o out/
```

Each command produces its own `.uxml` / `.uss` so the Unity side can
load them independently.

If a designer ever needs a sub-region of a screen exported separately
(say, a chat overlay), give it its own labeled wrapper too. Use a
hyphenated id rather than a dot so the value is a clean CSS id selector:

```html
<section id="login" class="screen">
  <div id="login-chat-overlay" class="overlay">...</div>
</section>
```

`--selector "#login-chat-overlay"` then targets the overlay alone. The
prefix (`login-`) keeps the namespace readable when scanning the canvas.
Don't put a literal `.` in an id — the CSS selector parser reads it as a
class join (`#login.chat-overlay` means id `login` AND class
`chat-overlay`).

## 12. File Layout the Converter Expects

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

## 13. Hard-No List (Avoid Entirely)

Features in this list are **silently dropped** and have no bridge in
flight. If the design depends on any of them, redesign the affected
piece before handing it off — the converter will not warn loudly enough
to save you.

### Layout
- `display: grid`, `display: table`, `display: inline-grid`, `display: inline-block`
- `float`, `clear`
- `column-count` / multi-column layout
- `position: sticky`, `position: fixed` (the latter is silently re-aliased)
- `place-items`, `place-content`, `place-self` (grid-only shorthands)
- Container queries (`@container`)
- CSS subgrid

### Visual effects
- `backdrop-filter` (frosted glass)
- `mask`, `mask-image`, `-webkit-mask`
- `mix-blend-mode`, `background-blend-mode`
- `filter` functions outside Unity 6.4's native list and the converter's
  `drop-shadow(...)` bridge. `brightness()` and `saturate()` are examples
  that still drop.
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
- `text-shadow` with multiple shadows
- `writing-mode`, `direction: rtl`
- `font-variant-*`, `font-feature-settings`
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
- `@keyframes`, `animation`, and `animation-*`
- CSS scroll-driven animations (`animation-timeline`, `view-timeline`)
- `@scope`, `@layer`, `@property` registrations

### Interactivity / scripting
- Inline `onclick`, `onchange`, `oninput`, `onsubmit` handlers
- Declarative `data-toggle-*`, `data-show-*`, and `data-action` attributes
  (not forwarded today; bind behavior in C# by `name`/class).
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

## 14. Quick Self-Check

Before handing a design to the converter, run through:

- [ ] No `display: grid` anywhere.
- [ ] Every flex container declares `flex-direction` explicitly.
- [ ] All fonts come from Google Fonts.
- [ ] No `@keyframes` / `animation-*`; use USS transitions or C#.
- [ ] Interactive buttons have stable `id`/class hooks for C#; no JS or
      `data-toggle-*` assumptions.
- [ ] SVGs have `width`+`height` or `viewBox`.
- [ ] No `<canvas>`, `<iframe>`, `<video>`, `<audio>`.
- [ ] No `calc()` mixing units.
- [ ] No `position: fixed` reliance (treated as absolute).

If every box checks, conversion is high-fidelity. If a box fails, expect
the matching feature to drop or approximate per `docs/unsupported.md`.

## 15. CLI Reference

```bash
html2uxml <input> [-o OUT_DIR] [--name NAME] [--selector CSS]
                  [--css FILE]+ [--bundle-assets] [--download-assets]
                  [--download-fonts] [--timeout SECONDS] [-q]
```

- `<input>` — local HTML file path or an `http(s)://` URL.
- `-o OUT_DIR` — where the converted `.uxml` / `.uss` and `Assets/UI/`
  folders land. Defaults to the input's parent directory.
- `--name NAME` — base name for the output files (default: input stem).
- `--selector CSS` — convert only the first matching subtree (e.g.
  `--selector "#chat-overlay"`). Useful for a multi-screen design canvas.
- `--css FILE` — additional CSS files to merge in (repeatable).
- `--bundle-assets` — copy referenced images into
  `<out>/Assets/UI/Images/` and rewrite `url()` in USS.
- `--download-assets` — also fetch remote `http(s)` image URLs (implies
  `--bundle-assets`).
- `--download-fonts` — pull TTFs for Google Fonts referenced in CSS into
  `<out>/Assets/UI/Fonts/` and inject `-unity-font-definition` rules.
- `--timeout` — network timeout in seconds (default 10).
- `-q` — suppress the conversion report.

Typical full conversion:

```bash
html2uxml http://localhost:8000/test.html \
  -o my-unity-project \
  --name MyScreen \
  --bundle-assets --download-assets --download-fonts
```

Output layout:

```
my-unity-project/
  MyScreen.uxml
  MyScreen.uss
  Assets/
    UI/
      Images/   # copied PNG/JPG and inlined svg-N.svg files
      Fonts/    # downloaded TTFs from Google Fonts
```

Drop the generated UI assets into your Unity project's `Assets/` folder and
install the runtime package (`au.com.oddgames.html2uxml`) through Unity's
Package Manager.

## 16. Minimal Compliant Sample

Copy-paste skeleton showing a flex layout, gap, gradient background,
Unity 6.4 filter transition, SVG asset capture, and stable C# hooks:

```html
<!DOCTYPE html>
<html>
<head>
<link href="https://fonts.googleapis.com/css2?family=Inter:wght@500;700&display=swap" rel="stylesheet">
<style>
  body { margin: 0; background: #0a0a0a; color: #fff;
         font-family: 'Inter', sans-serif; }

  .panel {
    display: flex;
    flex-direction: column;
    gap: 12px;
    padding: 16px;
    width: 360px;
    background: linear-gradient(180deg, #1f2937 0%, #0f172a 100%);
    border-radius: 12px;
    box-shadow: 0 8px 24px rgba(0, 0, 0, 0.5);
  }

  .row {
    display: flex;
    flex-direction: row;
    gap: 8px;
    align-items: center;
  }

  .pill {
    padding: 4px 10px;
    border-radius: 999px;
    background: #2563eb;
    font-size: 12px;
    font-weight: 700;
    transition-property: translate, filter;
    transition-duration: 160ms;
    transition-timing-function: ease-out-cubic;
    translate: 0px 0px;
  }

  .pill:hover {
    translate: 0px -2px;
    filter: tint(#7dd3fc);
  }

  .menu { display: flex; flex-direction: column; gap: 6px; }
  .menu.open { display: flex; }
</style>
</head>
<body>
  <div class="panel">
    <div class="row">
      <span class="pill">LIVE</span>
      <button id="menuButton"
              title="Toggle the menu"
              data-h2u-on="click"
              data-h2u-action="toggle-class"
              data-h2u-target="#menu"
              data-h2u-class="open">Menu</button>
    </div>

    <div id="menu" class="menu">
      <button>Profile</button>
      <button>Settings</button>
    </div>

    <svg width="48" height="48" viewBox="0 0 48 48">
      <circle cx="24" cy="24" r="20" fill="#22c55e" />
    </svg>
  </div>
</body>
</html>
```

Every feature in this snippet round-trips through the converter.
Anything you'd add beyond this should be checked against the Hard-No
list before committing time to it.
