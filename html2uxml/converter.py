"""Top-level converter: HTML(+CSS) -> UXML + USS strings."""
from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
import math
import re
import urllib.parse

from .assets import _decode_image_data_uri, _safe_name
from .css_parser import (
    CompoundSelector,
    Selector,
    _parse_declarations,
    parse_css,
    parse_selector,
)
from .html_parser import Node, parse_html
from .mappings import SKIP_TAGS, map_declarations, map_element
from .resolver import (
    _ParsedRule, parse_rules, resolve, ResolvedStyle, selector_matches,
)
import hashlib


@dataclass
class ConvertResult:
    uxml: str
    uss: str
    warnings: list[str] = field(default_factory=list)
    stats: "ConvertStats | None" = None
    svg_files: list[tuple[str, str]] = field(default_factory=list)  # (filename, raw svg)
    text_gradient_files: list[tuple[str, str]] = field(default_factory=list)  # (filename, json body)
    data_uri_files: list[tuple[str, bytes]] = field(default_factory=list)  # (filename, raw bytes)
    font_usages: list["FontUsage"] = field(default_factory=list)


@dataclass(frozen=True)
class FontUsage:
    family: str
    weight: int = 400
    italic: bool = False
    dynamic: bool = False
    text: str = ""


@dataclass
class ConvertStats:
    elements: int = 0
    labels: int = 0
    buttons: int = 0
    images: int = 0
    html2uxml_panels: int = 0
    inline_overrides: int = 0     # number of h2u-N rules emitted
    css_class_rules: int = 0      # number of original CSS rules emitted
    uss_rules: int = 0            # final rule count
    bridged_props: dict = field(default_factory=dict)
    dropped_props: dict = field(default_factory=dict)
    skipped_at_rules: int = 0     # @media/@keyframes/@supports/@import


@dataclass
class _OverlayClone:
    node: Node
    z: int
    index: int
    decls: list[tuple[str, str]]


@dataclass
class _AnimationFrame:
    offset: float
    decls: list[tuple[str, str]]


@dataclass
class _AnimationKeyframes:
    name: str
    frames: list[_AnimationFrame] = field(default_factory=list)


def convert(
    html: str,
    extra_css: str = "",
    *,
    uss_filename: str = "styles.uss",
    base_dir: Path | None = None,
    select: str | None = None,
    svg_assets_subdir: str = "Images",
    text_gradients_assets_subdir: str = "TextGradients",
) -> ConvertResult:
    """Convert HTML+CSS to UXML+USS.

    `select` (a CSS selector) restricts emission to the first matching
    subtree. The full document's CSS is still parsed so rules can match
    descendants, but only rules with at least one matching node in the
    subtree are emitted to USS.
    """
    parsed = parse_html(html)
    css_chunks = list(parsed.inline_styles)
    if base_dir is not None:
        for href in parsed.linked_stylesheets:
            try:
                css_chunks.append((base_dir / href).read_text(encoding="utf-8"))
            except OSError:
                pass
    if extra_css:
        css_chunks.append(extra_css)
    css_text = "\n".join(css_chunks)
    rules = parse_css(css_text)
    parsed_rules = parse_rules(rules)

    # Subtree selection: find the first matching node, wrap it in a synthetic
    # root so the converter renders that element AND its descendants. Browser
    # screens often mount global HUD controls as positioned siblings of the
    # selected screen; include those when they are clearly viewport overlays.
    selection_warnings: list[str] = []
    if select:
        match = _find_first_match_with_ancestors(parsed.root, select)
        if match is None:
            selection_warnings.append(f"selector matched no element: {select!r}")
        else:
            target, ancestors = match
            full_resolved = resolve(parsed.root, parsed_rules)
            selected_nodes, overlay_warnings = _selection_nodes_with_overlay_siblings(
                target, ancestors, full_resolved
            )
            selection_warnings.extend(overlay_warnings)
            new_root = Node(tag="__root__")
            if len(selected_nodes) > 1 and ancestors:
                new_root.children = [
                    _selection_overlay_wrapper(target, ancestors[-1], selected_nodes, full_resolved)
                ]
            else:
                new_root.children = selected_nodes
            parsed.root = new_root

    state = _EmitState()
    state.used_bridge = True
    state.animation_keyframes = _extract_animation_keyframes(css_text)
    state.svg_blocks = list(parsed.svg_blocks)
    state.svg_assets_subdir = svg_assets_subdir.strip("/\\") or "Images"
    state.text_gradients_assets_subdir = text_gradients_assets_subdir.strip("/\\") or "TextGradients"
    state.warnings.extend(selection_warnings)

    # Strip CSS that drives a text-gradient (background-clip:text + linear-gradient
    # + color:transparent) from parsed rules BEFORE resolve/bridge-flag passes,
    # otherwise the host element gets promoted to Html2UxmlPanel for a bg
    # gradient we are about to redirect onto the Label's rich-text content.
    _preprocess_text_gradients(parsed_rules, state)

    resolved = resolve(parsed.root, parsed_rules)
    rule_bridge_flags = _compute_rule_bridge_flags(parsed_rules)

    if _tree_contains_button(parsed.root):
        _emit_button_reset(state)

    # Prune USS to selectors that actually hit something in the (sub)tree.
    used_selectors: set = set()
    for rs in resolved.values():
        used_selectors.update(rs.matched_selectors)
    _emit_css_rules(parsed_rules, state, allowed_selectors=used_selectors)

    body_xml = _emit_node_children(parsed.root, resolved, state, rule_bridge_flags, indent=2)
    uxml = _wrap_uxml(body_xml, uss_filename, with_bridge=state.used_bridge)
    uss = _emit_uss(state)
    state.stats.uss_rules = len(state.uss_order)
    return ConvertResult(
        uxml=uxml, uss=uss, warnings=state.warnings, stats=state.stats,
        svg_files=state.svg_files,
        text_gradient_files=state.text_gradient_files,
        data_uri_files=state.data_uri_files,
        font_usages=state.font_usages,
    )


def _find_first_match(root: Node, selector_raw: str) -> Node | None:
    match = _find_first_match_with_ancestors(root, selector_raw)
    return match[0] if match is not None else None


def _find_first_match_with_ancestors(root: Node, selector_raw: str) -> tuple[Node, list[Node]] | None:
    sel = parse_selector(selector_raw)
    if sel is None:
        return None
    found: list[tuple[Node, list[Node]]] = []

    def walk(node: Node, ancestors: list[Node]) -> None:
        if found:
            return
        if not node.is_text and node.tag != "__root__":
            if ancestors:
                parent = ancestors[-1]
                sibs = [c for c in parent.children if not c.is_text]
                try:
                    sib_idx = sibs.index(node)
                except ValueError:
                    sib_idx = -1
            else:
                sibs, sib_idx = [node], 0
            if selector_matches(node, sel, ancestors, sib_idx, sibs):
                found.append((node, ancestors))
                return
        for child in node.children:
            if child.is_text:
                continue
            walk(child, ancestors + [node])

    walk(root, [])
    return found[0] if found else None


def _selection_nodes_with_overlay_siblings(
    target: Node,
    ancestors: list[Node],
    resolved: dict[int, ResolvedStyle],
) -> tuple[list[Node], list[str]]:
    nodes: list[Node] = [target]
    warnings: list[str] = []
    if not ancestors:
        return nodes, warnings

    parent = ancestors[-1]
    siblings = [child for child in parent.children if not child.is_text]
    if target not in siblings:
        return nodes, warnings

    seen = {id(target)}
    for sibling in siblings:
        if id(sibling) in seen:
            continue
        if not _is_selected_screen_overlay_sibling(sibling, resolved):
            continue
        seen.add(id(sibling))
        nodes.append(sibling)
        warnings.append(
            "selector included positioned overlay sibling outside selected subtree: "
            + _overlay_source_label(sibling)
        )
    return nodes, warnings


def _selection_overlay_wrapper(
    target: Node,
    parent: Node,
    children: list[Node],
    resolved: dict[int, ResolvedStyle],
) -> Node:
    wrapper_style = _selection_overlay_wrapper_style(target, parent, resolved)
    return Node(
        tag="div",
        attrs={
            "class": "h2u-selection-viewport",
            "style": wrapper_style,
        },
        children=children,
    )


def _selection_overlay_wrapper_style(
    target: Node,
    parent: Node,
    resolved: dict[int, ResolvedStyle],
) -> str:
    parent_style = resolved.get(id(parent))
    target_style = resolved.get(id(target))
    decls: list[tuple[str, str]] = [
        ("position", "relative"),
        ("overflow", "hidden"),
    ]
    for prop in ("width", "height"):
        value = (
            _inline_or_resolved_value(parent, parent_style, prop)
            or _inline_or_resolved_value(target, target_style, prop)
        )
        if value and value.strip().lower() not in ("auto", "initial", "inherit", "unset"):
            decls.append((prop, value))
    return "; ".join(f"{prop}: {value}" for prop, value in decls)


def _is_selected_screen_overlay_sibling(
    node: Node,
    resolved: dict[int, ResolvedStyle],
) -> bool:
    if node.is_text:
        return False
    style = resolved.get(id(node))
    display = (_inline_or_resolved_value(node, style, "display") or "").strip().lower()
    if display == "none":
        return False
    visibility = (_inline_or_resolved_value(node, style, "visibility") or "").strip().lower()
    if visibility == "hidden":
        return False
    opacity = (_inline_or_resolved_value(node, style, "opacity") or "").strip().lower()
    if opacity in ("0", "0.0"):
        return False

    position = (_inline_or_resolved_value(node, style, "position") or "static").strip().lower()
    if position not in ("absolute", "fixed", "sticky"):
        return False
    if position != "fixed":
        raw_z = _inline_or_resolved_value(node, style, "z-index")
        z = _parse_z_index(raw_z) if raw_z is not None else None
        if z is None or z < 1:
            return False

    return any(
        _inline_or_resolved_value(node, style, prop) is not None
        for prop in ("top", "right", "bottom", "left")
    )


# ---------------------------------------------------------------------------
# Emit state
# ---------------------------------------------------------------------------


@dataclass
class _EmitState:
    uss_rules: dict[str, list[tuple[str, str]]] = field(default_factory=dict)
    uss_order: list[str] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)
    used_bridge: bool = False
    stats: ConvertStats = field(default_factory=ConvertStats)
    svg_files: list[tuple[str, str]] = field(default_factory=list)
    svg_blocks: list[str] = field(default_factory=list)
    svg_assets_subdir: str = "Images"
    data_uri_files: list[tuple[str, bytes]] = field(default_factory=list)
    data_uri_dedup: dict[str, str] = field(default_factory=dict)  # sha1(data) -> filename
    data_uri_name_counts: dict[str, int] = field(default_factory=dict)  # slug -> highest used count
    synthetic_by_selector: dict[str, list[tuple[str, str]]] = field(default_factory=dict)
    text_gradient_by_selector: dict[str, str] = field(default_factory=dict)
    text_gradient_files: list[tuple[str, str]] = field(default_factory=list)
    text_gradient_dedup: dict[tuple, str] = field(default_factory=dict)
    text_gradients_assets_subdir: str = "TextGradients"
    generated_class_cache: dict[tuple[tuple[str, str], ...], str] = field(default_factory=dict)
    svg_warning_emitted: bool = False
    svg_name_counts: dict[str, int] = field(default_factory=dict)
    animation_keyframes: dict[str, _AnimationKeyframes] = field(default_factory=dict)
    font_usages: list[FontUsage] = field(default_factory=list)
    # HTML tags whose CSS rules were rewritten to `.h2u-tag-<tag>` selectors.
    # Used by _emit_node to know which elements need the matching class.
    tagged_tags: set = field(default_factory=set)
    _gen_counter: int = 0
    _name_class_counts: dict[str, int] = field(default_factory=dict)

    def add_rule(self, selector: str, decls: list[tuple[str, str]]) -> None:
        if not decls:
            return
        if selector in self.uss_rules:
            self.uss_rules[selector].extend(decls)
        else:
            self.uss_rules[selector] = list(decls)
            self.uss_order.append(selector)

    def gen_class(self, name_hint: str | None = None) -> str:
        # Prefer a slug derived from the element's effective name
        # (`tap-to-ready-up`, `finals`, etc.) so the generated USS class
        # is recognisable in the inspector. Fall back to a counter when
        # no usable hint is provided.
        slug = _slug_for_class(name_hint)
        if slug:
            base = f"h2u-{slug}"
            count = self._name_class_counts.get(base, 0) + 1
            self._name_class_counts[base] = count
            return base if count == 1 else f"{base}-{count}"
        self._gen_counter += 1
        return f"h2u-{self._gen_counter}"

    def class_for_generated_decls(
        self,
        decls: list[tuple[str, str]],
        name_hint: str | None = None,
        *,
        cache: bool = True,
    ) -> tuple[str, bool]:
        key = _decl_cache_key(decls)
        if cache and key in self.generated_class_cache:
            return self.generated_class_cache[key], False
        cls = self.gen_class(name_hint)
        if cache:
            self.generated_class_cache[key] = cls
        self.add_rule(f".{cls}", decls)
        return cls, True

    def svg_filename(self, node: Node, parent: Node | None) -> str:
        slug = _svg_context_slug(node, parent)
        count = self.svg_name_counts.get(slug, 0) + 1
        self.svg_name_counts[slug] = count
        suffix = "" if count == 1 else f"-{count}"
        return f"{slug}{suffix}.svg"

    def intern_data_uri(self, data_uri: str, slug: str) -> str | None:
        """Decode a `data:image/*` URI and register the bytes for later writing.

        Returns the project-relative filename (without subdir prefix) on
        success, or None if the URI is unsupported and should be left as-is.
        """
        decoded = _decode_image_data_uri(data_uri)
        if decoded is None:
            return None
        data, ext = decoded
        sha = hashlib.sha1(data).hexdigest()
        existing = self.data_uri_dedup.get(sha)
        if existing is not None:
            return existing
        base_slug = _safe_name(slug) or "embedded"
        if base_slug == "embedded":
            # No semantic source for the name; tag with the content hash so
            # the file is at least identified by its bytes.
            base_slug = f"embedded-{sha[:8]}"
        count = self.data_uri_name_counts.get(base_slug, 0) + 1
        self.data_uri_name_counts[base_slug] = count
        suffix = "" if count == 1 else f"-{count}"
        filename = _safe_name(f"{base_slug}{suffix}{ext}")
        self.data_uri_dedup[sha] = filename
        self.data_uri_files.append((filename, data))
        return filename

    def warn_once(self, message: str) -> None:
        if message not in self.warnings:
            self.warnings.append(message)

    def record_font_text(
        self,
        text: str,
        text_raw: dict[str, str] | None,
        *,
        dynamic: bool = False,
    ) -> None:
        usage = _font_usage_from_text(text, text_raw or {}, dynamic=dynamic)
        if usage is not None:
            self.font_usages.append(usage)


def _decl_cache_key(decls: list[tuple[str, str]]) -> tuple[tuple[str, str], ...]:
    dedup: dict[str, str] = {}
    for k, v in decls:
        dedup[k] = v
    return tuple(sorted(dedup.items()))


# ---------------------------------------------------------------------------
# CSS rule emission (originally-named selectors)
# ---------------------------------------------------------------------------


# USS recognises a small set of element-type selectors (Unity built-in
# controls plus the Html2Uxml* runtime types). Every other tag in user CSS
# (body, section, h1..h6, nav, p, ul, li, table, …) must be rewritten to a
# class selector — `.h2u-tag-<tag>` — and the matching class added to each
# emitted UXML node. Without this rewrite, plain HTML pages whose CSS uses
# element-name selectors get zero styling in Unity.
_UNITY_USS_TYPES_LOWER = frozenset({
    "button", "toggle", "label", "scrollview", "textfield", "floatfield",
    "integerfield", "slider", "sliderint", "image", "visualelement",
    "radiobutton", "radiobuttongroup", "dropdownfield", "progressbar",
    "foldout", "groupbox", "repeatbutton", "box", "listview", "treeview",
    "minmaxslider", "vector2field", "vector3field", "vector4field",
    "boundsfield", "rectfield", "colorfield", "objectfield", "enumfield",
    "maskfield", "longfield", "doublefield", "helpbox", "tabview", "tab",
    "html2uxmlpanel", "html2uxmlelement", "html2uxmlbutton", "html2uxmllabel",
    "html2uxmlscrollview", "html2uxmltextfield", "html2uxmlfloatfield",
    "html2uxmlslider", "html2uxmltoggle", "html2uxmlradiobutton",
    "html2uxmldropdownfield", "html2uxmlprogressbar", "html2uxmlfoldout",
    "html2uxmlgroupbox", "html2uxmlscaleroot",
})


def _is_unity_uss_type(tag: str) -> bool:
    return bool(tag) and tag != "*" and tag.lower() in _UNITY_USS_TYPES_LOWER


