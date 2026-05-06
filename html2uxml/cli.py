"""Command-line interface."""
from __future__ import annotations

import argparse
import sys
from pathlib import Path

from .converter import convert


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(
        prog="html2uxml",
        description="Convert HTML+CSS to Unity UXML+USS, bridging USS gaps where possible.",
    )
    p.add_argument("input", help="Path to an HTML file.")
    p.add_argument(
        "-o", "--out-dir",
        default=None,
        help="Directory for output files (default: alongside input).",
    )
    p.add_argument(
        "--name",
        default=None,
        help="Output base name (default: input stem). Writes <name>.uxml + <name>.uss.",
    )
    p.add_argument(
        "--css",
        action="append",
        default=[],
        help="Additional CSS file(s) to include. May be repeated.",
    )
    p.add_argument(
        "-q", "--quiet",
        action="store_true",
        help="Don't print conversion warnings to stderr.",
    )
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

    uxml_path.write_text(result.uxml, encoding="utf-8")
    uss_path.write_text(result.uss, encoding="utf-8")

    print(f"wrote {uxml_path}")
    print(f"wrote {uss_path}")
    if not args.quiet:
        _print_report(result, file=sys.stderr)
    return 0


def _print_report(result, *, file) -> None:
    s = result.stats
    if s is None:
        return
    print("", file=file)
    print("== conversion report ==", file=file)
    print(
        f"  elements: VisualElement={s.elements} "
        f"BridgeBox={s.bridge_boxes} Label={s.labels} Button={s.buttons}",
        file=file,
    )
    print(f"  USS rules: {s.uss_rules}", file=file)
    if s.bridged_props:
        rows = ", ".join(f"{k}={v}" for k, v in sorted(s.bridged_props.items()))
        print(f"  bridged via runtime kit: {rows}", file=file)
    if s.dropped_props:
        rows = ", ".join(
            f"{k}={v}" for k, v in sorted(s.dropped_props.items(), key=lambda kv: -kv[1])
        )
        print(f"  dropped (no USS equivalent): {rows}", file=file)


if __name__ == "__main__":
    raise SystemExit(main())
