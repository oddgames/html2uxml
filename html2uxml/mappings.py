"""HTML element and CSS property mappings to UXML / USS.

USS is CSS-like but:
  - layout is Yoga-flexbox (no `block`, `inline`, `grid`, `float`).
  - text alignment uses `-unity-text-align` not `text-align`.
  - font weight/style is folded into `-unity-font-style` (normal|bold|italic|bold-and-italic).
  - `font-family` requires a Unity font asset, set via `-unity-font` / `-unity-font-definition`.
  - shorthand `border`, `background`, `font` aren't supported.
  - some CSS effects need bridges: shadows, clip paths, gradients, and
    drop-shadow filters. Browser backdrop blur is only approximated.
  - `box-sizing` doesn't exist; padding/border are inside the box.
  - percentages on `transform: translate(...)` work; `translateX(50%)` becomes
    `translate: 50% 0;`.
"""
from __future__ import annotations

import re
import colorsys
import math
from dataclasses import dataclass


# ---------------------------------------------------------------------------
# Element mapping
# ---------------------------------------------------------------------------


# (uxml_type, extra_attrs, treat_text_as) where treat_text_as is one of:
#   "label"   -> wrap text in a child ui:Label
#   "text"    -> assign to text="" attribute
#   None      -> drop text
ELEMENT_MAP: dict[str, tuple[str, dict, str | None]] = {
    "div":     ("ui:VisualElement", {}, "label"),
    "section": ("ui:VisualElement", {}, "label"),
    "article": ("ui:VisualElement", {}, "label"),
    "header":  ("ui:VisualElement", {}, "label"),
    "footer":  ("ui:VisualElement", {}, "label"),
    "main":    ("ui:VisualElement", {}, "label"),
    "nav":     ("ui:VisualElement", {}, "label"),
    "aside":   ("ui:VisualElement", {}, "label"),
    "form":    ("ui:VisualElement", {}, "label"),
    "details": ("ui:Foldout", {}, "label"),
    "summary": ("ui:Label", {}, "text"),  # consumed by parent <details> handler
    "progress":("ui:ProgressBar", {}, None),
    "meter":   ("ui:ProgressBar", {}, None),
    "dialog":  ("ui:VisualElement", {}, "label"),
    "menu":    ("ui:VisualElement", {}, "label"),
    "figure":  ("ui:VisualElement", {}, "label"),
    "figcaption":("ui:Label", {}, "text"),
    "ul":      ("ui:VisualElement", {}, "label"),
    "ol":      ("ui:VisualElement", {}, "label"),
    "li":      ("ui:VisualElement", {}, "label"),
    "table":   ("ui:VisualElement", {}, "label"),
    "tr":      ("ui:VisualElement", {}, "label"),
    "td":      ("ui:VisualElement", {}, "label"),
    "th":      ("ui:VisualElement", {}, "label"),
    "thead":   ("ui:VisualElement", {}, "label"),
    "tbody":   ("ui:VisualElement", {}, "label"),
    "tfoot":   ("ui:VisualElement", {}, "label"),

    "span":    ("ui:Label", {}, "text"),
    "p":       ("ui:Label", {}, "text"),
    "label":   ("ui:Label", {}, "text"),
    "small":   ("ui:Label", {}, "text"),
    "strong":  ("ui:Label", {}, "text"),
    "em":      ("ui:Label", {}, "text"),
    "b":       ("ui:Label", {}, "text"),
    "i":       ("ui:Label", {}, "text"),
    "u":       ("ui:Label", {}, "text"),
    "code":    ("ui:Label", {}, "text"),
    "pre":     ("ui:Label", {}, "text"),
    "h1":      ("ui:Label", {}, "text"),
    "h2":      ("ui:Label", {}, "text"),
    "h3":      ("ui:Label", {}, "text"),
    "h4":      ("ui:Label", {}, "text"),
    "h5":      ("ui:Label", {}, "text"),
    "h6":      ("ui:Label", {}, "text"),
    "legend":  ("ui:Label", {}, "text"),
    "title":   ("ui:Label", {}, "text"),

    "button":  ("ui:Button", {}, "text"),
    "a":       ("ui:Button", {}, "text"),

    "img":     ("ui:VisualElement", {}, None),
    "br":      ("ui:VisualElement", {"class": "br"}, None),
    "hr":      ("ui:VisualElement", {"class": "hr"}, None),

    "fieldset":("ui:GroupBox", {}, "label"),
    "textarea":("ui:TextField", {"multiline": "true"}, "text"),
    "select":  ("ui:DropdownField", {}, None),
}


def map_input(attrs: dict) -> tuple[str, dict, str | None]:
    """Map an <input> based on its `type=`."""
    t = (attrs.get("type") or "text").lower()
    value = attrs.get("value", "")
    if t in ("text", "email", "search", "url", "tel"):
        return "ui:TextField", ({"value": value} if value else {}), None
    if t == "password":
        return "ui:TextField", {"password": "true", **({"value": value} if value else {})}, None
    if t == "number":
        return "ui:FloatField", ({"value": value} if value else {}), None
    if t == "range":
        slider_attrs = {}
        if "min" in attrs:
            slider_attrs["low-value"] = attrs["min"]
        if "max" in attrs:
            slider_attrs["high-value"] = attrs["max"]
        if value:
            slider_attrs["value"] = value
        return "ui:Slider", slider_attrs, None
    if t == "checkbox":
        return "ui:Toggle", {}, None
    if t == "radio":
        return "ui:RadioButton", {}, None
    if t in ("button", "submit", "reset"):
        return "ui:Button", {}, None
    if t == "color":
        # ColorField is editor-only in Unity. Runtime UI should still expose a
        # queryable field, so keep the raw value in a TextField.
        return "ui:TextField", ({"value": value} if value else {}), None
    if t == "date":
        return "ui:TextField", ({"value": value} if value else {}), None
    if t == "file":
        return "ui:Button", {}, None
    return "ui:TextField", {}, None


SKIP_TAGS = {"svg", "canvas", "video", "audio", "iframe", "embed", "object",
             "noscript", "script", "template", "math"}


def map_element(tag: str, attrs: dict) -> tuple[str, dict, str | None]:
    if tag == "input":
        return map_input(attrs)
    if tag in SKIP_TAGS:
        # Replace with a placeholder VisualElement; children dropped at the
        # converter level via the SKIP_TAGS guard.
        return "ui:VisualElement", {"class": f"placeholder-{tag}"}, None
    if tag in ELEMENT_MAP:
        return ELEMENT_MAP[tag]
    return "ui:VisualElement", {}, "label"


# ---------------------------------------------------------------------------
# Style mapping
# ---------------------------------------------------------------------------


# Values that mean "don't apply" - filter out before mapping.
SKIP_VALUES = {"unset", "initial", "inherit", "revert", "revert-layer"}

# CSS properties that USS does not support and we drop with a warning.
DROP_PROPS = {
    "mask", "mask-type", "appearance",
    "float", "clear", "box-sizing", "user-select",
    "perspective", "perspective-origin", "transform-style",
    "backface-visibility", "mix-blend-mode", "background-blend-mode",
    "isolation", "contain", "will-change",
    "outline-offset", "list-style", "table-layout",
    "border-collapse", "border-spacing", "caption-side",
    "scroll-behavior", "scroll-snap-type", "scroll-snap-align",
    "touch-action", "writing-mode", "direction",
    "text-decoration",  # consumed by Label rich-text in the converter
    "text-transform",   # consumed by Label text pre-processing
    "text-indent", "vertical-align",
    "word-break", "overflow-wrap", "hyphens",
}

# Common HTML cursor keywords -> USS cursor keywords. USS supports a fixed
# set: arrow, text, resize-vertical, resize-horizontal, link, slide-arrow,
# pan, orbit, zoom, move-arrow.
CURSOR_MAP = {
    "default": "arrow",
    "auto": "arrow",
    "pointer": "link",
    "text": "text",
    "move": "pan",
    "grab": "pan",
    "grabbing": "pan",
    "ns-resize": "resize-vertical",
    "ew-resize": "resize-horizontal",
    "n-resize": "resize-vertical",
    "s-resize": "resize-vertical",
    "e-resize": "resize-horizontal",
    "w-resize": "resize-horizontal",
    "zoom-in": "zoom",
    "zoom-out": "zoom",
}