def _h2u_tag_class(tag: str) -> str:
    return f"h2u-tag-{tag.lower()}"


def _render_compound_for_uss(comp: "CompoundSelector",
                              tagged_tags: set | None = None) -> str:
    parts: list[str] = []
    if comp.tag and comp.tag != "*":
        if _is_unity_uss_type(comp.tag):
            parts.append(comp.tag)
        else:
            parts.append("." + _h2u_tag_class(comp.tag))
            if tagged_tags is not None:
                tagged_tags.add(comp.tag.lower())
    if comp.id:
        parts.append(f"#{comp.id}")
    for c in comp.classes:
        parts.append(f".{c}")
    for ps in comp.pseudo:
        parts.append(ps if ps.startswith(":") else f":{ps}")
    return "".join(parts) or "*"


def _rewrite_selector_for_uss(sel: "Selector",
                               tagged_tags: set | None = None) -> str:
    if not sel.chain:
        return sel.raw
    out: list[str] = []
    for i, (combinator, comp) in enumerate(sel.chain):
        if i > 0:
            out.append(" " if combinator == " " else f" {combinator} ")
        out.append(_render_compound_for_uss(comp, tagged_tags))
    return "".join(out)


def _emit_css_rules(parsed_rules: list[_ParsedRule], state: _EmitState,
                    *, allowed_selectors: set | None = None) -> None:
    for rule in parsed_rules:
        emit_selectors = [
            s for s in rule.selectors
            if (
                (allowed_selectors is None or s.raw in allowed_selectors)
                and not s.has_unsupported_features()
            )
        ]
        # Drop the rule entirely if none of its selectors hit the (sub)tree.
        if not emit_selectors:
            continue
        mapped = map_declarations([(d.prop, d.value) for d in rule.declarations])
        _record_warnings(state, mapped.warnings)
        animation_decls = _animation_custom_decls(
            [(d.prop, d.value) for d in rule.declarations],
            state,
        )
        if not mapped.decls and not animation_decls:
            continue
        real_decls, synth_decls = _split_synthetic_decls(mapped.decls)
        if animation_decls:
            real_decls = _combine_generated_decls(real_decls, animation_decls)
        for k, _ in real_decls:
            if _requires_bridge_prop(k):
                state.stats.bridged_props[k] = state.stats.bridged_props.get(k, 0) + 1
                state.used_bridge = True
        for sel in emit_selectors:
            uss_selector = _rewrite_selector_for_uss(sel, state.tagged_tags)
            if real_decls:
                # Resolve any data: URIs in the rule body using a slug derived
                # from the selector. Each selector gets its own copy because
                # the slug differs per rule.
                rule_decls = list(real_decls)
                _rewrite_data_uri_decls(rule_decls, state, _selector_slug(sel.raw))
                state.add_rule(uss_selector, rule_decls)
                state.stats.css_class_rules += 1
            if synth_decls:
                state.synthetic_by_selector.setdefault(sel.raw, []).extend(synth_decls)


# ---------------------------------------------------------------------------
# Text-gradient detection
#
# CSS pattern:
#   background-image: linear-gradient(...);  /* or shorthand */
#   -webkit-background-clip: text;           /* or background-clip: text */
#   color: transparent;
# is bridged to Unity TextCore by emitting a TextColorGradient sidecar JSON
# the bridge package materializes into a real .asset, plus wrapping the
# Label text in <gradient="name">...</gradient> rich-text at emit time.
# Conflicting bg props are stripped from the rule so the panel does not also
# paint the gradient as a background.
# ---------------------------------------------------------------------------


def _preprocess_text_gradients(parsed_rules: list[_ParsedRule], state: _EmitState) -> None:
    from .css_parser import Declaration as _Declaration
    for rule in parsed_rules:
        decls_in = [(d.prop, d.value) for d in rule.declarations]
        decls_out, gradient_name = _extract_text_gradient(decls_in, state)
        if gradient_name is None:
            continue
        for sel in rule.selectors:
            state.text_gradient_by_selector[sel.raw] = gradient_name
        original = list(rule.declarations)
        out_set = {(p, v) for p, v in decls_out}
        new_decls: list = []
        for d in original:
            if (d.prop, d.value) in out_set:
                new_decls.append(d)
                out_set.discard((d.prop, d.value))
        rule.declarations = new_decls


def _extract_text_gradient(
    decls: list[tuple[str, str]],
    state: _EmitState,
) -> tuple[list[tuple[str, str]], str | None]:
    bg_clip_text = False
    for prop, value in decls:
        if prop.lower() in ("background-clip", "-webkit-background-clip"):
            if value.strip().lower() == "text":
                bg_clip_text = True
                break
    if not bg_clip_text:
        return decls, None
    grad_text = _find_text_linear_gradient(decls)
    if grad_text is None:
        return decls, None
    parsed = _parse_text_gradient_for_text(grad_text)
    if parsed is None:
        return decls, None
    gradient_name = _register_text_gradient(state, parsed)
    return _strip_text_gradient_decls(decls), gradient_name


def _find_text_linear_gradient(decls: list[tuple[str, str]]) -> str | None:
    for prop, value in decls:
        p = prop.lower()
        if p in ("background", "background-image"):
            grad = _find_first_linear_gradient(value)
            if grad is not None:
                return grad
    return None


def _find_first_linear_gradient(value: str) -> str | None:
    s = value.strip()
    low = s.lower()
    idx = low.find("linear-gradient(")
    if idx < 0:
        idx = low.find("repeating-linear-gradient(")
    if idx < 0:
        return None
    depth = 0
    start = idx
    open_paren = s.find("(", idx)
    if open_paren < 0:
        return None
    j = open_paren
    while j < len(s):
        c = s[j]
        if c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
            if depth == 0:
                return s[start:j + 1]
        j += 1
    return None


def _strip_text_gradient_decls(decls: list[tuple[str, str]]) -> list[tuple[str, str]]:
    out: list[tuple[str, str]] = []
    for prop, value in decls:
        p = prop.lower()
        if p in ("background-clip", "-webkit-background-clip"):
            continue
        if p == "color" and value.strip().lower() in ("transparent", "rgba(0, 0, 0, 0)", "rgba(0,0,0,0)"):
            continue
        if p in ("background", "background-image"):
            replaced = _remove_linear_gradient(value)
            if replaced is None:
                continue
            out.append((prop, replaced))
            continue
        out.append((prop, value))
    return out


def _remove_linear_gradient(value: str) -> str | None:
    grad = _find_first_linear_gradient(value)
    if grad is None:
        return value
    s = value.replace(grad, "").strip()
    s = re.sub(r"\s*,\s*,\s*", ", ", s).strip(",").strip()
    return s if s else None


def _parse_text_gradient_for_text(grad: str) -> dict | None:
    open_paren = grad.find("(")
    if open_paren < 0 or not grad.endswith(")"):
        return None
    body = grad[open_paren + 1:-1].strip()
    parts = _split_top_level_args(body)
    if not parts:
        return None
    angle_deg = 180.0
    first = parts[0].strip()
    angle = _parse_gradient_angle(first)
    if angle is not None:
        angle_deg = angle
        stop_strs = parts[1:]
    else:
        stop_strs = parts
    if len(stop_strs) < 2:
        return None
    stops: list[tuple[float, tuple[float, float, float, float]]] = []
    for i, s in enumerate(stop_strs):
        col, pos = _parse_color_stop(s.strip())
        if col is None:
            return None
        if pos is None:
            pos = i / max(1, len(stop_strs) - 1)
        stops.append((pos, col))
    stops.sort(key=lambda x: x[0])
    return {"angle": angle_deg, "stops": stops}


def _split_top_level_args(s: str) -> list[str]:
    out: list[str] = []
    depth = 0
    buf = []
    for c in s:
        if c == "," and depth == 0:
            out.append("".join(buf))
            buf = []
            continue
        if c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
        buf.append(c)
    if buf:
        out.append("".join(buf))
    return out


def _parse_gradient_angle(token: str) -> float | None:
    t = token.strip().lower()
    if t.endswith("deg"):
        try:
            return float(t[:-3].strip())
        except ValueError:
            return None
    if t.endswith("turn"):
        try:
            return float(t[:-4].strip()) * 360.0
        except ValueError:
            return None
    if t.startswith("to "):
        rest = t[3:].strip().replace("  ", " ")
        mapping = {
            "top": 0.0, "bottom": 180.0, "right": 90.0, "left": 270.0,
            "top right": 45.0, "right top": 45.0,
            "bottom right": 135.0, "right bottom": 135.0,
            "bottom left": 225.0, "left bottom": 225.0,
            "top left": 315.0, "left top": 315.0,
        }
        return mapping.get(rest)
    return None


def _parse_color_stop(s: str) -> tuple[tuple[float, float, float, float] | None, float | None]:
    s = s.strip()
    if not s:
        return None, None
    pos: float | None = None
    color_part = s
    parts = s.rsplit(" ", 1)
    if len(parts) == 2 and parts[1].endswith("%"):
        try:
            pos = float(parts[1][:-1]) / 100.0
            color_part = parts[0]
        except ValueError:
            pass
    rgba = _color_to_rgba(color_part.strip())
    return rgba, pos


def _color_to_rgba(value: str) -> tuple[float, float, float, float] | None:
    v = value.strip().lower()
    if not v:
        return None
    if v in ("transparent",):
        return (0.0, 0.0, 0.0, 0.0)
    m = re.fullmatch(r"#([0-9a-f]{3,8})", v)
    if m:
        h = m.group(1)
        if len(h) == 3:
            h = "".join(c * 2 for c in h) + "ff"
        elif len(h) == 4:
            h = "".join(c * 2 for c in h)
        elif len(h) == 6:
            h = h + "ff"
        elif len(h) != 8:
            return None
        return (
            int(h[0:2], 16) / 255.0,
            int(h[2:4], 16) / 255.0,
            int(h[4:6], 16) / 255.0,
            int(h[6:8], 16) / 255.0,
        )
    m = re.fullmatch(r"rgba?\(\s*([^)]+)\)", v)
    if m:
        items = [x.strip() for x in re.split(r"[,/]", m.group(1)) if x.strip()]
        if len(items) < 3:
            return None
        try:
            r = _color_channel(items[0])
            g = _color_channel(items[1])
            b = _color_channel(items[2])
            a = float(items[3]) if len(items) >= 4 else 1.0
            return (r, g, b, a)
        except ValueError:
            return None
    named = {
        "white": (1.0, 1.0, 1.0, 1.0), "black": (0.0, 0.0, 0.0, 1.0),
        "red":   (1.0, 0.0, 0.0, 1.0), "green": (0.0, 0.5, 0.0, 1.0),
        "blue":  (0.0, 0.0, 1.0, 1.0), "yellow":(1.0, 1.0, 0.0, 1.0),
    }
    return named.get(v)


def _color_channel(token: str) -> float:
    token = token.strip()
    if token.endswith("%"):
        return float(token[:-1]) / 100.0
    return float(token) / 255.0


def _sample_gradient(stops: list[tuple[float, tuple[float, float, float, float]]], t: float) -> tuple[float, float, float, float]:
    if not stops:
        return (1.0, 1.0, 1.0, 1.0)
    t = max(0.0, min(1.0, t))
    if t <= stops[0][0]:
        return stops[0][1]
    if t >= stops[-1][0]:
        return stops[-1][1]
    for i in range(len(stops) - 1):
        p0, c0 = stops[i]
        p1, c1 = stops[i + 1]
        if p0 <= t <= p1:
            span = p1 - p0
            k = 0.0 if span <= 1e-6 else (t - p0) / span
            return tuple(c0[j] + (c1[j] - c0[j]) * k for j in range(4))  # type: ignore
    return stops[-1][1]


def _classify_text_gradient(angle_deg: float, stops) -> dict:
    a = angle_deg % 360.0
    # Map CSS angle (0=up, 90=right, 180=down, 270=left) to Unity 4-corner
    # colors. Axis-aligned cases pick endpoint stops; arbitrary angles project
    # each corner onto the gradient line and sample.
    if abs(a - 0.0) < 0.5 or abs(a - 360.0) < 0.5:
        top = stops[-1][1]
        bot = stops[0][1]
        return {"mode": "Vertical", "tl": top, "tr": top, "bl": bot, "br": bot}
    if abs(a - 180.0) < 0.5:
        top = stops[0][1]
        bot = stops[-1][1]
        return {"mode": "Vertical", "tl": top, "tr": top, "bl": bot, "br": bot}
    if abs(a - 90.0) < 0.5:
        left = stops[0][1]
        right = stops[-1][1]
        return {"mode": "Horizontal", "tl": left, "tr": right, "bl": left, "br": right}
    if abs(a - 270.0) < 0.5:
        left = stops[-1][1]
        right = stops[0][1]
        return {"mode": "Horizontal", "tl": left, "tr": right, "bl": left, "br": right}
    rad = math.radians(a)
    dx = math.sin(rad)
    dy = -math.cos(rad)
    corners = {"tl": (-0.5, -0.5), "tr": (0.5, -0.5), "bl": (-0.5, 0.5), "br": (0.5, 0.5)}
    projs = {k: dx * v[0] + dy * v[1] for k, v in corners.items()}
    pmin = min(projs.values())
    pmax = max(projs.values())
    span = pmax - pmin
    out = {}
    for k, p in projs.items():
        t = 0.5 if span <= 1e-6 else (p - pmin) / span
        out[k] = _sample_gradient(stops, t)
    return {"mode": "FourCornersGradient", **out}


def _register_text_gradient(state: _EmitState, parsed: dict) -> str:
    spec = _classify_text_gradient(parsed["angle"], parsed["stops"])
    key = (
        spec["mode"],
        tuple(round(c, 4) for c in spec["tl"]),
        tuple(round(c, 4) for c in spec["tr"]),
        tuple(round(c, 4) for c in spec["bl"]),
        tuple(round(c, 4) for c in spec["br"]),
    )
    if key in state.text_gradient_dedup:
        return state.text_gradient_dedup[key]
    name = f"h2u-tg-{len(state.text_gradient_dedup) + 1}"
    state.text_gradient_dedup[key] = name
    body = _text_gradient_json(name, spec)
    state.text_gradient_files.append((f"{name}.h2utg.json", body))
    return name


def _node_text_gradient_name(style, state: _EmitState) -> str | None:
    if style is None or not state.text_gradient_by_selector:
        return None
    for sel in style.matched_selectors:
        name = state.text_gradient_by_selector.get(sel)
        if name:
            return name
    return None


def _maybe_wrap_text_gradient(text: str, gradient_name: str | None) -> str:
    if not gradient_name or not text:
        return text
    if "<gradient=" in text:
        return text
    return f'<gradient="{gradient_name}">{text}</gradient>'


def _text_gradient_json(name: str, spec: dict) -> str:
    import json
    payload = {
        "name": name,
        "mode": spec["mode"],
        "topLeft":     {"r": spec["tl"][0], "g": spec["tl"][1], "b": spec["tl"][2], "a": spec["tl"][3]},
        "topRight":    {"r": spec["tr"][0], "g": spec["tr"][1], "b": spec["tr"][2], "a": spec["tr"][3]},
        "bottomLeft":  {"r": spec["bl"][0], "g": spec["bl"][1], "b": spec["bl"][2], "a": spec["bl"][3]},
        "bottomRight": {"r": spec["br"][0], "g": spec["br"][1], "b": spec["br"][2], "a": spec["br"][3]},
    }
    return json.dumps(payload, indent=2) + "\n"


_BRIDGE_REQUIRED_PROPS = {
    "--odd-shadow-offset-x",
    "--odd-shadow-offset-y",
    "--odd-shadow-blur",
    "--odd-shadow-color",
    "--odd-box-shadows",
    "--odd-inner-shadow-offset-x",
    "--odd-inner-shadow-offset-y",
    "--odd-inner-shadow-blur",
    "--odd-inner-shadow-spread",
    "--odd-inner-shadow-color",
    "--odd-gradient",
    "--odd-radial-gradient",
    "--odd-radial-gradient-2",
    "--odd-repeating-linear-gradient",
    "--odd-tiled-radial-gradient",
    "--odd-background-pattern-size",
    "--odd-background-pattern-position",
    "--odd-background-color",
    "--odd-mask-image",
    "--odd-mask-fade-color",
    "--odd-clip-polygon",
    "--odd-vector-icon",
}


def _requires_bridge_prop(prop: str) -> bool:
    return prop in _BRIDGE_REQUIRED_PROPS


def _is_button_tag(uxml_tag: str) -> bool:
    return uxml_tag in ("ui:Button", "odd:Html2UxmlButton")


