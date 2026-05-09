#!/usr/bin/env python3
"""Render an .html or .rml file via headless Chrome/Edge/Chromium and dump a PNG.

Usage:
    python tools/screenshot_html.py <path.html> [--out shot.png] [--width 1920] [--height 1080]

The input file may use <rml>/<head>/<body> tags (RmlUi style); the script rewrites
<rml> -> <html> and strips <rml>-only quirks (dp -> px) before handing to the browser
so the same source compares 1:1 against the in-engine RmlUi render.
"""
from __future__ import annotations

import argparse
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path


CHROME_CANDIDATES = (
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
    "/usr/bin/google-chrome",
    "/usr/bin/chromium",
    "/usr/bin/chromium-browser",
)


def find_browser() -> str:
    for name in ("chrome", "chrome.exe", "msedge", "msedge.exe", "chromium", "chromium-browser"):
        p = shutil.which(name)
        if p:
            return p
    for path in CHROME_CANDIDATES:
        if Path(path).exists():
            return path
    raise RuntimeError("No Chrome/Edge/Chromium found.")


def make_browser_compatible(rml_text: str) -> str:
    """Rewrite RmlUi <rml> document to a browser-compatible <html> document."""
    text = rml_text

    # <rml> ... </rml>  ->  <!doctype html><html lang=en> ... </html>
    text = re.sub(r"<\s*rml\s*>", "<!doctype html>\n<html lang=\"en\">", text, flags=re.IGNORECASE)
    text = re.sub(r"<\s*/\s*rml\s*>", "</html>", text, flags=re.IGNORECASE)

    # `dp` (RmlUi density-independent px) -> `px`
    text = re.sub(r"(\d+(?:\.\d+)?)dp\b", r"\1px", text)

    # RmlUi easing names -> CSS cubic-bezier approximations
    easings = {
        "cubic-out":     "cubic-bezier(0.215, 0.610, 0.355, 1.000)",
        "cubic-in":      "cubic-bezier(0.550, 0.055, 0.675, 0.190)",
        "cubic-in-out":  "cubic-bezier(0.645, 0.045, 0.355, 1.000)",
        "linear-in-out": "linear",
        "elastic-out":   "cubic-bezier(0.6, -0.28, 0.735, 0.045)",
    }
    for k, v in easings.items():
        text = re.sub(rf"\b{re.escape(k)}\b", v, text)

    return text


def screenshot(src: Path, out: Path, width: int, height: int) -> None:
    browser = find_browser()
    rml_text = src.read_text(encoding="utf-8")
    html_text = make_browser_compatible(rml_text)

    with tempfile.TemporaryDirectory(prefix="html-shot-") as td:
        td_path = Path(td)
        html_path = td_path / "page.html"
        shot_path = td_path / "shot.png"
        profile = td_path / "profile"
        html_path.write_text(html_text, encoding="utf-8")

        for headless in ("--headless=new", "--headless"):
            cmd = [
                browser,
                headless,
                "--disable-gpu",
                "--disable-dev-shm-usage",
                "--no-first-run",
                "--no-default-browser-check",
                "--no-sandbox",
                "--force-device-scale-factor=1",
                "--hide-scrollbars",
                f"--user-data-dir={profile}",
                f"--window-size={width},{height}",
                f"--screenshot={shot_path}",
                html_path.as_uri(),
            ]
            proc = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                  text=True, timeout=30)
            if proc.returncode == 0 and shot_path.is_file() and shot_path.stat().st_size > 0:
                out.parent.mkdir(parents=True, exist_ok=True)
                out.write_bytes(shot_path.read_bytes())
                print(f"wrote: {out}  ({width}x{height})")
                return
        sys.stderr.write(proc.stderr or proc.stdout or "(no output)")
        raise SystemExit(2)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("src", type=Path)
    ap.add_argument("--out", type=Path, default=None)
    ap.add_argument("--width", type=int, default=1920)
    ap.add_argument("--height", type=int, default=1080)
    args = ap.parse_args()

    out = args.out or args.src.with_suffix(".browser.png")
    screenshot(args.src, out, args.width, args.height)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