# object-fit -> -unity-background-scale-mode (only meaningful when an element
# has a background-image rather than child Image content).
OBJECT_FIT_MAP = {
    "fill":     "stretch-to-fill",
    "cover":    "scale-and-crop",
    "contain":  "scale-to-fit",
    "scale-down": "scale-to-fit",
    "none":     "stretch-to-fill",
}

# Unity USS uses `-unity-text-align` for text alignment.
TEXT_ALIGN_MAP = {
    "left":   "middle-left",
    "center": "middle-center",
    "right":  "middle-right",
    "start":  "middle-left",
    "end":    "middle-right",
    "justify":"middle-left",
}

NAMED_COLORS = {
    "transparent", "black", "white", "red", "green", "blue", "yellow",
    "orange", "purple", "gray", "grey", "silver", "maroon", "navy",
    "teal", "aqua", "fuchsia", "lime", "olive", "pink", "brown", "cyan",
    "magenta", "gold", "indigo", "violet", "tan", "khaki", "salmon",
    "crimson", "coral", "azure", "beige", "ivory", "lavender", "plum",
    "turquoise", "wheat", "snow",
}


@dataclass
class MapResult:
    decls: list[tuple[str, str]]   # USS property/value pairs to emit
    warnings: list[str]            # human-readable notes about gaps


def map_declarations(decls: list[tuple[str, str]]) -> MapResult:
    """Convert a list of (prop, value) CSS pairs to USS pairs."""
    out: list[tuple[str, str]] = []
    warnings: list[str] = []
    normalized_decls = [
        (prop, _coerce_modern_color(_coerce_units(value.strip())))
        for prop, value in decls
    ]
    pattern_decls, pattern_skip, pattern_rewrites = _extract_background_pattern_decls(
        normalized_decls,
        warnings,
    )
    # Collected font-style flags that combine into -unity-font-style.
    is_bold = False
    is_italic = False
    has_font_style_decl = False

    input_props = {p.lower() for p, _ in normalized_decls}
    # CSS default for `display: flex` is `flex-direction: row`. USS default is
    # `column`. Backfill row when the author relied on the CSS default and didn't
    # declare flex-direction explicitly.
    needs_row_default = (
        "display" in input_props
        and "flex-direction" not in input_props
        and any(p.lower() == "display" and v.strip().lower() in ("flex", "inline-flex")
                for p, v in decls)
    )

    for idx, (prop, value) in enumerate(normalized_decls):
        if idx in pattern_skip:
            continue
        v = pattern_rewrites.get(idx, value)
        if not v or v.lower() in SKIP_VALUES:
            continue
        # auto/normal often mean "no override" in computed-style dumps
        if v.lower() in ("auto", "normal", "none") and prop not in (
            "display", "overflow", "white-space", "visibility",
            "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
            "width", "height", "min-width", "max-width", "min-height", "max-height",
            "left", "right", "top", "bottom",
            "pointer-events",
        ):
            continue

        result = _map_one(prop, v, warnings)
        if result is None:
            continue
        for k, val in result:
            if k == "__bold__":
                is_bold = True
                has_font_style_decl = True
            elif k == "__italic__":
                is_italic = True
                has_font_style_decl = True
            elif k == "__font-normal__":
                has_font_style_decl = True
            else:
                out.append((k, val))

    out.extend(pattern_decls)

    if has_font_style_decl:
        if is_bold and is_italic:
            out.append(("-unity-font-style", "bold-and-italic"))
        elif is_bold:
            out.append(("-unity-font-style", "bold"))
        elif is_italic:
            out.append(("-unity-font-style", "italic"))
        else:
            out.append(("-unity-font-style", "normal"))

    if needs_row_default and not any(k == "flex-direction" for k, _ in out):
        out.append(("flex-direction", "row"))

    # Deduplicate keeping last occurrence.
    seen: dict[str, str] = {}
    for k, val in out:
        seen[k] = val
    if "--odd-clip-polygon" in seen and "background-color" in seen:
        seen["--odd-background-color"] = seen.pop("background-color")
    _drop_inert_unity_slices(seen, normalized_decls)
    _apply_content_box_sizing(seen, normalized_decls)
    final = list(seen.items())
    return MapResult(decls=final, warnings=warnings)


_LEN_RE = re.compile(r"^-?\d*\.?\d+(px|em|rem|%|vw|vh|)$")
_GRAD_RE = re.compile(r"\b(linear|radial|conic|repeating-linear|repeating-radial)-gradient\s*\(",
                      re.IGNORECASE)
_BORDER_HAIRLINE_SCALE = 0.5


def _extract_call_body(value: str, fn_name: str) -> str | None:
    """Find `<fn_name>(...)` with balanced parens, return inner body or None."""
    pat = re.compile(re.escape(fn_name) + r"\s*\(", re.IGNORECASE)
    m = pat.search(value)
    if not m:
        return None
    i = m.end()
    depth = 1
    start = i
    while i < len(value) and depth > 0:
        c = value[i]
        if c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
            if depth == 0:
                return value[start:i]
        i += 1
    return None


def _find_gradient(value: str) -> tuple[int, int, str] | None:
    """Locate a `*gradient(...)` call with balanced parens. Returns (start, end_excl, text)."""
    m = _GRAD_RE.search(value)
    if not m:
        return None
    start = m.start()
    i = value.index("(", m.end() - 1)
    depth = 1
    i += 1
    while i < len(value) and depth > 0:
        c = value[i]
        if c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
        i += 1
    if depth != 0:
        return None
    return start, i, value[start:i]


def _map_background_layers(value: str, warnings: list[str]) -> list[tuple[str, str]] | None:
    """Map CSS background layers to the subset Unity plus Html2UxmlPanel can draw.

    CSS permits many comma-separated background layers. The runtime bridge has
    one linear and one radial slot, so keep the first useful layer of each kind
    and derive a solid fallback from the bottom-most layer.
    """
    out: list[tuple[str, str]] = []
    linear: str | None = None
    repeating_linear: str | None = None
    radials: list[str] = []
    other_bits: list[str] = []
    fallback_gradient_layers: list[str] = []

    for layer in _split_top_level_commas(value):
        grad = _find_gradient(layer)
        if not grad:
            if layer.strip():
                other_bits.append(layer.strip())
            continue
        start, end, text = grad
        fn = text.split("(", 1)[0].strip().lower()
        if fn == "linear-gradient":
            if linear is None:
                linear = text
            fallback_gradient_layers.append(text)
        elif fn == "repeating-linear-gradient":
            if repeating_linear is None:
                repeating_linear = text
            else:
                warnings.append("extra repeating-linear-gradient background layers ignored after 1")
        elif fn in ("radial-gradient", "repeating-radial-gradient"):
            if len(radials) < 2:
                radials.append(text)
            else:
                warnings.append("extra radial-gradient background layers ignored after 2")
            if fn == "radial-gradient":
                fallback_gradient_layers.append(text)
        else:
            warnings.append(f"{fn or 'gradient'} not bridged; use linear/radial gradients")
        rest = (layer[:start] + layer[end:]).strip(" ,")
        if rest:
            other_bits.append(rest)

    if linear is not None:
        out.append(("--odd-gradient", _quote_for_uss(linear)))
    if repeating_linear is not None:
        out.append(("--odd-repeating-linear-gradient", _quote_for_uss(repeating_linear)))
    if radials:
        out.append(("--odd-radial-gradient", _quote_for_uss(radials[0])))
    if len(radials) > 1:
        out.append(("--odd-radial-gradient-2", _quote_for_uss(radials[1])))

    color = None
    for bit in reversed(other_bits):
        color = _extract_color(bit)
        if color:
            break
    if color is None and fallback_gradient_layers:
        color = _extract_color(fallback_gradient_layers[-1])
    if color:
        out.append(("background-color", color))

    for bit in other_bits:
        url_m = re.search(r"url\([^)]+\)|resource\([^)]+\)", bit)
        if url_m:
            out.append(("background-image", url_m.group(0)))
            break

    return out or None