def _style_has_nonzero_flex_gap(style: ResolvedStyle | None) -> bool:
    if style is None:
        return False
    display = (_resolved_value(style, "display") or "").strip().lower()
    if display not in ("flex", "inline-flex", "grid", "inline-grid"):
        return False

    def any_nonzero(value: str | None) -> bool:
        if not value:
            return False
        for part in value.split():
            px = _css_length_px(part)
            if px is not None and abs(px) > 0.0001:
                return True
        return False

    return (
        any_nonzero(_resolved_value(style, "gap"))
        or any_nonzero(_resolved_value(style, "row-gap"))
        or any_nonzero(_resolved_value(style, "column-gap"))
    )


_VAR_RESOLVED_PROPS = {
    "background",
    "background-image",
    "box-shadow",
    "filter",
    "mask-image",
    "-webkit-mask-image",
    "clip-path",
}


def _compute_rule_bridge_flags(parsed_rules: list[_ParsedRule]) -> list[bool]:
    """For each parsed rule (parallel to parsed_rules), True iff its mapped
    declarations include a custom property that the runtime Html2UxmlPanel reads."""
    flags = []
    for rule in parsed_rules:
        mapped = map_declarations([(d.prop, d.value) for d in rule.declarations])
        flags.append(any(_requires_bridge_prop(k) for k, _ in mapped.decls))
    return flags


# ---------------------------------------------------------------------------
# CSS animation bridge
# ---------------------------------------------------------------------------


_ANIMATION_PROPS = {
    "opacity",
    "translate",
    "rotate",
    "scale",
    "color",
    "background-color",
}

_ANIMATION_TIMING_KEYWORDS = {
    "linear", "ease", "ease-in", "ease-out", "ease-in-out",
    "ease-in-sine", "ease-out-sine", "ease-in-out-sine",
    "ease-in-quad", "ease-out-quad", "ease-in-out-quad",
    "ease-in-cubic", "ease-out-cubic", "ease-in-out-cubic",
}

_ANIMATION_DIRECTION_KEYWORDS = {
    "normal", "reverse", "alternate", "alternate-reverse",
}

_ANIMATION_FILL_KEYWORDS = {
    "none", "forwards", "backwards", "both",
}

_ANIMATION_PLAY_STATE_KEYWORDS = {
    "running", "paused",
}


_ODD_UXML_TAGS = {
    "ui:VisualElement": "odd:Html2UxmlElement",
    "ui:Label": "odd:Html2UxmlLabel",
    "ui:Button": "odd:Html2UxmlButton",
    "ui:ScrollView": "odd:Html2UxmlScrollView",
    "ui:TextField": "odd:Html2UxmlTextField",
    "ui:FloatField": "odd:Html2UxmlFloatField",
    "ui:Slider": "odd:Html2UxmlSlider",
    "ui:Toggle": "odd:Html2UxmlToggle",
    "ui:RadioButton": "odd:Html2UxmlRadioButton",
    "ui:DropdownField": "odd:Html2UxmlDropdownField",
    "ui:ProgressBar": "odd:Html2UxmlProgressBar",
    "ui:Foldout": "odd:Html2UxmlFoldout",
    "ui:GroupBox": "odd:Html2UxmlGroupBox",
}


def _to_odd_uxml_tag(tag: str) -> str:
    return _ODD_UXML_TAGS.get(tag, tag)


def _extract_animation_keyframes(css_text: str) -> dict[str, _AnimationKeyframes]:
    keyframes: dict[str, _AnimationKeyframes] = {}
    pat = re.compile(r"@(?:-[A-Za-z]+-)?keyframes\s+([A-Za-z_][\w-]*)\s*\{", re.IGNORECASE)
    pos = 0
    while True:
        m = pat.search(css_text, pos)
        if not m:
            break
        body_start = m.end() - 1
        body_end = _find_matching_brace(css_text, body_start)
        if body_end < 0:
            break
        name = m.group(1)
        frames = _parse_keyframe_body(css_text[body_start + 1:body_end])
        if frames:
            keyframes[name] = _AnimationKeyframes(name=name, frames=frames)
        pos = body_end + 1
    return keyframes


def _find_matching_brace(source: str, open_index: int) -> int:
    depth = 0
    quote: str | None = None
    escaped = False
    for idx in range(open_index, len(source)):
        c = source[idx]
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
        elif c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return idx
    return -1


def _parse_keyframe_body(body: str) -> list[_AnimationFrame]:
    frames: list[_AnimationFrame] = []
    pos = 0
    while pos < len(body):
        brace = body.find("{", pos)
        if brace < 0:
            break
        selector_text = body[pos:brace].strip()
        end = _find_matching_brace(body, brace)
        if end < 0:
            break
        decls = [(d.prop, d.value) for d in _parse_declarations(body[brace + 1:end])]
        for raw_sel in _split_top_level_args(selector_text):
            offset = _parse_keyframe_offset(raw_sel.strip())
            if offset is not None:
                frames.append(_AnimationFrame(offset=offset, decls=decls))
        pos = end + 1
    frames.sort(key=lambda f: f.offset)
    return frames


def _parse_keyframe_offset(value: str) -> float | None:
    v = value.strip().lower()
    if v == "from":
        return 0.0
    if v == "to":
        return 1.0
    if v.endswith("%"):
        try:
            return max(0.0, min(1.0, float(v[:-1].strip()) / 100.0))
        except ValueError:
            return None
    return None


def _animation_custom_decls(pairs: list[tuple[str, str]], state: _EmitState) -> list[tuple[str, str]]:
    spec = _animation_spec_from_pairs(pairs)
    name = spec.get("name", "").strip()
    if not name or name.lower() == "none":
        return []
    keyframes = state.animation_keyframes.get(name)
    if keyframes is None:
        state.warn_once(f"animation '{name}' has no matching @keyframes; dropped")
        return []
    encoded = _encode_animation_keyframes(keyframes, state)
    if not encoded:
        state.warn_once(f"animation '{name}' has no supported keyframe properties; dropped")
        return []

    state.stats.bridged_props["--odd-animation"] = state.stats.bridged_props.get("--odd-animation", 0) + 1
    return [
        ("--odd-animation-name", _uss_string(name)),
        ("--odd-animation-duration-ms", str(_duration_to_ms(spec.get("duration", "0s")))),
        ("--odd-animation-delay-ms", str(_duration_to_ms(spec.get("delay", "0s")))),
        ("--odd-animation-timing", _uss_string(spec.get("timing", "linear"))),
        ("--odd-animation-iteration-count", _uss_string(spec.get("iteration-count", "1"))),
        ("--odd-animation-direction", _uss_string(spec.get("direction", "normal"))),
        ("--odd-animation-fill-mode", _uss_string(spec.get("fill-mode", "none"))),
        ("--odd-animation-play-state", _uss_string(spec.get("play-state", "running"))),
        ("--odd-animation-keyframes", _uss_string(encoded)),
    ]


def _animation_spec_from_pairs(pairs: list[tuple[str, str]]) -> dict[str, str]:
    spec: dict[str, str] = {}
    for prop, value in pairs:
        low = prop.lower()
        if low == "animation":
            spec.update(_parse_animation_shorthand(_first_animation_layer(value)))
        elif low.startswith("animation-"):
            key = low[len("animation-"):]
            spec[key] = _first_animation_layer(value).strip()
    return spec


def _first_animation_layer(value: str) -> str:
    parts = _split_top_level_args(value)
    return parts[0] if parts else value


def _parse_animation_shorthand(value: str) -> dict[str, str]:
    spec: dict[str, str] = {}
    for token in _split_animation_tokens(value):
        low = token.lower()
        if _looks_like_duration(low):
            if "duration" not in spec:
                spec["duration"] = token
            elif "delay" not in spec:
                spec["delay"] = token
        elif low in _ANIMATION_TIMING_KEYWORDS or low.startswith(("cubic-bezier(", "steps(")):
            spec["timing"] = token
        elif low in _ANIMATION_DIRECTION_KEYWORDS:
            spec["direction"] = token
        elif low in _ANIMATION_FILL_KEYWORDS:
            spec["fill-mode"] = token
        elif low in _ANIMATION_PLAY_STATE_KEYWORDS:
            spec["play-state"] = token
        elif low == "infinite" or _looks_like_number(low):
            spec["iteration-count"] = token
        elif low not in ("normal", "none"):
            spec["name"] = token
    return spec


def _split_animation_tokens(value: str) -> list[str]:
    out: list[str] = []
    buf: list[str] = []
    depth = 0
    quote: str | None = None
    escaped = False
    for c in value.strip():
        if quote:
            buf.append(c)
            if escaped:
                escaped = False
            elif c == "\\":
                escaped = True
            elif c == quote:
                quote = None
            continue
        if c in ("'", '"'):
            quote = c
            buf.append(c)
        elif c == "(":
            depth += 1
            buf.append(c)
        elif c == ")":
            depth = max(0, depth - 1)
            buf.append(c)
        elif c.isspace() and depth == 0:
            if buf:
                out.append("".join(buf))
                buf = []
        else:
            buf.append(c)
    if buf:
        out.append("".join(buf))
    return out


def _looks_like_duration(value: str) -> bool:
    return bool(re.fullmatch(r"-?\d*\.?\d+(ms|s)", value.strip(), re.IGNORECASE))


def _looks_like_number(value: str) -> bool:
    return bool(re.fullmatch(r"\d*\.?\d+", value.strip()))


def _duration_to_ms(value: str) -> float:
    v = value.strip().lower()
    try:
        if v.endswith("ms"):
            return max(0.0, float(v[:-2]))
        if v.endswith("s"):
            return max(0.0, float(v[:-1]) * 1000.0)
        return max(0.0, float(v))
    except ValueError:
        return 0.0


def _encode_animation_keyframes(keyframes: _AnimationKeyframes, state: _EmitState) -> str:
    records: list[str] = []
    unsupported: set[str] = set()
    for frame in keyframes.frames:
        mapped = map_declarations(frame.decls)
        _record_warnings(state, mapped.warnings)
        props: list[str] = []
        for prop, value in mapped.decls:
            if prop in _ANIMATION_PROPS:
                props.append(f"{prop}={urllib.parse.quote(value, safe='')}")
            elif not prop.startswith("--"):
                unsupported.add(prop)
        if props:
            records.append(f"{frame.offset:g}|" + "&".join(props))
    if unsupported:
        state.warn_once(
            "animation keyframe properties ignored: " + ", ".join(sorted(unsupported))
        )
    return ";".join(records)


def _uss_string(value: str) -> str:
    inner = value.replace("\\", "\\\\").replace('"', '\\"')
    return f'"{inner}"'


# ---------------------------------------------------------------------------
# UXML emission
# ---------------------------------------------------------------------------


_RICH_TEXT = {
    "b": "b", "strong": "b",
    "i": "i", "em": "i",
    "u": "u",
    "br": "br",
}


def _wrap_uxml(body: str, uss_filename: str, *, with_bridge: bool) -> str:
    bridge_ns = f' xmlns:odd="ODDGames.Html2Uxml"' if with_bridge else ""
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        f'<ui:UXML xmlns:ui="UnityEngine.UIElements" xmlns:uie="UnityEditor.UIElements"{bridge_ns}>\n'
        f'  <Style src="{_xml_escape(uss_filename)}" />\n'
        f'{body}'
        '</ui:UXML>\n'
    )


import re as _re
_PROP_RE = _re.compile(r"\b([a-zA-Z][a-zA-Z0-9-]*)\s*:")


def _record_warnings(state: _EmitState, warnings: list[str]) -> None:
    for w in warnings:
        state.warnings.append(w)
        if not any(token in w for token in ("dropped", "approximated", "unmapped", "unsupported")):
            continue
        m = _PROP_RE.search(w)
        if not m:
            continue
        prop = m.group(1)
        if prop in ("unmapped", "CSS", "dropped", "approximated"):
            tail = _PROP_RE.search(w[m.end():])
            if tail:
                prop = tail.group(1)
        state.stats.dropped_props[prop] = state.stats.dropped_props.get(prop, 0) + 1


_BUTTON_RESET_DECLS = [
    ("margin", "0"),
    ("padding", "0"),
    ("min-width", "0"),
    ("min-height", "0"),
    ("background-color", "rgba(0, 0, 0, 0)"),
    ("border-top-width", "0"),
    ("border-right-width", "0"),
    ("border-bottom-width", "0"),
    ("border-left-width", "0"),
    ("border-top-left-radius", "0"),
    ("border-top-right-radius", "0"),
    ("border-bottom-right-radius", "0"),
    ("border-bottom-left-radius", "0"),
    ("-unity-text-align", "middle-center"),
]


def _emit_button_reset(state: _EmitState) -> None:
    # Unity's built-in Button chrome otherwise stacks with authored borders,
    # padding, and backgrounds. Source classes emitted later override this.
    state.add_rule("Button", _BUTTON_RESET_DECLS)
    state.add_rule(".unity-button", _BUTTON_RESET_DECLS)


def _tree_contains_button(node: Node) -> bool:
    if node.is_text:
        return False
    if node.tag in ("button", "a"):
        return True
    if node.tag == "input":
        t = node.attrs.get("type", "text").lower()
        if t in ("button", "submit", "reset"):
            return True
    return any(_tree_contains_button(child) for child in node.children)


def _emit_node_children(parent: Node, resolved: dict[int, ResolvedStyle],
                        state: _EmitState, rule_bridge_flags: list[bool],
                        indent: int,
                        inherited_text_raw: dict[str, str] | None = None) -> str:
    out_parts: list[str] = []
    li_counter = 1
    children = list(parent.children)
    overlay_clones = _z_index_overlay_clones(parent, children, resolved)
    emit_children = children if overlay_clones else _ordered_children_for_paint(parent, resolved)
    for child in emit_children:
        if child.is_text:
            state.record_font_text(child.text or "", inherited_text_raw or {})
            out_parts.append(_emit_text_label(child.text or "", indent))
            continue
        out_parts.append(
            _emit_node(child, resolved, state, rule_bridge_flags, indent,
                       parent=parent, li_index=li_counter,
                       inherited_text_raw=inherited_text_raw)
        )
        if child.tag == "li":
            li_counter += 1
    for clone in overlay_clones:
        out_parts.append(
            _emit_node(
                clone.node,
                resolved,
                state,
                rule_bridge_flags,
                indent,
                parent=parent,
                inherited_text_raw=inherited_text_raw,
                extra_generated_decls=clone.decls,
                suppress_name=True,
                force_picking_ignore=True,
                extra_classes=_overlay_clone_classes(clone.node),
                forced_name=_overlay_clone_name(clone.node, clone.index),
                forced_tooltip=f"html2uxml z-index overlay clone of {_overlay_source_label(clone.node)}",
            )
        )
    return "".join(out_parts)


_INTERACTIVE_TAGS = {
    "a", "button", "input", "select", "textarea", "details", "summary",
    "option", "label",
}


def _z_index_overlay_clones(
    parent: Node,
    children: list[Node],
    resolved: dict[int, ResolvedStyle],
) -> list[_OverlayClone]:
    """Create paint-only clones for flow-positioned flex children with z-index.

    CSS z-index changes paint order without changing flex layout. Unity has no
    z-index, and physically moving a relative/static flex item to paint it later
    also moves its layout slot. For fixed-size, non-interactive flow items we
    keep the original child in normal flow and append an absolute, picking-less
    clone at the same slot so later siblings no longer bleed over it.
    """
    if parent.tag == "__root__" or len(children) < 2:
        return []
    parent_style = resolved.get(id(parent))
    display = (_resolved_value(parent_style, "display") or "").lower()
    if display not in ("flex", "inline-flex"):
        return []
    flex_dir = (_resolved_value(parent_style, "flex-direction") or "row").lower()
    if flex_dir not in ("row", ""):
        return []

    z_values: list[int] = []
    for child in children:
        z_values.append(_node_z_index(child, resolved) or 0)
    if z_values == sorted(z_values):
        return []

    clones: list[_OverlayClone] = []
    for index, child in enumerate(children):
        if child.is_text:
            continue
        z = _node_z_index(child, resolved)
        if z is None:
            continue
        if not any(later_z < z for later_z in z_values[index + 1:]):
            continue
        if not _can_emit_flow_overlay_clone(child, resolved):
            continue
        decls = _flex_row_overlay_decls(parent_style, children, index, resolved)
        if decls:
            clones.append(_OverlayClone(child, z, index, decls))
    return sorted(clones, key=lambda clone: (clone.z, clone.index))


def _overlay_source_label(node: Node) -> str:
    if node.attrs.get("id"):
        return f"#{node.attrs['id']}"
    classes = node.classes()
    if classes:
        return "." + ".".join(classes)
    return node.tag or "element"


_OM_ID_FILE_LINE_RE = re.compile(r"([A-Za-z0-9_-]+)\.[A-Za-z0-9]+(:\d+(?::\d+)*)?$")


