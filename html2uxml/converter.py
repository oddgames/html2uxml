"""Top-level converter: HTML(+CSS) -> UXML + USS strings."""
from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path

from .css_parser import Declaration, parse_css
from .html_parser import Node, parse_html
from .mappings import map_declarations, map_element
from .resolver import _ParsedRule, parse_rules, resolve, ResolvedStyle


@dataclass
class ConvertResult:
    uxml: str
    uss: str
    warnings: list[str] = field(default_factory=list)
    stats: "ConvertStats | None" = None


@dataclass
class ConvertStats:
    elements: int = 0
    labels: int = 0
    buttons: int = 0
    bridge_boxes: int = 0
    uss_rules: int = 0
    bridged_props: dict = field(default_factory=dict)   # prop -> count
    dropped_props: dict = field(default_factory=dict)   # prop -> count


def convert(
    html: str,
    extra_css: str = "",
    *,
    uss_filename: str = "styles.uss",
    base_dir: Path | None = None,
) -> ConvertResult:
    """Convert HTML+CSS to UXML and USS strings.

    `extra_css` is concatenated after any <style> blocks found in the HTML.
    `base_dir` is used to resolve <link rel="stylesheet" href="...">.
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
    rules = parse_css("\n".join(css_chunks))
    parsed_rules = parse_rules(rules)
    resolved = resolve(parsed.root, parsed_rules)

    state = _EmitState()
    body_xml = _emit_node_children(parsed.root, resolved, state, indent=2)
    uxml = _wrap_uxml(body_xml, uss_filename, with_bridge=state.used_bridge)
    uss = _emit_uss(state)
    state.stats.uss_rules = len(state.uss_order)
    return ConvertResult(uxml=uxml, uss=uss, warnings=state.warnings, stats=state.stats)


# ---------------------------------------------------------------------------
# Emit state: gathers per-node generated classes and the USS rules they yield.
# ---------------------------------------------------------------------------


@dataclass
class _EmitState:
    # selector -> list of (prop, value)
    uss_rules: dict[str, list[tuple[str, str]]] = field(default_factory=dict)
    # rule order, for stable output
    uss_order: list[str] = field(default_factory=list)
    warnings: list[str] = field(default_factory=list)
    used_bridge: bool = False
    stats: ConvertStats = field(default_factory=ConvertStats)
    _gen_counter: int = 0

    def add_rule(self, selector: str, decls: list[tuple[str, str]]) -> None:
        if not decls:
            return
        if selector in self.uss_rules:
            # Append (later wins on dedup at write time).
            self.uss_rules[selector].extend(decls)
        else:
            self.uss_rules[selector] = list(decls)
            self.uss_order.append(selector)

    def gen_class(self) -> str:
        self._gen_counter += 1
        return f"h2u-{self._gen_counter}"


# ---------------------------------------------------------------------------
# UXML emission
# ---------------------------------------------------------------------------


_INLINE_TAGS = {"b", "strong", "i", "em", "u"}
_RICH_TEXT = {
    "b": "b", "strong": "b",
    "i": "i", "em": "i",
    "u": "u",
    "br": "br",
}


def _wrap_uxml(body: str, uss_filename: str, *, with_bridge: bool) -> str:
    bridge_ns = ' xmlns:gg="HtmlToUxml.Bridge"' if with_bridge else ""
    return (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<ui:UXML xmlns:ui="UnityEngine.UIElements" '
        'xmlns:uie="UnityEditor.UIElements"'
        f'{bridge_ns} '
        'xsi:noNamespaceSchemaLocation="../../UIElementsSchema/UIElements.xsd" '
        'xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">\n'
        f'  <Style src="{_xml_escape(uss_filename)}" />\n'
        f'{body}'
        '</ui:UXML>\n'
    )


def _record_warnings(state: _EmitState, warnings: list[str]) -> None:
    for w in warnings:
        state.warnings.append(w)
        # Pull the property name out of "<verb>: <prop>: <value>" if present.
        head, _, _rest = w.partition(":")
        prop = ""
        if "dropped" in head or "approximated" in head or "unmapped" in head or "unsupported" in head:
            # Form: "verb...: <prop>: <value>"
            after = w.split(":", 2)
            if len(after) >= 2:
                prop = after[1].strip().split(":")[0]
        if prop:
            state.stats.dropped_props[prop] = state.stats.dropped_props.get(prop, 0) + 1


def _emit_node_children(parent: Node, resolved: dict[int, ResolvedStyle],
                        state: _EmitState, indent: int) -> str:
    out_parts: list[str] = []
    for child in parent.children:
        if child.is_text:
            # Top-level stray text becomes a Label.
            out_parts.append(_emit_text_label(child.text or "", indent))
            continue
        out_parts.append(_emit_node(child, resolved, state, indent))
    return "".join(out_parts)


def _emit_node(node: Node, resolved: dict[int, ResolvedStyle],
               state: _EmitState, indent: int) -> str:
    uxml_tag, extra_attrs, text_mode = map_element(node.tag, node.attrs)
    pad = " " * indent

    # Pull HTML class= and any generated class for this node's resolved styles.
    classes = node.classes()
    style = resolved.get(id(node))

    own_class = None
    needs_bridge = False
    if style is not None and (style.base or style.pseudo_rules):
        # Always emit per-element class so inline styles materialize as USS.
        own_class = state.gen_class()
        mapped = map_declarations(style.base)
        _record_warnings(state, mapped.warnings)
        for k, _ in mapped.decls:
            if k.startswith("--gg-"):
                needs_bridge = True
                state.stats.bridged_props[k] = state.stats.bridged_props.get(k, 0) + 1
        if mapped.decls:
            state.add_rule(f".{own_class}", mapped.decls)
        for sel_raw, decls in style.pseudo_rules:
            rewritten = _attach_pseudo_to_class(sel_raw, own_class)
            mapped_pseudo = map_declarations([(d.prop, d.value) for d in decls])
            _record_warnings(state, mapped_pseudo.warnings)
            for k, _ in mapped_pseudo.decls:
                if k.startswith("--gg-"):
                    needs_bridge = True
                    state.stats.bridged_props[k] = state.stats.bridged_props.get(k, 0) + 1
            state.add_rule(rewritten, mapped_pseudo.decls)
    if needs_bridge and uxml_tag == "ui:VisualElement":
        uxml_tag = "gg:BridgeBox"
        state.used_bridge = True
        state.stats.bridge_boxes += 1
    if uxml_tag == "ui:VisualElement":
        state.stats.elements += 1
    elif uxml_tag == "ui:Label":
        state.stats.labels += 1
    elif uxml_tag == "ui:Button":
        state.stats.buttons += 1

    # Text handling.
    text_attr = None
    inner_children = list(node.children)
    if text_mode == "text":
        text_attr = _gather_inline_text(node)
        inner_children = []  # already folded in
    elif text_mode == "label":
        # We'll emit child Labels for stray text runs (handled in recursion).
        pass

    # Build attributes
    attrs_out: list[tuple[str, str]] = []
    for k, v in extra_attrs.items():
        attrs_out.append((k, v))
    cls_list = list(classes)
    if own_class:
        cls_list.append(own_class)
    if cls_list:
        attrs_out.append(("class", " ".join(cls_list)))
    if "id" in node.attrs and node.attrs["id"]:
        attrs_out.append(("name", node.attrs["id"]))
    if text_attr is not None:
        attrs_out.append(("text", text_attr))
    # Forward title/alt as tooltip.
    if "title" in node.attrs and node.attrs["title"]:
        attrs_out.append(("tooltip", node.attrs["title"]))
    elif "alt" in node.attrs and node.attrs["alt"]:
        attrs_out.append(("tooltip", node.attrs["alt"]))

    attr_str = "".join(f' {k}="{_xml_escape(v)}"' for k, v in attrs_out)

    # Determine if there are rendered inner children.
    rendered_children: list[str] = []
    if inner_children:
        for child in inner_children:
            if child.is_text:
                t = (child.text or "").strip()
                if t and uxml_tag == "ui:VisualElement":
                    rendered_children.append(_emit_text_label(t, indent + 2))
                continue
            rendered_children.append(_emit_node(child, resolved, state, indent + 2))

    if not rendered_children:
        return f"{pad}<{uxml_tag}{attr_str} />\n"
    body = "".join(rendered_children)
    return f"{pad}<{uxml_tag}{attr_str}>\n{body}{pad}</{uxml_tag}>\n"


def _gather_inline_text(node: Node) -> str:
    """Concatenate direct text and inline-tag descendants into a rich-text string."""
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


def _emit_text_label(text: str, indent: int) -> str:
    pad = " " * indent
    return f'{pad}<ui:Label text="{_xml_escape(text.strip())}" />\n'


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
        # Dedup: later wins.
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


def _attach_pseudo_to_class(selector_raw: str, generated_class: str) -> str:
    """Rewrite the rightmost compound of a selector to use our generated class.

    `.btn:hover` -> `.h2u-3:hover`
    `:hover`     -> `.h2u-3:hover`
    """
    raw = selector_raw.strip()
    # Find the start of the rightmost compound.
    # Walk backwards until whitespace or a combinator.
    i = len(raw)
    while i > 0 and raw[i - 1] not in " \t>+~":
        i -= 1
    head = raw[:i]
    tail = raw[i:]
    # Strip any tag/class/id from tail; keep only the pseudo suffix.
    pseudo_start = 0
    if tail.startswith(":"):
        pseudo_start = 0
    else:
        # Find first ":" in tail.
        idx = tail.find(":")
        pseudo_start = idx if idx >= 0 else len(tail)
    pseudo_part = tail[pseudo_start:] if pseudo_start < len(tail) else ""
    return f"{head}.{generated_class}{pseudo_part}"