def _map_gradient(value: str, warnings: list[str]) -> list[tuple[str, str]] | None:
    fn = value.split("(", 1)[0].strip().lower()
    if fn == "linear-gradient":
        return [("--odd-gradient", _quote_for_uss(value))]
    if fn == "repeating-linear-gradient":
        return [("--odd-repeating-linear-gradient", _quote_for_uss(value))]
    if fn in ("radial-gradient", "repeating-radial-gradient"):
        return [("--odd-radial-gradient", _quote_for_uss(value))]
    warnings.append(f"{fn or 'gradient'} not bridged; use linear/radial gradients")
    return None


def _extract_background_pattern_decls(
    decls: list[tuple[str, str]],
    warnings: list[str],
) -> tuple[list[tuple[str, str]], set[int], dict[int, str]]:
    """Pull CSS tiled radial dot patterns out of normal gradient mapping.

    A browser commonly authors subtle grain with:

        background-image: radial-gradient(rgba(...) 1px, transparent 1px);
        background-size: 6px 6px;

    That is a repeated background image, not one giant radial gradient. UI
    Toolkit has no CSS gradient image tiling, so emit runtime pattern bridge
    props and remove that radial layer from the normal gradient bridge.
    """
    size_value: str | None = None
    position_value: str | None = None
    repeat_value: str | None = None
    for prop, value in decls:
        low = prop.lower()
        if low == "background-size":
            size_value = value
        elif low == "background-position":
            position_value = value
        elif low == "background-repeat":
            repeat_value = value

    if not size_value or not _is_tiled_background_size(size_value):
        return [], set(), {}
    if repeat_value and repeat_value.strip().lower() in ("no-repeat", "round", "space"):
        return [], set(), {}

    out: list[tuple[str, str]] = []
    skip: set[int] = set()
    rewrites: dict[int, str] = {}
    emitted = False

    for idx, (prop, value) in enumerate(decls):
        if prop.lower() not in ("background-image", "background"):
            continue
        layers = _split_top_level_commas(value)
        if not layers:
            continue

        kept_layers: list[str] = []
        for layer in layers:
            grad = _find_gradient(layer)
            if not grad:
                kept_layers.append(layer)
                continue
            start, end, text = grad
            fn = text.split("(", 1)[0].strip().lower()
            if fn == "radial-gradient" and _looks_like_radial_dot_pattern(text):
                if not emitted:
                    out.append(("--odd-tiled-radial-gradient", _quote_for_uss(text)))
                    out.append(("--odd-background-pattern-size", _quote_for_uss(size_value)))
                    if position_value:
                        out.append(("--odd-background-pattern-position", _quote_for_uss(position_value)))
                    warnings.append(
                        "tiled radial-gradient background mapped to Html2UxmlPanel pattern renderer"
                    )
                    emitted = True
                rest = (layer[:start] + layer[end:]).strip(" ,")
                if rest:
                    kept_layers.append(rest)
            else:
                kept_layers.append(layer)

        if len(kept_layers) == len(layers):
            continue
        if kept_layers:
            rewrites[idx] = ", ".join(kept_layers)
        else:
            skip.add(idx)

    return out, skip, rewrites


def _is_tiled_background_size(value: str) -> bool:
    v = value.strip().lower()
    if not v or v in ("auto", "cover", "contain", "initial", "inherit", "unset"):
        return False
    # Tiled CSS pattern art normally uses explicit lengths. Avoid treating
    # cover/contain image scaling as a pattern signal.
    return bool(re.search(r"\d", v) and any(unit in v for unit in ("px", "%", "em", "rem")))


def _looks_like_radial_dot_pattern(value: str) -> bool:
    body = _extract_gradient_body(value)
    if body is None:
        return False
    parts = _split_top_level_commas(body)
    if len(parts) < 2:
        return False
    first_color, first_positions = _split_color_stop_positions(parts[0])
    second_color, second_positions = _split_color_stop_positions(parts[1])
    if not first_color or not second_color:
        return False
    if not first_positions or not second_positions:
        return False
    if "transparent" not in second_color.lower() and not re.search(r"rgba?\([^)]*,\s*0(?:\.0+)?\s*\)", second_color, re.IGNORECASE):
        return False
    first_px = _small_px_stop(first_positions[-1])
    second_px = _small_px_stop(second_positions[-1])
    return first_px is not None and second_px is not None and abs(first_px - second_px) <= 1.0


def _extract_gradient_body(value: str) -> str | None:
    open_idx = value.find("(")
    close_idx = value.rfind(")")
    if open_idx < 0 or close_idx <= open_idx:
        return None
    return value[open_idx + 1:close_idx]


def _split_color_stop_positions(stop: str) -> tuple[str, list[str]]:
    tokens = _split_whitespace_top_level(stop.strip())
    if not tokens:
        return "", []
    color_tokens: list[str] = []
    positions: list[str] = []
    for token in tokens:
        if positions or _is_gradient_stop_position(token):
            positions.append(token)
        else:
            color_tokens.append(token)
    return " ".join(color_tokens).strip(), positions


def _split_whitespace_top_level(value: str) -> list[str]:
    parts: list[str] = []
    depth = 0
    quote: str | None = None
    buf: list[str] = []
    for c in value:
        if quote:
            buf.append(c)
            if c == quote:
                quote = None
            continue
        if c in ("'", '"'):
            quote = c
            buf.append(c)
            continue
        if c == "(":
            depth += 1
            buf.append(c)
            continue
        if c == ")":
            depth = max(0, depth - 1)
            buf.append(c)
            continue
        if c.isspace() and depth == 0:
            if buf:
                parts.append("".join(buf))
                buf = []
            continue
        buf.append(c)
    if buf:
        parts.append("".join(buf))
    return parts


def _is_gradient_stop_position(token: str) -> bool:
    return bool(re.fullmatch(r"-?\d*\.?\d+(?:px|%|em|rem)?", token.strip().lower()))


def _small_px_stop(token: str) -> float | None:
    t = token.strip().lower()
    factor = 1.0
    if t.endswith("px"):
        t = t[:-2]
    elif t.endswith("rem"):
        factor = 16.0
        t = t[:-3]
    elif t.endswith("em"):
        factor = 16.0
        t = t[:-2]
    elif t.endswith("%"):
        return None
    try:
        px = float(t) * factor
    except ValueError:
        return None
    return px if 0.0 <= px <= 16.0 else None

# em/rem -> px (16px base). vw/vh -> %.
_UNIT_LEN_RE = re.compile(r"(-?\d*\.?\d+)(rem|em|vw|vh)\b")
# rgb(r g b) and rgb(r g b / a) modern slash syntax.
_MODERN_RGB_RE = re.compile(
    r"\brgba?\(\s*"
    r"(\d+(?:\.\d+)?%?)\s+(\d+(?:\.\d+)?%?)\s+(\d+(?:\.\d+)?%?)"
    r"(?:\s*/\s*(\d+(?:\.\d+)?%?))?"
    r"\s*\)",
    re.IGNORECASE,
)
_HSL_RE = re.compile(
    r"\bhsla?\(\s*"
    r"([+-]?\d*\.?\d+)(?:deg)?"
    r"(?:\s*,\s*|\s+)"
    r"(\d*\.?\d+)%"
    r"(?:\s*,\s*|\s+)"
    r"(\d*\.?\d+)%"
    r"(?:(?:\s*,\s*|\s*/\s*)(\d*\.?\d+%?))?"
    r"\s*\)",
    re.IGNORECASE,
)


def _coerce_units(value: str) -> str:
    def repl(m: re.Match) -> str:
        n = float(m.group(1))
        unit = m.group(2).lower()
        if unit in ("em", "rem"):
            return f"{n * 16:g}px"
        # vw/vh -> %; the absolute reference is the panel, which Unity scales.
        return f"{n:g}%"
    return _UNIT_LEN_RE.sub(repl, value)


def _coerce_modern_color(value: str) -> str:
    def repl_rgb(m: re.Match) -> str:
        r, g, b, a = m.group(1), m.group(2), m.group(3), m.group(4)
        if a is None:
            return f"rgb({r}, {g}, {b})"
        return f"rgba({r}, {g}, {b}, {a})"

    def repl_hsl(m: re.Match) -> str:
        h = float(m.group(1)) % 360
        s = max(0.0, min(100.0, float(m.group(2)))) / 100.0
        light = max(0.0, min(100.0, float(m.group(3)))) / 100.0
        r, g, b = colorsys.hls_to_rgb(h / 360.0, light, s)
        rgb = tuple(round(c * 255) for c in (r, g, b))
        alpha = m.group(4)
        if alpha is None:
            return f"rgb({rgb[0]}, {rgb[1]}, {rgb[2]})"
        if alpha.endswith("%"):
            a = float(alpha[:-1]) / 100.0
        else:
            a = float(alpha)
        return f"rgba({rgb[0]}, {rgb[1]}, {rgb[2]}, {a:g})"

    value = _MODERN_RGB_RE.sub(repl_rgb, value)
    return _HSL_RE.sub(repl_hsl, value)


