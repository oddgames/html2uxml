# html2uxml

Unity UPM package that imports HTML/CSS into UI Toolkit UXML/USS and ships the
runtime bridge elements, custom filters, shaders, and rendering resources
needed to display the conversion output faithfully.

Package id: `au.com.oddgames.html2uxml`

## Install With Unity UPM

### Option A — npmjs scoped registry (recommended)

Each tagged release is published to npmjs.com. Add a scoped registry and the
package to `Packages/manifest.json`:

```json
{
  "scopedRegistries": [
    {
      "name": "ODD Games",
      "url": "https://registry.npmjs.org",
      "scopes": ["au.com.oddgames"]
    }
  ],
  "dependencies": {
    "au.com.oddgames.html2uxml": "0.1.0"
  }
}
```

Unity's Package Manager will then list the package under **My Registries**
and show updates whenever a new version is published.

### Option B — Git URL

Use Unity Package Manager's **Add package from git URL** option:

```text
https://github.com/oddgames/html2uxml.git#v0.1.0
```

Or add it directly to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "au.com.oddgames.html2uxml": "https://github.com/oddgames/html2uxml.git#v0.1.0"
  }
}
```

### Option C — Local file dependency (for development)

```json
{
  "dependencies": {
    "au.com.oddgames.html2uxml": "file:C:/Workspaces/html2uxml"
  }
}
```

## What This Package Provides

- `ODDGames.Html2Uxml.Html2UxmlPanel`
  - Renders bridged CSS features that Unity USS does not cover directly:
    inset and multi-layer `box-shadow`, `linear-gradient(...)`,
    `radial-gradient(...)`, axis-aligned `repeating-linear-gradient(...)`
    patterns, tiled radial dot fields, `clip-path: polygon(...)`, and complex
    linear `mask-image` edge fades.
  - Simple outer `box-shadow`, `filter: drop-shadow(...)`, and two-stop
    percentage linear masks use package custom filter assets instead of panel
    promotion.
  - CSS gradients use the package mesh renderer, including one linear layer
    plus two radial layers. This avoids assigning hand-written materials that
    Unity rejects as non-UITK-compatible.
  - Supports up to eight inset or multi-layer shadow records for glows,
    bevel, and emboss. These remain approximations, not full browser shadow
    compositing.
  - Static CSS variables can resolve before parsing bridged gradients,
    shadows, filters, clip paths, and mask fades. Runtime custom property
    changes and variables that resolve to unsupported syntax are still
    outside the package contract.
  - Native Unity 6.3 `filter` functions such as `blur(...)` pass through
    as USS. `drop-shadow(...)`, `brightness(...)`, `saturate(...)`, and simple
    masks use package custom filter assets.
- ODD runtime element types
  - Converted UXML emits ODD subclasses for core UI Toolkit elements:
    `Html2UxmlElement`, `Html2UxmlLabel`, `Html2UxmlButton`,
    `Html2UxmlScrollView`, fields, toggles, dropdowns, foldouts, and
    progress/group containers.
  - `Html2UxmlElement` inherits the paint-capable panel path. Typed controls
    install dormant manipulators for animation and lightweight paint hooks
    without scanning the visual tree.
- CSS animation bridge
  - `@keyframes` and `animation` are bridged for `opacity`, `translate`,
    `scale`, `rotate`, `color`, and `background-color`.
  - The runtime uses one shared ticker for active animated elements. Elements
    with no `--odd-animation-*` custom properties do not tick.
  - The bridge is implemented as `Manipulator` classes attached by the ODD
    element constructors, not as hidden overlay children.
- `ODDGames.Html2Uxml.Html2UxmlButton`
  - Button subclass used when a converted `<button>` needs runtime bridge
    behavior such as flex `gap` while still being queryable as a normal
    `Button` by controllers.
- Flex `gap`
  - Static converted containers bake `gap`, `row-gap`, and `column-gap` into
    generated child margins where possible, avoiding runtime custom elements.
  - Elements that already need `Html2UxmlPanel` for paint effects can still
    apply gap through the same `--odd-row-gap` / `--odd-column-gap` runtime
    path.
- `ODDGames.Html2Uxml.ShaderPanel`
  - Material-backed UI Toolkit quad for authored shader fills and complex
    gradients that should not be approximated with geometry.
  - Can instantiate package material templates with
    `material-resource="ODDGames/html2uxml/Materials/ODDGamesComplexGradient"`.
- `ODDGames.Html2Uxml.BackdropBlurPanel`
  - RenderTexture/camera-backed frosted glass panel using the bundled
    Gaussian blur shader. The project supplies the camera or render texture
    that represents the backdrop.
  - `capture-each-frame` defaults to `false`. Call `Refresh()` after a
    meaningful backdrop change, or explicitly enable per-frame capture only
    for live blur effects.
- `ODDGames.Html2Uxml.BackdropCaptureSource`
  - Optional scene component for shared live backdrop blur. It captures and
    blurs one source camera/render texture, then `BackdropBlurPanel`
    instances can sample it with `source-id="<id>"`.
  - `CaptureEachFrame` also defaults to `false`; call `Refresh()` for static
    captures or set it to `true` for live camera-backed blur.
  - Use this for several frosted panels over the same scene instead of
    enabling a per-panel capture loop.
- `ODDGames.Html2Uxml.UXMLController`
  - Base MonoBehaviour for generated UI controllers.
  - Owns UIDocument setup, delayed visual-tree wiring, callback unwiring,
    and transient Unity object disposal.
- Package assets
  - Reusable material templates ship inside
    `Runtime/Resources/ODDGames/html2uxml/Materials`.
  - The `ODDGamesColorAdjust`, `ODDGamesBoxShadow`, and
    `ODDGamesLinearMask` filter definitions ship inside `Runtime/Filters`,
    referencing package materials. Generated USS references them through
    package paths; no project-local filter assets are generated under
    `Assets/ODDGames`.
## Converting HTML

HTML conversion is manual and runs in the Editor. With one or more `.html` /
`.htm` files selected in the Project window, either:

- Use the **HTML to UXML** panel in the Inspector header (toggles for font
  download, TextCore SDF font bake, and bundle layout, plus a **Convert**
  button), or
- Right-click ▸ **Convert HTML to UXML**.

Each conversion's outcome (timestamp, warnings, errors, and resulting
`.uxml` / `.uss` paths) is cached under `Library/Html2UxmlResults/` and
re-rendered in the inspector panel after domain reloads, so failures don't
get lost in the Console.

## Generated UXML Namespace

Converted UXML that uses bridged CSS emits:

```xml
xmlns:odd="ODDGames.Html2Uxml"
```

and uses ODD element types. Complex static paint uses the paint-capable
`Html2UxmlPanel` / `Html2UxmlElement` path; typed controls use manipulators
for bridge behavior.
Current converter output emits ODD element subclasses for all supported
HTML/UI Toolkit element mappings, so generated UXML depends on this package
even when a screen does not contain a gradient or shadow bridge.

The package must compile before Unity imports those UXML files. If UI Builder
shows `Unknown Type Html2Uxml...`, first clear any package compile errors, then
reimport the UXML or reopen the project.

## Shared Backdrop Blur

Use a shared capture when several panels need live frosted glass over the same
scene:

```csharp
var source = gameObject.AddComponent<BackdropCaptureSource>();
source.SourceId = "main";
source.SourceCamera = Camera.main;
source.CaptureEachFrame = true;
```

Then reference it from UXML:

```xml
<odd:BackdropBlurPanel source-id="main" use-screen-space-uv="true" />
```

The source captures and blurs once; each panel draws one screen-space shader
quad sampling that shared texture.

## Generated Controller Pattern

Generated controllers should derive from `UXMLController`:

```csharp
using UnityEngine;
using UnityEngine.UIElements;
using ODDGames.Html2Uxml;

