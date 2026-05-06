"""HTML -> simple node tree.

Uses stdlib html.parser. Tolerant of malformed input: unclosed tags are closed
implicitly when their parent closes, void elements never push onto the stack.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from html.parser import HTMLParser
from typing import Optional


VOID_ELEMENTS = {
    "area", "base", "br", "col", "embed", "hr", "img", "input",
    "link", "meta", "param", "source", "track", "wbr",
}

# Tags that should be skipped entirely (we read their contents separately).
HEAD_ONLY_TAGS = {"script", "noscript", "meta", "link", "title", "head"}


@dataclass
class Node:
    tag: Optional[str] = None        # None => text node
    text: Optional[str] = None
    attrs: dict = field(default_factory=dict)
    children: list = field(default_factory=list)

    @property
    def is_text(self) -> bool:
        return self.tag is None

    def classes(self) -> list[str]:
        cls = self.attrs.get("class", "")
        return [c for c in cls.split() if c]


class _TreeBuilder(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.root = Node(tag="__root__")
        self.stack: list[Node] = [self.root]
        self.style_blocks: list[str] = []
        self.linked_stylesheets: list[str] = []
        self._in_style = False
        self._in_skip = 0  # depth of HEAD_ONLY tags we're inside

    def handle_starttag(self, tag, attrs):
        tag = tag.lower()
        attr_dict = {k.lower(): (v if v is not None else "") for k, v in attrs}
        if tag == "link" and attr_dict.get("rel", "").lower() == "stylesheet":
            href = attr_dict.get("href")
            if href:
                self.linked_stylesheets.append(href)
            return
        if tag == "style":
            self._in_style = True
            return
        if tag in HEAD_ONLY_TAGS:
            self._in_skip += 1
            return
        node = Node(tag=tag, attrs=attr_dict)
        self.stack[-1].children.append(node)
        if tag not in VOID_ELEMENTS:
            self.stack.append(node)

    def handle_endtag(self, tag):
        tag = tag.lower()
        if tag == "style":
            self._in_style = False
            return
        if tag in HEAD_ONLY_TAGS:
            if self._in_skip > 0:
                self._in_skip -= 1
            return
        for i in range(len(self.stack) - 1, 0, -1):
            if self.stack[i].tag == tag:
                del self.stack[i:]
                return

    def handle_startendtag(self, tag, attrs):
        tag = tag.lower()
        attr_dict = {k.lower(): (v if v is not None else "") for k, v in attrs}
        node = Node(tag=tag, attrs=attr_dict)
        self.stack[-1].children.append(node)

    def handle_data(self, data):
        if self._in_style:
            self.style_blocks.append(data)
            return
        if self._in_skip:
            return
        if not data:
            return
        # Preserve whitespace inside <pre>; collapse elsewhere.
        parent = self.stack[-1]
        preserve = parent.tag in ("pre", "code", "textarea")
        text = data if preserve else _collapse(data)
        if text == "" or (not preserve and text.isspace()):
            return
        parent.children.append(Node(text=text))


def _collapse(s: str) -> str:
    out, prev_space = [], False
    for ch in s:
        if ch in " \t\n\r":
            if not prev_space:
                out.append(" ")
            prev_space = True
        else:
            out.append(ch)
            prev_space = False
    return "".join(out)


@dataclass
class ParsedHTML:
    root: Node
    inline_styles: list[str]
    linked_stylesheets: list[str]


def parse_html(source: str) -> ParsedHTML:
    # Some sources (docx text extraction, copy-paste from rendered pages) use
    # NBSP where regular spaces should be. Inside `<...>` this breaks tag
    # tokenization, and UI Toolkit text doesn't care about nbsp distinctions,
    # so normalize across the whole input.
    source = source.replace(" ", " ")
    builder = _TreeBuilder()
    builder.feed(source)
    builder.close()
    body = _find_body(builder.root) or builder.root
    return ParsedHTML(
        root=body,
        inline_styles=builder.style_blocks,
        linked_stylesheets=builder.linked_stylesheets,
    )


def _find_body(root: Node) -> Optional[Node]:
    for child in root.children:
        if child.tag == "body":
            return child
        if child.tag == "html":
            for inner in child.children:
                if inner.tag == "body":
                    return inner
    return None