def _parse_box_shadow(value: str):
    """Parse a single `<color> <ox> <oy> <blur>` (any order) shadow.

    Returns (offset-x, offset-y, blur, color) or None for unsupported shapes.
    Only the first shadow is taken if multiple comma-separated shadows are
    present (USS bridge supports a single shadow per element).
    """
    for layer in _split_top_level_commas(value):
        parsed = _parse_shadow_layer(layer)
        if parsed is not None and not parsed[0]:
            _, ox, oy, blur, _spread, color = parsed
            return ox, oy, blur, color
    return None


def _shadow_record(kind: str, ox: str, oy: str, blur: str, spread: str, color: str) -> str:
    return "|".join((kind, ox, oy, blur, spread, color))


def _emit_shadow_props(records: list[str]) -> list[tuple[str, str]]:
    if not records:
        return []
    out = [("--odd-box-shadows", _quote_for_uss(";".join(records)))]
    for record in records:
        parts = record.split("|", 5)
        if len(parts) < 6:
            continue
        kind, ox, oy, blur, spread, color = parts
        if kind == "outset":
            out.extend([
                ("--odd-shadow-offset-x", ox),
                ("--odd-shadow-offset-y", oy),
                ("--odd-shadow-blur", blur),
                ("--odd-shadow-color", color),
            ])
            break
    for record in records:
        parts = record.split("|", 5)
        if len(parts) < 6:
            continue
        kind, ox, oy, blur, spread, color = parts
        if kind == "inset":
            out.extend([
                ("--odd-inner-shadow-offset-x", ox),
                ("--odd-inner-shadow-offset-y", oy),
                ("--odd-inner-shadow-blur", blur),
                ("--odd-inner-shadow-spread", spread),
                ("--odd-inner-shadow-color", color),
            ])
            break
    return out


def _map_box_shadow(value: str, warnings: list[str]) -> list[tuple[str, str]] | None:
    out: list[tuple[str, str]] = []
    saw_supported = False
    skipped_layers = 0
    records: list[str] = []
    for layer in _split_top_level_commas(value):
        parsed = _parse_shadow_layer(layer)
        if parsed is None:
            skipped_layers += 1
            continue
        inset, ox, oy, blur, spread, color = parsed
        if len(records) >= 8:
            skipped_layers += 1
            continue
        records.append(_shadow_record("inset" if inset else "outset", ox, oy, blur, spread, color))
        saw_supported = True
    out = _emit_shadow_props(records) + out
    if skipped_layers:
        warnings.append(f"box-shadow: {value} -- additional/complex shadow layers ignored after 8 supported layers")
    if not saw_supported:
        warnings.append(f"box-shadow: {value} -- complex shadow not bridged")
        return None
    return out


def _map_mask_image(prop: str, value: str, warnings: list[str]) -> list[tuple[str, str]] | None:
    grad = _find_gradient(value)
    if not grad:
        warnings.append(f"{prop}: {value} -- only linear-gradient mask fades are bridged")
        return None
    text = grad[2]
    fn = text.split("(", 1)[0].strip().lower()
    if fn not in ("linear-gradient", "repeating-linear-gradient"):
        warnings.append(f"{prop}: {value} -- only linear-gradient mask fades are bridged")
        return None
    return [("--odd-mask-image", _quote_for_uss(text))]


def _split_top_level_commas(value: str) -> list[str]:
    parts: list[str] = []
    depth = 0
    start = 0
    quote: str | None = None
    escaped = False
    for i, c in enumerate(value):
        if quote:
            if escaped:
                escaped = False
            elif c == "\\":
                escaped = True
            elif c == quote:
                quote = None
            continue
        if c in ("'", '"'):
            quote = c
        if c == "(":
            depth += 1
        elif c == ")":
            depth = max(0, depth - 1)
        elif c == "," and depth == 0:
            part = value[start:i].strip()
            if part:
                parts.append(part)
            start = i + 1
    tail = value[start:].strip()
    if tail:
        parts.append(tail)
    return parts


def _parse_shadow_layer(layer: str):
    low = layer.lower()
    inset = bool(re.search(r"\binset\b", low))
    color = _extract_color(layer)
    if color is None:
        return None
    # Strip the color and any other function calls before scanning lengths,
    # so we don't pick up channel digits from rgba()/hsla().
    scrubbed = re.sub(r"(rgba?|hsla?)\s*\([^)]*\)", " ", layer, flags=re.IGNORECASE)
    scrubbed = re.sub(r"#[0-9a-fA-F]{3,8}\b", " ", scrubbed)
    scrubbed = re.sub(r"\binset\b", " ", scrubbed, flags=re.IGNORECASE)
    nums = re.findall(r"-?\d*\.?\d+(?:px|em|rem|%)?", scrubbed)
    nums = [n for n in nums if n.strip()]
    if len(nums) < 2:
        return None
    ox = _strip_unit(nums[0])
    oy = _strip_unit(nums[1])
    blur = _strip_unit(nums[2]) if len(nums) >= 3 else "0"
    spread = _strip_unit(nums[3]) if len(nums) >= 4 else "0"
    return inset, ox, oy, blur, spread, color


def _ensure_unit(n: str) -> str:
    return n if n.endswith(("px", "em", "rem", "%")) or n in ("0",) else f"{n}px"


def _strip_unit(n: str) -> str:
    """Drop length unit so the value parses as a plain number.
    Used for bridge custom props typed as float (CustomStyleProperty<float>)."""
    for u in ("px", "rem", "em", "%"):
        if n.endswith(u):
            return n[: -len(u)] or "0"
    return n


_VENDOR_KEEP = {
    "-webkit-backdrop-filter",
    "-webkit-mask-image",
    "-webkit-text-stroke",
    "-webkit-text-stroke-width",
    "-webkit-text-stroke-color",
}


