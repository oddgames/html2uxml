# Unity UI Toolkit Rendering Package Notes

This document records the rendering decisions behind `au.com.oddgames.html2uxml`.
It is based on Unity 6.3 UI Toolkit documentation plus the 2022.3 custom
control examples that still describe the core rendering APIs.

## Rendering Paths

## Automatic vs Opt-In

The converter should use package rendering features only when the dependency
is deterministic:

| Feature | Converter behavior | Unity setup |
|---------|--------------------|-------------|
| simple outer `box-shadow`, `drop-shadow`, simple linear mask fades | Automatic package custom `filter("...")` calls | Install package |
| inset/multi-layer `box-shadow`, CSS gradients, polygon clips, complex linear mask fades | Automatic `odd:Html2UxmlPanel` promotion | Install package |
| static flex `gap` | Generated child margins where possible; runtime helper only when already bridged | Install package only for bridged buttons/panels |
| Unity-native `filter` functions | Automatic USS pass-through | Install package only if other generated elements use it |
| `brightness(...)`, `saturate(...)` | Automatic `filter("Packages/au.com.oddgames.html2uxml/Runtime/Filters/ODDGamesColorAdjust.asset" ...)` | Install package |
| CSS linear/radial gradient layers | Automatic mesh renderer inside `Html2UxmlPanel` | Package material templates are available in Resources for opt-in shader panels |
| authored material chrome / non-CSS shader effects | Opt-in `odd:ShaderPanel` or hand-authored USS `-unity-material` | Pick/package a material |
| true browser-style backdrop blur | Opt-in `odd:BackdropBlurPanel` | Supply a local render texture, or point it at a shared `BackdropCaptureSource` |

Do not auto-convert a normal HTML element to `BackdropBlurPanel`. The source
camera/render texture is game-specific, and choosing it incorrectly is worse
than emitting the documented approximation.

For live blur over multiple UI elements, prefer a shared capture:

1. Add `ODDGames.Html2Uxml.BackdropCaptureSource` to a GameObject.
2. Set `Source Id` to a stable name such as `main`.
3. Assign a source camera or an existing render texture.
4. Enable `Capture Each Frame` only when the backdrop is genuinely live.
5. Use `odd:BackdropBlurPanel source-id="main"` for each UI element that
   samples that shared blurred backdrop.

This is the package equivalent of a screen grab pass. It captures and blurs
once in the source component, then each panel renders a single shader quad.
It intentionally avoids ShaderLab `GrabPass`: Unity documents that GrabPass
can significantly increase CPU/GPU frame time, and it is not supported by URP
or HDRP.

### Native USS

Use native USS first when Unity has the property. Unity 6.3 covers more than
older UI Toolkit builds:

- `filter: blur(...) grayscale(...) invert(...) opacity(...) sepia(...) tint(...) hue-rotate(...) contrast(...)`
- `text-shadow`
- Unity text outline properties (`-unity-text-outline-width`,
  `-unity-text-outline-color`)
- background images, positioning, repeat, and sizing
- flex layout, absolute positioning, transforms, transitions, and opacity

Do not bridge a CSS feature that Unity already supports directly unless the
web syntax needs translation.

### Custom VisualElement

Use a custom `VisualElement` when an effect can be drawn from geometry and
style inputs without reading the already-rendered pixels:

- bevels / emboss
- linear and radial gradients when the shader is unavailable
- polygon covers
- approximate box shadows and glows
- simple edge fade overlays
- custom gauges, rings, meters, and HUD shapes

Implementation shape:

- Derive from `VisualElement`.
- Add `[UxmlElement]` so generated UXML can use it.
- Read custom USS properties with `CustomStyleProperty<T>` inside
  `CustomStyleResolvedEvent`.
- Register `generateVisualContent`.
- Use `MeshGenerationContext.painter2D` for simple vector paths.
- Use `MeshGenerationContext.Allocate(...)` for hand-authored meshes.
- Call `MarkDirtyRepaint()` when style or runtime state changes.
- Do not mutate element properties inside `generateVisualContent`; Unity
  treats the element as read-only during that callback.

`Html2UxmlPanel` follows this path.

### Mesh API

Use Mesh API when Painter2D is not enough:

- many vertices with custom UVs
- textured quads
- non-trivial triangulated shapes
- procedural glow geometry
- efficient repeated HUD primitives

Mesh API allocates vertices/indices through `MeshGenerationContext.Allocate`.
It is lower level than Painter2D and should stay inside small, reusable
runtime elements.

### Painter2D / Vector API

Use Painter2D for paths:

- lines, arcs, Beziers, rounded rectangles, polygons
- filled shapes with holes via fill rules
- simple stripes/rings for gradient approximation

This is the preferred no-shader bridge path for CSS effects that can be
approximated as geometry.

### USS Filters And Custom Filters

Unity 6.3 filters render an element subtree to a texture, process it, and
insert the result back into the visual tree. This is the right path when an
effect needs the element's own rendered pixels:

- real blur of an element subtree
- saturation / brightness / LUT-style color processing
- shader-based masks
- distortion / swirl / scanline filters
- glow based on alpha of rendered children

The converter emits native filter functions directly. For custom filters, the
package ships Unity filter assets/materials and maps source CSS to known USS
filter calls. When several CSS properties become filters, the converter keeps
them in one `filter:` chain because separate Unity USS selectors do not merge
filter values.

