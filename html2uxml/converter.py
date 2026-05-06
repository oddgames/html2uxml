"""Top-level converter: HTML(+CSS) -> UXML + USS strings."""
from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path

from .css_parser import _parse_declarations, parse_css, parse_selector
from .html_parser import Node, parse_html
from .mappings import map_declarations, map_element
from .resolver import (
    _ParsedRule, parse_rules, resolve, ResolvedStyle, selector_matches,
)


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
    images: int = 0
    bridge_boxes: int = 0
    inline_overrides: int = 0     # number of h2u-N rules emitted
    css_class_rules: int = 0      # number of original CSS rules emitted
    uss_rules: int = 0            # final rule count
    bridged_props: dict = field(default_factory=dict)
    dropped_props: dict = field(default_factory=dict)
    skipped_at_rules: int = 0     # @media/@keyframes/@supports/@import


def convert(
    html: str,
    extra_css: str = "",
    *,
    uss_filename: str = "styles.uss",
    base_dir: Path | None = None,
    select: str | None = None,
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
    rules = parse_css("\n".join(css_chunks))
    parsed_rules = parse_rules(rules)

    # Subtree selection: find the first matching node, wrap it in a synthetic
    # root so the converter renders that element AND its descendants.
    selection_warning: str | None = None
    if select:
        target = _find_first_match(parsed.root, select)
        if target is None:
            selection_warning = f"selector matched no element: {select!r}"
        else:
            new_root = Node(tag="__root__")
            new_root.children = [target]
            parsed.root = new_root

    resolved = resolve(parsed.root, parsed_rules)

    state = _EmitState()
    if selection_warning:
        state.warnings.append(selection_warning)

    rule_bridge_flags = _compute_rule_bridge_flags(parsed_rules)

    # Prune USS to selectors that actually hit something in the (sub)tree.
    used_selectors: set = set()
    for rs in resolved.values():
        used_selectors.update(rs.matched_selectors)
    _emit_css_rules(parsed_rules, state, allowed_selectors=used_selectors)

    body_xml = _emit_node_children(parsed.root, resolved, state, rule_bridge_flags, indent=2)
    uxml = _wrap_uxml(body_xml, uss_filename, with_bridge=state.used_bridge)
    uss = _emit_uss(state)
    state.stats.uss_rules = len(state.uss_order)
    return ConvertResult(uxml=uxml, uss=uss, warnings=state.warnings, stats=state.stats)


def _find_first_match(root: Node, selector_raw: str) -> Node | None:
    sel = parse_selector(selector_raw)
    if sel is None:
        return None
    found: list[Node] = []

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
                found.append(node)
                return
        for child in node.children:
            if child.is_text:
                continue
            walk(child, ancestors + [node])

    walk(root, [])
    return found[0] if found else None


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
    _gen_counter: int = 0

    def add_rule(self, selector: str, decls: list[tuple[str, str]]) -> None:
        if not decls:
            return
        if selector in self.uss_rules:
            self.uss_rules[selector].extend(decls)
        else:
            self.uss_rules[selector] = list(decls)
            self.uss_order.append(selector)

    def gen_class(self) -> str:
        self._gen_counter += 1
        return f"h2u-{self._gen_counter}"


# ---------------------------------------------------------------------------
# CSS rule emission (originally-named selectors)
# ---------------------------------------------------------------------------


def _emit_css_rules(parsed_rules: list[_ParsedRule], state: _EmitState,
                    *, allowed_selectors: set | None = None) -> None:
    for rule in parsed_rules:
        # Drop the rule entirely if none of its selectors hit the (sub)tree.
        if allowed_selectors is not None and not any(
            s.raw in allowed_selectors for s in rule.selectors
        ):
            continue
        mapped = map_declarations([(d.prop, d.value) for d in rule.declarations])
        _record_warnings(state, mapped.warnings)
        if not mapped.decls:
            continue
        for k, _ in mapped.decls:
            if k.startswith("--gg-"):
                state.stats.bridged_props[k] = state.stats.bridged_props.get(k, 0) + 1
                state.used_bridge = True
        for sel in rule.selectors:
            if allowed_selectors is not None and sel.raw not in allowed_selectors:
                continue
            state.add_rule(sel.raw, mapped.decls)
            state.stats.css_class_rules += 1


def _compute_rule_bridge_flags(parsed_rules: list[_ParsedRule]) -> list[bool]:
    """For each parsed rule (parallel to parsed_rules), True iff its mapped
    declarations include any --gg-* custom property."""
    flags = []
    for rule in parsed_rules:
        mapped = map_declarations([(d.prop, d.value) for d in rule.declarations])
        flags.append(any(k.startswith("--gg-") for k, _ in mapped.decls))
    return flags


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
        prop = ""
        if any(token in w for token in ("dropped", "approximated", "unmapped", "unsupported")):
            after = w.split(":", 2)
            if len(after) >= 2:
                prop = after[1].strip().split(":")[0]
        if prop:
            state.stats.dropped_props[prop] = state.stats.dropped_props.get(prop, 0) + 1


def _emit_node_children(parent: Node, resolved: dict[int, ResolvedStyle],
                        state: _EmitState, rule_bridge_flags: list[bool],
                        indent: int) -> str:
    out_parts: list[str] = []
    li_counter = 1
    for child in parent.children:
        if child.is_text:
            out_parts.append(_emit_text_label(child.text or "", indent))
            continue
        out_parts.append(
            _emit_node(child, resolved, state, rule_bridge_flags, indent,
                       parent=parent, li_index=li_counter)
        )
        if child.tag == "li":
            li_counter += 1
    return "".join(out_parts)


def _emit_node(node: Node, resolved: dict[int, ResolvedStyle],
               state: _EmitState, rule_bridge_flags: list[bool],
               indent: int, *, parent: Node | None = None,
               li_index: int = 1) -> str:
    uxml_tag, extra_attrs, text_mode = map_element(node.tag, node.attrs)
    pad = " " * indent
    classes = list(node.classes())
    style = resolved.get(id(node))

    # Bridge promotion: triggered by either a matching CSS rule with --gg-* or
    # an inline style="" with --gg-*. Compute both.
    needs_bridge = False
    if style is not None:
        for rule_idx in style.matched_rule_indices:
            if 0 <= rule_idx < len(rule_bridge_flags) and rule_bridge_flags[rule_idx]:
                needs_bridge = True
                break

    # Hoist inline style="" + unsupported-selector rule decls into h2u-N.
    own_class = None
    inline_pairs: list[tuple[str, str]] = []
    if style is not None and style.unsupported_decls:
        inline_pairs.extend((d.prop, d.value) for d in style.unsupported_decls)
    inline = node.attrs.get("style", "")
    if inline:
        inline_pairs.extend((d.prop, d.value) for d in _parse_declarations(inline))
    if inline_pairs:
        mapped = map_declarations(inline_pairs)
        _record_warnings(state, mapped.warnings)
        if mapped.decls:
            own_class = state.gen_class()
            state.add_rule(f".{own_class}", mapped.decls)
            state.stats.inline_overrides += 1
            for k, _ in mapped.decls:
                if k.startswith("--gg-"):
                    needs_bridge = True
                    state.stats.bridged_props[k] = state.stats.bridged_props.get(k, 0) + 1

    # ScrollView promotion: any element with effective overflow auto/scroll
    # gets emitted as a ScrollView so Unity scrolls instead of clipping.
    overflow = _resolved_value(style, "overflow")
    if overflow and overflow.lower() in ("auto", "scroll") and uxml_tag == "ui:VisualElement":
        uxml_tag = "ui:ScrollView"

    text_transform = _resolved_value(style, "text-transform")
    text_decoration = _resolved_value(style, "text-decoration")

    if needs_bridge and uxml_tag == "ui:VisualElement":
        uxml_tag = "gg:BridgeBox"
        state.used_bridge = True
        state.stats.bridge_boxes += 1
    elif uxml_tag == "ui:VisualElement":
        state.stats.elements += 1
    if uxml_tag == "ui:Label":
        state.stats.labels += 1
    elif uxml_tag == "ui:Button":
        state.stats.buttons += 1
    elif uxml_tag == "ui:Image":
        state.stats.images += 1

    # <img>: synthesize a per-element class with background-image so the URL
    # ends up in USS where the user can swap it for a Unity asset reference.
    if node.tag == "img":
        src = node.attrs.get("src", "")
        if src:
            if own_class is None:
                own_class = state.gen_class()
                state.stats.inline_overrides += 1
            state.add_rule(
                f".{own_class}",
                [("background-image", f'url("{src}")')],
            )

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
    inner_children = list(node.children)

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

    if text_mode == "text":
        text_attr = _gather_inline_text(node)
        if text_transform:
            text_attr = _apply_text_transform(text_attr, text_transform)
        if text_decoration and "underline" in text_decoration.lower():
            text_attr = f"<u>{text_attr}</u>"
        inner_children = []

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
    if foldout_text is not None:
        attrs_out.append(("text", foldout_text))
    if text_attr is not None:
        attrs_out.append(("text", text_attr))
    for k, v in progress_attrs:
        attrs_out.append((k, v))
    if select_choices is not None and select_choices:
        attrs_out.append(("choices", ",".join(select_choices)))
        inner_children = []  # don't render <option> children
    if "title" in node.attrs and node.attrs["title"]:
        attrs_out.append(("tooltip", node.attrs["title"]))
    elif "alt" in node.attrs and node.attrs["alt"]:
        attrs_out.append(("tooltip", node.attrs["alt"]))

    attr_str = "".join(f' {k}="{_xml_escape(v)}"' for k, v in attrs_out)

    rendered_children: list[str] = []

    # ::before pseudo-element synthesized as a leading Label child.
    if style is not None and style.before_content is not None:
        rendered_children.append(
            _emit_synthetic_pseudo(style.before_content, style.before_decls, state, indent + 2)
        )

    # list-style marker for <li> based on parent <ul>/<ol>.
    li_marker = _list_marker(node, parent=parent, ordinal=li_index)
    if li_marker:
        rendered_children.append(_emit_text_label(li_marker, indent + 2))

    if inner_children:
        inner_li = 1
        for child in inner_children:
            if child.is_text:
                t = (child.text or "").strip()
                if t and uxml_tag in ("ui:VisualElement", "gg:BridgeBox", "ui:ScrollView"):
                    if text_transform:
                        t = _apply_text_transform(t, text_transform)
                    rendered_children.append(_emit_text_label(t, indent + 2))
                continue
            rendered_children.append(
                _emit_node(child, resolved, state, rule_bridge_flags, indent + 2,
                           parent=node, li_index=inner_li)
            )
            if child.tag == "li":
                inner_li += 1

    if style is not None and style.after_content is not None:
        rendered_children.append(
            _emit_synthetic_pseudo(style.after_content, style.after_decls, state, indent + 2)
        )

    if not rendered_children:
        return f"{pad}<{uxml_tag}{attr_str} />\n"
    body = "".join(rendered_children)
    return f"{pad}<{uxml_tag}{attr_str}>\n{body}{pad}</{uxml_tag}>\n"


def _emit_synthetic_pseudo(content: str, decls, state: _EmitState, indent: int) -> str:
    """Emit a Label child that materializes a ::before / ::after rule's content."""
    if not decls:
        return _emit_text_label(content, indent)
    own_class = state.gen_class()
    mapped = map_declarations([(d.prop, d.value) for d in decls])
    if mapped.decls:
        state.add_rule(f".{own_class}", mapped.decls)
        state.stats.inline_overrides += 1
    pad = " " * indent
    return f'{pad}<ui:Label class="{own_class}" text="{_xml_escape(content)}" />\n'


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


def _apply_text_transform(text: str, transform: str) -> str:
    t = transform.strip().lower()
    if t == "uppercase":
        return text.upper()
    if t == "lowercase":
        return text.lower()
    if t == "capitalize":
        return text.title()
    return text


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