def _slug_from_node_text(node: Node) -> str:
    """Derive a short slug from a node's direct text content. Skips
    descendant text so a deeply-nested container doesn't inherit the slug
    of its grandchild's first label.

    Returns ``""`` when the node has no usable text — the caller falls
    through to the next naming strategy.
    """
    if node.is_text:
        return ""
    parts: list[str] = []
    for child in node.children:
        if child.is_text:
            text = (child.text or "").strip()
            if text:
                parts.append(text)
    raw = " ".join(parts)
    if not raw:
        return ""
    raw = raw.strip()
    if len(raw) > 32:
        raw = raw[:32]
    slug = re.sub(r"[^A-Za-z0-9]+", "-", raw).strip("-").lower()
    return slug


def _slug_from_om_id(value: str) -> str:
    """Extract a stable, human-readable slug from a Figma/Onlook
    design-canvas `data-om-id` like
    ``jsx:/.../screens/Bracket.jsx:6264:128:15`` -> ``Bracket-6264-128-15``.

    Falls back to the last path segment, sanitised, when the value does
    not match the expected `file.ext:line:col[...]` shape.
    """
    if not value:
        return ""
    last = re.sub(r".*[/\\]", "", value.strip())
    match = _OM_ID_FILE_LINE_RE.search(last)
    if match:
        head = match.group(1)
        tail = (match.group(2) or "").replace(":", "-")
        slug = head + tail
    else:
        slug = re.sub(r"\.[A-Za-z0-9]+", "", last)
    slug = re.sub(r"[^A-Za-z0-9_-]+", "-", slug).strip("-")
    return slug


def _overlay_clone_name(node: Node, index: int) -> str:
    source = _overlay_source_label(node)
    slug = re.sub(r"[^A-Za-z0-9_-]+", "-", source).strip("-").lower()
    return f"h2u-z-overlay-clone-{slug or index}"


def _overlay_clone_classes(node: Node) -> list[str]:
    source = _overlay_source_label(node)
    slug = re.sub(r"[^A-Za-z0-9_-]+", "-", source).strip("-").lower()
    classes = ["h2u-z-overlay-clone"]
    if slug:
        classes.append(f"h2u-z-overlay-source-{slug}")
    return classes


def _node_z_index(node: Node, resolved: dict[int, ResolvedStyle]) -> int | None:
    if node.is_text:
        return None
    style = resolved.get(id(node))
    raw_z = _inline_or_resolved_value(node, style, "z-index")
    if raw_z is None:
        return None
    return _parse_z_index(raw_z)


def _can_emit_flow_overlay_clone(node: Node, resolved: dict[int, ResolvedStyle]) -> bool:
    style = resolved.get(id(node))
    position = (_inline_or_resolved_value(node, style, "position") or "static").lower()
    if position not in ("static", "relative"):
        return False
    return not _contains_interactive_node(node)


def _contains_interactive_node(node: Node) -> bool:
    if node.is_text:
        return False
    if (node.tag or "").lower() in _INTERACTIVE_TAGS:
        return True
    if node.attrs.get("data-h2u-on") or node.attrs.get("onclick"):
        return True
    return any(_contains_interactive_node(child) for child in node.children)


def _flex_row_overlay_decls(
    parent_style: ResolvedStyle | None,
    children: list[Node],
    index: int,
    resolved: dict[int, ResolvedStyle],
) -> list[tuple[str, str]] | None:
    child = children[index]
    child_style = resolved.get(id(child))
    child_width = _resolved_main_size_px(child_style)
    child_height = _resolved_cross_size_px(child_style)
    if child_width is None or child_height is None:
        return None

    gap = _flex_column_gap_px(parent_style)
    x = 0.0
    flow_items_seen = 0
    for prev in children[:index]:
        if prev.is_text:
            return None
        prev_style = resolved.get(id(prev))
        prev_width = _resolved_main_size_px(prev_style)
        if prev_width is None:
            return None
        if flow_items_seen:
            x += gap
        x += _box_px(prev_style, "margin-left") + prev_width + _box_px(prev_style, "margin-right")
        flow_items_seen += 1
    if flow_items_seen:
        x += gap
    x += _box_px(child_style, "margin-left")

    parent_height = _css_length_px(_resolved_value(parent_style, "height"))
    align = (
        _resolved_value(child_style, "align-self")
        or _resolved_value(parent_style, "align-items")
        or "flex-start"
    ).strip().lower()
    margin_top = _box_px(child_style, "margin-top")
    margin_bottom = _box_px(child_style, "margin-bottom")
    y = margin_top
    if parent_height is not None:
        if align in ("flex-end", "end"):
            y = parent_height - child_height - margin_bottom
        elif align == "center":
            y = (parent_height - child_height) / 2.0 + margin_top - margin_bottom

    return [
        ("position", "absolute"),
        ("left", _format_px(x)),
        ("top", _format_px(max(0.0, y))),
        ("margin-left", "0"),
        ("margin-right", "0"),
        ("margin-top", "0"),
        ("margin-bottom", "0"),
    ]


def _resolved_main_size_px(style: ResolvedStyle | None) -> float | None:
    for prop in ("width", "flex-basis", "min-width"):
        value = _resolved_value(style, prop)
        if value and value.strip().lower() not in ("auto", "content"):
            px = _css_length_px(value)
            if px is not None:
                return px
    return None


def _resolved_cross_size_px(style: ResolvedStyle | None) -> float | None:
    for prop in ("height", "min-height"):
        value = _resolved_value(style, prop)
        if value and value.strip().lower() not in ("auto", "content"):
            px = _css_length_px(value)
            if px is not None:
                return px
    return None


def _box_px(style: ResolvedStyle | None, prop: str) -> float:
    value = _resolved_value(style, prop)
    if not value or value.strip().lower() == "auto":
        return 0.0
    return _css_length_px(value) or 0.0


def _flex_column_gap_px(style: ResolvedStyle | None) -> float:
    column_gap = _resolved_value(style, "column-gap")
    if column_gap:
        return _css_length_px(column_gap) or 0.0
    gap = _resolved_value(style, "gap")
    if not gap:
        return 0.0
    parts = gap.split()
    value = parts[1] if len(parts) > 1 else parts[0]
    return _css_length_px(value) or 0.0


def _flex_row_gap_px(style: ResolvedStyle | None) -> float:
    row_gap = _resolved_value(style, "row-gap")
    if row_gap:
        return _css_length_px(row_gap) or 0.0
    gap = _resolved_value(style, "gap")
    if not gap:
        return 0.0
    parts = gap.split()
    return _css_length_px(parts[0]) or 0.0


def _bakes_flex_gap(uxml_tag: str, style: ResolvedStyle | None) -> bool:
    display = (_resolved_value(style, "display") or "").strip().lower()
    if display not in ("flex", "inline-flex"):
        return False
    flex_dir = (_resolved_value(style, "flex-direction") or "row").strip().lower()
    if flex_dir in ("column", "column-reverse"):
        return _flex_row_gap_px(style) > 0.0001
    if flex_dir in ("row", "row-reverse", ""):
        return _flex_column_gap_px(style) > 0.0001
    return False


def _static_gap_decls(
    parent_style: ResolvedStyle | None,
    child_style: ResolvedStyle | None,
    visual_index: int,
) -> list[tuple[str, str]]:
    if visual_index <= 0:
        return []
    flex_dir = (_resolved_value(parent_style, "flex-direction") or "row").strip().lower()
    if flex_dir in ("column", "column-reverse"):
        gap = _flex_row_gap_px(parent_style)
        prop = "margin-bottom" if flex_dir == "column-reverse" else "margin-top"
    else:
        gap = _flex_column_gap_px(parent_style)
        prop = "margin-right" if flex_dir == "row-reverse" else "margin-left"
    if gap <= 0.0001:
        return []
    return [(prop, _format_px(_box_px(child_style, prop) + gap))]


def _ordered_children_for_paint(parent: Node, resolved: dict[int, ResolvedStyle]) -> list[Node]:
    children = list(parent.children)
    if len(children) < 2:
        return children
    enriched: list[tuple[int, int, Node]] = []
    any_z = False
    for index, child in enumerate(children):
        z = 0
        if not child.is_text:
            style = resolved.get(id(child))
            raw_z = _inline_or_resolved_value(child, style, "z-index")
            if raw_z is not None:
                parsed = _parse_z_index(raw_z)
                if parsed is not None:
                    z = parsed
                    any_z = True
        enriched.append((z, index, child))
    if not any_z:
        return children
    # Unity paints later siblings on top. CSS higher z-index should therefore
    # appear later in the generated UXML; stable sorting preserves DOM order
    # for equal z-index values.
    sorted_children = sorted(enriched, key=lambda item: (item[0], item[1]))
    # Negative side-margins relied on the original sibling adjacency. After a
    # paint-order reorder, swap them so the layout still produces the same
    # visual overlap (margin-right:-X on a now-trailing item becomes
    # margin-left:-X, and vice versa).
    for new_idx, (_, original_idx, child) in enumerate(sorted_children):
        if child.is_text or new_idx == original_idx:
            continue
        _swap_overlap_margins_for_reorder(child, resolved, moved_later=new_idx > original_idx)
    return [child for _, _, child in sorted_children]


_REORDER_MARGIN_SWAP_MARK = "data-h2u-reorder-margin-swap"


def _swap_overlap_margins_for_reorder(
    node: Node,
    resolved: dict[int, ResolvedStyle],
    *,
    moved_later: bool,
) -> None:
    if node.attrs.get(_REORDER_MARGIN_SWAP_MARK):
        return
    style = resolved.get(id(node))
    src_prop = "margin-right" if moved_later else "margin-left"
    dst_prop = "margin-left" if moved_later else "margin-right"
    src_value = _inline_or_resolved_value(node, style, src_prop)
    if not _is_negative_length(src_value):
        return
    node.attrs[_REORDER_MARGIN_SWAP_MARK] = "1"
    inline = node.attrs.get("style", "")
    extra = f"{dst_prop}: {src_value}; {src_prop}: 0"
    if inline:
        inline = inline.rstrip()
        if not inline.endswith(";"):
            inline += ";"
        node.attrs["style"] = f"{inline} {extra}"
    else:
        node.attrs["style"] = extra


def _is_negative_length(value: str | None) -> bool:
    if value is None:
        return False
    v = value.strip().lower()
    if not v.startswith("-"):
        return False
    try:
        return float(v.replace("px", "").strip()) < 0
    except ValueError:
        return False


def _inline_or_resolved_value(node: Node, style: ResolvedStyle | None, prop: str) -> str | None:
    inline = node.attrs.get("style", "")
    if inline:
        for d in _parse_declarations(inline):
            if d.prop == prop:
                return d.value
    return _resolved_value(style, prop)


def _parse_z_index(value: str) -> int | None:
    v = value.strip().lower()
    if v in ("auto", "initial", "inherit", "unset"):
        return None
    try:
        return int(float(v))
    except ValueError:
        return None


def _parse_opacity(value: str | None) -> float | None:
    if value is None:
        return None
    v = value.strip().lower()
    if not v or v in ("auto", "initial", "inherit", "unset", "revert", "revert-layer"):
        return None
    try:
        if v.endswith("%"):
            n = float(v[:-1].strip()) / 100.0
        else:
            n = float(v)
    except ValueError:
        return None
    return max(0.0, min(1.0, n))


def _format_opacity(value: float) -> str:
    return f"{max(0.0, min(1.0, value)):.4f}".rstrip("0").rstrip(".")


def _is_transparent_css_color(value: str | None) -> bool:
    if value is None:
        return True
    v = value.strip().lower()
    if not v or v in ("transparent", "none", "initial", "inherit", "unset"):
        return True
    if v in ("#0000", "#00000000"):
        return True
    m = re.match(r"rgba?\((.*)\)", v)
    if m:
        parts = [p.strip() for p in re.split(r"[,/]", m.group(1)) if p.strip()]
        if len(parts) >= 4:
            alpha = parts[-1]
            try:
                if alpha.endswith("%"):
                    return float(alpha[:-1].strip()) <= 0
                return float(alpha) <= 0
            except ValueError:
                return False
    return False


def _node_has_paint_surface(node: Node, style: ResolvedStyle | None) -> bool:
    for prop in ("background-color", "--odd-background-color"):
        if not _is_transparent_css_color(_inline_or_resolved_value(node, style, prop)):
            return True
    background = _inline_or_resolved_value(node, style, "background")
    if background and not _is_transparent_css_color(background):
        return True
    bg_image = _inline_or_resolved_value(node, style, "background-image")
    if bg_image and bg_image.strip().lower() not in ("none", "initial", "inherit", "unset"):
        return True
    if _inline_or_resolved_value(node, style, "box-shadow"):
        return True
    for prop in ("--odd-gradient", "--odd-radial-gradient", "--odd-radial-gradient-2", "--odd-box-shadows"):
        if _inline_or_resolved_value(node, style, prop):
            return True
    return False


def _is_absolute_painted_overlay(node: Node, resolved: dict[int, ResolvedStyle]) -> bool:
    if node.is_text:
        return False
    style = resolved.get(id(node))
    position = (_inline_or_resolved_value(node, style, "position") or "static").strip().lower()
    if position not in ("absolute", "fixed", "sticky"):
        return False
    if not any(_inline_or_resolved_value(node, style, prop) is not None for prop in ("top", "right", "bottom", "left")):
        return False
    return _node_has_paint_surface(node, style)


def _css_group_opacity_needs_isolation(
    node: Node,
    style: ResolvedStyle | None,
    resolved: dict[int, ResolvedStyle],
) -> float | None:
    opacity = _parse_opacity(_inline_or_resolved_value(node, style, "opacity"))
    if opacity is None or opacity >= 0.999:
        return None
    children = [child for child in node.children if not child.is_text]
    if len(children) < 2:
        return None
    if any(_is_absolute_painted_overlay(child, resolved) for child in children):
        return opacity
    return None


def _opacity_for_child_after_parent_isolation(
    parent_opacity: float,
    child: Node,
    resolved: dict[int, ResolvedStyle],
) -> str:
    child_style = resolved.get(id(child))
    child_opacity = _parse_opacity(_inline_or_resolved_value(child, child_style, "opacity"))
    if child_opacity is None:
        child_opacity = 1.0
    return _format_opacity(parent_opacity * child_opacity)


