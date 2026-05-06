"""Command-line interface."""
from __future__ import annotations

import argparse
import re
import sys
import urllib.error
import urllib.request
from pathlib import Path
from urllib.parse import urljoin

from .assets import (
    AssetReport,
    collect_and_rewrite,
    download_google_fonts,
)
from .converter import convert
from .html_parser import parse_html


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(
        prog="html2uxml",
        description="Convert HTML+CSS to Unity UXML+USS, bridging USS gaps where possible.",
    )
    p.add_argument("input", help="Path to an HTML file, or an http(s):// URL.")
    p.add_argument("-o", "--out-dir", default=None,
                   help="Directory for output files (default: alongside input).")
    p.add_argument("--name", default=None,
                   help="Output base name (default: input stem).")
    p.add_argument("--selector", default=None,
                   help="CSS selector identifying which subtree to convert.")
    p.add_argument("--css", action="append", default=[],
                   help="Additional CSS file(s) to include. May be repeated.")
    p.add_argument("--bundle-assets", action="store_true",
                   help="Copy referenced images into <out>/Assets/UI/Images and rewrite url() in USS.")
    p.add_argument("--download-assets", action="store_true",
                   help="Also download remote http(s) image URLs (implies --bundle-assets).")
    p.add_argument("--download-fonts", action="store_true",
                   help="Download referenced fonts from Google Fonts into <out>/Assets/UI/Fonts.")
    p.add_argument("--timeout", type=float, default=10.0,
                   help="Network timeout in seconds for URL fetches (default: 10).")
    p.add_argument("-q", "--quiet", action="store_true",
                   help="Suppress the conversion report.")
    args = p.parse_args(argv)

    is_url = bool(re.match(r"^https?://", args.input))
    if is_url:
        try:
            html = _fetch_url_text(args.input, timeout=args.timeout)
        except (urllib.error.URLError, TimeoutError, OSError) as e:
            print(f"error: failed to fetch {args.input}: {e}", file=sys.stderr)
            return 2
        if args.out_dir is None:
            print("error: --out-dir is required when input is a URL", file=sys.stderr)
            return 2
        out_dir = Path(args.out_dir)
        # Default name from URL path; fall back to "page".
        url_name = re.sub(r"\W+", "_", args.input.rsplit("/", 1)[-1].split("?")[0]).strip("_") or "page"
        base = args.name or url_name
        page_url: str | None = args.input
        local_base_dir: Path | None = None
    else:
        in_path = Path(args.input)
        if not in_path.is_file():
            print(f"error: input not found: {in_path}", file=sys.stderr)
            return 2
        html = in_path.read_text(encoding="utf-8")
        out_dir = Path(args.out_dir) if args.out_dir else in_path.parent
        base = args.name or in_path.stem
        page_url = None
        local_base_dir = in_path.parent

    out_dir.mkdir(parents=True, exist_ok=True)
    uxml_path = out_dir / f"{base}.uxml"
    uss_path = out_dir / f"{base}.uss"

    extra_css_parts: list[str] = []

    # When the input is a URL we resolve linked stylesheets against the page
    # URL and fetch each one over HTTP, since `convert()`'s file-based
    # base_dir can't reach them.
    if is_url:
        prefetched = parse_html(html)
        for href in prefetched.linked_stylesheets:
            full = urljoin(page_url, href)
            try:
                extra_css_parts.append(_fetch_url_text(full, timeout=args.timeout))
            except (urllib.error.URLError, TimeoutError, OSError) as e:
                print(f"warning: failed to fetch {full}: {e}", file=sys.stderr)
        # Absolutize relative url() and src= so the asset bundler downloads
        # them as absolute http URLs.
        html = _absolutize_refs(html, page_url)

    for css_file in args.css:
        extra_css_parts.append(Path(css_file).read_text(encoding="utf-8"))
    extra_css = "\n".join(extra_css_parts)

    result = convert(
        html,
        extra_css=extra_css,
        uss_filename=uss_path.name,
        base_dir=local_base_dir,
        select=args.selector,
    )

    images_dir = out_dir / "Assets" / "UI" / "Images"
    if result.svg_files:
        images_dir.mkdir(parents=True, exist_ok=True)
        for filename, raw in result.svg_files:
            (images_dir / filename).write_text(raw, encoding="utf-8")

    asset_report: AssetReport | None = None
    if args.bundle_assets or args.download_assets:
        result.uss, asset_report = collect_and_rewrite(
            result.uss,
            base_dir=local_base_dir,
            assets_dir=images_dir,
            download_remote=args.download_assets,
        )

    font_report: AssetReport | None = None
    if args.download_fonts:
        families = _families_from_uss(result.uss)
        fonts_dir = out_dir / "Assets" / "UI" / "Fonts"
        family_to_path, font_report = download_google_fonts(
            families, assets_dir=fonts_dir,
        )
        result.uss = _inject_font_definitions(result.uss, family_to_path)

    uxml_path.write_text(result.uxml, encoding="utf-8")
    uss_path.write_text(result.uss, encoding="utf-8")

    print(f"wrote {uxml_path}")
    print(f"wrote {uss_path}")
    if not args.quiet:
        _print_report(result, asset_report, font_report, file=sys.stderr)
    return 0