def _map_one(prop: str, value: str, warnings: list[str]) -> list[tuple[str, str]] | None:
    if prop not in _VENDOR_KEEP and (
        prop.startswith("-webkit-") or prop.startswith("-moz-") or prop.startswith("-ms-")
    ):
        return None
    if prop in ("text-decoration", "text-transform"):
        # These are consumed while emitting text nodes, not written as USS.
        return None
    if prop == "animation" or prop.startswith("animation-"):
        # CSS keyframes are converted by converter.py into --odd-animation-*
        # props after @keyframes have been parsed. Keep this mapper quiet.
        return []
    if prop == "z-index":
        # Consumed by the converter as a static sibling paint-order sort.
        # Unity has no z-index property to emit.
        return None
    if prop == "pointer-events":
        v = value.strip().lower()
        if v == "none":
            return [("__picking-mode__", "Ignore")]
        return None
    if prop in ("backdrop-filter", "-webkit-backdrop-filter"):
        return _map_backdrop_filter(value, warnings)
    if prop in ("mask-image", "-webkit-mask-image"):
        return _map_mask_image(prop, value, warnings)
    if prop in DROP_PROPS:
        warnings.append(f"unsupported in USS, dropped: {prop}: {value}")
        return None

    # display
    if prop == "display":
        v = value.lower()
        if v == "none":
            return [("display", "none")]
        if v == "flex":
            return [("display", "flex")]
        if v == "inline-flex":
            return [("display", "flex"), ("flex-direction", "row")]
        if v in ("block", "list-item"):
            return [("display", "flex"), ("flex-direction", "column")]
        if v in ("inline", "inline-block"):
            return [("display", "flex"), ("flex-direction", "row")]
        if v.startswith("grid") or v.startswith("table") or v == "contents" or v == "ruby":
            warnings.append(f"display: {value} approximated as flex")
            return [("display", "flex")]
        return [("display", "flex")]

    # text alignment
    if prop == "text-align":
        v = value.lower()
        out_decls = [("-unity-text-align", TEXT_ALIGN_MAP.get(v, "middle-left"))]
        # Browsers honour `text-align` for inline children of a flex
        # container (each line gets centered/right-aligned within the
        # container). Unity USS doesn't propagate text-align that way,
        # so mirror the alignment via `justify-content` on the parent.
        # `justify-content` is inert on non-flex containers, so emitting
        # it unconditionally is safe.
        justify = {
            "center": "center",
            "right": "flex-end",
            "end": "flex-end",
            "left": "flex-start",
            "start": "flex-start",
            "justify": "space-between",
        }.get(v)
        if justify:
            out_decls.append(("justify-content", justify))
        return out_decls

    # font weight / style fold into -unity-font-style. Keep the numeric
    # weight as a custom property so the CLI can inject the closest real font
    # file instead of asking Unity to synthesize every bold weight.
    if prop == "font-weight":
        v = value.lower()
        try:
            n = int(v)
        except ValueError:
            n = None
        out: list[tuple[str, str]] = []
        if n is not None:
            out.append(("--odd-font-weight", str(n)))
        elif v in ("bold", "bolder"):
            out.append(("--odd-font-weight", "700"))
        elif v in ("normal", "lighter"):
            out.append(("--odd-font-weight", "400"))
        if v in ("bold", "bolder") or (n is not None and n >= 600):
            out.append(("__bold__", "1"))
        else:
            out.append(("__font-normal__", "0"))
        return out
    if prop == "font-style":
        v = value.lower()
        if v in ("italic", "oblique"):
            return [("__italic__", "1")]
        return [("__font-normal__", "0")]

    if prop == "font-family":
        # Keep the primary family in a custom prop so an asset bundler can
        # later swap it for `-unity-font-definition: url("...")`.
        first = value.split(",", 1)[0].strip().strip('"').strip("'")
        if not first or first.lower() in (
            "serif", "sans-serif", "monospace", "cursive", "fantasy",
            "system-ui", "ui-serif", "ui-sans-serif", "ui-monospace",
            "ui-rounded", "math", "emoji", "fangsong",
        ):
            return None
        return [("--odd-font-family", f'"{first}"')]

    # font shorthand: too ambiguous; only pull font-size if obvious.
    if prop == "font":
        m = re.search(r"(\d+(?:\.\d+)?(px|em|%))", value)
        out: list[tuple[str, str]] = []
        if m:
            out.append(("font-size", m.group(1)))
        warnings.append(
            f"font shorthand split partially; specify font-size/-unity-font-definition explicitly"
        )
        return out or None

    # background shorthand: pull color, image, and bridge gradients via custom prop.
    if prop == "background":
        return _map_background_layers(value, warnings)
    if prop == "background-image":
        # Computed-style background-image often comes through as multiple
        # comma-separated layers (e.g. `radial-gradient(...), radial-gradient(...), none`).
        # Route through the multi-layer bridge so all gradients survive.
        return _map_background_layers(value, warnings)
    if prop == "background-color":
        return [("background-color", value)]

    # border shorthand: split into width + color (style is always "solid" in USS)
    if prop == "border":
        return _split_border(value, sides=("top", "right", "bottom", "left"))
    if prop in ("border-top", "border-right", "border-bottom", "border-left"):
        side = prop.split("-", 1)[1]
        return _split_border(value, sides=(side,))
    if prop in (
        "border-width", "border-top-width", "border-right-width",
        "border-bottom-width", "border-left-width",
    ):
        return _map_border_width_property(prop, value)
    if prop == "border-radius":
        return _map_border_radius(value, warnings)
    if prop in (
        "border-top-left-radius", "border-top-right-radius",
        "border-bottom-right-radius", "border-bottom-left-radius",
    ):
        return [(prop, _first_radius_component(value, warnings))]

    # cursor: USS supports a fixed keyword set or url()/resource().
    if prop == "cursor":
        if "url(" in value or "resource(" in value:
            return [("cursor", value)]
        first = value.split()[0].lower() if value.split() else ""
        mapped = CURSOR_MAP.get(first)
        if mapped:
            return [("cursor", mapped)]
        warnings.append(f"cursor: {value} -- no USS keyword equivalent, dropped")
        return None

    # position: USS supports absolute|relative|initial only.
    if prop == "position":
        v = value.lower()
        if v in ("relative", "absolute", "initial", "static"):
            return [("position", "relative" if v == "static" else v)]
        if v in ("fixed", "sticky"):
            warnings.append(f"position: {value} approximated as absolute")
            return [("position", "absolute")]
        return None

    # overflow: USS supports hidden|visible only.
    if prop == "overflow":
        v = value.lower()
        if v in ("hidden", "visible"):
            return [("overflow", v)]
        if v in ("auto", "scroll", "clip"):
            warnings.append(f"overflow: {value} approximated as hidden (use a ScrollView for scroll)")
            return [("overflow", "hidden")]
        return None
    if prop in ("overflow-x", "overflow-y"):
        warnings.append(f"{prop} not supported in USS; use overflow")
        return None

    # object-fit: only meaningful when paired with a background-image.
    if prop == "object-fit":
        v = value.lower()
        mapped = OBJECT_FIT_MAP.get(v)
        if mapped:
            return [("-unity-background-scale-mode", mapped)]
        return None

    # box-shadow: not in USS proper; emit custom props for the runtime package.
    if prop == "box-shadow":
        return _map_box_shadow(value, warnings)

    # transform: USS prefers individual translate/rotate/scale properties.
    if prop == "transform":
        return _split_transform(value, warnings)

    # gap / row-gap / column-gap: USS doesn't support these natively, and the
    # converter bakes them into per-child margins at conversion time via
    # _static_gap_decls. The properties themselves are dropped from output;
    # _flex_row_gap_px / _flex_column_gap_px read from the resolved style.
    if prop in ("gap", "row-gap", "column-gap"):
        return []

    # inset shorthand -> top/right/bottom/left.
    if prop == "inset":
        sides = _expand_box(value)
        if not sides:
            return None
        top, right, bottom, left = sides
        return [("top", top), ("right", right), ("bottom", bottom), ("left", left)]

    # white-space: Unity 6.4 USS supports normal, nowrap, pre, pre-wrap.
    if prop == "white-space":
        v = value.lower()
        if v in ("normal", "nowrap", "pre", "pre-wrap"):
            return [("white-space", v)]
        if v in ("pre-line", "break-spaces"):
            return [("white-space", "pre-wrap")]
        warnings.append(f"white-space: {value} approximated as normal")
        return [("white-space", "normal")]

    # outline: USS has no outline; approximate as border on all sides + warn
    # that outline (unlike border) doesn't normally occupy layout space.
    if prop == "outline":
        warnings.append(f"outline approximated as border (occupies layout space)")
        return _split_border(value, sides=("top", "right", "bottom", "left"))

    # clip-path: bridge polygon() via a custom prop the Html2UxmlPanel renders.
    if prop == "clip-path":
        v = value.strip()
        if v.lower().startswith("polygon"):
            return [("--odd-clip-polygon", _quote_for_uss(v))]
        warnings.append(f"clip-path: {value} -- only polygon() is bridged")
        return None

    # filter: Unity 6.4 native functions pass through; drop-shadow() routes
    # into the box-shadow bridge because USS still omits that CSS function.
    if prop == "filter":
        return _map_filter(value, warnings)

    # text-overflow: USS supports clip and ellipsis.
    if prop == "text-overflow":
        v = value.lower()
        if v in ("clip", "ellipsis"):
            return [("text-overflow", v)]
        return None

    # border-image -> background-image + -unity-slice-* (9-slice).
    # Accepts: `<source> <slice> / <width>` shorthand or just <source> <slice>.
    if prop == "border-image":
        url_m = re.search(r"url\([^)]+\)", value)
        if not url_m:
            warnings.append(f"border-image: {value} dropped (no url() source)")
            return None
        url = url_m.group(0)
        rest = value[:url_m.start()] + value[url_m.end():]
        # Parse slice numbers (before any '/'), accepting 1, 2, or 4 values.
        slice_part = rest.split("/", 1)[0]
        nums = re.findall(r"-?\d*\.?\d+", slice_part)
        if not nums:
            return [("background-image", url)]
        if len(nums) == 1:
            t = r = b = l_ = nums[0]
        elif len(nums) == 2:
            t, r = nums; b, l_ = t, r
        elif len(nums) == 3:
            t, r, b = nums; l_ = r
        else:
            t, r, b, l_ = nums[:4]
        out: list[tuple[str, str]] = [
            ("background-image", url),
            ("-unity-slice-top", str(int(float(t)))),
            ("-unity-slice-right", str(int(float(r)))),
            ("-unity-slice-bottom", str(int(float(b)))),
            ("-unity-slice-left", str(int(float(l_)))),
        ]
        return out
    if prop == "border-image-source":
        return [("background-image", value)]
    if prop == "border-image-slice":
        nums = re.findall(r"-?\d*\.?\d+", value)
        if not nums:
            return None
        if len(nums) == 1:
            t = r = b = l_ = nums[0]
        elif len(nums) == 2:
            t, r = nums; b, l_ = t, r
        elif len(nums) == 3:
            t, r, b = nums; l_ = r
        else:
            t, r, b, l_ = nums[:4]
        return [
            ("-unity-slice-top", str(int(float(t)))),
            ("-unity-slice-right", str(int(float(r)))),
            ("-unity-slice-bottom", str(int(float(b)))),
            ("-unity-slice-left", str(int(float(l_)))),
        ]

    # line-height -> -unity-paragraph-spacing (approximate). USS has no real
    # line-height; paragraph-spacing controls extra space between wrapped
    # lines. We pass the px value through as a best-effort substitute.
    if prop == "line-height":
        v = value.strip().lower()
        if v in ("normal", "inherit", "initial"):
            return None
        if v.endswith("px"):
            return [("-unity-paragraph-spacing", v)]
        # Unitless multiplier or em/% — can't resolve without font-size context.
        warnings.append(f"line-height: {value} approximated as 0 paragraph spacing")
        return [("-unity-paragraph-spacing", "0")]

    # -webkit-text-stroke -> -unity-text-outline-{width,color}
    if prop in ("-webkit-text-stroke", "text-stroke"):
        parts = value.split(None, 1)
        if not parts:
            return None
        width = parts[0]
        color = parts[1] if len(parts) > 1 else None
        out: list[tuple[str, str]] = [("-unity-text-outline-width", width)]
        if color:
            out.append(("-unity-text-outline-color", color))
        return out
    if prop == "-webkit-text-stroke-width":
        return [("-unity-text-outline-width", value)]
    if prop == "-webkit-text-stroke-color":
        return [("-unity-text-outline-color", value)]

    # text-shadow: USS (newer) accepts the same syntax.
    if prop == "text-shadow":
        return [("text-shadow", value)]

    # Unity 6.4 has a growing set of -unity-* properties. Pass them through
    # unless a handler above deliberately translated a web equivalent.
    if prop.startswith("-unity-"):
        return [(prop, value)]

    # opacity, color, and most numeric properties pass through.
    pass_through = {
        "all",
        "color", "opacity", "background-color",
        "background-position", "background-position-x", "background-position-y",
        "background-repeat", "background-size",
        "width", "height", "min-width", "min-height", "max-width", "max-height",
        "left", "right", "top", "bottom",
        "padding", "padding-top", "padding-right", "padding-bottom", "padding-left",
        "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
        "border-width", "border-top-width", "border-right-width",
        "border-bottom-width", "border-left-width",
        "border-color", "border-top-color", "border-right-color",
        "border-bottom-color", "border-left-color",
        "border-top-left-radius", "border-top-right-radius",
        "border-bottom-left-radius", "border-bottom-right-radius",
        "flex", "flex-grow", "flex-shrink", "flex-basis", "flex-direction", "flex-wrap",
        "align-items", "align-self", "align-content",
        "justify-content", "justify-self",
        "visibility",
        "letter-spacing", "font-size", "word-spacing",
        "aspect-ratio",
        "text-shadow",
        "transition", "transition-property",
        "transition-duration", "transition-delay", "transition-timing-function",
        "translate", "rotate", "scale",
        "transform-origin",
    }
    if prop in pass_through:
        # USS doesn't accept comma-separated multi-layer values for
        # background-{repeat,position,size}, even though CSS allows
        # `repeat, repeat, repeat` for stacked backgrounds. Collapse to
        # the first non-empty token. Unity's style parser otherwise
        # bails with "Expected end of value but found ','" and drops
        # every subsequent declaration in the rule (including our
        # `--odd-*` gradient props), so leaving the comma in place
        # silently wipes out gradient/shadow paint on the panel.
        if (prop in ("background-repeat", "background-position", "background-size")
                and "," in value):
            first = value.split(",", 1)[0].strip()
            value = first or ("no-repeat" if prop == "background-repeat" else "0% 0%")
        return [(prop, value)]
    # Pass-through CSS variables (USS supports `--var: value;` and `var()`).
    if prop.startswith("--"):
        return [(prop, value)]

    # Anything else -> drop with note.
    warnings.append(f"unmapped CSS property dropped: {prop}: {value}")
    return None


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def _quote_for_uss(value: str) -> str:
    """Wrap a raw CSS value as a USS string literal so the parser accepts it.
    runtime panel reads --odd-* as strings; functions like `linear-gradient(...)`
    aren't recognized by the USS lexer otherwise."""
    inner = value.replace('\\', '\\\\').replace('"', '\\"')
    return f'"{inner}"'