def _emit_node(node: Node, resolved: dict[int, ResolvedStyle],
               state: _EmitState, rule_bridge_flags: list[bool],
               indent: int, *, parent: Node | None = None,
               li_index: int = 1,
               inherited_label_class: str | None = None,
               inherited_text_raw: dict[str, str] | None = None,
               extra_generated_decls: list[tuple[str, str]] | None = None,
               suppress_name: bool = False,
               force_picking_ignore: bool = False,
               extra_classes: list[str] | None = None,
               forced_name: str | None = None,
               forced_tooltip: str | None = None) -> str:
    if node.tag == "svg":
        return _emit_svg(node, state, indent, parent=parent, extra_generated_decls=extra_generated_decls)
    uxml_tag, extra_attrs, text_mode = map_element(node.tag, node.attrs)
    pad = " " * indent
    classes = list(node.classes())
    # Tag-as-class so element-name CSS selectors (e.g. `section`, `h1`,
    # `body`) — rewritten to `.h2u-tag-<tag>` in USS — can match. Only
    # add the class when the rewrite actually fired for this tag, so
    # untargeted elements don't get noise classes.
    if (node.tag
            and node.tag != "__root__"
            and node.tag.lower() in state.tagged_tags):
        tag_class = _h2u_tag_class(node.tag)
        if tag_class not in classes:
            classes.insert(0, tag_class)
    style = resolved.get(id(node))
    name_hint = _resolve_node_name_hint(node, forced_name)
    isolated_parent_opacity = _css_group_opacity_needs_isolation(node, style, resolved)
    own_text_raw = _text_raw_from_style(style)
    effective_text_raw = _merge_inherited_text_raw(inherited_text_raw or {}, own_text_raw)
    dynamic_font = _node_requests_dynamic_font(node) or _text_raw_requests_dynamic_font(effective_text_raw)
    if dynamic_font:
        state.record_font_text("", effective_text_raw, dynamic=True)

    # Bridge promotion: triggered by either a matching CSS rule with --odd-* or
    # an inline style="" with --odd-*. Compute both.
    needs_bridge = False
    if style is not None:
        for rule_idx in style.matched_rule_indices:
            if 0 <= rule_idx < len(rule_bridge_flags) and rule_bridge_flags[rule_idx]:
                needs_bridge = True
                break

    # Hoist inline style="" + unsupported-selector rule decls into h2u-N.
    own_classes: list[str] = []
    inline_pairs: list[tuple[str, str]] = []
    if style is not None and style.unsupported_decls:
        inline_pairs.extend((d.prop, d.value) for d in style.unsupported_decls)
    inline = node.attrs.get("style", "")
    if inline:
        inline_pairs.extend((d.prop, d.value) for d in _parse_declarations(inline))
    if style is not None:
        inline_pairs.extend(_resolved_var_effect_pairs(style))
    synthetic_attrs: list[tuple[str, str]] = []
    if style is not None:
        for sel in style.matched_selectors:
            for k, v in state.synthetic_by_selector.get(sel, ()):
                synthetic_attrs.append((k, v))
    if inline_pairs:
        mapped = map_declarations(inline_pairs)
        _record_warnings(state, mapped.warnings)
        animation_decls = _animation_custom_decls(inline_pairs, state)
        if mapped.decls or animation_decls:
            real_decls, synth_decls = _split_synthetic_decls(mapped.decls)
            if animation_decls:
                real_decls = _combine_generated_decls(real_decls, animation_decls)
            if isolated_parent_opacity is not None:
                real_decls = [(k, v) for k, v in real_decls if k != "opacity"]
                synth_decls = [(k, v) for k, v in synth_decls if k != "opacity"]
            synthetic_attrs.extend(synth_decls)
            if real_decls:
                _rewrite_data_uri_decls(real_decls, state, _data_uri_context_slug(node, parent))
                own_class, created = state.class_for_generated_decls(real_decls, name_hint)
                own_classes.append(own_class)
                if created:
                    state.stats.inline_overrides += 1
                for k, _ in real_decls:
                    if _requires_bridge_prop(k):
                        needs_bridge = True
                        state.stats.bridged_props[k] = state.stats.bridged_props.get(k, 0) + 1
    if isolated_parent_opacity is not None:
        opacity_reset_class, created = state.class_for_generated_decls([("opacity", "1")], name_hint)
        own_classes.append(opacity_reset_class)
        if created:
            state.stats.inline_overrides += 1
        state.warn_once(
            "CSS opacity group approximated for positioned overlay children; "
            "parent opacity was pushed to non-overlay children"
        )
    if extra_generated_decls:
        # These are per-node cascade overrides (baked flex gap, isolated
        # opacity). Reusing an earlier identical class can put the rule before
        # this node's own generated style and let that base style cancel it.
        own_class, created = state.class_for_generated_decls(
            extra_generated_decls,
            name_hint,
            cache=False,
        )
        own_classes.append(own_class)
        if created:
            state.stats.inline_overrides += 1

    # ScrollView promotion: any element with effective overflow auto/scroll
    # gets emitted as a ScrollView so Unity scrolls instead of clipping.
    overflow = _resolved_value(style, "overflow")
    if overflow and overflow.lower() in ("auto", "scroll") and uxml_tag == "ui:VisualElement":
        uxml_tag = "ui:ScrollView"

    text_transform = effective_text_raw.get("text-transform")
    text_decoration = effective_text_raw.get("text-decoration")

    if uxml_tag == "ui:Label" and _empty_label_should_be_visual(node, style):
        uxml_tag = "ui:VisualElement"
        text_mode = None

    if uxml_tag == "ui:Button" and _style_has_nonzero_flex_gap(style):
        uxml_tag = "odd:Html2UxmlButton"
        state.used_bridge = True
    if needs_bridge and uxml_tag == "ui:VisualElement":
        uxml_tag = "odd:Html2UxmlPanel"
        state.used_bridge = True
        state.stats.html2uxml_panels += 1
    elif uxml_tag == "ui:VisualElement":
        state.stats.elements += 1
    if uxml_tag == "ui:Label":
        state.stats.labels += 1
    elif _is_button_tag(uxml_tag):
        state.stats.buttons += 1
    elif uxml_tag == "ui:Image":
        state.stats.images += 1

    # <img>: synthesize a per-element class with background-image so the URL
    # ends up in USS where the user can swap it for a Unity asset reference.
    if node.tag == "img":
        src = node.attrs.get("src", "")
        if src:
            image_decls = [
                ("background-image", f'url("{src}")'),
                ("-unity-background-scale-mode", "scale-to-fit"),
            ]
            ratio = _aspect_ratio_from_attrs(node)
            if ratio is not None:
                image_decls.append(("aspect-ratio", f"{ratio:g}"))
            _rewrite_data_uri_decls(image_decls, state, _data_uri_context_slug(node, parent))
            image_class, created = state.class_for_generated_decls(image_decls, name_hint)
            own_classes.append(image_class)
            if created:
                state.stats.inline_overrides += 1

    # <select>: collect <option> text into the `choices` attribute (Unity
    # DropdownField accepts a comma-separated list) and drop the children.
    select_choices: list[str] | None = None
    if node.tag == "select":
        select_choices = []
        for child in node.children:
            if not child.is_text and child.tag == "option":
                opt_text = _gather_inline_text(child)
                if opt_text:
                    select_choices.append(opt_text)

    # Text handling.
    text_attr = None
    inner_children = [] if node.tag in SKIP_TAGS else list(node.children)

    # Prefer a single Label for text-only wrappers. This preserves class-based
    # typography rules on the element itself instead of creating a styled
    # container with an unstyled child Label.
    if (
        text_mode == "label"
        and uxml_tag == "ui:VisualElement"
        and _has_only_text_children(node)
    ):
        text_value = _gather_inline_text(node)
        if text_value:
            if text_transform:
                text_value = _apply_text_transform(text_value, text_transform)
            if text_decoration and "underline" in text_decoration.lower():
                text_value = f"<u>{text_value}</u>"
            uxml_tag = "ui:Label"
            text_mode = "text"
            text_attr = text_value
            inner_children = []
            if state.stats.html2uxml_panels > 0 and needs_bridge:
                state.stats.html2uxml_panels -= 1
            elif state.stats.elements > 0:
                state.stats.elements -= 1
            state.stats.labels += 1

    # Browser inline text runs flow horizontally by default. UI Toolkit child
    # elements use column layout unless told otherwise, so add a small generated
    # row class for containers that only hold inline text fragments.
    inline_label_class: str | None = None
    inline_label_decls: list[tuple[str, str]] = []
    if (
        text_mode == "label"
        and uxml_tag in ("ui:VisualElement", "odd:Html2UxmlPanel", "ui:ScrollView")
        and _has_inline_text_run(node)
        and _resolved_value(style, "flex-direction") is None
    ):
        inline_class, created = state.class_for_generated_decls([
            ("display", "flex"),
            ("flex-direction", "row"),
            ("flex-wrap", "wrap"),
            ("align-items", "center"),
        ], name_hint)
        own_classes.append(inline_class)
        if created:
            state.stats.inline_overrides += 1
        # Mixed raw text + inline element ("Title <span>X</span>") needs the
        # full line-box clamp + left alignment. Span-only rows already have
        # their own widths/padding and only need shared min-height + center.
        has_raw_text = any(c.is_text and (c.text or "").strip() for c in node.children)
        label_decls = (
            _inline_text_run_label_decls(style)
            if has_raw_text
            else _inline_child_label_decls(style)
        )
        if label_decls:
            inline_label_decls = label_decls
            inline_label_class, created = state.class_for_generated_decls(label_decls, name_hint)
            if created:
                state.stats.inline_overrides += 1

    if (
        inline_label_class is None
        and text_mode == "label"
        and uxml_tag in ("ui:VisualElement", "odd:Html2UxmlPanel", "ui:ScrollView")
        and _has_only_inline_text(node)
    ):
        label_decls = _inline_child_label_decls(style)
        if label_decls:
            inline_label_decls = label_decls
            inline_label_class, created = state.class_for_generated_decls(label_decls, name_hint)
            if created:
                state.stats.inline_overrides += 1

    flatten_inline_text_attr = (
        text_mode == "text"
        and _has_only_inline_text(node)
        and _can_flatten_text_attr(node, resolved, uxml_tag)
    )

    direct_text_label_class: str | None = inline_label_class
    direct_text_label_decls: list[tuple[str, str]] = inline_label_decls
    if (
        uxml_tag in ("ui:VisualElement", "odd:Html2UxmlPanel", "ui:ScrollView", "ui:Button", "odd:Html2UxmlButton")
        and any(c.is_text and (c.text or "").strip() for c in inner_children)
        and not flatten_inline_text_attr
    ):
        direct_text_decls = _combine_generated_decls(
            inline_label_decls,
            _direct_text_label_decls(effective_text_raw),
        )
        if direct_text_decls:
            direct_text_label_decls = direct_text_decls
            direct_text_label_class, created = state.class_for_generated_decls(direct_text_decls, name_hint)
            if created:
                state.stats.inline_overrides += 1

    vector_icon = _vector_icon_name_for_node(node, style, needs_bridge)
    if vector_icon and uxml_tag == "odd:Html2UxmlPanel":
        icon_class, created = state.class_for_generated_decls([
            ("--odd-vector-icon", f'"{vector_icon}"'),
        ], name_hint)
        own_classes.append(icon_class)
        inner_children = []
        text_attr = None
        state.used_bridge = True
        state.stats.bridged_props["--odd-vector-icon"] = state.stats.bridged_props.get("--odd-vector-icon", 0) + 1
        if created:
            state.stats.inline_overrides += 1

    compact_container_decls = _compact_text_container_decls(node, resolved, style)
    if compact_container_decls and uxml_tag in ("ui:VisualElement", "odd:Html2UxmlPanel", "ui:ScrollView"):
        compact_class, created = state.class_for_generated_decls(compact_container_decls, name_hint)
        own_classes.append(compact_class)
        if created:
            state.stats.inline_overrides += 1

    # <details>: extract <summary> text into the Foldout's text= and skip it.
    foldout_text: str | None = None
    if node.tag == "details":
        for child in node.children:
            if not child.is_text and child.tag == "summary":
                foldout_text = _gather_inline_text(child)
                break
        if foldout_text is not None:
            inner_children = [c for c in node.children
                              if c.is_text or c.tag != "summary"]

    # <progress value=50 max=100>
    progress_attrs: list[tuple[str, str]] = []
    if node.tag in ("progress", "meter"):
        if node.attrs.get("value"):
            progress_attrs.append(("value", node.attrs["value"]))
        if node.attrs.get("max"):
            progress_attrs.append(("high-value", node.attrs["max"]))
        if node.attrs.get("min"):
            progress_attrs.append(("low-value", node.attrs["min"]))

    if flatten_inline_text_attr:
        text_attr = _gather_inline_text(node)
        if text_transform:
            text_attr = _apply_text_transform(text_attr, text_transform)
        if text_decoration and "underline" in text_decoration.lower():
            text_attr = f"<u>{text_attr}</u>"
        inner_children = []

    spaced_text_attr: str | None = None
    if (
        uxml_tag == "ui:Label"
        and text_attr is not None
        and _should_emit_spaced_text(text_attr, effective_text_raw)
    ):
        spaced_text_attr = text_attr
        text_attr = None
        uxml_tag = "ui:VisualElement"
        if state.stats.labels > 0:
            state.stats.labels -= 1
        state.stats.elements += 1

    compact_label_decls = (
        _compact_nowrap_label_decls(style)
        or _compact_explicit_line_height_label_decls(style)
        or _compact_small_display_label_decls(style)
        or _compact_inherited_display_label_decls(effective_text_raw, style)
    )
    has_text_output = text_attr is not None or spaced_text_attr is not None
    if compact_label_decls and has_text_output and uxml_tag in ("ui:Label", "ui:VisualElement"):
        compact_class, created = state.class_for_generated_decls(compact_label_decls, name_hint)
        own_classes.append(compact_class)
        if created:
            state.stats.inline_overrides += 1
    inherited_text_label_decls = _inherited_text_label_decls(effective_text_raw, own_text_raw)
    if inherited_text_label_decls and uxml_tag == "ui:Label" and text_attr:
        inherited_text_label_class, created = state.class_for_generated_decls(inherited_text_label_decls, name_hint)
        own_classes.append(inherited_text_label_class)
        if created:
            state.stats.inline_overrides += 1
    if inherited_label_class and uxml_tag == "ui:Label" and text_attr:
        own_classes.append(inherited_label_class)

    # Build attributes
    attrs_out: list[tuple[str, str]] = []
    cls_list = list(classes)
    for k, v in extra_attrs.items():
        if k == "class":
            cls_list.extend(v.split())
        else:
            attrs_out.append((k, v))
    for k, v in synthetic_attrs:
        attrs_out.append((k, v))
    cls_list.extend(own_classes)
    if extra_classes:
        cls_list.extend(extra_classes)
    if cls_list:
        seen = set()
        deduped = [c for c in cls_list if not (c in seen or seen.add(c))]
        attrs_out.append(("class", " ".join(deduped)))
    if forced_name:
        attrs_out.append(("name", forced_name))
    elif not suppress_name:
        # Name precedence:
        #   1. data-h2u-name override
        #   2. HTML id
        #   3. HTML name attr (`<input>`, `<form>`, etc.)
        #   4. first source class — so Figma exports without ids still leave
        #      every element addressable in UI Builder + via UQuery.
        #   5. data-om-id slug — Figma/Onlook design-canvas IDs encode
        #      `screens/Bracket.jsx:6264:128:15`; the slug `Bracket-6264-128-15`
        #      is unique per source location and survives reconverts, so it
        #      makes a far better hierarchy hint than `h2u-N`.
        #   6. first synthesized class (h2u-N) — last-resort fallback so
        #      every element gets a unique, stable name.
        name_value = (
            node.attrs.get("data-h2u-name")
            or node.attrs.get("id")
            or node.attrs.get("name")
        )
        if not name_value:
            for cls in node.classes():
                if cls and not cls.startswith("h2u-") and not cls.startswith("__om-"):
                    name_value = cls
                    break
        if not name_value:
            text_slug = _slug_from_node_text(node)
            if text_slug:
                name_value = text_slug
        if not name_value:
            om = node.attrs.get("data-om-id")
            if om:
                name_value = _slug_from_om_id(om)
        if not name_value:
            for cls in own_classes:
                if cls and cls.startswith("h2u-"):
                    name_value = cls
                    break
        if name_value:
            attrs_out.append(("name", name_value))
    text_gradient_name = _node_text_gradient_name(style, state)
    if foldout_text is not None:
        state.record_font_text(foldout_text, effective_text_raw, dynamic=dynamic_font)
        attrs_out.append(("text", _maybe_wrap_text_gradient(foldout_text, text_gradient_name)))
    if text_attr is not None:
        state.record_font_text(text_attr, effective_text_raw, dynamic=dynamic_font)
        attrs_out.append(("text", _maybe_wrap_text_gradient(text_attr, text_gradient_name)))
    for k, v in progress_attrs:
        attrs_out.append((k, v))
    if select_choices is not None and select_choices:
        for choice in select_choices:
            state.record_font_text(choice, effective_text_raw, dynamic=dynamic_font)
        attrs_out.append(("choices", ",".join(select_choices)))
        inner_children = []  # don't render <option> children
    if forced_tooltip:
        attrs_out.append(("tooltip", forced_tooltip))
    elif "title" in node.attrs and node.attrs["title"]:
        attrs_out.append(("tooltip", node.attrs["title"]))
    elif "alt" in node.attrs and node.attrs["alt"]:
        attrs_out.append(("tooltip", node.attrs["alt"]))
    elif "aria-label" in node.attrs and node.attrs["aria-label"]:
        attrs_out.append(("tooltip", node.attrs["aria-label"]))

    # Pass-through hooks — preserve `data-*`, `role`, and `aria-*` markup so
    # gameplay code can find / decorate elements without re-parsing the source
    # HTML. UXML accepts unknown attributes; UI Toolkit ignores ones it does
    # not recognise, but they still show up when reading the file or via
    # element.GetAttribute("data-…"). We skip converter-internal markers
    # (`data-h2u-*`, `data-svg-*`, `data-om-*`) to keep the output clean.
    for k, v in node.attrs.items():
        if not v:
            continue
        kl = k.lower()
        if kl == "role":
            attrs_out.append((k, v))
            continue
        if kl.startswith("aria-") and kl != "aria-label":
            attrs_out.append((k, v))
            continue
        if kl.startswith("data-"):
            if kl.startswith(("data-h2u-", "data-svg-", "data-om-")):
                continue
            attrs_out.append((k, v))

    # In CSS, pointer-events does not inherit, but a direct text node, pseudo
    # content, or list marker is not its own element in the source DOM — so
    # browsers route hits past it to whatever the element itself resolves to.
    # In UI Toolkit those become real <ui:Label> children whose default
    # picking-mode would otherwise capture clicks the parent meant to ignore.
    if force_picking_ignore and not any(k == "picking-mode" for k, _ in attrs_out):
        attrs_out.append(("picking-mode", "Ignore"))
    picking_ignore = force_picking_ignore or any(k == "picking-mode" and v == "Ignore" for k, v in attrs_out)

    attr_str = "".join(f' {k}="{_xml_escape(v)}"' for k, v in attrs_out)

    rendered_children: list[str] = []

    if spaced_text_attr is not None:
        rendered_children.append(
            _emit_generated_text_label(
                spaced_text_attr,
                indent + 2,
                state,
                effective_text_raw,
                _direct_text_label_decls(effective_text_raw),
                preserve=True,
                picking_ignore=picking_ignore,
                dynamic_font=dynamic_font,
            )
        )

    # ::before pseudo-element synthesized as a leading Label child.
    if style is not None and style.before_content is not None:
        rendered_children.append(
            _emit_synthetic_pseudo(
                style.before_content,
                style.before_decls,
                state,
                indent + 2,
                inherited_text_raw=effective_text_raw,
                parent_picking_ignore=picking_ignore,
            )
        )

    # list-style marker for <li> based on parent <ul>/<ol>.
    li_marker = _list_marker(node, parent=parent, ordinal=li_index)
    if li_marker:
        marker_class = None
        marker_decls = _direct_text_label_decls(effective_text_raw)
        if marker_decls:
            marker_class, created = state.class_for_generated_decls(marker_decls)
            if created:
                state.stats.inline_overrides += 1
        state.record_font_text(li_marker, effective_text_raw, dynamic=dynamic_font)
        rendered_children.append(
            _emit_text_label(li_marker, indent + 2, class_name=marker_class,
                             picking_ignore=picking_ignore)
        )

    if inner_children:
        inner_li = 1
        overlay_clones = _z_index_overlay_clones(node, list(inner_children), resolved)
        ordered_inner = list(inner_children) if overlay_clones else _ordered_children_for_paint(
            Node(tag="__root__", children=inner_children),
            resolved,
        )
        bake_gap = _bakes_flex_gap(uxml_tag, style)
        visual_child_index = 0
        for child in ordered_inner:
            if child.is_text:
                t = (child.text or "").strip()
                if t and uxml_tag in ("ui:VisualElement", "odd:Html2UxmlPanel", "ui:ScrollView", "ui:Button", "odd:Html2UxmlButton"):
                    if text_transform:
                        t = _apply_text_transform(t, text_transform)
                    if text_decoration and "underline" in text_decoration.lower():
                        t = f"<u>{t}</u>"
                    rendered_children.append(
                        _emit_generated_text_label(
                            t,
                            indent + 2,
                            state,
                            effective_text_raw,
                            direct_text_label_decls,
                            preserve=True,
                            class_name=direct_text_label_class,
                            picking_ignore=picking_ignore,
                            dynamic_font=dynamic_font,
                            extra_container_decls=(
                                _static_gap_decls(style, None, visual_child_index)
                                if bake_gap else None
                            ),
                        )
                    )
                    visual_child_index += 1
                continue
            gap_decls = (
                _static_gap_decls(style, resolved.get(id(child)), visual_child_index)
                if bake_gap else []
            )
            opacity_decls = (
                [("opacity", _opacity_for_child_after_parent_isolation(
                    isolated_parent_opacity,
                    child,
                    resolved,
                ))]
                if isolated_parent_opacity is not None
                and not _is_absolute_painted_overlay(child, resolved)
                else []
            )
            child_extra_decls = _combine_generated_decls(opacity_decls, gap_decls)
            rendered_children.append(
                _emit_node(child, resolved, state, rule_bridge_flags, indent + 2,
                           parent=node, li_index=inner_li,
                           inherited_label_class=inline_label_class,
                           inherited_text_raw=effective_text_raw,
                           extra_generated_decls=(child_extra_decls or None),
                           suppress_name=suppress_name,
                           force_picking_ignore=force_picking_ignore)
            )
            visual_child_index += 1
            if child.tag == "li":
                inner_li += 1

    if style is not None and style.after_content is not None:
        rendered_children.append(
            _emit_synthetic_pseudo(
                style.after_content,
                style.after_decls,
                state,
                indent + 2,
                inherited_text_raw=effective_text_raw,
                parent_picking_ignore=picking_ignore,
            )
        )

    if inner_children:
        for clone in _z_index_overlay_clones(node, list(inner_children), resolved):
            rendered_children.append(
                _emit_node(
                    clone.node,
                    resolved,
                    state,
                    rule_bridge_flags,
                    indent + 2,
                    parent=node,
                    inherited_text_raw=effective_text_raw,
                    extra_generated_decls=clone.decls,
                    suppress_name=True,
                    force_picking_ignore=True,
                    extra_classes=_overlay_clone_classes(clone.node),
                    forced_name=_overlay_clone_name(clone.node, clone.index),
                    forced_tooltip=f"html2uxml z-index overlay clone of {_overlay_source_label(clone.node)}",
                )
            )

    if not rendered_children:
        emit_tag = _to_odd_uxml_tag(uxml_tag)
        return f"{pad}<{emit_tag}{attr_str} />\n"
    body = "".join(rendered_children)
    emit_tag = _to_odd_uxml_tag(uxml_tag)
    return f"{pad}<{emit_tag}{attr_str}>\n{body}{pad}</{emit_tag}>\n"