public partial class MyScreenController : UXMLController
{
    [SerializeField] VisualTreeAsset visualTree;

    Button closeButton;

    protected override void ConfigureDocument(UIDocument doc)
    {
        if (visualTree != null)
            doc.visualTreeAsset = visualTree;
    }

    protected override bool WireUI(VisualElement root)
    {
        closeButton = root.Q<Button>("closeButton");
        if (closeButton == null)
            return false;

        WireClick(closeButton, OnCloseClicked);
        return true;
    }

    private void OnCloseClicked()
    {
        Close();
    }

    partial void Close();
}
```

Put hand-written behavior in a separate partial class file so generated
controllers can be replaced safely.

## Docs

Long-form documentation lives in `Documentation~/` (the trailing tilde is the
Unity UPM convention for files Unity should not import as project assets):

- [`Documentation~/design-guide.md`](Documentation~/design-guide.md) — source-authoring contract and conversion rules.
- [`Documentation~/unity-ui-toolkit-rendering.md`](Documentation~/unity-ui-toolkit-rendering.md) — Unity rendering and shader notes.
- [`Documentation~/unsupported.md`](Documentation~/unsupported.md) — browser CSS support matrix and bridge behavior.

## Licence

Copyright (c) 2026 ODD Games. All rights reserved. This package is
proprietary — see [`LICENSE.md`](LICENSE.md) for terms.

Bundled precompiled .NET libraries (`AngleSharp`, `ExCSS`) under
`Editor/Plugins/` ship under the MIT licence. Their copyright notices and
full licence text are in
[`Editor/Plugins/THIRD-PARTY-NOTICES.md`](Editor/Plugins/THIRD-PARTY-NOTICES.md).
