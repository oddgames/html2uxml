# html2uxml

`html2uxml` converts static or browser-rendered HTML/CSS into Unity UI Toolkit
UXML/USS, plus an ODD Games Unity runtime package for CSS features that USS
does not support directly.

## Unity Package

The Unity package lives in `Package/`.

Package id:

```text
au.com.oddgames.html2uxml
```

Install it through Unity Package Manager with:

```text
https://github.com/oddgames/html2uxml.git?path=/Package
```

Or add it to `Packages/manifest.json`:

```json
{
  "dependencies": {
    "au.com.oddgames.html2uxml": "https://github.com/oddgames/html2uxml.git?path=/Package"
  }
}
```

For local development:

```json
{
  "dependencies": {
    "au.com.oddgames.html2uxml": "file:C:/Workspaces/html2uxml/Package"
  }
}
```

Generated UXML uses:

```xml
xmlns:odd="ODDGames.Html2Uxml"
```

and promotes elements to `odd:Html2UxmlPanel` when custom rendering is needed.
The matching USS bridge properties use the `--odd-*` prefix.

## CLI

Run the converter from the repo root:

```powershell
python -m html2uxml.cli "http://localhost:8000/Monster%20Truck%20Drag%20Racing.html" -o out_bracket_event_select_v1b --name screen-bracket-event-select-v1b --selector "#screen-bracket-event-select-v1b" --bundle-assets --download-assets
```

The CLI renders URL inputs in a browser by default when needed, discovers linked
CSS files from the target HTML, and bundles fonts/assets unless opted out.
Inline SVGs are rasterized to PNG by default with `--svg-raster-min-px 256`
and `--svg-raster-scale 4`, so small browser icons stay sharp in Unity without
requiring Unity's SVG importer. The rasterizer order is CairoSVG, ImageMagick
`magick`, then browser screenshot fallback. Use `--no-rasterize-svg` only when
you want to keep emitted `.svg` files.

Subtle CSS pattern art is bridged automatically: axis-aligned
`repeating-linear-gradient(...)` scanlines/stripes and tiny tiled
`radial-gradient(...)` dot grids with explicit `background-size` are painted by
the runtime package instead of being stretched into one large gradient.

Install the Python package before running the CLI from another project:

```powershell
python -m pip install -e .
```

CairoSVG is included as the default Python SVG rasterizer. ImageMagick remains a
supported fallback and can be forced by setting `H2U_MAGICK` to a `magick.exe`
path when needed.

`--selector` exports the selected subtree and also keeps positioned overlay
siblings from the same viewport parent when they have positive `z-index` and
explicit edge offsets. That preserves framework-mounted HUD controls like
top-right chat/camera/options toolbars without pulling in normal artboard
siblings.

Conversions write screen files into a shared `UI` folder:

```text
<out>/UI/
  <screen-name>.uxml
  <screen-name>.uss
  Images/
  Fonts/
  TextGradients/
```

Generated `url(...)` paths are relative to `UI`, for example
`url("Images/chat-toggle-button.png")` and `url("Fonts/Inter-700.ttf")`.
When TextCore font assets are enabled, `UI/Fonts/html2uxml-fonts.json` records
the default static glyph set plus screen text so the Unity package can bake
static SDF font assets automatically.

## Docs

- `docs/design-guide.md`: source-authoring contract and conversion rules.
- `docs/unsupported.md`: browser CSS support matrix and bridge behavior.
- `docs/unity-ui-toolkit-rendering.md`: Unity rendering and shader notes.
- `Package/README.md`: Unity package usage.
