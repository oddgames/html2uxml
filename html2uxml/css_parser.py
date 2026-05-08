"""CSS -> rule list. Minimal but tolerant.

Supports: tag/class/id/universal selectors, descendant + child combinators,
pseudo-classes/elements (kept verbatim in the selector), comma-separated
selector lists, !important, comments. At-rules (@media, @keyframes, @import)
are skipped; their bodies are dropped.
"""
from __future__ import annotations

import re
from dataclasses import dataclass


_COMMENT_RE = re.compile(r"/\*.*?\*/", re.DOTALL)


@dataclass
class Declaration:
    prop: str
    value: str
    important: bool = False


@dataclass
class Rule:
    selectors: list[str]
    declarations: list[Declaration]
    # Source order, used as final tiebreaker after specificity.
    order: int = 0


def parse_css(source: str) -> list[Rule]:
    source = _COMMENT_RE.sub("", source)
    rules: list[Rule] = []
    i = 0
    n = len(source)
    order = 0
    while i < n:
        # Skip whitespace
        while i < n and source[i].isspace():
            i += 1
        if i >= n:
            break
        # At-rule? Skip its prelude and (optional) block.
        if source[i] == "@":
            i = _skip_at_rule(source, i)
            continue
        # Find selector list end
        brace = source.find("{", i)
        if brace == -1:
            break
        selectors_text = source[i:brace]
        end = _find_block_end(source, brace)
        if end == -1:
            break
        body = source[brace + 1:end]
        decls = _parse_declarations(body)
        sels = _split_selector_list(selectors_text)
        if sels and decls:
            rules.append(Rule(selectors=sels, declarations=decls, order=order))
            order += 1
        i = end + 1
    return rules


def _skip_at_rule(source: str, i: int) -> int:
    n = len(source)
    semi = source.find(";", i)
    brace = source.find("{", i)
    # If no block, skip to semicolon (e.g. @import)
    if brace == -1 or (semi != -1 and semi < brace):
        return (semi + 1) if semi != -1 else n
    end = _find_block_end(source, brace)
    return end + 1 if end != -1 else n


def _find_block_end(source: str, start: int) -> int:
    """Given index of `{`, find matching `}` accounting for nesting."""
    depth = 0
    i = start
    n = len(source)
    while i < n:
        c = source[i]
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return -1


def _parse_declarations(body: str) -> list[Declaration]:
    out: list[Declaration] = []
    for raw in _split_declarations(body):
        if ":" not in raw:
            continue
        prop, value = raw.split(":", 1)
        prop = prop.strip().lower()
        value = value.strip()
        if not prop or not value:
            continue
        important = False
        m = re.search(r"!\s*important\s*$", value, re.IGNORECASE)
        if m:
            important = True
            value = value[:m.start()].rstrip()
        out.append(Declaration(prop=prop, value=value, important=important))
    return out


def _split_declarations(body: str) -> list[str]:
    """Split on `;` but respect parens and quoted strings."""
    parts, buf, depth = [], [], 0
    quote: str | None = None
    escaped = False
    for c in body:
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
        elif c == ";" and depth == 0:
            parts.append("".join(buf).strip())
            buf = []
        else:
            buf.append(c)
    tail = "".join(buf).strip()
    if tail:
        parts.append(tail)
    return parts


def _split_selector_list(selectors_text: str) -> list[str]:
    """Split selector lists on top-level commas only.

    Functional pseudos such as `:not(.a, .b)` can legally contain commas; a
    plain `str.split(",")` turns those into invalid selectors and loses the
    whole rule.
    """
    selectors: list[str] = []
    buf: list[str] = []
    paren_depth = 0
    bracket_depth = 0
    quote: str | None = None
    escaped = False
    for c in selectors_text:
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
            paren_depth += 1
            buf.append(c)
        elif c == ")":
            paren_depth = max(0, paren_depth - 1)
            buf.append(c)
        elif c == "[":
            bracket_depth += 1
            buf.append(c)
        elif c == "]":
            bracket_depth = max(0, bracket_depth - 1)
            buf.append(c)
        elif c == "," and paren_depth == 0 and bracket_depth == 0:
            sel = "".join(buf).strip()
            if sel:
                selectors.append(sel)
            buf = []
        else:
            buf.append(c)
    tail = "".join(buf).strip()
    if tail:
        selectors.append(tail)
    return selectors


# ---------------------------------------------------------------------------
# Selector parsing & specificity
# ---------------------------------------------------------------------------


@dataclass
class CompoundSelector:
    tag: str = "*"          # "*" => any tag
    id: str | None = None
    classes: tuple[str, ...] = ()
    pseudo: tuple[str, ...] = ()       # pseudo-classes/elements, kept for emit
    attrs: tuple = ()                   # tuple of (op, name, value); op in {"", "=", "~=", "^=", "$=", "*=", "|="}