def _fetch_url_text(url: str, *, timeout: float) -> str:
    req = urllib.request.Request(
        url,
        headers={"User-Agent": "html2uxml/0.2 (+https://github.com/oddgames/html2uxml)"},
    )
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        raw = resp.read()
        # Honour the HTTP charset when present; default to utf-8.
        charset = resp.headers.get_content_charset() if hasattr(resp, "headers") else None
    return raw.decode(charset or "utf-8", errors="replace")


# Rewrite relative `src="..."` and CSS `url(...)` references to absolute URLs
# so the asset bundler can pull them via HTTP.
_HTML_SRC_RE = re.compile(r'(?P<attr>(?:src|href|poster))\s*=\s*"([^"]+)"', re.IGNORECASE)
_CSS_URL_IN_HTML_RE = re.compile(r"""url\(\s*(?:"([^"]*)"|'([^']*)'|([^)\s]+))\s*\)""")


def _absolutize_refs(html: str, page_url: str) -> str:
    def repl_attr(m: re.Match) -> str:
        attr = m.group("attr")
        ref = m.group(2)
        if not ref or ref.startswith(("http://", "https://", "data:", "#", "mailto:")):
            return m.group(0)
        return f'{attr}="{urljoin(page_url, ref)}"'

    def repl_url(m: re.Match) -> str:
        ref = m.group(1) or m.group(2) or m.group(3) or ""
        if not ref or ref.startswith(("http://", "https://", "data:", "#")):
            return m.group(0)
        return f'url("{urljoin(page_url, ref)}")'

    html = _HTML_SRC_RE.sub(repl_attr, html)
    html = _CSS_URL_IN_HTML_RE.sub(repl_url, html)
    return html


_FF_RULE_RE = re.compile(r'--gg-font-family\s*:\s*"([^"]+)"')


def _families_from_uss(uss: str) -> list[str]:
    seen: set[str] = set()
    out: list[str] = []
    for m in _FF_RULE_RE.finditer(uss):
        f = m.group(1)
        if f not in seen:
            seen.add(f)
            out.append(f)
    return out


def _inject_font_definitions(uss: str, mapping: dict) -> str:
    if not mapping:
        return uss
    def repl(m: re.Match) -> str:
        family = m.group(1)
        path = mapping.get(family)
        if not path:
            return m.group(0)
        return f'-unity-font-definition: url("{path}")'
    return _FF_RULE_RE.sub(repl, uss)


def _print_report(result, asset_report, font_report, *, file) -> None:
    s = result.stats
    if s is None:
        return
    print("", file=file)
    print("== conversion report ==", file=file)
    print(
        f"  elements: VisualElement={s.elements} BridgeBox={s.bridge_boxes} "
        f"Label={s.labels} Button={s.buttons} Image={s.images}",
        file=file,
    )
    print(
        f"  USS rules: {s.uss_rules} "
        f"(class rules={s.css_class_rules}, inline overrides={s.inline_overrides})",
        file=file,
    )
    if s.bridged_props:
        rows = ", ".join(f"{k}={v}" for k, v in sorted(s.bridged_props.items()))
        print(f"  bridged via runtime kit: {rows}", file=file)
    if s.dropped_props:
        rows = ", ".join(
            f"{k}={v}" for k, v in sorted(s.dropped_props.items(), key=lambda kv: -kv[1])
        )
        print(f"  dropped (no USS equivalent): {rows}", file=file)
    if asset_report is not None:
        print(
            f"  assets: copied={len(asset_report.copied)} "
            f"downloaded={len(asset_report.downloaded)} "
            f"failed={len(asset_report.failed)}",
            file=file,
        )
        for u, why in asset_report.failed[:5]:
            print(f"    - {u}: {why}", file=file)
    if font_report is not None:
        rows = ", ".join(f"{f}->{p}" for f, p in font_report.fonts_downloaded[:5])
        if rows:
            print(f"  fonts: {rows}", file=file)
        for f, why in font_report.failed[:5]:
            print(f"    - {f}: {why}", file=file)


if __name__ == "__main__":
    raise SystemExit(main())