_SVG_DIM_RE = __import__("re").compile(
    r'(?<![-A-Za-z0-9_])width\s*=\s*["\']([^"\']+)["\']|'
    r'(?<![-A-Za-z0-9_])height\s*=\s*["\']([^"\']+)["\']|'
    r'(?<![-A-Za-z0-9_])viewBox\s*=\s*["\']([^"\']+)["\']'
)


def _svg_dimensions(raw: str) -> tuple[float, float] | None:
    """Pull (width, height) from <svg ...> attrs or viewBox. Returns None if
    no usable dimensions are present."""
    width: float | None = None
    height: float | None = None
    view_w: float | None = None
    view_h: float | None = None
    head_end = raw.find(">")
    head = raw[:head_end] if head_end > 0 else raw
    for m in _SVG_DIM_RE.finditer(head):
        if m.group(1) is not None:
            width = _to_float(m.group(1))
        elif m.group(2) is not None:
            height = _to_float(m.group(2))
        elif m.group(3) is not None:
            parts = m.group(3).replace(",", " ").split()
            if len(parts) == 4:
                view_w = _to_float(parts[2])
                view_h = _to_float(parts[3])
    w = width if width is not None else view_w
    h = height if height is not None else view_h
    if w is None or h is None:
        return None
    return w, h


def _to_float(s: str) -> float | None:
    try:
        return float(s.rstrip("px").strip())
    except ValueError:
        return None


def _emit_svg(
    node: Node,
    state: _EmitState,
    indent: int,
    *,
    parent: Node | None = None,
    extra_generated_decls: list[tuple[str, str]] | None = None,
) -> str:
    """Always render an <svg> as a VisualElement with a background-image
    pointing at a captured copy of the SVG markup, sized at 2x the SVG's own
    width/height so it survives DPR scaling."""
    pad = " " * indent
    sid_raw = node.attrs.get("data-svg-id")
    raw = ""
    if sid_raw is not None:
        try:
            sid = int(sid_raw)
            if 0 <= sid < len(state.svg_blocks):
                raw = state.svg_blocks[sid]
        except ValueError:
            pass
    state.stats.elements += 1
    if not raw:
        return f"{pad}<odd:Html2UxmlElement />\n"

    filename = state.svg_filename(node, parent)
    state.svg_files.append((filename, raw))
    if not state.svg_warning_emitted:
        state.warnings.append(
            "SVG assets emitted; Unity requires SVG import support "
            "(com.unity.vectorgraphics) or these icons may render as warning triangles"
        )
        state.svg_warning_emitted = True

    own_class = state.gen_class()
    decls: list[tuple[str, str]] = [
        ("background-image", f'url("{state.svg_assets_subdir}/{filename}")'),
        ("-unity-background-scale-mode", "scale-to-fit"),
    ]
    dims = _svg_dimensions(raw)
    if dims is not None:
        w, h = dims
        decls.append(("width", f"{w:g}px"))
        decls.append(("height", f"{h:g}px"))
    if extra_generated_decls:
        decls.extend(extra_generated_decls)
    state.add_rule(f".{own_class}", decls)
    state.stats.inline_overrides += 1

    classes = list(node.classes())
    classes.append(own_class)
    cls_attr = " ".join(classes)
    return f'{pad}<odd:Html2UxmlElement class="{_xml_escape(cls_attr)}" />\n'


def _svg_context_slug(node: Node, parent: Node | None) -> str:
    for src in (parent, node):
        if src is None:
            continue
        for key in ("id", "name", "title", "aria-label"):
            value = src.attrs.get(key)
            if value:
                return _slugify_asset_label(value)
        method = src.attrs.get("data-h2u-method")
        if method:
            return _slugify_asset_label(re.sub(r"^On", "", method))
        for cls in src.classes():
            if cls and not cls.startswith("h2u-"):
                return _slugify_asset_label(cls)
    return "svg"


def _selector_slug(selector: str) -> str:
    """Pick a human slug from a CSS selector for naming an extracted asset.

    Prefers an id (`#name`) or class (`.name`) token, ignoring synthesized
    `h2u-N` classes; falls back to a sanitized form of the whole selector.
    """
    for m in re.finditer(r"[#.]([A-Za-z_][\w-]*)", selector):
        token = m.group(1)
        if not re.fullmatch(r"h2u-\d+", token):
            return _slugify_asset_label(token)
    return _slugify_asset_label(selector) or "embedded"


_DATA_URI_IN_URL_RE = re.compile(
    r"""url\(\s*(["']?)(data:[^)\s"']+)\1\s*\)""",
    re.IGNORECASE,
)


def _rewrite_data_uri_decls(
    decls: list[tuple[str, str]],
    state: _EmitState,
    slug: str,
) -> None:
    """Decode any `url("data:image/...;base64,...")` values inline.

    Mutates `decls` in place: each data: URI becomes
    `url("Images/<filename>")`, and the bytes are queued on `state` for the
    CLI to write to disk.
    """
    if not decls:
        return
    subdir = state.svg_assets_subdir
    for i, (prop, value) in enumerate(decls):
        if "data:" not in value:
            continue

        def repl(m: re.Match) -> str:
            uri = m.group(2)
            filename = state.intern_data_uri(uri, slug=slug)
            if filename is None:
                return m.group(0)
            return f'url("{subdir}/{filename}")'

        new_value = _DATA_URI_IN_URL_RE.sub(repl, value)
        if new_value != value:
            decls[i] = (prop, new_value)


def _data_uri_context_slug(node: Node | None, parent: Node | None) -> str:
    """Slug for an extracted data: URI asset.

    Prefers the element that owns the URI (e.g. an `<img alt="...">` or a
    `<div id="...">`), falling back to the parent for context (e.g. an
    `<a title="...">` wrapping a logo div).
    """
    for src in (node, parent):
        if src is None:
            continue
        for key in ("alt", "id", "name", "title", "aria-label"):
            value = src.attrs.get(key)
            if value:
                return _slugify_asset_label(value)
        for cls in src.classes():
            if cls and not cls.startswith("h2u-"):
                return _slugify_asset_label(cls)
    return "embedded"


def _slugify_asset_label(value: str) -> str:
    value = re.sub(r"([a-z0-9])([A-Z])", r"\1-\2", value)
    slug = re.sub(r"[^A-Za-z0-9]+", "-", value).strip("-").lower()
    return slug[:64].strip("-") or "svg"


def _resolve_node_name_hint(node: Node, forced_name: str | None) -> str | None:
    """Pick the best human name for a node *without* falling back to a
    synthesized `h2u-N` class. Used to seed generated USS class names so
    they read like `.h2u-tap-to-ready-up` instead of `.h2u-1`.
    """
    if forced_name:
        return forced_name
    name = (
        node.attrs.get("data-h2u-name")
        or node.attrs.get("id")
        or node.attrs.get("name")
    )
    if not name:
        for cls in node.classes():
            if cls and not cls.startswith("h2u-") and not cls.startswith("__om-"):
                name = cls
                break
    if not name:
        text_slug = _slug_from_node_text(node)
        if text_slug:
            name = text_slug
    if not name:
        om = node.attrs.get("data-om-id")
        if om:
            name = _slug_from_om_id(om)
    return name


def _slug_for_class(name_hint: str | None) -> str | None:
    """Sanitize an element name hint for use as a USS class suffix.

    Returns None when the hint is empty or already a synthetic
    `h2u-N` placeholder (in which case the caller should fall back
    to the counter-based gen path).
    """
    if not name_hint:
        return None
    raw = name_hint.strip()
    if not raw:
        return None
    if re.fullmatch(r"h2u-\d+", raw):
        return None
    slug = _slugify_asset_label(raw)
    return slug or None


def _emit_synthetic_pseudo(
    content: str,
    decls,
    state: _EmitState,
    indent: int,
    *,
    inherited_text_raw: dict[str, str] | None = None,
    parent_picking_ignore: bool = False,
) -> str:
    """Emit a Label child that materializes a ::before / ::after rule's content."""
    pseudo_text_raw = _merge_inherited_text_raw(
        inherited_text_raw or {},
        _text_raw_from_decls(decls),
    )
    text = content
    text_transform = pseudo_text_raw.get("text-transform")
    if text_transform:
        text = _apply_text_transform(text, text_transform)
    text_decoration = pseudo_text_raw.get("text-decoration")
    if text_decoration and "underline" in text_decoration.lower():
        text = f"<u>{text}</u>"
    state.record_font_text(text, pseudo_text_raw)

    text_decls = _direct_text_label_decls(pseudo_text_raw)
    if not decls and not text_decls:
        return _emit_text_label(text, indent, picking_ignore=parent_picking_ignore)

    own_class = state.gen_class()
    mapped = map_declarations([(d.prop, d.value) for d in decls or []])
    mapped_decls, synthetic_attrs = _split_synthetic_decls(mapped.decls)
    real_decls = _combine_generated_decls(text_decls, mapped_decls)
    if not content.strip() and not _pseudo_has_visible_output(real_decls):
        return ""
    if real_decls:
        state.add_rule(f".{own_class}", real_decls)
        state.stats.inline_overrides += 1
    pseudo_picking_ignore = any(
        k == "picking-mode" and v == "Ignore" for k, v in synthetic_attrs
    )
    if parent_picking_ignore and not pseudo_picking_ignore:
        synthetic_attrs = [*synthetic_attrs, ("picking-mode", "Ignore")]
    pad = " " * indent
    attrs = [("class", own_class), ("text", text), *synthetic_attrs]
    attr_str = "".join(f' {k}="{_xml_escape(v)}"' for k, v in attrs)
    return f"{pad}<odd:Html2UxmlLabel{attr_str} />\n"


def _split_synthetic_decls(
    decls: list[tuple[str, str]]
) -> tuple[list[tuple[str, str]], list[tuple[str, str]]]:
    """Split internal converter markers out of USS declarations.

    Mapping functions use ``__attr-name__`` keys for values that must become
    UXML attributes, for example ``pointer-events: none`` ->
    ``picking-mode="Ignore"``. These are never valid USS properties.
    """
    real_decls: list[tuple[str, str]] = []
    synthetic_attrs: list[tuple[str, str]] = []
    for k, v in decls:
        if k.startswith("__") and k.endswith("__"):
            synthetic_attrs.append((k.strip("_"), v))
        else:
            real_decls.append((k, v))
    return real_decls, synthetic_attrs


def _list_marker(node: Node, parent: Node | None, ordinal: int) -> str | None:
    if node.tag != "li" or parent is None:
        return None
    if parent.tag == "ol":
        return f"{ordinal}. "
    return "•  "  # bullet + two spaces


def _resolved_value(style, prop: str) -> str | None:
    if style is None:
        return None
    for k, v in style.base:
        if k == prop:
            return v
    return None


_TEXT_INHERIT_RAW_PROPS = (
    "font-family",
    "font-weight",
    "font-style",
    "font-size",
    "letter-spacing",
    "word-spacing",
    "color",
    "text-shadow",
    "text-align",
    "text-transform",
    "text-decoration",
    "white-space",
    "text-overflow",
    "-webkit-text-stroke",
    "-webkit-text-stroke-width",
    "-webkit-text-stroke-color",
    "text-stroke",
    "-unity-font-definition",
    "-unity-font-style",
    "-unity-text-align",
    "-unity-text-outline-width",
    "-unity-text-outline-color",
    "--odd-font-family",
    "--odd-font-weight",
    "--odd-font-atlas-mode",
)

_TEXT_LABEL_MAPPED_RAW_PROPS = (
    "font-family",
    "font-weight",
    "font-style",
    "font-size",
    "letter-spacing",
    "word-spacing",
    "color",
    "text-shadow",
    "text-align",
    "white-space",
    "text-overflow",
    "-webkit-text-stroke",
    "-webkit-text-stroke-width",
    "-webkit-text-stroke-color",
    "text-stroke",
    "-unity-font-definition",
    "-unity-font-style",
    "-unity-text-align",
    "-unity-text-outline-width",
    "-unity-text-outline-color",
    "--odd-font-family",
    "--odd-font-weight",
)

