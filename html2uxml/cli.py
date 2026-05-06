"""Command-line interface."""
from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

from .assets import (
    AssetReport,
    collect_and_rewrite,
    download_google_fonts,
)
from .converter import convert


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(
        prog="html2uxml",
        description="Convert HTML+CSS to Unity UXML+USS, bridging USS gaps where possible.",
    )
    p.add_argument("input", help="Path to an HTML file.")
    p.add_argument("-o", "--out-dir", default=None,
                   help="Directory for output files (default: alongside input).")
    p.add_argument("--name", default=None,
                   help="Output base name (default: input stem).")
    p.add_argument("--css", action="append", default=[],
                   help="Additional CSS file(s) to include. May be repeated.")
    p.add_argument("--bundle-assets", action="store_true",
                   help="Copy referenced images into <out>/Assets/UI/Images and rewrite url() in USS.")
    p.add_argument("--download-assets", action="store_true",
                   help="Also download remote http(s) image URLs (implies --bundle-assets).")
    p.add_argument("--download-fonts", action="store_true",
                   help="Download referenced fonts from Google Fonts into <out>/Assets/UI/Fonts.")
    p.add_argument("-q", "--quiet", action="store_true",
                   help="Suppress the conversion report.")
    args = p.parse_args(argv)

    in_path = Path(args.input)
    if not in_path.is_file():
        print(f"error: input not found: {in_path}", file=sys.stderr)
        return 2

    out_dir = Path(args.out_dir) if args.out_dir else in_path.parent
    out_dir.mkdir(parents=True, exist_ok=True)
    base = args.name or in_path.stem
    uxml_path = out_dir / f"{base}.uxml"
    uss_path = out_dir / f"{base}.uss"

    extra_css = ""
    for css_file in args.css:
        extra_css += "\n" + Path(css_file).read_text(encoding="utf-8")

    html = in_path.read_text(encoding="utf-8")
    result = convert(
        html,
        extra_css=extra_css,
        uss_filename=uss_path.name,
        base_dir=in_path.parent,
    )

    asset_report: AssetReport | None = None
    if args.bundle_assets or args.download_assets:
        assets_dir = out_dir / "Assets" / "UI" / "Images"
        result.uss, asset_report = collect_and_rewrite(
            result.uss,
            base_dir=in_path.parent,
            assets_dir=assets_dir,
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
