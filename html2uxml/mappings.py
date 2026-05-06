"""HTML element and CSS property mappings to UXML / USS.

USS is CSS-like but:
  - layout is Yoga-flexbox (no `block`, `inline`, `grid`, `float`).
  - text alignment uses `-unity-text-align` not `text-align`.
  - font weight/style is folded into `-unity-font-style` (normal|bold|italic|bold-and-italic).
  - `font-family` requires a Unity font asset, set via `-unity-font` / `-unity-font-definition`.
  - shorthand `border`, `background`, `font` aren't supported.
  - `box-shadow`, `clip-path`, `filter`, `backdrop-filter`, `mask`, vendor prefixes,
    keyframe animations, gradient backgrounds, and named cursors aren't supported.
  - `box-sizing` doesn't exist; padding/border are inside the box.
  - percentages on `transform: translate(...)` work; `translateX(50%)` becomes
    `translate: 50% 0;`.
"""
from __future__ import annotations

import re
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

    "img":     ("ui:Image", {}, None),
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
        return "ui:ColorField", {}, None
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
    "backdrop-filter",
    "mask", "mask-image", "mask-type", "animation", "appearance",
    "float", "clear", "box-sizing", "user-select", "pointer-events",
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
    "word-break", "overflow-wrap", "hyphens", "line-height",
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
    # Collected font-style flags that combine into -unity-font-style.
    is_bold = False
    is_italic = False
    has_font_style_decl = False

    input_props = {p.lower() for p, _ in decls}
    # CSS default for `display: flex` is `flex-direction: row`. USS default is
    # `column`. Backfill row when the author relied on the CSS default and didn't
    # declare flex-direction explicitly.
    needs_row_default = (
        "display" in input_props
        and "flex-direction" not in input_props
        and any(p.lower() == "display" and v.strip().lower() in ("flex", "inline-flex")
                for p, v in decls)
    )

    for prop, value in decls:
        v = _coerce_units(value.strip())
        v = _coerce_modern_color(v)
        if not v or v.lower() in SKIP_VALUES:
            continue
        # auto/normal often mean "no override" in computed-style dumps
        if v.lower() in ("auto", "normal", "none") and prop not in (
            "display", "overflow", "white-space", "visibility",
            "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
            "width", "height", "min-width", "max-width", "min-height", "max-height",
            "left", "right", "top", "bottom",
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
    final = list(seen.items())
    return MapResult(decls=final, warnings=warnings)


_LEN_RE = re.compile(r"^-?\d*\.?\d+(px|em|rem|%|vw|vh|)$")
_GRAD_RE = re.compile(r"\b(linear|radial|conic|repeating-linear|repeating-radial)-gradient\s*\(",
                      re.IGNORECASE)


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
    def repl(m: re.Match) -> str:
        r, g, b, a = m.group(1), m.group(2), m.group(3), m.group(4)
        if a is None:
            return f"rgb({r}, {g}, {b})"
        return f"rgba({r}, {g}, {b}, {a})"
    return _MODERN_RGB_RE.sub(repl, value)


_BOX_SHADOW_PARTS_RE = re.compile(
    r"""
    \s*
    (?P<color>(?:rgba?|hsla?)\([^)]*\)|\#[0-9a-fA-F]{3,8}|[a-zA-Z]+)?
    \s*
    (?P<rest>[^,]*)
    """,
    re.VERBOSE,
)


def _parse_box_shadow(value: str):
    """Parse a single `<color> <ox> <oy> <blur>` (any order) shadow.

    Returns (offset-x, offset-y, blur, color) or None for unsupported shapes.
    Only the first shadow is taken if multiple comma-separated shadows are
    present (USS bridge supports a single shadow per element).
    """
    # Take the first comma-separated shadow at the *top* level (skip commas
    # inside parens like rgba(...)).
    depth = 0
    first_end = len(value)
    for i, c in enumerate(value):
        if c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
        elif c == "," and depth == 0:
            first_end = i
            break
    first = value[:first_end]
    if "inset" in first.lower():
        return None  # inset shadows aren't supported by the bridge.
    color = _extract_color(first)
    if color is None:
        return None
    # Strip the color and any other function calls before scanning lengths,
    # so we don't pick up channel digits from rgba()/hsla().
    scrubbed = re.sub(r"(rgba?|hsla?)\s*\([^)]*\)", " ", first, flags=re.IGNORECASE)
    scrubbed = re.sub(r"#[0-9a-fA-F]{3,8}\b", " ", scrubbed)
    nums = re.findall(r"-?\d*\.?\d+(?:px|em|rem|%)?", scrubbed)
    nums = [n for n in nums if n.strip()]
    if len(nums) < 2:
        return None
    ox = _strip_unit(nums[0])
    oy = _strip_unit(nums[1])
    blur = _strip_unit(nums[2]) if len(nums) >= 3 else "0"
    return ox, oy, blur, color


def _ensure_unit(n: str) -> str:
    return n if n.endswith(("px", "em", "rem", "%")) or n in ("0",) else f"{n}px"


def _strip_unit(n: str) -> str:
    """Drop length unit so the value parses as a plain number.
    Used for bridge custom props typed as float (CustomStyleProperty<float>)."""
    for u in ("px", "rem", "em", "%"):
        if n.endswith(u):
            return n[: -len(u)] or "0"
    return n


def _map_one(prop: str, value: str, warnings: list[str]) -> list[tuple[str, str]] | None:
    if prop.startswith("-webkit-") or prop.startswith("-moz-") or prop.startswith("-ms-"):
        return None
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
        return [("-unity-text-align", TEXT_ALIGN_MAP.get(v, "middle-left"))]

    # font weight / style fold into -unity-font-style
    if prop == "font-weight":
        v = value.lower()
        try:
            n = int(v)
        except ValueError:
            n = None
        if v in ("bold", "bolder") or (n is not None and n >= 600):
            return [("__bold__", "1")]
        return [("__font-normal__", "0")]
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
        return [("--gg-font-family", f'"{first}"')]

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
        out: list[tuple[str, str]] = []
        grad = _find_gradient(value)
        if grad:
            start, end, text = grad
            out.append(("--gg-gradient", _quote_for_uss(text)))
            scrubbed = (value[:start] + value[end:]).strip(" ,")
        else:
            scrubbed = value
        color = _extract_color(scrubbed) if scrubbed else None
        if color:
            out.append(("background-color", color))
        url_m = re.search(r"url\([^)]+\)|resource\([^)]+\)", scrubbed or "")
        if url_m:
            out.append(("background-image", url_m.group(0)))
        return out or None
    if prop == "background-image":
        grad = _find_gradient(value)
        if grad:
            return [("--gg-gradient", _quote_for_uss(grad[2]))]
        return [("background-image", value)]
    if prop == "background-color":
        return [("background-color", value)]

    # border shorthand: split into width + color (style is always "solid" in USS)
    if prop == "border":
        return _split_border(value, sides=("top", "right", "bottom", "left"))
    if prop in ("border-top", "border-right", "border-bottom", "border-left"):
        side = prop.split("-", 1)[1]
        return _split_border(value, sides=(side,))

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

    # box-shadow: not in USS proper; emit custom props for the bridge kit.
    if prop == "box-shadow":
        parsed = _parse_box_shadow(value)
        if not parsed:
            warnings.append(f"box-shadow: {value} -- complex/multi shadows not bridged")
            return None
        ox, oy, blur, color = parsed
        return [
            ("--gg-shadow-offset-x", ox),
            ("--gg-shadow-offset-y", oy),
            ("--gg-shadow-blur", blur),
            ("--gg-shadow-color", color),
        ]

    # transform: USS prefers individual translate/rotate/scale properties.
    if prop == "transform":
        return _split_transform(value, warnings)

    # gap / row-gap / column-gap: USS doesn't support these. Bridge them by
    # emitting --gg-row-gap / --gg-column-gap so BridgeBox can apply margin
    # to direct children at runtime based on the parent's flex-direction.
    if prop == "gap":
        parts = value.split()
        rg = _strip_unit(parts[0])
        cg = _strip_unit(parts[1] if len(parts) > 1 else parts[0])
        return [("--gg-row-gap", rg), ("--gg-column-gap", cg)]
    if prop == "row-gap":
        return [("--gg-row-gap", _strip_unit(value))]
    if prop == "column-gap":
        return [("--gg-column-gap", _strip_unit(value))]

    # inset shorthand -> top/right/bottom/left.
    if prop == "inset":
        sides = _expand_box(value)
        if not sides:
            return None
        top, right, bottom, left = sides
        return [("top", top), ("right", right), ("bottom", bottom), ("left", left)]

    # white-space: USS supports normal, nowrap, pre.
    if prop == "white-space":
        v = value.lower()
        if v in ("normal", "nowrap", "pre"):
            return [("white-space", v)]
        if v in ("pre-wrap", "pre-line", "break-spaces"):
            return [("white-space", "pre")]
        warnings.append(f"white-space: {value} approximated as normal")
        return [("white-space", "normal")]

    # outline: USS has no outline; approximate as border on all sides + warn
    # that outline (unlike border) doesn't normally occupy layout space.
    if prop == "outline":
        warnings.append(f"outline approximated as border (occupies layout space)")
        return _split_border(value, sides=("top", "right", "bottom", "left"))

    # clip-path: bridge polygon() via a custom prop the BridgeBox renders.
    if prop == "clip-path":
        v = value.strip()
        if v.lower().startswith("polygon"):
            return [("--gg-clip-polygon", _quote_for_uss(v))]
        warnings.append(f"clip-path: {value} -- only polygon() is bridged")
        return None

    # filter: only drop-shadow() routes into the box-shadow bridge.
    if prop == "filter":
        body = _extract_call_body(value, "drop-shadow")
        if body is not None:
            shadow = _parse_box_shadow(body)
            if shadow:
                ox, oy, blur, color = shadow
                return [
                    ("--gg-shadow-offset-x", ox),
                    ("--gg-shadow-offset-y", oy),
                    ("--gg-shadow-blur", blur),
                    ("--gg-shadow-color", color),
                ]
        warnings.append(f"filter: {value} -- only drop-shadow() is bridged")
        return None

    # text-overflow: USS supports clip and ellipsis.
    if prop == "text-overflow":
        v = value.lower()
        if v in ("clip", "ellipsis"):
            return [("text-overflow", v)]
        return None

    # text-shadow: USS (newer) accepts the same syntax.
    if prop == "text-shadow":
        return [("text-shadow", value)]

    # opacity, color, and most numeric properties pass through.
    pass_through = {
        "color", "opacity", "background-color",
        "width", "height", "min-width", "min-height", "max-width", "max-height",
        "left", "right", "top", "bottom",
        "padding", "padding-top", "padding-right", "padding-bottom", "padding-left",
        "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
        "border-width", "border-top-width", "border-right-width",
        "border-bottom-width", "border-left-width",
        "border-color", "border-top-color", "border-right-color",
        "border-bottom-color", "border-left-color",
        "border-radius", "border-top-left-radius", "border-top-right-radius",
        "border-bottom-left-radius", "border-bottom-right-radius",
        "flex", "flex-grow", "flex-shrink", "flex-basis", "flex-direction", "flex-wrap",
        "align-items", "align-self", "align-content",
        "justify-content", "justify-self",
        "visibility",
        "letter-spacing", "font-size", "word-spacing",
        "transition", "transition-property",
        "transition-duration", "transition-delay", "transition-timing-function",
        "translate", "rotate", "scale",
        "transform-origin",
    }
    if prop in pass_through:
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
    Bridge kit reads --gg-* as strings; functions like `linear-gradient(...)`
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
            out.append((f"border-{side}-width", width))
        if color is not None:
            out.append((f"border-{side}-color", color))
    return out or None


def _split_transform(value: str, warnings: list[str]) -> list[tuple[str, str]] | None:
    """Map `transform: translateX(50%) rotate(10deg)` to USS individual props."""
    out_translate = ["0", "0"]
    out_rotate = None
    out_scale = None
    has_translate = False
    for fn, args in re.findall(r"([a-zA-Z]+)\s*\(([^)]*)\)", value):
        fn = fn.lower()
        parts = [p.strip() for p in args.split(",")]
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
        elif fn in ("rotate", "rotatez"):
            if parts:
                out_rotate = parts[0]
        elif fn == "scale":
            if len(parts) == 1:
                out_scale = parts[0]
            elif len(parts) >= 2:
                out_scale = f"{parts[0]} {parts[1]}"
        elif fn in ("scalex", "scaley", "rotatex", "rotatey", "translate3d", "matrix",
                    "skew", "skewx", "skewy", "perspective", "matrix3d"):
            warnings.append(f"transform function {fn}() not supported in USS, ignored")
    pairs: list[tuple[str, str]] = []
    if has_translate:
        pairs.append(("translate", f"{out_translate[0]} {out_translate[1]}"))
    if out_rotate is not None:
        pairs.append(("rotate", out_rotate))
    if out_scale is not None:
        pairs.append(("scale", out_scale))
    return pairs or None


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