_FONT_TEXT_RAW_PROPS = (
    "font-family",
    "font-weight",
    "font-style",
    "font-size",
    "--odd-font-family",
    "--odd-font-weight",
    "-unity-font-definition",
    "-unity-font-style",
)


def _text_raw_from_style(style) -> dict[str, str]:
    if style is None:
        return {}
    wanted = set(_TEXT_INHERIT_RAW_PROPS)
    out: dict[str, str] = {}
    for prop, value in style.base:
        p = prop.lower()
        if p in wanted:
            out[p] = value
    return out


def _text_raw_from_decls(decls) -> dict[str, str]:
    wanted = set(_TEXT_INHERIT_RAW_PROPS)
    out: dict[str, str] = {}
    for d in decls or []:
        prop = getattr(d, "prop", "").lower()
        if prop in wanted:
            out[prop] = getattr(d, "value", "")
    return out


def _merge_inherited_text_raw(
    inherited: dict[str, str],
    own: dict[str, str],
) -> dict[str, str]:
    effective = dict(inherited)
    for prop, value in own.items():
        low = value.strip().lower()
        if low in ("inherit", "unset", "revert", "revert-layer"):
            continue
        if low == "initial":
            effective.pop(prop, None)
            continue
        effective[prop] = value
    return effective


_GENERIC_FONT_FAMILIES = {
    "serif", "sans-serif", "monospace", "cursive", "fantasy",
    "system-ui", "ui-serif", "ui-sans-serif", "ui-monospace",
    "ui-rounded", "math", "emoji", "fangsong",
}


def _font_usage_from_text(
    text: str,
    text_raw: dict[str, str],
    *,
    dynamic: bool = False,
) -> FontUsage | None:
    family = _font_family_from_text_raw(text_raw)
    if family is None:
        return None
    return FontUsage(
        family=family,
        weight=_font_weight_from_text_raw(text_raw),
        italic=_font_italic_from_text_raw(text_raw),
        dynamic=dynamic or _text_raw_requests_dynamic_font(text_raw),
        text=_plain_text_for_font_usage(text),
    )


def _font_family_from_text_raw(text_raw: dict[str, str]) -> str | None:
    raw = text_raw.get("--odd-font-family") or text_raw.get("font-family") or ""
    if not raw:
        return None
    first = raw.split(",", 1)[0].strip().strip('"').strip("'")
    if not first or first.lower() in _GENERIC_FONT_FAMILIES:
        return None
    return first


def _font_weight_from_text_raw(text_raw: dict[str, str]) -> int:
    raw = (text_raw.get("--odd-font-weight") or text_raw.get("font-weight") or "").strip().lower()
    if raw in ("bold", "bolder"):
        return 700
    if raw in ("normal", "lighter"):
        return 400
    try:
        return int(float(raw))
    except ValueError:
        return 400


def _font_italic_from_text_raw(text_raw: dict[str, str]) -> bool:
    font_style = (text_raw.get("font-style") or "").lower()
    unity_style = (text_raw.get("-unity-font-style") or "").lower()
    return "italic" in font_style or "italic" in unity_style


def _text_raw_requests_dynamic_font(text_raw: dict[str, str]) -> bool:
    raw = (text_raw.get("--odd-font-atlas-mode") or "").strip().strip('"').strip("'").lower()
    return raw in ("dynamic", "runtime", "true", "1", "yes")


def _node_requests_dynamic_font(node: Node) -> bool:
    for attr in ("data-h2u-dynamic-text", "data-h2u-font-dynamic"):
        raw = node.attrs.get(attr)
        if raw is not None and raw.strip().lower() not in ("", "0", "false", "no"):
            return True
    mode = (node.attrs.get("data-h2u-font-atlas") or "").strip().lower()
    if mode in ("dynamic", "runtime"):
        return True
    if node.attrs.get("contenteditable", "").strip().lower() in ("", "true", "plaintext-only"):
        return "contenteditable" in node.attrs
    if node.tag == "textarea":
        return True
    if node.tag == "input":
        return (node.attrs.get("type") or "text").strip().lower() in (
            "text", "email", "search", "url", "tel", "password", "number",
        )
    return False


def _plain_text_for_font_usage(text: str) -> str:
    if not text:
        return ""
    return re.sub(r"<[^>]+>", "", text)


def _direct_text_label_decls(text_raw: dict[str, str]) -> list[tuple[str, str]]:
    return _text_label_decls_for_props(text_raw, _TEXT_LABEL_MAPPED_RAW_PROPS)


def _inherited_text_label_decls(
    effective_text_raw: dict[str, str],
    own_text_raw: dict[str, str],
) -> list[tuple[str, str]]:
    if not effective_text_raw:
        return []
    own_explicit = {
        prop
        for prop, value in own_text_raw.items()
        if value.strip().lower() not in ("inherit", "unset", "revert", "revert-layer")
    }
    missing = [
        prop for prop in _TEXT_LABEL_MAPPED_RAW_PROPS
        if prop in effective_text_raw and prop not in own_explicit
    ]
    if not missing:
        return []

    props: list[str] = []
    if any(prop in missing for prop in _FONT_TEXT_RAW_PROPS):
        props.extend(prop for prop in _FONT_TEXT_RAW_PROPS if prop in effective_text_raw)
    props.extend(
        prop for prop in missing
        if prop not in _FONT_TEXT_RAW_PROPS
    )
    return _text_label_decls_for_props(effective_text_raw, props)


def _text_label_decls_for_props(
    text_raw: dict[str, str],
    props: tuple[str, ...] | list[str],
) -> list[tuple[str, str]]:
    pairs = [(prop, text_raw[prop]) for prop in props if prop in text_raw]
    if not pairs:
        return []
    mapped = map_declarations(pairs)
    real_decls, _ = _split_synthetic_decls(mapped.decls)
    return real_decls


def _combine_generated_decls(*groups: list[tuple[str, str]]) -> list[tuple[str, str]]:
    out: list[tuple[str, str]] = []
    positions: dict[str, int] = {}
    for group in groups:
        for prop, value in group:
            if prop in positions:
                out[positions[prop]] = (prop, value)
            else:
                positions[prop] = len(out)
                out.append((prop, value))
    return out


def _compact_nowrap_label_decls(style) -> list[tuple[str, str]]:
    """Add Unity-only line-box sizing for single-line clipped labels.

    UI Toolkit Label text reserves a taller line box than browser CSS for
    most imported fonts. In dense HUD rows that makes 9px/7px stacked labels
    spill outside a 20-24px row, and in mid-size nameplates a 16px label can
    push the next stacked sibling out of an absolutely-positioned panel.
    When the source explicitly describes a single-line clipped label
    (`white-space: nowrap` plus `overflow: hidden`/`clip` or
    `text-overflow: ellipsis`), constrain the generated Unity label box
    without changing the source CSS contract.
    """
    font_px = _resolved_font_px(style)
    if font_px is None or font_px > 24:
        return []
    if _resolved_value(style, "height") or _resolved_value(style, "min-height") or _resolved_value(style, "line-height"):
        return []
    white_space = (_resolved_value(style, "white-space") or "").lower()
    overflow = (_resolved_value(style, "overflow") or "").lower()
    text_overflow = (_resolved_value(style, "text-overflow") or "").lower()
    if white_space != "nowrap" and text_overflow != "ellipsis":
        return []
    if overflow not in ("hidden", "clip") and text_overflow != "ellipsis":
        return []

    line_px = max(1, int(math.ceil(font_px + 3)))
    decls = [
        ("height", f"{line_px}px"),
        ("min-height", f"{line_px}px"),
        ("max-height", f"{line_px}px"),
        ("margin-top", "0"),
        ("margin-bottom", "0"),
        ("padding", "0"),
        ("-unity-paragraph-spacing", "0"),
    ]
    if _style_is_italic(style):
        guard_px = max(1, int(math.ceil(font_px * 0.2)))
        decls.extend([
            ("margin-left", f"-{guard_px}px"),
            ("padding-left", f"{guard_px}px"),
            ("padding-right", f"{guard_px}px"),
        ])
    if _resolved_value(style, "text-align") is None and _resolved_value(style, "-unity-text-align") is None:
        decls.append(("-unity-text-align", "middle-left"))
    return decls


def _compact_small_display_label_decls(style) -> list[tuple[str, str]]:
    """Constrain small styled HUD labels to browser-like text line boxes.

    Browser div/span text with 8-12px display fonts usually occupies a tight
    line-height. UI Toolkit Labels can reserve a much taller default line box,
    which pushes stacked absolute-position HUD groups down even when their
    container `top` is correct. Limit this to display-like labels so ordinary
    body copy keeps Unity's default metrics.
    """
    font_px = _resolved_font_px(style)
    if font_px is None or font_px > 12:
        return []
    if _resolved_value(style, "height") or _resolved_value(style, "min-height") or _resolved_value(style, "line-height"):
        return []
    if _resolved_value(style, "padding") or _resolved_value(style, "padding-top") or _resolved_value(style, "padding-bottom"):
        return []

    has_display_trait = (
        _style_is_italic(style)
        or _resolved_value(style, "letter-spacing") is not None
        or _resolved_value(style, "text-shadow") is not None
        or _font_weight_is_bold(style)
        or (_resolved_value(style, "text-transform") or "").lower() == "uppercase"
    )
    if not has_display_trait:
        return []

    line_px = max(1, int(math.ceil(font_px * 1.25)))
    decls = [
        ("height", f"{line_px}px"),
        ("min-height", f"{line_px}px"),
        ("max-height", f"{line_px}px"),
        ("padding", "0"),
        ("-unity-paragraph-spacing", "0"),
    ]
    if _resolved_value(style, "margin-top") is None and _resolved_value(style, "margin") is None:
        decls.append(("margin-top", "0"))
    if _resolved_value(style, "margin-bottom") is None and _resolved_value(style, "margin") is None:
        decls.append(("margin-bottom", "0"))
    if _resolved_value(style, "text-align") is None and _resolved_value(style, "-unity-text-align") is None:
        decls.append(("-unity-text-align", "middle-left"))
    return decls


def _compact_explicit_line_height_label_decls(style) -> list[tuple[str, str]]:
    """Turn CSS line-height on single-line display labels into a real line box.

    Browsers center glyphs within the authored line box. UI Toolkit maps
    `line-height` to paragraph spacing, which does not fix the first line's
    baseline inside compact flex rows. This keeps the fix scoped to stylized
    HUD/display text and avoids labels that have their own padding/borders.
    """
    font_px = _resolved_font_px(style)
    if font_px is None or font_px > 24:
        return []
    line_px = _resolved_line_height_px(style, font_px)
    if line_px is None:
        return []
    if _resolved_value(style, "height") or _resolved_value(style, "min-height"):
        return []
    if _resolved_value(style, "padding") or _resolved_value(style, "padding-top") or _resolved_value(style, "padding-bottom"):
        return []
    if _resolved_value(style, "border") or _resolved_value(style, "border-top") or _resolved_value(style, "border-bottom"):
        return []

    has_display_trait = (
        _style_is_italic(style)
        or _resolved_value(style, "letter-spacing") is not None
        or _resolved_value(style, "text-shadow") is not None
        or _font_weight_is_bold(style)
        or (_resolved_value(style, "text-transform") or "").lower() == "uppercase"
    )
    if not has_display_trait:
        return []

    # A literal 1.0 line-height can be too tight for Unity's font renderer,
    # especially after synthetic italic slant. Add a tiny guard while preserving
    # the browser's compact intent.
    box_px = max(line_px, int(math.ceil(font_px + 2)))
    decls = [
        ("height", f"{box_px}px"),
        ("min-height", f"{box_px}px"),
        ("max-height", f"{box_px}px"),
        ("padding", "0"),
        ("-unity-paragraph-spacing", "0"),
    ]
    if _resolved_value(style, "margin-top") is None and _resolved_value(style, "margin") is None:
        decls.append(("margin-top", "0"))
    if _resolved_value(style, "margin-bottom") is None and _resolved_value(style, "margin") is None:
        decls.append(("margin-bottom", "0"))
    if _style_is_italic(style):
        guard_px = max(1, int(math.ceil(font_px * 0.12)))
        decls.extend([
            ("padding-left", f"{guard_px}px"),
            ("padding-right", f"{guard_px}px"),
        ])
    if _resolved_value(style, "text-align") is None and _resolved_value(style, "-unity-text-align") is None:
        decls.append(("-unity-text-align", "middle-center"))
    return decls


def _compact_inherited_display_label_decls(
    effective_text_raw: dict[str, str],
    style,
) -> list[tuple[str, str]]:
    """Constrain generated child Labels whose display font is inherited.

    Browser inline spans in a flex ticker inherit text metrics from the parent
    line box. Once converted to independent UI Toolkit Labels, those children
    need the same compact line-box normalization even when their own selector
    only says `display: inline` and all typography comes from the parent.
    """
    if not effective_text_raw:
        return []
    if _resolved_value(style, "height") or _resolved_value(style, "min-height") or _resolved_value(style, "line-height"):
        return []
    if _resolved_value(style, "padding") or _resolved_value(style, "padding-top") or _resolved_value(style, "padding-bottom"):
        return []

    font_px = _css_length_px(effective_text_raw.get("font-size"))
    if font_px is None or font_px > 12:
        return []

    font_style = effective_text_raw.get("font-style", "") + " " + effective_text_raw.get("-unity-font-style", "")
    has_display_trait = (
        "italic" in font_style.lower()
        or "letter-spacing" in effective_text_raw
        or "text-shadow" in effective_text_raw
        or _raw_font_weight_is_bold(effective_text_raw)
        or effective_text_raw.get("text-transform", "").strip().lower() == "uppercase"
        or "-unity-text-outline-width" in effective_text_raw
        or "-webkit-text-stroke" in effective_text_raw
        or "text-stroke" in effective_text_raw
    )
    if not has_display_trait:
        return []

    line_px = max(1, int(math.ceil(font_px * 1.25)))
    decls = [
        ("height", f"{line_px}px"),
        ("min-height", f"{line_px}px"),
        ("max-height", f"{line_px}px"),
        ("padding", "0"),
        ("-unity-paragraph-spacing", "0"),
    ]
    if _resolved_value(style, "margin-top") is None and _resolved_value(style, "margin") is None:
        decls.append(("margin-top", "0"))
    if _resolved_value(style, "margin-bottom") is None and _resolved_value(style, "margin") is None:
        decls.append(("margin-bottom", "0"))
    if _resolved_value(style, "text-align") is None and _resolved_value(style, "-unity-text-align") is None:
        decls.append(("-unity-text-align", "middle-left"))
    return decls


def _inline_text_run_label_decls(style) -> list[tuple[str, str]]:
    """Constrain synthetic inline Labels to a browser-like line box.

    A DOM run such as ``Title <span>Accent</span>`` paints all fragments inside
    the same inline line box. In UI Toolkit those fragments become separate
    Labels, and their default Label metrics can sit too high/low or clip italic
    glyph overhang. This generated class only affects children of that inline
    run, not the authored CSS classes themselves.
    """
    font_px = _resolved_font_px(style)
    if font_px is None:
        return []
    line_px = _resolved_line_height_px(style, font_px)
    if line_px is None:
        line_px = max(1, int(math.ceil(font_px * 1.2)))
    decls = [
        ("height", f"{line_px}px"),
        ("min-height", f"{line_px}px"),
        ("max-height", f"{line_px}px"),
        ("margin-top", "0"),
        ("margin-bottom", "0"),
        ("padding", "0"),
        ("-unity-paragraph-spacing", "0"),
        ("-unity-text-align", "middle-left"),
    ]
    if _style_is_italic(style):
        guard_px = max(1, int(math.ceil(font_px * 0.18)))
        decls.extend([
            ("margin-left", f"-{guard_px}px"),
            ("padding-left", f"{guard_px}px"),
            ("padding-right", f"{guard_px}px"),
        ])
    return decls


def _inline_child_label_decls(style) -> list[tuple[str, str]]:
    """Carry inherited line-height alignment into child Labels.

    Some rows are authored as a flex container with only inline child elements
    and shared typography on the parent, for example the countdown number row.
    The children may have their own padding/borders, so this deliberately avoids
    resetting padding or forcing a fixed height.
    """
    font_px = _resolved_font_px(style)
    if font_px is None:
        return []
    line_px = _resolved_line_height_px(style, font_px)
    if line_px is None:
        return []
    return [
        ("min-height", f"{line_px}px"),
        ("-unity-paragraph-spacing", "0"),
        ("-unity-text-align", "middle-center"),
    ]