@dataclass
class Selector:
    """Parsed selector chain. Combinators between compounds: ' ' or '>'."""
    chain: list[tuple[str, CompoundSelector]]  # [(combinator_to_left, compound)]
    raw: str

    def specificity(self) -> tuple[int, int, int]:
        ids = sum(1 for _, c in self.chain if c.id)
        classes = sum(len(c.classes) + len(c.pseudo) + len(c.attrs) for _, c in self.chain)
        tags = sum(1 for _, c in self.chain if c.tag != "*")
        return (ids, classes, tags)

    def has_unsupported_features(self) -> bool:
        """Return True if any compound has features USS won't parse (attribute
        selectors, ::pseudo-elements, :nth-* / :not(...) etc.)."""
        for combinator, c in self.chain:
            if combinator in ("+", "~"):
                return True
            if c.attrs:
                return True
            for ps in c.pseudo:
                if ps.startswith("::"):
                    return True
                kw = ps.lstrip(":").split("(", 1)[0]
                if kw not in _USS_PSEUDO_KEYWORDS:
                    return True
        return False

    def has_runtime_pseudo(self) -> bool:
        for _, c in self.chain:
            for ps in c.pseudo:
                kw = ps.lstrip(":").split("(", 1)[0]
                if kw in _USS_PSEUDO_KEYWORDS:
                    return True
        return False


_USS_PSEUDO_KEYWORDS = {
    "hover", "active", "focus", "disabled", "enabled", "checked", "root",
    "selected", "inactive",
}


_SEL_TOKEN = re.compile(r"""
    \s*(?P<combinator>[>+~]|\s)?\s*
    (?P<compound>(?:[a-zA-Z*][\w-]*)?      # tag (optional)
                 (?:[#.][\w-]+|::?[\w-]+(?:\([^)]*\))?)*)
""", re.VERBOSE)


def parse_selector(raw: str) -> Selector | None:
    raw = raw.strip()
    if not raw:
        return None
    chain: list[tuple[str, CompoundSelector]] = []
    pos = 0
    n = len(raw)
    pending_combinator = " "  # first compound has no real left-side
    first = True
    while pos < n:
        # Eat whitespace and combinator
        combinator = " "
        had_ws = False
        while pos < n and raw[pos].isspace():
            had_ws = True
            pos += 1
        if pos < n and raw[pos] in "+>~":
            combinator = raw[pos]
            pos += 1
            while pos < n and raw[pos].isspace():
                pos += 1
        elif had_ws:
            combinator = " "
        if pos >= n:
            break
        # Read compound
        start = pos
        while pos < n and not raw[pos].isspace() and raw[pos] not in "+>~":
            if raw[pos] == "(":
                depth = 1
                pos += 1
                while pos < n and depth > 0:
                    if raw[pos] == "(":
                        depth += 1
                    elif raw[pos] == ")":
                        depth -= 1
                    pos += 1
                continue
            pos += 1
        token = raw[start:pos]
        if not token:
            break
        compound = _parse_compound(token)
        if compound is None:
            return None
        chain.append((pending_combinator if first else combinator, compound))
        first = False
        pending_combinator = " "
    return Selector(chain=chain, raw=raw) if chain else None


_COMPOUND_RE = re.compile(r"""
    (?P<tag>^[a-zA-Z*][\w-]*)?
    (?P<rest>(?:[#.][\w-]+|\[[^\]]*\]|::?[\w-]+(?:\([^)]*\))?)*)
    $
""", re.VERBOSE)


_ATTR_RE = re.compile(
    r"""\[\s*(?P<name>[\w-]+)\s*
        (?:(?P<op>[~|^$*]?=)\s*
           (?:"(?P<dq>[^"]*)"|'(?P<sq>[^']*)'|(?P<bare>[^\]\s]+))
        )?\s*\]""",
    re.VERBOSE,
)


def _parse_compound(token: str) -> CompoundSelector | None:
    m = _COMPOUND_RE.match(token)
    if not m:
        return None
    tag = (m.group("tag") or "*").lower()
    rest = m.group("rest") or ""
    classes: list[str] = []
    pseudo: list[str] = []
    attrs: list[tuple[str, str, str]] = []
    cid: str | None = None
    i = 0
    while i < len(rest):
        c = rest[i]
        if c == ".":
            j = i + 1
            while j < len(rest) and (rest[j].isalnum() or rest[j] in "_-"):
                j += 1
            classes.append(rest[i + 1:j])
            i = j
        elif c == "#":
            j = i + 1
            while j < len(rest) and (rest[j].isalnum() or rest[j] in "_-"):
                j += 1
            cid = rest[i + 1:j]
            i = j
        elif c == "[":
            j = rest.find("]", i)
            if j == -1:
                i = len(rest)
                continue
            seg = rest[i:j + 1]
            am = _ATTR_RE.match(seg)
            if am is not None:
                name = am.group("name")
                op = am.group("op") or ""
                value = am.group("dq") or am.group("sq") or am.group("bare") or ""
                attrs.append((op, name, value))
            i = j + 1
        elif c == ":":
            j = i + 1
            if j < len(rest) and rest[j] == ":":
                j += 1
            while j < len(rest) and (rest[j].isalnum() or rest[j] in "_-"):
                j += 1
            if j < len(rest) and rest[j] == "(":
                depth = 1
                j += 1
                while j < len(rest) and depth > 0:
                    if rest[j] == "(":
                        depth += 1
                    elif rest[j] == ")":
                        depth -= 1
                    j += 1
            pseudo.append(rest[i:j])
            i = j
        else:
            i += 1
    return CompoundSelector(
        tag=tag, id=cid,
        classes=tuple(classes), pseudo=tuple(pseudo),
        attrs=tuple(attrs),
    )