def _extract_color(value: str) -> str | None:
    """Return the last color-like token in a shorthand value, if any."""
    m_funcs = list(re.finditer(
        r"(rgba?|hsla?)\s*\([^)]*\)", value, re.IGNORECASE,
    ))
    if m_funcs:
        return m_funcs[-1].group(0)
    m_hex = list(re.finditer(r"#[0-9a-fA-F]{3,8}\b", value))
    if m_hex:
        return m_hex[-1].group(0)
    for tok in reversed(value.split()):
        if tok.lower() in NAMED_COLORS:
            return tok
    return None


def _split_border(value: str, sides: tuple[str, ...]) -> list[tuple[str, str]] | None:
    """Convert `border: 1px solid red` into (width, color) for the given sides."""
    width = None
    color = _extract_color(value)
    # Strip color before scanning for width tokens.
    scrubbed = re.sub(r"(rgba?|hsla?)\s*\([^)]*\)", " ", value, flags=re.IGNORECASE)
    scrubbed = re.sub(r"#[0-9a-fA-F]{3,8}\b", " ", scrubbed)
    for tok in scrubbed.split():
        if _LEN_RE.match(tok):
            width = tok
            break
    out: list[tuple[str, str]] = []
    for side in sides:
        if width is not None:
            out.append((f"border-{side}-width", _scale_border_width(width)))
        if color is not None:
            out.append((f"border-{side}-color", color))
    return out or None


def _map_border_width_property(prop: str, value: str) -> list[tuple[str, str]] | None:
    """Map border width longhands and CSS box shorthand into Unity-sized widths.

    UI Toolkit renders CSS pixel borders heavier than browsers at the target
    scales used by authored HTML mockups. Scale px hairlines at conversion time
    so 1px browser strokes land closer to the same visual weight in USS.
    """
    if prop != "border-width":
        return [(prop, _scale_border_width(value))]

    sides = _expand_box(value)
    if not sides:
        return [("border-width", _scale_border_width(value))]
    top, right, bottom, left = sides
    return [
        ("border-top-width", _scale_border_width(top)),
        ("border-right-width", _scale_border_width(right)),
        ("border-bottom-width", _scale_border_width(bottom)),
        ("border-left-width", _scale_border_width(left)),
    ]


def _scale_border_width(value: str) -> str:
    raw = value.strip()
    if raw in ("0", "0px", "0.0px", "0.00px"):
        return "0"
    m = re.fullmatch(r"(-?\d*\.?\d+)(px)?", raw, re.IGNORECASE)
    if not m:
        return value
    unit = m.group(2)
    if unit is None and float(m.group(1)) != 0:
        unit = "px"
    if unit is None:
        return "0"
    width = max(0.0, float(m.group(1)) * _BORDER_HAIRLINE_SCALE)
    if width == 0:
        return "0"
    return f"{width:g}px"


