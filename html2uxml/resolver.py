"""Match CSS rules against the parsed HTML tree and produce per-element styles."""
from __future__ import annotations

from dataclasses import dataclass, field

from .css_parser import (
    Declaration,
    Rule,
    Selector,
    parse_selector,
)
from .html_parser import Node


@dataclass
class _ParsedRule:
    selectors: list[Selector]    # one entry per comma-separated selector
    declarations: list[Declaration]
    order: int


def parse_rules(rules: list[Rule]) -> list[_ParsedRule]:
    out: list[_ParsedRule] = []
    for r in rules:
        sels: list[Selector] = []
        for raw in r.selectors:
            s = parse_selector(raw)
            if s is not None:
                sels.append(s)
        if sels:
            out.append(_ParsedRule(selectors=sels, declarations=r.declarations, order=r.order))
    return out


# ---------------------------------------------------------------------------
# Selector matching
# ---------------------------------------------------------------------------


def _matches_compound(node: Node, comp) -> bool:
    if comp.tag != "*" and node.tag != comp.tag:
        return False
    if comp.id is not None and node.attrs.get("id") != comp.id:
        return False
    if comp.classes:
        node_classes = set(node.classes())
        for c in comp.classes:
            if c not in node_classes:
                return False
    # Pseudo-classes/elements aren't matched here; we keep them on the rule
    # and emit them verbatim into USS so Unity handles :hover/:focus/:active.
    return True


def _matches_chain(node: Node, chain: list, parent_chain: list[Node]) -> bool:
    """Walk a parsed selector chain right-to-left across (node + ancestors)."""
    # chain is [(combinator_to_left, compound), ...]; rightmost is the target.
    if not chain:
        return False
    combinator, compound = chain[-1]
    if not _matches_compound(node, compound):
        return False
    if len(chain) == 1:
        return True
    rest = chain[:-1]
    # Walk leftward through ancestors.
    if combinator == ">":
        # Direct parent only.
        if not parent_chain:
            return False
        return _matches_chain(parent_chain[-1], rest, parent_chain[:-1])
    # Default: descendant. Try every ancestor.
    for i in range(len(parent_chain) - 1, -1, -1):
        if _matches_chain(parent_chain[i], rest, parent_chain[:i]):
            return True
    return False


def selector_matches(node: Node, selector: Selector, ancestors: list[Node]) -> bool:
    return _matches_chain(node, selector.chain, ancestors)


# ---------------------------------------------------------------------------
# Cascade
# ---------------------------------------------------------------------------


@dataclass
class ResolvedStyle:
    """Final styles for one element, plus surfaced rules (with pseudos) keyed by selector."""
    base: list[tuple[str, str]] = field(default_factory=list)
    # For each pseudo-bearing selector that matched, keep the declarations so we
    # can emit a synthetic class with the pseudo intact.
    pseudo_rules: list[tuple[str, list[Declaration]]] = field(default_factory=list)


def resolve(root: Node, parsed_rules: list[_ParsedRule]) -> dict[int, ResolvedStyle]:
    """Walk the tree; return a map from id(node) to ResolvedStyle."""
    out: dict[int, ResolvedStyle] = {}
    _walk(root, [], parsed_rules, out)
    return out


def _walk(node: Node, ancestors: list[Node], rules: list[_ParsedRule],
          out: dict[int, ResolvedStyle]) -> None:
    if not node.is_text and node.tag != "__root__":
        out[id(node)] = _resolve_for(node, ancestors, rules)
    for child in node.children:
        if child.is_text:
            continue
        _walk(child, ancestors + [node], rules, out)


def _has_pseudo(selector: Selector) -> bool:
    return any(c.pseudo for _, c in selector.chain)


def _resolve_for(node: Node, ancestors: list[Node], rules: list[_ParsedRule]) -> ResolvedStyle:
    # Collect (specificity, source-order, declarations) for matched rules
    # without pseudo-classes; pseudo-bearing matches are surfaced separately.
    matches: list[tuple[tuple[int, int, int], int, list[Declaration]]] = []
    pseudo_rules: list[tuple[str, list[Declaration]]] = []
    for rule in rules:
        for sel in rule.selectors:
            if not selector_matches(node, sel, ancestors):
                continue
            if _has_pseudo(sel):
                pseudo_rules.append((sel.raw, rule.declarations))
            else:
                matches.append((sel.specificity(), rule.order, rule.declarations))
    matches.sort(key=lambda t: (t[0], t[1]))

    # Apply: lowest specificity first, later wins. !important wins over normal.
    bag: dict[str, tuple[str, bool]] = {}  # prop -> (value, important)
    for _, _, decls in matches:
        for d in decls:
            cur = bag.get(d.prop)
            if cur is None or (d.important and not cur[1]) or (d.important == cur[1]):
                bag[d.prop] = (d.value, d.important)

    # Inline style="" attribute beats anything except !important rules.
    inline = node.attrs.get("style", "")
    if inline:
        from .css_parser import _parse_declarations
        for d in _parse_declarations(inline):
            cur = bag.get(d.prop)
            if cur is None or not cur[1] or d.important:
                bag[d.prop] = (d.value, d.important)

    base = [(prop, val) for prop, (val, _) in bag.items()]
    return ResolvedStyle(base=base, pseudo_rules=pseudo_rules)
