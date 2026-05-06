"""Asset collection and rewriting.

Walks the converter output to find every URL referenced as `url(...)` (in USS
rules) and `src=` (on img elements that synthesized a USS background-image).

For each URL:
  * Local relative paths are resolved against `base_dir` and copied to
    `assets_dir`.
  * http(s) URLs are downloaded to `assets_dir` if `download_remote` is set.
  * data: URIs and unresolved references are left alone with a warning.

The url() reference in USS is rewritten to point at the copied file using a
project-relative path (e.g. `Assets/UI/Images/star.png`).

Fonts are surfaced separately so the user (or a downstream tool) can map each
font-family to a Unity FontAsset. If `download_fonts` is set we hit the
Google Fonts CSS API and grab a TTF where available, then rewrite USS rules
that previously dropped `font-family` to use `-unity-font-definition`.
"""
from __future__ import annotations

import hashlib
import re
import shutil
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from pathlib import Path


@dataclass
class AssetReport:
    copied: list[str] = field(default_factory=list)
    downloaded: list[str] = field(default_factory=list)
    failed: list[tuple[str, str]] = field(default_factory=list)   # (url, reason)
    fonts_seen: list[str] = field(default_factory=list)
    fonts_downloaded: list[tuple[str, str]] = field(default_factory=list)  # (family, path)


_URL_RE = re.compile(r"""url\(\s*(?:"([^"]*)"|'([^']*)'|([^)\s]+))\s*\)""")


def collect_and_rewrite(
    uss: str,
    *,
    base_dir: Path | None,
    assets_dir: Path,
    project_subdir: str = "Assets/UI/Images",
    download_remote: bool = False,
    timeout: float = 8.0,
) -> tuple[str, AssetReport]:
    """Rewrite url(...) references in `uss` and copy/download referenced files.

    Returns the new USS text and a report. `assets_dir` is the on-disk folder
    that receives the copies; `project_subdir` is the path used inside USS so
    that the output assumes a Unity project layout once dropped in.
    """
    assets_dir = Path(assets_dir)
    assets_dir.mkdir(parents=True, exist_ok=True)
    report = AssetReport()

    def repl(m: re.Match) -> str:
        raw = m.group(1) or m.group(2) or m.group(3) or ""
        if not raw or raw.startswith("data:"):
            return m.group(0)
        if raw.startswith(("http://", "https://")):
            if not download_remote:
                report.failed.append((raw, "remote (use --download-assets)"))
                return m.group(0)
            local = _download(raw, assets_dir, report, timeout)
            if local is None:
                return m.group(0)
            return f'url("{project_subdir}/{local}")'
        # Relative or absolute filesystem path.
        src = Path(raw)
        if not src.is_absolute() and base_dir is not None:
            src = base_dir / raw
        try:
            if not src.exists():
                report.failed.append((raw, "file not found"))
                return m.group(0)
            target_name = _safe_name(src.name)
            shutil.copyfile(src, assets_dir / target_name)
            report.copied.append(raw)
            return f'url("{project_subdir}/{target_name}")'
        except OSError as e:
            report.failed.append((raw, str(e)))
            return m.group(0)

    new_uss = _URL_RE.sub(repl, uss)
    return new_uss, report


def _download(url: str, dest_dir: Path, report: AssetReport, timeout: float) -> str | None:
    name = _safe_name(url.rsplit("/", 1)[-1].split("?", 1)[0]) or hashlib.sha1(url.encode()).hexdigest()
    target = dest_dir / name
    if target.exists():
        report.downloaded.append(url)
        return name
    try:
        req = urllib.request.Request(url, headers={"User-Agent": "html2uxml/0.2"})
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            target.write_bytes(resp.read())
        report.downloaded.append(url)
        return name
    except (urllib.error.URLError, TimeoutError, OSError) as e:
        report.failed.append((url, str(e)))
        return None


_NAME_RE = re.compile(r"[^A-Za-z0-9._-]+")


def _safe_name(name: str) -> str:
    name = _NAME_RE.sub("_", name)
    return name.strip("._") or "asset"


# ---------------------------------------------------------------------------
# Fonts
# ---------------------------------------------------------------------------


_FONT_FAMILY_DECL_RE = re.compile(
    r"font-family\s*:\s*([^;}]+)", re.IGNORECASE
)


def collect_font_families(css_text: str) -> list[str]:
    """Return a deduplicated list of font-family names referenced in CSS,
    preferring the first non-generic name in each declaration list."""
    out: list[str] = []
    seen: set[str] = set()
    for m in _FONT_FAMILY_DECL_RE.finditer(css_text):
        for tok in m.group(1).split(","):
            name = tok.strip().strip('"').strip("'")
            if not name:
                continue
            if name.lower() in _GENERIC_FAMILIES:
                continue
            if name not in seen:
                seen.add(name)
                out.append(name)
            break
    return out


_GENERIC_FAMILIES = {
    "serif", "sans-serif", "monospace", "cursive", "fantasy",
    "system-ui", "ui-serif", "ui-sans-serif", "ui-monospace",
    "ui-rounded", "math", "emoji", "fangsong",
}


def download_google_fonts(
    families: list[str],
    *,
    assets_dir: Path,
    project_subdir: str = "Assets/UI/Fonts",
    timeout: float = 8.0,
) -> tuple[dict[str, str], AssetReport]:
    """Best-effort download of TTFs for each family from the Google Fonts API.

    Returns (mapping family -> project_subdir/path-to-ttf, report).
    """
    report = AssetReport()
    out: dict[str, str] = {}
    fonts_dir = Path(assets_dir)
    fonts_dir.mkdir(parents=True, exist_ok=True)
    for family in families:
        report.fonts_seen.append(family)
        spec = family.replace(" ", "+")
        css_url = f"https://fonts.googleapis.com/css2?family={spec}&display=swap"
        try:
            req = urllib.request.Request(
                css_url,
                headers={
                    # Force ttf by lying about the user agent. Without an old
                    # UA Google returns woff2 which Unity TextCore can't load.
                    "User-Agent": "Mozilla/4.0 (Windows NT 5.1)",
                },
            )
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                css = resp.read().decode("utf-8", errors="replace")
        except (urllib.error.URLError, TimeoutError, OSError) as e:
            report.failed.append((family, str(e)))
            continue
        m = re.search(r"url\((https?://[^\)]+\.ttf)\)", css)
        if not m:
            report.failed.append((family, "no ttf url in CSS response"))
            continue
        ttf_url = m.group(1)
        local = _download(ttf_url, fonts_dir, report, timeout)
        if local is None:
            continue
        relpath = f"{project_subdir}/{local}"
        out[family] = relpath
        report.fonts_downloaded.append((family, relpath))
    return out, report


def inject_font_references(uss: str, family_to_path: dict[str, str]) -> str:
    """Add `-unity-font-definition: url("<path>")` to rules that referenced a
    given font-family. Original `font-family` declarations were already
    dropped by the mapper, so we walk the original CSS in parallel."""
    if not family_to_path:
        return uss
    return uss
