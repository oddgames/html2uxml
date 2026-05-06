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
    for op, name, value in comp.attrs:
        actual = node.attrs.get(name)
        if op == "":
            if actual is None:
                return False
        elif op == "=":
            if actual != value:
                return False
        elif op == "~=":
            if actual is None or value not in actual.split():
                return False
        elif op == "|=":
            if actual is None or (actual != value and not actual.startswith(value + "-")):
                return False
        elif op == "^=":
            if actual is None or not actual.startswith(value):
                return False
        elif op == "$=":
            if actual is None or not actual.endswith(value):
                return False
        elif op == "*=":
            if actual is None or value not in actual:
                return False
    return True


def _matches_chain(node: Node, chain: list, parent_chain: list[Node],
                   sibling_index: int, siblings: list[Node]) -> bool:
    if not chain:
        return False
    combinator, compound = chain[-1]
    if not _matches_compound(node, compound):
        return False
    if len(chain) == 1:
        return True
    rest = chain[:-1]
    if combinator == ">":
        if not parent_chain:
            return False
        parent = parent_chain[-1]
        gs = [c for c in parent.children if not c.is_text]
        try:
            pidx = gs.index(parent)
        except ValueError:
            pidx = -1
        return _matches_chain(parent, rest, parent_chain[:-1], pidx, gs)
    if combinator == "+":
        if sibling_index <= 0:
            return False
        prev = siblings[sibling_index - 1]
        return _matches_chain(prev, rest, parent_chain, sibling_index - 1, siblings)
    if combinator == "~":
        for i in range(sibling_index - 1, -1, -1):
            if _matches_chain(siblings[i], rest, parent_chain, i, siblings):
                return True
        return False
    # Default: descendant. Try every ancestor.
    for i in range(len(parent_chain) - 1, -1, -1):
        # Compute that ancestor's siblings/index for further left walks.
        anc = parent_chain[i]
        if i > 0:
            grand = parent_chain[i - 1]
            sibs = [c for c in grand.children if not c.is_text]
            try:
                idx = sibs.index(anc)
            except ValueError:
                idx = -1
        else:
            sibs, idx = [anc], 0
        if _matches_chain(anc, rest, parent_chain[:i], idx, sibs):
            return True
    return False


def selector_matches(node: Node, selector: Selector,
                     ancestors: list[Node], sibling_index: int,
                     siblings: list[Node]) -> bool:
    return _matches_chain(node, selector.chain, ancestors, sibling_index, siblings)


# ---------------------------------------------------------------------------
# Cascade
# ---------------------------------------------------------------------------


@dataclass
class ResolvedStyle:
    """Final styles for one element."""
    base: list[tuple[str, str]] = field(default_factory=list)
    pseudo_rules: list[tuple[str, list[Declaration]]] = field(default_factory=list)
    matched_rule_indices: list[int] = field(default_factory=list)
    # Declarations from selectors that USS won't parse (attribute selectors,
    # ::pseudo-elements, :nth-child, etc). These are hoisted onto the
    # element's per-instance USS rule so the styles still apply.
    unsupported_decls: list[Declaration] = field(default_factory=list)
    # When ::before / ::after rules with `content:` match this element,
    # synthesize sibling Labels.
    before_content: str | None = None
    after_content: str | None = None
    before_decls: list[Declaration] = field(default_factory=list)
    after_decls: list[Declaration] = field(default_factory=list)


def resolve(root: Node, parsed_rules: list[_ParsedRule]) -> dict[int, ResolvedStyle]:
    """Walk the tree; return a map from id(node) to ResolvedStyle."""
    out: dict[int, ResolvedStyle] = {}
    _walk(root, [], parsed_rules, out)
    return out


def _walk(node: Node, ancestors: list[Node], rules: list[_ParsedRule],
          out: dict[int, ResolvedStyle]) -> None:
    if not node.is_text and node.tag != "__root__":
        # Compute siblings + sibling_index for use in selector matching.
        if ancestors:
            parent = ancestors[-1]
            sibs = [c for c in parent.children if not c.is_text]
            try:
                sib_idx = sibs.index(node)
            except ValueError:
                sib_idx = -1
        else:
            sibs, sib_idx = [node], 0
        out[id(node)] = _resolve_for(node, ancestors, rules, sib_idx, sibs)
    for child in node.children:
        if child.is_text:
            continue
        _walk(child, ancestors + [node], rules, out)


def _has_pseudo(selector: Selector) -> bool:
    return any(c.pseudo for _, c in selector.chain)


def _resolve_for(node: Node, ancestors: list[Node], rules: list[_ParsedRule],
                 sib_idx: int, siblings: list[Node]) -> ResolvedStyle:
    matches: list[tuple[tuple[int, int, int], int, list[Declaration]]] = []
    pseudo_rules: list[tuple[str, list[Declaration]]] = []
    matched_indices: list[int] = []
    unsupported_decls: list[Declaration] = []
    before_content: str | None = None
    after_content: str | None = None
    before_decls: list[Declaration] = []
    after_decls: list[Declaration] = []
    for idx, rule in enumerate(rules):
        for sel in rule.selectors:
            if not selector_matches(node, sel, ancestors, sib_idx, siblings):
                continue
            # Pseudo-element ::before / ::after rules become synthetic siblings.
            pseudo_elem = _pseudo_element_kind(sel)
            if pseudo_elem in ("before", "after"):
                content_value = None
                rest: list[Declaration] = []
                for d in rule.declarations:
                    if d.prop == "content":
                        content_value = _strip_quotes(d.value)
                    else:
                        rest.append(d)
                if pseudo_elem == "before":
                    if content_value is not None:
                        before_content = content_value
                    before_decls.extend(rest)
                else:
                    if content_value is not None:
                        after_content = content_value
                    after_decls.extend(rest)
                continue
            if sel.has_unsupported_features():
                unsupported_decls.extend(rule.declarations)
                continue
            if _has_pseudo(sel):
                pseudo_rules.append((sel.raw, rule.declarations))
            else:
                matches.append((sel.specificity(), rule.order, rule.declarations))
                if idx not in matched_indices:
                    matched_indices.append(idx)
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
    return ResolvedStyle(
        base=base, pseudo_rules=pseudo_rules,
        matched_rule_indices=matched_indices,
        unsupported_decls=unsupported_decls,
        before_content=before_content,
        after_content=after_content,
        before_decls=before_decls,
        after_decls=after_decls,
    )


def _pseudo_element_kind(sel) -> str | None:
    """If the rightmost compound has `::before` or `::after`, return that kind."""
    if not sel.chain:
        return None
    _, comp = sel.chain[-1]
    for ps in comp.pseudo:
        low = ps.lower()
        if low in ("::before", "::after"):
            return low.lstrip(":")
    return None


def _strip_quotes(s: str) -> str:
    s = s.strip()
    if len(s) >= 2 and s[0] == s[-1] and s[0] in ("'", '"'):
        return s[1:-1]
    return s