def _map_border_radius(value: str, warnings: list[str]) -> list[tuple[str, str]] | None:
    horizontal = value.split("/", 1)[0].strip()
    if "/" in value:
        warnings.append(
            f"border-radius elliptical values approximated with horizontal radii: {value}"
        )
    sides = _expand_box(horizontal)
    if not sides:
        return None
    top_left, top_right, bottom_right, bottom_left = sides
    return [
        ("border-top-left-radius", _first_radius_component(top_left, warnings)),
        ("border-top-right-radius", _first_radius_component(top_right, warnings)),
        ("border-bottom-right-radius", _first_radius_component(bottom_right, warnings)),
        ("border-bottom-left-radius", _first_radius_component(bottom_left, warnings)),
    ]


def _first_radius_component(value: str, warnings: list[str]) -> str:
    parts = value.split()
    if len(parts) > 1:
        warnings.append(
            f"border radius elliptical corner approximated with first radius: {value}"
        )
    return parts[0] if parts else value


def _split_transform_args(args: str) -> list[str]:
    comma_parts = _split_top_level_commas(args)
    if len(comma_parts) > 1:
        return comma_parts
    return [p for p in re.split(r"\s+", args.strip()) if p]


def _split_transform(value: str, warnings: list[str]) -> list[tuple[str, str]] | None:
    """Map `transform: translateX(50%) rotate(10deg)` to USS individual props."""
    out_translate = ["0", "0"]
    out_rotate = None
    has_translate = False
    scale_x = "1"
    scale_y = "1"
    has_scale = False
    force_scale_pair = False
    for fn, args in re.findall(r"([a-zA-Z][a-zA-Z0-9-]*)\s*\(([^)]*)\)", value):
        fn = fn.lower()
        parts = _split_transform_args(args)
        if fn == "translatex" and parts:
            out_translate[0] = parts[0]
            has_translate = True
        elif fn == "translatey" and parts:
            out_translate[1] = parts[0]
            has_translate = True
        elif fn == "translate":
            if parts:
                out_translate[0] = parts[0]
            if len(parts) > 1:
                out_translate[1] = parts[1]
            has_translate = True
        elif fn == "translate3d":
            if len(parts) >= 2:
                out_translate[0] = parts[0]
                out_translate[1] = parts[1]
                has_translate = True
            warnings.append("transform function translate3d() flattened to 2D; z component ignored")
        elif fn in ("rotate", "rotatez"):
            if parts:
                out_rotate = parts[0]
        elif fn == "scale":
            if len(parts) == 1:
                scale_x = parts[0]
                scale_y = parts[0]
                has_scale = True
            elif len(parts) >= 2:
                scale_x = parts[0]
                scale_y = parts[1]
                has_scale = True
                force_scale_pair = True
        elif fn == "scalex" and parts:
            scale_x = parts[0]
            has_scale = True
            force_scale_pair = True
        elif fn == "scaley" and parts:
            scale_y = parts[0]
            has_scale = True
            force_scale_pair = True
        elif fn == "matrix":
            matrix = _parse_transform_matrix(parts)
            if matrix is None:
                warnings.append("transform function matrix() not supported in USS, ignored")
                continue
            a, b, c, d, e, f = matrix
            if not _near_zero(e) or not _near_zero(f):
                out_translate[0] = _format_matrix_px(e)
                out_translate[1] = _format_matrix_px(f)
                has_translate = True

            sx = math.hypot(a, b)
            sy = math.hypot(c, d)
            shear = a * c + b * d
            if sx > 0.00001 and sy > 0.00001 and abs(shear) <= 0.0001:
                angle = math.degrees(math.atan2(b, a))
                if not _near_zero(angle):
                    out_rotate = _format_matrix_degrees(angle)
                # Negative-determinant matrices include reflection. USS can
                # represent the common axis-aligned case as negative scale;
                # otherwise keep the position correction and warn.
                det = a * d - b * c
                if det < 0 and not (_near_zero(b) and _near_zero(c)):
                    warnings.append("transform matrix() reflection/skew approximated; translate preserved")
                else:
                    if _near_zero(b) and _near_zero(c):
                        sx = a
                        sy = d
                    if not _near(sx, 1.0) or not _near(sy, 1.0):
                        scale_x = _format_matrix_scalar(sx)
                        scale_y = _format_matrix_scalar(sy)
                        has_scale = True
                        force_scale_pair = not _near(sx, sy)
            elif not (_near(a, 1.0) and _near_zero(b) and _near_zero(c) and _near(d, 1.0)):
                warnings.append("transform matrix() with skew could not be represented in USS; translate preserved")
        elif fn in ("rotatex", "rotatey", "translatez",
                    "skew", "skewx", "skewy", "perspective", "matrix3d"):
            warnings.append(f"transform function {fn}() not supported in USS, ignored")
    pairs: list[tuple[str, str]] = []
    if has_translate:
        pairs.append(("translate", f"{out_translate[0]} {out_translate[1]}"))
    if out_rotate is not None:
        pairs.append(("rotate", out_rotate))
    if has_scale:
        if scale_x == scale_y and not force_scale_pair:
            pairs.append(("scale", scale_x))
        else:
            pairs.append(("scale", f"{scale_x} {scale_y}"))
    return pairs or None


def _parse_transform_matrix(parts: list[str]) -> tuple[float, float, float, float, float, float] | None:
    if len(parts) != 6:
        return None
    parsed: list[float] = []
    for part in parts:
        raw = part.strip().lower()
        if raw.endswith("px"):
            raw = raw[:-2].strip()
        try:
            parsed.append(float(raw))
        except ValueError:
            return None
    return tuple(parsed)  # type: ignore[return-value]


def _near(a: float, b: float) -> bool:
    return abs(a - b) <= 0.0001


def _near_zero(value: float) -> bool:
    return abs(value) <= 0.0001


def _format_matrix_scalar(value: float) -> str:
    if _near_zero(value):
        value = 0.0
    return f"{value:g}"


def _format_matrix_px(value: float) -> str:
    if _near_zero(value):
        value = 0.0
    return f"{value:g}px"


def _format_matrix_degrees(value: float) -> str:
    if _near_zero(value):
        value = 0.0
    return f"{value:g}deg"


def _apply_content_box_sizing(
    seen: dict[str, str],
    normalized_decls: list[tuple[str, str]],
) -> None:
    """Convert CSS content-box dimensions to Yoga/USS border-box dimensions.

    Browsers treat `width`/`height` as the content box unless `box-sizing:
    border-box` is set. UI Toolkit has no `box-sizing` and lays padding/border
    inside the assigned width/height, so content-box elements need their fixed
    pixel sizes inflated by their horizontal/vertical padding and borders.
    """
    box_sizing = None
    for prop, value in normalized_decls:
        if prop == "box-sizing":
            box_sizing = value.strip().lower()
    if box_sizing == "border-box":
        return

    horizontal = (
        _box_side_px(seen, "padding", "left")
        + _box_side_px(seen, "padding", "right")
        + _box_side_px(seen, "border-width", "left")
        + _box_side_px(seen, "border-width", "right")
    )
    vertical = (
        _box_side_px(seen, "padding", "top")
        + _box_side_px(seen, "padding", "bottom")
        + _box_side_px(seen, "border-width", "top")
        + _box_side_px(seen, "border-width", "bottom")
    )

    if horizontal > 0:
        _inflate_px_dimension(seen, "width", horizontal)
    if vertical > 0:
        _inflate_px_dimension(seen, "height", vertical)


def _drop_inert_unity_slices(
    seen: dict[str, str],
    normalized_decls: list[tuple[str, str]],
) -> None:
    """Drop computed border-image defaults that would 9-slice normal images.

    Chrome reports `border-image-slice: 100%` even when
    `border-image-source` is `none`. Unity applies `-unity-slice-*` to the
    element background image, so carrying that computed default through will
    corrupt ordinary PNG/SVG backgrounds.
    """
    if not any(prop.startswith("-unity-slice-") for prop in seen):
        return

    has_border_image_source = False
    for prop, value in normalized_decls:
        p = prop.lower()
        v = value.strip().lower()
        if p == "border-image" and re.search(r"\b(url|resource)\s*\(", value, re.IGNORECASE):
            has_border_image_source = True
            break
        if p == "border-image-source" and v not in ("", "none", "initial", "unset"):
            if re.search(r"\b(url|resource)\s*\(", value, re.IGNORECASE):
                has_border_image_source = True
                break

    if has_border_image_source:
        return

    for prop in (
        "-unity-slice-top",
        "-unity-slice-right",
        "-unity-slice-bottom",
        "-unity-slice-left",
    ):
        seen.pop(prop, None)