def _empty_label_should_be_visual(node: Node, style) -> bool:
    if not _is_effectively_empty(node):
        return False
    visual_props = (
        "width", "height", "min-width", "min-height",
        "background", "background-color", "background-image",
        "border", "border-top", "border-right", "border-bottom", "border-left",
        "border-radius", "box-shadow", "opacity",
    )
    return any(_resolved_value(style, prop) is not None for prop in visual_props)


def _vector_icon_name_for_node(node: Node, style, needs_bridge: bool) -> str | None:
    if not needs_bridge or style is None:
        return None
    if not _has_only_text_children(node):
        return None
    text = _gather_inline_text(node).strip()
    if text != "★":
        return None
    if not (_resolved_value(style, "width") and _resolved_value(style, "height")):
        return None
    if _resolved_value(style, "background") is None and _resolved_value(style, "background-image") is None:
        return None
    return "star"


def _aspect_ratio_from_attrs(node: Node) -> float | None:
    w = _html_number_attr(node.attrs.get("width"))
    h = _html_number_attr(node.attrs.get("height"))
    if w is None or h is None or w <= 0 or h <= 0:
        return None
    return w / h


def _html_number_attr(value: str | None) -> float | None:
    if value is None:
        return None
    m = re.match(r"^\s*(\d*\.?\d+)", value)
    if not m:
        return None
    try:
        return float(m.group(1))
    except ValueError:
        return None


def _is_effectively_empty(node: Node) -> bool:
    for child in node.children:
        if child.is_text:
            if (child.text or "").strip():
                return False
            continue
        if child.tag == "br":
            return False
        if not _is_effectively_empty(child):
            return False
    return True


def _compact_text_container_decls(node: Node, resolved: dict[int, ResolvedStyle], style) -> list[tuple[str, str]]:
    if style is None:
        return []
    display = (_resolved_value(style, "display") or "").lower()
    if display not in ("flex", "inline-flex"):
        return []

    flex_dir = (_resolved_value(style, "flex-direction") or "").lower()
    children = [c for c in node.children if not c.is_text]
    if not children:
        return []

    if flex_dir == "column" and _is_compact_text_stack(node, resolved):
        decls: list[tuple[str, str]] = []
        if _resolved_value(style, "min-height") is None:
            decls.append(("min-height", "0"))
        if _resolved_value(style, "overflow") is None:
            decls.append(("overflow", "hidden"))
        return decls

    height_px = _css_length_px(_resolved_value(style, "height"))
    if flex_dir == "row" and height_px is not None and height_px <= 32:
        if any(_is_compact_text_stack(child, resolved) for child in children):
            if _resolved_value(style, "overflow") is None:
                return [("overflow", "hidden")]
    return []


def _is_compact_text_stack(node: Node, resolved: dict[int, ResolvedStyle]) -> bool:
    style = resolved.get(id(node))
    if style is None:
        return False
    display = (_resolved_value(style, "display") or "").lower()
    flex_dir = (_resolved_value(style, "flex-direction") or "").lower()
    if display not in ("flex", "inline-flex") or flex_dir != "column":
        return False
    text_children = [c for c in node.children if not c.is_text]
    if not text_children:
        return False
    compact_count = 0
    for child in text_children:
        if not _has_only_text_children(child) and not _has_only_inline_text(child):
            return False
        child_style = resolved.get(id(child))
        if _compact_nowrap_label_decls(child_style):
            compact_count += 1
        else:
            return False
    return compact_count > 0


def _resolved_font_px(style) -> float | None:
    return _css_length_px(_resolved_value(style, "font-size"))


def _resolved_line_height_px(style, font_px: float) -> int | None:
    value = _resolved_value(style, "line-height")
    if value is None:
        return None
    v = value.strip().lower()
    if not v or v in ("normal", "inherit", "initial", "unset"):
        return None
    if re.match(r"^-?\d*\.?\d+$", v):
        try:
            return max(1, int(math.ceil(font_px * float(v))))
        except ValueError:
            return None
    px = _css_length_px(v)
    if px is not None:
        return max(1, int(math.ceil(px)))
    if v.endswith("%"):
        try:
            return max(1, int(math.ceil(font_px * float(v[:-1]) / 100.0)))
        except ValueError:
            return None
    return None


def _style_is_italic(style) -> bool:
    font_style = (_resolved_value(style, "font-style") or "").lower()
    unity_style = (_resolved_value(style, "-unity-font-style") or "").lower()
    return "italic" in font_style or "italic" in unity_style


def _font_weight_is_bold(style) -> bool:
    raw = (_resolved_value(style, "font-weight") or "").strip().lower()
    if raw in ("bold", "bolder"):
        return True
    try:
        return float(raw) >= 700
    except ValueError:
        return False


def _raw_font_weight_is_bold(text_raw: dict[str, str]) -> bool:
    raw = (
        text_raw.get("font-weight")
        or text_raw.get("--odd-font-weight")
        or ""
    ).strip().lower()
    if raw in ("bold", "bolder"):
        return True
    try:
        return float(raw) >= 700
    except ValueError:
        return False


def _css_length_px(value: str | None) -> float | None:
    if value is None:
        return None
    v = value.strip().lower()
    if not v or v in ("auto", "normal", "none"):
        return None
    m = re.match(r"^(-?\d*\.?\d+)(px|rem|em)?$", v)
    if not m:
        return None
    n = float(m.group(1))
    unit = m.group(2) or "px"
    if unit in ("rem", "em"):
        return n * 16.0
    return n


def _apply_text_transform(text: str, transform: str) -> str:
    t = transform.strip().lower()
    if t == "uppercase":
        return text.upper()
    if t == "lowercase":
        return text.lower()
    if t == "capitalize":
        return text.title()
    return text


def _resolved_var_effect_pairs(style: ResolvedStyle) -> list[tuple[str, str]]:
    custom_props = {k: v for k, v in style.base if k.startswith("--")}
    if not custom_props:
        return []
    out: list[tuple[str, str]] = []
    for prop, value in style.base:
        if prop not in _VAR_RESOLVED_PROPS or "var(" not in value:
            continue
        resolved = _resolve_css_vars(value, custom_props)
        if resolved != value:
            out.append((prop, resolved))
    return out


_VAR_RE = re.compile(r"var\(\s*(--[\w-]+)\s*(?:,\s*([^)]*))?\)")


def _resolve_css_vars(value: str, custom_props: dict[str, str]) -> str:
    previous = value
    for _ in range(8):
        def repl(m: re.Match) -> str:
            name = m.group(1)
            fallback = (m.group(2) or "").strip()
            if name in custom_props:
                return custom_props[name]
            return fallback if fallback else m.group(0)

        current = _VAR_RE.sub(repl, previous)
        if current == previous or "var(" not in current:
            return current
        previous = current
    return previous


def _gather_inline_text(node: Node) -> str:
    parts: list[str] = []
    for child in node.children:
        if child.is_text:
            parts.append(child.text or "")
            continue
        if child.tag == "br":
            parts.append("\n")
            continue
        rt = _RICH_TEXT.get(child.tag)
        inner = _gather_inline_text(child)
        if rt:
            parts.append(f"<{rt}>{inner}</{rt}>")
        else:
            parts.append(inner)
    return "".join(parts).strip()


def _has_only_text_children(node: Node) -> bool:
    saw_text = False
    for child in node.children:
        if child.is_text:
            if (child.text or "").strip():
                saw_text = True
            continue
        if child.tag in ("br",):
            saw_text = True
            continue
        return False
    return saw_text


_INLINE_TEXT_TAGS = {
    "a",
    "abbr",
    "b",
    "code",
    "em",
    "i",
    "label",
    "mark",
    "small",
    "span",
    "strong",
    "sub",
    "sup",
    "u",
}


def _has_inline_text_run(node: Node) -> bool:
    saw_text = False
    element_count = 0
    for child in node.children:
        if child.is_text:
            if child.text:
                saw_text = True
            continue
        if child.tag == "br":
            saw_text = True
            continue
        if child.tag not in _INLINE_TEXT_TAGS or not _has_only_inline_text(child):
            return False
        element_count += 1
    # Container needs row layout when it mixes raw text with inline elements
    # (classic <div>text<span>...</span></div>) or stacks multiple inline
    # children side by side (<div><span>...</span><span>...</span></div>).
    return (saw_text and element_count > 0) or element_count >= 2


def _has_only_inline_text(node: Node) -> bool:
    saw_text = False
    for child in node.children:
        if child.is_text:
            if child.text:
                saw_text = True
            continue
        if child.tag == "br":
            saw_text = True
            continue
        if child.tag not in _INLINE_TEXT_TAGS or not _has_only_inline_text(child):
            return False
        saw_text = True
    return saw_text


def _can_flatten_text_attr(
    node: Node,
    resolved: dict[int, ResolvedStyle],
    uxml_tag: str,
) -> bool:
    if not _is_button_tag(uxml_tag):
        return True
    return not _button_has_structured_inline_text(node, resolved)


def _button_has_structured_inline_text(
    node: Node,
    resolved: dict[int, ResolvedStyle],
) -> bool:
    inline_elements = [child for child in node.children if not child.is_text]
    if len(inline_elements) > 1:
        return True
    return any(_inline_element_carries_button_structure(child, resolved) for child in inline_elements)


def _inline_element_carries_button_structure(
    node: Node,
    resolved: dict[int, ResolvedStyle],
) -> bool:
    if node.tag == "br":
        return False
    if any(node.attrs.get(attr) for attr in ("class", "id", "style", "title")):
        return True
    if node.tag in ("strong", "em", "b", "i", "u", "code", "small"):
        return True
    style = resolved.get(id(node))
    if _text_raw_from_style(style):
        return True
    for child in node.children:
        if not child.is_text and _inline_element_carries_button_structure(child, resolved):
            return True
    return False


def _pseudo_has_visible_output(decls: list[tuple[str, str]]) -> bool:
    for prop, value in decls:
        low = prop.lower()
        if low.startswith("--odd-"):
            return True
        if low.startswith(("background", "border", "outline", "filter")):
            return True
        if low in ("color", "opacity", "width", "height", "min-width", "min-height"):
            if value.strip().lower() not in ("0", "0px", "none", "transparent"):
                return True
    return False


_SPACED_TEXT_MIN_LETTER_PX = 2.0
_SPACED_TEXT_MAX_CHARS = 40
_SPACED_TEXT_TRACKING_SCALE = 0.45
_SPACED_TEXT_SPACE_EM = 0.22


def _emit_generated_text_label(
    text: str,
    indent: int,
    state: _EmitState,
    effective_text_raw: dict[str, str],
    label_decls: list[tuple[str, str]],
    *,
    preserve: bool = False,
    class_name: str | None = None,
    picking_ignore: bool = False,
    dynamic_font: bool = False,
    extra_container_decls: list[tuple[str, str]] | None = None,
) -> str:
    value = text if preserve else text.strip()
    state.record_font_text(value, effective_text_raw, dynamic=dynamic_font)
    if not _should_emit_spaced_text(value, effective_text_raw):
        if extra_container_decls:
            # Gap/override classes must be emitted at this point in the cascade
            # so a generated Label's own style cannot overwrite them.
            extra_class, created = state.class_for_generated_decls(
                extra_container_decls,
                cache=False,
            )
            if created:
                state.stats.inline_overrides += 1
            class_name = f"{class_name} {extra_class}" if class_name else extra_class
        return _emit_text_label(
            value,
            indent,
            preserve=True,
            class_name=class_name,
            picking_ignore=picking_ignore,
        )

    letter_px = _css_length_px(effective_text_raw.get("letter-spacing")) or 0.0
    tracking_px = letter_px * _SPACED_TEXT_TRACKING_SCALE
    font_px = _css_length_px(effective_text_raw.get("font-size")) or 16.0
    row_decls = [
        ("display", "flex"),
        ("flex-direction", "row"),
        ("align-items", "center"),
        ("justify-content", "center"),
        ("flex-shrink", "0"),
    ]
    if extra_container_decls:
        row_decls = _combine_generated_decls(row_decls, extra_container_decls)
    row_class, created = state.class_for_generated_decls(row_decls)
    if created:
        state.stats.inline_overrides += 1

    base_label_decls = [
        (prop, val)
        for prop, val in (label_decls or _direct_text_label_decls(effective_text_raw))
        if prop not in ("letter-spacing", "word-spacing", "text-overflow")
    ]
    char_decls = _combine_generated_decls(
        base_label_decls,
        [
            ("padding", "0"),
            ("margin-right", _format_px(tracking_px)),
            ("-unity-paragraph-spacing", "0"),
        ],
    )
    last_char_decls = _combine_generated_decls(
        base_label_decls,
        [
            ("padding", "0"),
            ("margin-right", "0"),
            ("-unity-paragraph-spacing", "0"),
        ],
    )
    char_class, created = state.class_for_generated_decls(char_decls)
    if created:
        state.stats.inline_overrides += 1
    last_char_class, created = state.class_for_generated_decls(last_char_decls)
    if created:
        state.stats.inline_overrides += 1

    word_px = _css_length_px(effective_text_raw.get("word-spacing")) or 0.0
    # Per-character Labels already include font advance and side bearings.
    # Use a measured fraction of the requested browser tracking so the fallback
    # widens tight Unity text without making HUD labels visibly over-spaced.
    space_px = max(1.0, font_px * _SPACED_TEXT_SPACE_EM + tracking_px + word_px)
    space_class, created = state.class_for_generated_decls([
        ("width", _format_px(space_px)),
        ("height", "1px"),
        ("flex-shrink", "0"),
    ])
    if created:
        state.stats.inline_overrides += 1

    pick_attr = ' picking-mode="Ignore"' if picking_ignore else ""
    pad = " " * indent
    child_pad = " " * (indent + 2)
    last_index = _last_non_space_index(value)
    body: list[str] = []
    for idx, ch in enumerate(value):
        if ch.isspace():
            body.append(f'{child_pad}<odd:Html2UxmlElement{pick_attr} class="{_xml_escape(space_class)}" />\n')
            continue
        cls = last_char_class if idx == last_index else char_class
        body.append(f'{child_pad}<odd:Html2UxmlLabel{pick_attr} class="{_xml_escape(cls)}" text="{_xml_escape(ch)}" />\n')
    return f'{pad}<odd:Html2UxmlElement{pick_attr} class="{_xml_escape(row_class)}">\n{"".join(body)}{pad}</odd:Html2UxmlElement>\n'


def _should_emit_spaced_text(text: str, effective_text_raw: dict[str, str]) -> bool:
    value = text.strip()
    if not value or len(value) > _SPACED_TEXT_MAX_CHARS:
        return False
    if "\n" in value or "\r" in value or "<" in value or ">" in value:
        return False
    if not any(ch.isalpha() for ch in value):
        return False
    if effective_text_raw.get("white-space", "").strip().lower() in ("pre", "pre-wrap", "break-spaces"):
        return False
    if effective_text_raw.get("text-overflow", "").strip().lower() == "ellipsis":
        return False
    letter_px = _css_length_px(effective_text_raw.get("letter-spacing"))
    if letter_px is None or letter_px < _SPACED_TEXT_MIN_LETTER_PX:
        return False
    font_px = _css_length_px(effective_text_raw.get("font-size"))
    if font_px is None or font_px > 18:
        return False
    return _last_non_space_index(value) >= 0


def _last_non_space_index(text: str) -> int:
    for idx in range(len(text) - 1, -1, -1):
        if not text[idx].isspace():
            return idx
    return -1


def _format_px(value: float) -> str:
    if abs(value - round(value)) < 0.0001:
        return f"{int(round(value))}px"
    return f"{value:.3f}".rstrip("0").rstrip(".") + "px"


def _emit_text_label(
    text: str,
    indent: int,
    *,
    preserve: bool = False,
    class_name: str | None = None,
    picking_ignore: bool = False,
) -> str:
    pad = " " * indent
    value = text if preserve else text.strip()
    class_attr = f' class="{_xml_escape(class_name)}"' if class_name else ""
    pick_attr = ' picking-mode="Ignore"' if picking_ignore else ""
    return f'{pad}<odd:Html2UxmlLabel{pick_attr}{class_attr} text="{_xml_escape(value)}" />\n'


def _xml_escape(s: str) -> str:
    return (
        s.replace("&", "&amp;")
         .replace("<", "&lt;")
         .replace(">", "&gt;")
         .replace('"', "&quot;")
    )


# ---------------------------------------------------------------------------
# USS emission
# ---------------------------------------------------------------------------


def _emit_uss(state: _EmitState) -> str:
    if not state.uss_order:
        return "/* no styles */\n"
    lines: list[str] = []
    for sel in state.uss_order:
        decls = state.uss_rules[sel]
        dedup: dict[str, str] = {}
        for k, v in decls:
            dedup[k] = v
        if not dedup:
            continue
        lines.append(f"{sel} {{")
        for k, v in dedup.items():
            lines.append(f"    {k}: {v};")
        lines.append("}")
        lines.append("")
    return "\n".join(lines)