Limit: a normal filter processes the element subtree. It does not sample the
world or UI behind the element, so it is not true CSS `backdrop-filter`.

### Automatic CSS Gradient Renderer

CSS gradients are still authored as normal CSS. The converter writes the
original gradient functions into USS custom properties:

- `--odd-gradient`: first supported non-repeating linear gradient layer.
- `--odd-radial-gradient`: first supported radial/repeating-radial layer.
- `--odd-radial-gradient-2`: second supported radial/repeating-radial layer.
- `--odd-repeating-linear-gradient`: axis-aligned repeated stripe/scanline
  pattern layer.
- `--odd-tiled-radial-gradient` plus `--odd-background-pattern-size`: small
  tiled radial dot fields such as studio grain.

At runtime, `Html2UxmlPanel` reads those values and paints gradients through
mesh generation. This avoids assigning hand-written materials that Unity can
reject as non-UITK-compatible. It also respects explicit radial sizes such as
`ellipse 80% 70% at 0% 50%`, and Chrome-normalized radial forms such as
`radial-gradient(at 100% 50%, ...)`. Unspecified ellipse radii are treated as
a farthest-corner style wash so edge-origin gradients feather across the panel
instead of ending as a visible hard oval.

Repeating and tiled pattern art is intentionally split from the normal gradient
mesh path. A CSS `repeating-linear-gradient(...)` or a dot-grid
`radial-gradient(...)` paired with `background-size` should repeat in local
pattern space. Treating those as full-panel gradients creates oversized bands
or visible dot/scanline artifacts. `Html2UxmlPanel` paints those patterns as
static generated visual content when the element is dirtied; it is not a
per-frame animation loop.

When a bridged element also has native USS borders, the mesh background is
inset by the resolved border widths. Unity calls custom visual content after
its own border/background pass, so painting the full border box would cover
thin side borders and edge highlights. Outer shadows still use the full border
box; inset shadows and bridged backgrounds use the inner paint rectangle.
For `clip-path: polygon(...)` panels, inset shadow and highlight bands are
clipped to the same polygon so slanted chrome does not leak rectangular edge
lines past the cut face.

The package still ships reusable material templates under
`Package/Runtime/Resources/ODDGames/html2uxml/Materials`. Use those through
opt-in controls such as `odd:ShaderPanel`, or replace them with UI
Toolkit-compatible Shader Graph materials when true per-pixel shader sampling
is required.

### UI Shader Graph / Materials

Use UI Shader Graph or UI-compatible materials when an element surface itself
needs a shader:

- material-backed chrome
- animated shader fills
- procedural noise
- shader masks on a generated quad

This path is more expensive than pure Painter2D and can affect batching.
Keep arbitrary authored materials opt-in and packaged as reusable ODD Games
effects. Package templates should be instantiated per element before their
properties are mutated.

### True Backdrop Blur

True `backdrop-filter` needs pixels from behind the element. A normal UI
Toolkit filter cannot provide that. The practical implementation is:

1. Capture or retain a render texture of the camera/world/UI layer behind the
   panel.
2. Blur that texture once per frame or when needed.
3. Use the blurred texture as the background of a custom UI element/material.
4. Clip/sample it to the element's screen rect.

This belongs in a dedicated runtime effect, not in the plain converter.

## Html2UxmlPanel Policy

Html2UxmlPanel owns the automatic geometry compatibility layer:

- It reads `--odd-*` custom properties.
- It draws predictable mesh/vector geometry in `generateVisualContent` for CSS
  gradients, complex shadows, polygon clips, and fallback mask effects.
- It keeps package materials as opt-in Resources templates and instantiates
  them before runtime mutation.
- It avoids render textures.
- It avoids per-frame work unless the element is explicitly animated.
- It uses overlay child elements only when the effect must paint after normal
  children, such as edge-fade cover bands.

Use a package custom filter for effects that require the element subtree's own
pixels. Use a dedicated render-texture element for effects that require scene
or backdrop pixels.

## Sources

- Unity UI Toolkit overview:
  https://docs.unity3d.com/6000.3/Documentation/Manual/UIElements.html
- USS custom properties:
  https://docs.unity3d.com/6000.3/Documentation/Manual/UIE-USS-CustomProperties.html
- Generate 2D visual content:
  https://docs.unity3d.com/6000.3/Documentation/Manual/UIE-generate-2d-visual-content.html
- Create custom controls:
  https://docs.unity3d.com/2022.3/Documentation/Manual/UIE-create-custom-controls.html
- Mesh radial progress example:
  https://docs.unity3d.com/2022.3/Documentation/Manual/UIE-radial-progress.html
- Vector radial progress example:
  https://docs.unity3d.com/2022.3/Documentation/Manual/UIE-radial-progress-use-vector-api.html
- Text effects:
  https://docs.unity3d.com/6000.3/Documentation/Manual/UIE-text-effects.html
- Work with text:
  https://docs.unity3d.com/6000.3/Documentation/Manual/UIE-work-with-text.html
- USS filters:
  https://docs.unity3d.com/6000.3/Documentation/Manual/ui-systems/uss-filter.html
- Built-in filters:
  https://docs.unity3d.com/6000.3/Documentation/Manual/ui-systems/built-in-filters.html