def _inflate_px_dimension(seen: dict[str, str], prop: str, delta: float) -> None:
    base = _length_px(seen.get(prop))
    if base is None:
        return
    seen[prop] = _format_px(base + delta)


def _box_side_px(seen: dict[str, str], family: str, side: str) -> float:
    longhand_prop = f"border-{side}-width" if family == "border-width" else f"{family}-{side}"
    longhand = seen.get(longhand_prop)
    parsed = _length_px(longhand)
    if parsed is not None:
        return parsed

    shorthand = seen.get(family)
    if not shorthand:
        return 0.0
    expanded = _expand_box(shorthand)
    if not expanded:
        return _length_px(shorthand) or 0.0
    top, right, bottom, left = expanded
    value = {
        "top": top,
        "right": right,
        "bottom": bottom,
        "left": left,
    }[side]
    return _length_px(value) or 0.0


_PX_LENGTH_RE = re.compile(r"^(-?\d*\.?\d+)(?:px)?$")


def _length_px(value: str | None) -> float | None:
    if value is None:
        return None
    raw = value.strip().lower()
    if raw in ("", "auto", "none", "normal"):
        return None
    m = _PX_LENGTH_RE.match(raw)
    if not m:
        return None
    try:
        return float(m.group(1))
    except ValueError:
        return None


def _format_px(value: float) -> str:
    if _near_zero(value):
        return "0"
    return f"{value:g}px"


_NATIVE_FILTER_FUNCS = {
    "blur",
    "grayscale",
    "invert",
    "opacity",
    "sepia",
    "tint",
    "hue-rotate",
    "contrast",
    "filter",
}

_ODDGAMES_PACKAGE_ID = "au.com.oddgames.html2uxml"
_ODDGAMES_FILTER_ASSET_ROOT = f"Packages/{_ODDGAMES_PACKAGE_ID}/Runtime/Filters"


def _oddgames_filter_asset(name: str) -> str:
    return f'{_ODDGAMES_FILTER_ASSET_ROOT}/{name}.asset'


def _map_filter(value: str, warnings: list[str]) -> list[tuple[str, str]] | None:
    """Map CSS filter functions to Unity 6.4 native filters where possible.

    Unity 6.4 supports most common filter functions natively but still omits
    CSS `drop-shadow()`, so that one remains routed through Html2UxmlPanel.
    Brightness/saturation route to package custom filter assets because Unity
    6.4 doesn't ship those CSS functions as built-ins.
    """
    funcs = _parse_function_list(value)
    if funcs is None:
        warnings.append(f"filter: {value} -- unsupported filter syntax")
        return None
    out: list[tuple[str, str]] = []
    native_parts: list[str] = []
    unsupported: list[str] = []
    drop_shadow_records: list[str] = []
    used_custom_assets = False
    for name, text, body in funcs:
        low = name.lower()
        if low == "drop-shadow":
            shadow = _parse_box_shadow(body)
            if shadow:
                ox, oy, blur, color = shadow
                if len(drop_shadow_records) < 8:
                    drop_shadow_records.append(_shadow_record("outset", ox, oy, blur, "0", color))
                else:
                    warnings.append("filter: extra drop-shadow() functions ignored after 8 layers")
            else:
                warnings.append(f"filter: drop-shadow({body}) -- complex shadow not bridged")
        elif low in _NATIVE_FILTER_FUNCS:
            native_parts.append(text)
        elif low == "brightness":
            brightness = _parse_filter_amount(body)
            if brightness is None:
                unsupported.append(name)
                continue
            native_parts.append(
                f'filter("{_oddgames_filter_asset("ODDGamesColorAdjust")}" {brightness:g} 1)'
            )
            used_custom_assets = True
        elif low == "saturate":
            saturation = _parse_filter_amount(body)
            if saturation is None:
                unsupported.append(name)
                continue
            native_parts.append(
                f'filter("{_oddgames_filter_asset("ODDGamesColorAdjust")}" 1 {saturation:g})'
            )
            used_custom_assets = True
        else:
            unsupported.append(name)
    out.extend(_emit_shadow_props(drop_shadow_records))
    if native_parts:
        out.append(("filter", " ".join(native_parts)))
    if used_custom_assets:
        warnings.append(
            "filter uses ODDGames package custom filter assets; install "
            "au.com.oddgames.html2uxml before loading the USS"
        )
    if unsupported:
        warnings.append(
            "filter functions not supported by Unity 6.4 USS, dropped: "
            + ", ".join(unsupported)
        )
    return out or None


def _map_backdrop_filter(value: str, warnings: list[str]) -> list[tuple[str, str]] | None:
    """Approximate browser backdrop-filter with Unity 6.4 element filters.

    UI Toolkit filters process the element subtree, not the pixels already
    behind it. Keeping native blur/color filters is still useful for source
    that authors a separate translucent glass layer.
    """
    funcs = _parse_function_list(value)
    if funcs is None:
        warnings.append(f"backdrop-filter: {value} -- unsupported filter syntax")
        return None
    native_parts: list[str] = []
    unsupported: list[str] = []
    used_custom_assets = False
    for name, text, _body in funcs:
        low = name.lower()
        if low in _NATIVE_FILTER_FUNCS:
            native_parts.append(text)
        elif low == "brightness":
            brightness = _parse_filter_amount(_body)
            if brightness is None:
                unsupported.append(name)
                continue
            native_parts.append(
                f'filter("{_oddgames_filter_asset("ODDGamesColorAdjust")}" {brightness:g} 1)'
            )
            used_custom_assets = True
        elif low == "saturate":
            saturation = _parse_filter_amount(_body)
            if saturation is None:
                unsupported.append(name)
                continue
            native_parts.append(
                f'filter("{_oddgames_filter_asset("ODDGamesColorAdjust")}" 1 {saturation:g})'
            )
            used_custom_assets = True
        else:
            unsupported.append(name)
    if unsupported:
        warnings.append(
            "backdrop-filter functions not supported by Unity 6.4 USS, dropped: "
            + ", ".join(unsupported)
        )
    if not native_parts:
        return None
    if used_custom_assets:
        warnings.append(
            "backdrop-filter uses ODDGames package custom filter assets; install "
            "au.com.oddgames.html2uxml before loading the USS"
        )
    warnings.append(
        "backdrop-filter approximated as filter; Unity blurs the element subtree, not the backdrop"
    )
    return [("filter", " ".join(native_parts))]


def _parse_filter_amount(value: str) -> float | None:
    v = value.strip().lower()
    if not v:
        return None
    try:
        if v.endswith("%"):
            return float(v[:-1].strip()) / 100.0
        return float(v)
    except ValueError:
        return None


def _parse_function_list(value: str) -> list[tuple[str, str, str]] | None:
    funcs: list[tuple[str, str, str]] = []
    i = 0
    n = len(value)
    while i < n:
        while i < n and value[i].isspace():
            i += 1
        if i >= n:
            break
        name_start = i
        while i < n and (value[i].isalpha() or value[i] == "-"):
            i += 1
        name = value[name_start:i]
        while i < n and value[i].isspace():
            i += 1
        if not name or i >= n or value[i] != "(":
            return None
        open_idx = i
        i += 1
        body_start = i
        depth = 1
        while i < n and depth > 0:
            if value[i] == "(":
                depth += 1
            elif value[i] == ")":
                depth -= 1
            i += 1
        if depth != 0:
            return None
        body = value[body_start:i - 1]
        funcs.append((name, value[name_start:i], body))
    return funcs


def _expand_box(value: str) -> tuple[str, str, str, str] | None:
    """CSS 1-4 token shorthand -> (top, right, bottom, left)."""
    parts = value.split()
    if len(parts) == 1:
        v = parts[0]
        return v, v, v, v
    if len(parts) == 2:
        v, h = parts
        return v, h, v, h
    if len(parts) == 3:
        t, h, b = parts
        return t, h, b, h
    if len(parts) == 4:
        return parts[0], parts[1], parts[2], parts[3]
    return None
