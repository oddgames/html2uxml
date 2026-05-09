"""Asset collection and rewriting.

Walks the converter output to find every URL referenced as `url(...)` (in USS
rules) and `src=` (on img elements that synthesized a USS background-image).

For each URL:
  * Local relative paths are resolved against `base_dir` and copied to
    `assets_dir`.
  * http(s) URLs are downloaded to `assets_dir` if `download_remote` is set.
  * data:image/* URIs are decoded and written to `assets_dir`; the URL is
    rewritten to the project-relative path. Other data: URIs are left alone.

The url() reference in USS is rewritten to point at the copied file using a
project-relative path (for generated output, usually `Images/star.png`).

Fonts are surfaced separately so the CLI can turn each font-family into a
Unity `-unity-font-definition`. Google Fonts is tried first; installed local
TTF/OTF files are used as a fallback.
"""
from __future__ import annotations

import base64
import binascii
import hashlib
import os
import re
import shutil
import struct
import urllib.error
import urllib.parse
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


@dataclass(frozen=True)
class FontVariant:
    path: str
    weight: int = 400
    italic: bool = False
    source: str = ""


_URL_RE = re.compile(r"""url\(\s*(?:"([^"]*)"|'([^']*)'|([^)\s]+))\s*\)""")


def collect_and_rewrite(
    uss: str,
    *,
    base_dir: Path | None,
    assets_dir: Path,
    project_subdir: str = "Images",
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
        if not raw:
            return m.group(0)
        if raw.startswith("data:"):
            decoded = _decode_image_data_uri(raw)
            if decoded is None:
                report.failed.append((_truncate_for_report(raw), "unsupported data URI"))
                return m.group(0)
            data, ext = decoded
            target_name = f"embedded-{hashlib.sha1(data).hexdigest()[:12]}{ext}"
            target_name = _write_bytes_smart(assets_dir, target_name, data)
            report.copied.append("data:")
            return f'url("{project_subdir}/{target_name}")'
        # Already a project-relative path (e.g. emitted by SVG inlining).
        if raw.startswith(("Assets/", "Assets\\")):
            return m.group(0)
        rel = _asset_ref_to_relative_path(raw, project_subdir)
        if rel is not None:
            if not (assets_dir / rel).exists():
                report.failed.append((raw, "file not found"))
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
            target_name = _copy_file_smart(src, assets_dir, target_name)
            report.copied.append(raw)
            return f'url("{project_subdir}/{target_name}")'
        except OSError as e:
            report.failed.append((raw, str(e)))
            return m.group(0)

    new_uss = _URL_RE.sub(repl, uss)
    return new_uss, report


def inject_image_aspect_ratios(
    uss: str,
    *,
    assets_dir: Path,
    project_subdir: str = "Images",
) -> str:
    """Add `aspect-ratio` beside bundled image backgrounds when possible.

    Converted `<img>` tags use a generated VisualElement with a
    background-image. Browsers preserve an image's intrinsic ratio for
    `height: Npx; width: auto`; UI Toolkit needs the ratio made explicit.
    """
    if not uss:
        return uss

    rule_re = re.compile(r"(?P<head>[^{]+)\{(?P<body>[^{}]*)\}", re.MULTILINE)

    def repl(m: re.Match) -> str:
        body = m.group("body")
        if "aspect-ratio" in body:
            return m.group(0)
        url_match = _URL_RE.search(body)
        if not url_match:
            return m.group(0)
        ref = url_match.group(1) or url_match.group(2) or url_match.group(3) or ""
        rel = _asset_ref_to_relative_path(ref, project_subdir)
        if rel is None:
            return m.group(0)
        dims = _image_dimensions(Path(assets_dir) / rel)
        if dims is None:
            return m.group(0)
        w, h = dims
        if w <= 0 or h <= 0:
            return m.group(0)
        ratio = w / h
        lines = body.splitlines()
        out_lines: list[str] = []
        inserted = False
        for line in lines:
            out_lines.append(line)
            if not inserted and "background-image" in line:
                indent_match = re.match(r"^(\s*)", line)
                indent = indent_match.group(1) if indent_match else "    "
                out_lines.append(f"{indent}aspect-ratio: {ratio:g};")
                inserted = True
        if not inserted:
            out_lines.append(f"    aspect-ratio: {ratio:g};")
        return f"{m.group('head')}{{" + "\n".join(out_lines) + "}"

    return rule_re.sub(repl, uss)


def _asset_ref_to_relative_path(ref: str, project_subdir: str) -> Path | None:
    normalized = ref.replace("\\", "/").lstrip("/")
    prefix = project_subdir.replace("\\", "/").strip("/")
    if not normalized.startswith(prefix + "/"):
        return None
    return Path(normalized[len(prefix) + 1:])


def _image_dimensions(path: Path) -> tuple[int, int] | None:
    try:
        with path.open("rb") as f:
            head = f.read(32)
        if head.startswith(b"\x89PNG\r\n\x1a\n") and len(head) >= 24:
            w, h = struct.unpack(">II", head[16:24])
            return int(w), int(h)
        if head[:3] == b"\xff\xd8\xff":
            return _jpeg_dimensions(path)
        if head[:6] in (b"GIF87a", b"GIF89a") and len(head) >= 10:
            w, h = struct.unpack("<HH", head[6:10])
            return int(w), int(h)
    except OSError:
        return None
    return None


def _jpeg_dimensions(path: Path) -> tuple[int, int] | None:
    try:
        with path.open("rb") as f:
            if f.read(2) != b"\xff\xd8":
                return None
            while True:
                if f.read(1) != b"\xff":
                    return None
                marker = f.read(1)
                while marker == b"\xff":
                    marker = f.read(1)
                if not marker:
                    return None
                marker_value = marker[0]
                if marker_value in (0xD8, 0xD9):
                    continue
                raw_len = f.read(2)
                if len(raw_len) != 2:
                    return None
                seg_len = struct.unpack(">H", raw_len)[0]
                if seg_len < 2:
                    return None
                if marker_value in (
                    0xC0, 0xC1, 0xC2, 0xC3,
                    0xC5, 0xC6, 0xC7,
                    0xC9, 0xCA, 0xCB,
                    0xCD, 0xCE, 0xCF,
                ):
                    data = f.read(5)
                    if len(data) != 5:
                        return None
                    h, w = struct.unpack(">HH", data[1:5])
                    return int(w), int(h)
                f.seek(seg_len - 2, 1)
    except OSError:
        return None
    return None


def _download(
    url: str,
    dest_dir: Path,
    report: AssetReport,
    timeout: float,
    *,
    target_name: str | None = None,
) -> str | None:
    name = _safe_name(target_name or url.rsplit("/", 1)[-1].split("?", 1)[0])
    if not name or name == "asset":
        name = hashlib.sha1(url.encode()).hexdigest()
    try:
        req = urllib.request.Request(url, headers={"User-Agent": "html2uxml/0.2"})
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            data = resp.read()
        name = _write_bytes_smart(dest_dir, name, data)
        report.downloaded.append(url)
        return name
    except (urllib.error.URLError, TimeoutError, OSError) as e:
        report.failed.append((url, str(e)))
        return None


_NAME_RE = re.compile(r"[^A-Za-z0-9._-]+")

_DATA_URI_RE = re.compile(
    r"^data:(image/[A-Za-z0-9+\-.]+)\s*(;base64)?\s*,(.*)$",
    re.DOTALL | re.IGNORECASE,
)

_DATA_URI_EXTENSIONS = {
    "image/png": ".png",
    "image/jpeg": ".jpg",
    "image/jpg": ".jpg",
    "image/gif": ".gif",
    "image/webp": ".webp",
    "image/bmp": ".bmp",
    "image/svg+xml": ".svg",
    "image/x-icon": ".ico",
    "image/vnd.microsoft.icon": ".ico",
}


def _decode_image_data_uri(uri: str) -> tuple[bytes, str] | None:
    """Decode a `data:image/*` URI to (bytes, extension) or None if unsupported."""
    m = _DATA_URI_RE.match(uri)
    if not m:
        return None
    mime = m.group(1).lower()
    is_base64 = m.group(2) is not None
    payload = m.group(3)
    try:
        if is_base64:
            data = base64.b64decode(payload, validate=False)
        else:
            data = urllib.parse.unquote_to_bytes(payload)
    except (binascii.Error, ValueError):
        return None
    ext = _DATA_URI_EXTENSIONS.get(mime)
    if ext is None:
        subtype = mime.split("/", 1)[1] if "/" in mime else "bin"
        ext = "." + re.sub(r"[^A-Za-z0-9]+", "", subtype)[:8] or ".bin"
    return data, ext


def _truncate_for_report(value: str, limit: int = 80) -> str:
    return value if len(value) <= limit else value[:limit] + "..."


def _safe_name(name: str) -> str:
    name = _NAME_RE.sub("_", name)
    return name.strip("._") or "asset"


def _copy_file_smart(src: Path, dest_dir: Path, target_name: str) -> str:
    return _write_bytes_smart(dest_dir, target_name, src.read_bytes())


def _write_bytes_smart(dest_dir: Path, target_name: str, data: bytes) -> str:
    """Write bytes without creating duplicate files for identical content.

    If the target name exists with the same bytes, keep that name. If it exists
    with different bytes, append a numeric suffix so existing references are
    not silently redirected to different content.
    """
    dest_dir.mkdir(parents=True, exist_ok=True)
    safe_name = _safe_name(target_name)
    stem = Path(safe_name).stem
    suffix = Path(safe_name).suffix
    candidate = safe_name
    i = 2
    while True:
        target = dest_dir / candidate
        if not target.exists():
            target.write_bytes(data)
            return candidate
        try:
            if target.read_bytes() == data:
                return candidate
        except OSError:
            pass
        candidate = f"{stem}-{i}{suffix}"
        i += 1


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


# ---------------------------------------------------------------------------
# Embedded @font-face support
# ---------------------------------------------------------------------------


@dataclass
class EmbeddedFontReport:
    extracted: list[tuple[str, str]] = field(default_factory=list)   # (family, project-relative path)
    failed: list[tuple[str, str]] = field(default_factory=list)      # (family/url, reason)
    skipped: list[tuple[str, str]] = field(default_factory=list)     # (url, reason) e.g. woff2 unsupported


_AT_FONT_FACE_RE = re.compile(r"@font-face\s*\{([^}]*)\}", re.IGNORECASE | re.DOTALL)
_FF_FACE_FAMILY_RE = re.compile(r"font-family\s*:\s*([^;]+)", re.IGNORECASE)
_FF_FACE_SRC_RE = re.compile(r"src\s*:\s*((?:[^;]|;(?=base64))+)", re.IGNORECASE)
_FF_FACE_SRC_ENTRY_RE = re.compile(
    r"""url\(\s*(?:"((?:[^"\\]|\\.)*)"|'((?:[^'\\]|\\.)*)'|([^)\s]+))\s*\)"""
    r"""(?:\s*format\(\s*(?:"([^"]*)"|'([^']*)'|([^)\s]+))\s*\))?""",
    re.IGNORECASE | re.DOTALL,
)
_FONT_DATA_URI_RE = re.compile(
    r"^data:(?P<mime>[^;,]*)(?P<base64>;base64)?,(?P<payload>.*)$",
    re.DOTALL | re.IGNORECASE,
)

_TTF_OTF_FORMATS = {"truetype", "opentype", "ttf", "otf"}
_TTF_OTF_MIMES = {
    "font/ttf", "font/otf",
    "application/x-font-ttf", "application/x-font-otf",
    "application/x-font-truetype", "application/x-font-opentype",
    "application/font-ttf", "application/font-otf",
    "application/font-sfnt",
    "application/octet-stream",  # browsers commonly mislabel font payloads
}
# WOFF / WOFF2 are supported by transcoding to SFNT through fontTools (woff2
# also needs brotli). They sit at a worse rank than direct TTF/OTF so the
# resolver still prefers a native source when both ship in the same `src:`.
_WOFF_FORMATS = {"woff", "woff2"}
_WOFF_MIMES = {
    "font/woff", "font/woff2",
    "application/font-woff", "application/font-woff2",
}
_UNSUPPORTED_FORMATS = {"embedded-opentype", "svg"}


def extract_embedded_font_faces(
    css_text: str,
    *,
    base_dir: Path | None,
    fonts_dir: Path,
    project_subdir: str = "Fonts",
    timeout: float = 8.0,
    download_remote: bool = True,
    families_filter: set[str] | None = None,
) -> tuple[dict[str, list[FontVariant]], EmbeddedFontReport]:
    """Pull `@font-face` rules out of the supplied CSS text and persist each
    referenced font into `fonts_dir`.

    Supports three `src:` shapes:
      * `url(data:font/ttf;base64,...)` — decoded and written.
      * `url(https://...)` — downloaded when `download_remote` is true.
      * `url('relative/path.ttf')` — copied from `base_dir`.

    WOFF / WOFF2 payloads are decompressed to SFNT (TTF/OTF) using
    fontTools when available; entries that cannot be decoded land in
    `report.skipped` so the caller can fall back to Google Fonts.

    Pages from font-host services (Google Fonts, Adobe Fonts) ship one
    `@font-face` per `unicode-range` subset, so naively writing every
    block produces dozens of near-duplicate TTFs per family/weight. This
    pass groups by `(family, weight, italic)` and writes only the best
    candidate, ranked by unicode-range coverage.

    `families_filter`, when provided, limits extraction to those family
    names. Useful for skipping `@font-face` declarations the converted
    page never references.
    """
    report = EmbeddedFontReport()
    mapping: dict[str, list[FontVariant]] = {}
    if not css_text:
        return mapping, report

    fonts_dir = Path(fonts_dir)
    fonts_dir.mkdir(parents=True, exist_ok=True)

    candidates: dict[tuple[str, int, bool], dict] = {}
    for face_body in _AT_FONT_FACE_RE.findall(css_text):
        family_match = _FF_FACE_FAMILY_RE.search(face_body)
        src_match = _FF_FACE_SRC_RE.search(face_body)
        if not family_match or not src_match:
            continue
        family = _clean_face_family(family_match.group(1))
        if not family:
            continue
        if families_filter is not None and family not in families_filter:
            continue
        weight = _font_face_weight(face_body)
        italic = _font_face_italic(face_body)
        chosen = _choose_face_src_entry(src_match.group(1))
        if chosen is None:
            report.failed.append((family, "no usable src in @font-face"))
            continue
        unicode_range = _font_face_unicode_range(face_body)
        score = _embedded_face_unicode_score(unicode_range)
        key = (family, weight, italic)
        existing = candidates.get(key)
        if existing is None or score < existing["score"]:
            candidates[key] = {
                "family": family,
                "weight": weight,
                "italic": italic,
                "src": chosen,
                "score": score,
                "unicode_range": unicode_range,
            }

    used_names: set[str] = set()
    for key in sorted(candidates.keys()):
        c = candidates[key]
        family = c["family"]
        weight = c["weight"]
        italic = c["italic"]
        url, fmt = c["src"]
        try:
            data, suffix = _resolve_face_src(
                url,
                fmt,
                base_dir=base_dir,
                timeout=timeout,
                download_remote=download_remote,
                report=report,
            )
        except _FaceSrcError as e:
            report.failed.append((family, str(e)))
            continue
        if data is None:
            continue

        target_name = _unique_name(
            _safe_font_name(family, suffix, weight=weight, italic=italic),
            used_names,
        )
        target_name = _write_bytes_smart(fonts_dir, target_name, data)
        relpath = f"{project_subdir}/{target_name}"
        mapping.setdefault(family, []).append(FontVariant(
            path=relpath,
            weight=weight,
            italic=italic,
            source="embedded",
        ))
        report.extracted.append((family, relpath))

    return mapping, report


def _embedded_face_unicode_score(unicode_range: str) -> int:
    """Lower score wins. Prefer faces that cover Basic Latin so English
    text sees no missing glyphs; treat unrestricted ranges as equivalent."""
    if not unicode_range:
        return 0
    r = unicode_range.lower()
    if "u+0000-00ff" in r or "u+0000-007f" in r or "u+0020-007f" in r:
        return 1
    if "u+0100-02af" in r:
        return 5
    return 10


def _clean_face_family(raw: str) -> str | None:
    name = raw.strip().rstrip(",").strip()
    name = name.split(",", 1)[0].strip().strip('"').strip("'")
    if not name or name.lower() in _GENERIC_FAMILIES:
        return None
    return name


def _choose_face_src_entry(src: str) -> tuple[str, str] | None:
    """Pick the most Unity-friendly entry from a @font-face `src:` list.

    Preference: TTF/OTF format hint > TTF/OTF extension > anything else.
    Returns ``(url, format)`` or None when nothing usable was found.
    """
    candidates: list[tuple[int, str, str]] = []
    for m in _FF_FACE_SRC_ENTRY_RE.finditer(src):
        url = (m.group(1) or m.group(2) or m.group(3) or "").strip()
        fmt = (m.group(4) or m.group(5) or m.group(6) or "").strip().lower()
        if not url:
            continue
        rank = _face_src_rank(url, fmt)
        candidates.append((rank, url, fmt))
    if not candidates:
        return None
    candidates.sort(key=lambda c: c[0])
    rank, url, fmt = candidates[0]
    if rank >= 100:
        return None  # all entries unsupported
    return url, fmt


def _face_src_rank(url: str, fmt: str) -> int:
    if fmt in _UNSUPPORTED_FORMATS:
        return 100
    suffix = Path(url.split("?", 1)[0].split("#", 1)[0]).suffix.lower()
    if suffix == ".eot":
        return 100
    if fmt in _TTF_OTF_FORMATS:
        return 0
    if suffix in (".ttf", ".otf"):
        return 1
    if fmt in _WOFF_FORMATS:
        return 50
    if suffix in (".woff", ".woff2"):
        return 51
    if url.startswith("data:"):
        m = _FONT_DATA_URI_RE.match(url)
        if m:
            mime = (m.group("mime") or "").lower()
            if mime in _TTF_OTF_MIMES:
                return 0
            if mime in _WOFF_MIMES or "woff" in mime:
                return 50
    return 5


class _FaceSrcError(RuntimeError):
    pass


def _resolve_face_src(
    url: str,
    fmt: str,
    *,
    base_dir: Path | None,
    timeout: float,
    download_remote: bool,
    report: EmbeddedFontReport,
) -> tuple[bytes | None, str]:
    """Resolve a @font-face src URL to (bytes, suffix). Returns (None, "")
    when the entry is intentionally skipped (unsupported format)."""
    if fmt in _UNSUPPORTED_FORMATS:
        report.skipped.append((url, f"format {fmt!r} not supported by Unity TextCore"))
        return None, ""

    if url.startswith("data:"):
        data, suffix = _read_data_uri_font(url, report)
        if data is None:
            return None, ""
        return _maybe_decode_woff(data, suffix, report, source=url[:60] + "…")

    if url.startswith(("http://", "https://")):
        if not download_remote:
            report.skipped.append((url, "remote (use --download-fonts)"))
            return None, ""
        try:
            req = urllib.request.Request(url, headers={"User-Agent": "html2uxml/0.2"})
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                data = resp.read()
        except (urllib.error.URLError, TimeoutError, OSError) as e:
            raise _FaceSrcError(f"download failed: {e}") from e
        suffix = _font_suffix_from_url(url)
        return _maybe_decode_woff(data, suffix, report, source=url)

    src = Path(url)
    if not src.is_absolute() and base_dir is not None:
        src = base_dir / url
    try:
        data = src.read_bytes()
    except OSError as e:
        raise _FaceSrcError(f"file not found: {url}") from e
    suffix = src.suffix.lower()
    if suffix not in (".ttf", ".otf", ".woff", ".woff2"):
        suffix = ".ttf"
    return _maybe_decode_woff(data, suffix, report, source=str(src))


_SFNT_TAGS = (b"\x00\x01\x00\x00", b"OTTO", b"true", b"typ1")


def _maybe_decode_woff(
    data: bytes,
    suffix: str,
    report: EmbeddedFontReport,
    *,
    source: str,
) -> tuple[bytes | None, str]:
    """If `data` is WOFF/WOFF2 bytes, decompress to SFNT (TTF/OTF). Otherwise
    return the bytes unchanged. WOFF detection by magic prefix is more
    reliable than trusting `format(...)` hints, since exporters often
    mislabel data: URIs.
    """
    if not data:
        return data, suffix
    head = data[:4]
    if head in _SFNT_TAGS:
        return data, suffix if suffix in (".ttf", ".otf") else ".ttf"
    if head == b"wOFF":
        return _decode_woff_bytes(data, suffix, report, source=source, version=1)
    if head == b"wOF2":
        return _decode_woff_bytes(data, suffix, report, source=source, version=2)
    # Unknown header — let the caller write the bytes anyway. TextCore will
    # surface the error during import if it can't parse it.
    return data, suffix if suffix in (".ttf", ".otf") else ".ttf"


def _decode_woff_bytes(
    data: bytes,
    suffix: str,
    report: EmbeddedFontReport,
    *,
    source: str,
    version: int,
) -> tuple[bytes | None, str]:
    label = "woff2" if version == 2 else "woff"
    try:
        from fontTools.ttLib import TTFont
    except ImportError as e:
        report.skipped.append((source, f"{label} decode requires fonttools: {e}"))
        return None, ""
    if version == 2:
        try:
            import brotli  # noqa: F401
        except ImportError as e:
            report.skipped.append((source, f"woff2 decode requires brotli: {e}"))
            return None, ""

    import io as _io
    try:
        with _io.BytesIO(data) as buf:
            font = TTFont(buf)
            font.flavor = None
            out = _io.BytesIO()
            font.save(out, reorderTables=False)
            decoded = out.getvalue()
    except Exception as e:  # fontTools raises a few different exception types
        report.skipped.append((source, f"{label} decode failed: {e}"))
        return None, ""

    new_suffix = ".otf" if decoded[:4] == b"OTTO" else ".ttf"
    return decoded, new_suffix


def _read_data_uri_font(
    url: str,
    report: EmbeddedFontReport,
) -> tuple[bytes | None, str]:
    m = _FONT_DATA_URI_RE.match(url)
    if not m:
        raise _FaceSrcError("malformed data: URI")
    mime = (m.group("mime") or "").lower()
    payload = m.group("payload")
    if m.group("base64"):
        try:
            data = base64.b64decode(payload, validate=False)
        except (binascii.Error, ValueError) as e:
            raise _FaceSrcError(f"base64 decode failed: {e}") from e
    else:
        data = urllib.parse.unquote_to_bytes(payload)
    if "woff2" in mime:
        suffix = ".woff2"
    elif "woff" in mime:
        suffix = ".woff"
    elif "otf" in mime or "opentype" in mime:
        suffix = ".otf"
    else:
        suffix = ".ttf"
    return data, suffix


def download_google_fonts(
    families: list[str],
    *,
    assets_dir: Path,
    project_subdir: str = "Fonts",
    timeout: float = 8.0,
    wanted: dict[str, set[tuple[int, bool]]] | None = None,
    seed: dict[str, list[FontVariant]] | None = None,
) -> tuple[dict[str, list[FontVariant]], AssetReport]:
    """Best-effort download of TTF/OTF files for each family.

    Google Fonts is preferred. If Google does not have a TTF for a family,
    the converter falls back to installed local fonts, such as Windows Fonts.

    `wanted` optionally narrows which `(weight, italic)` variants are
    fetched per family. When provided, only those variants are pulled and
    cache / local-font fallbacks are filtered to the same set. Families
    listed with an empty wanted set are skipped entirely.

    `seed` lets callers supply variants that already live in `assets_dir`
    (typically extracted from `@font-face` blocks via
    :func:`extract_embedded_font_faces`). Seeded variants count toward the
    `wanted` coverage check, so families that the source page embeds in
    full skip the Google round-trip.

    Returns (mapping family -> variants, report).
    """
    report = AssetReport()
    out: dict[str, list[FontVariant]] = {}
    fonts_dir = Path(assets_dir)
    fonts_dir.mkdir(parents=True, exist_ok=True)
    for family in families:
        report.fonts_seen.append(family)
        # wanted dict semantics:
        #   None         -> no narrowing (broad download)
        #   missing key  -> no narrowing for this family (broad download)
        #   empty set    -> skip family entirely (caller said no usage)
        #   non-empty    -> only fetch listed (weight, italic) variants
        if wanted is None:
            wanted_set: set[tuple[int, bool]] | None = None
        elif family not in wanted:
            wanted_set = None
        else:
            wanted_set = wanted[family]
            if not wanted_set:
                continue

        seeded = list(seed.get(family, [])) if seed else []
        seeded_keys = {(v.weight, v.italic) for v in seeded}
        if seeded:
            # Embedded `@font-face` payloads are authoritative — they were
            # bundled into the page deliberately. Skip cache/Google probes
            # entirely. Missing weight/italic variants are rendered with
            # `_select_font_variant`'s synthetic bold/italic fallback.
            out[family] = seeded
            continue

        remaining = (
            (wanted_set - seeded_keys) if wanted_set is not None else None
        )
        # remaining == set() means wanted is fully covered by seed already
        # (handled above) so we never hand cache/google a vacant filter.

        variants = list(seeded)
        variants.extend(_copy_cached_font_variants(
            family,
            fonts_dir=fonts_dir,
            project_subdir=project_subdir,
            report=report,
            wanted=remaining,
        ))
        if wanted_set is not None:
            covered = {(v.weight, v.italic) for v in variants}
            if wanted_set.issubset(covered):
                out[family] = variants
                continue
        elif _has_common_font_axis_coverage(variants):
            out[family] = variants
            continue

        css = ""
        css_errors: list[str] = []
        for css_url in _google_font_css_urls(family):
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
                if css:
                    break
            except (urllib.error.URLError, TimeoutError, OSError) as e:
                css_errors.append(str(e))

        faces = _preferred_google_faces(_google_ttf_faces(css) if css else [])
        if remaining is not None:
            faces = [
                f for f in faces
                if (int(f["weight"]), bool(f["italic"])) in remaining
            ]
        existing_keys = {(v.weight, v.italic) for v in variants}
        seen_urls: set[str] = set()
        used_names: set[str] = {Path(v.path).name.lower() for v in variants}
        for face in faces:
            if face["url"] in seen_urls:
                continue
            seen_urls.add(face["url"])
            weight = int(face["weight"])
            italic = bool(face["italic"])
            if (weight, italic) in existing_keys:
                continue
            target_name = _unique_name(
                _safe_font_name(family, _font_suffix_from_url(str(face["url"])), weight=weight, italic=italic),
                used_names,
            )
            local = _download(face["url"], fonts_dir, report, timeout, target_name=target_name)
            if local is None:
                continue
            _store_font_cache(fonts_dir / local, target_name)
            relpath = f"{project_subdir}/{local}"
            variants.append(FontVariant(
                path=relpath,
                weight=weight,
                italic=italic,
                source="google",
            ))
            existing_keys.add((weight, italic))
            report.fonts_downloaded.append((family, relpath))

        if not variants:
            variants = _copy_local_font_variants(
                family,
                fonts_dir=fonts_dir,
                project_subdir=project_subdir,
                report=report,
                wanted=remaining,
            )

        if variants:
            out[family] = variants
        elif css_errors:
            report.failed.append((family, "; ".join(css_errors[:2])))
        else:
            report.failed.append((family, "no ttf url in CSS response; no local ttf/otf match"))
    return out, report


def _has_common_font_axis_coverage(variants: list[FontVariant]) -> bool:
    keys = {(v.weight, v.italic) for v in variants}
    expected_weights = {400, 500, 600, 700, 800, 900}
    return all((weight, False) in keys and (weight, True) in keys for weight in expected_weights)


def _unique_name(name: str, used_names: set[str]) -> str:
    base_name = _safe_name(name)
    target_name = base_name
    i = 2
    while target_name.lower() in used_names:
        stem = Path(base_name).stem
        suffix = Path(base_name).suffix
        target_name = f"{stem}-{i}{suffix}"
        i += 1
    used_names.add(target_name.lower())
    return target_name


def _font_cache_dir() -> Path:
    raw = os.environ.get("H2U_CACHE_DIR")
    root = Path(raw) if raw else Path.cwd() / ".cache" / "html2uxml"
    return root / "fonts"


def _copy_cached_font_variants(
    family: str,
    *,
    fonts_dir: Path,
    project_subdir: str,
    report: AssetReport,
    wanted: set[tuple[int, bool]] | None = None,
) -> list[FontVariant]:
    cache_dir = _font_cache_dir()
    if not cache_dir.is_dir():
        return []

    prefixes = _font_search_prefixes(family)
    candidates: list[Path] = []
    try:
        for child in cache_dir.iterdir():
            if not child.is_file() or child.suffix.lower() not in (".ttf", ".otf"):
                continue
            stem = _normalise_font_name(child.stem)
            if any(stem.startswith(prefix) or prefix in stem for prefix in prefixes):
                candidates.append(child)
    except OSError:
        return []

    if not candidates:
        return []

    selected: dict[tuple[int, bool], Path] = {}
    for candidate in sorted(candidates, key=lambda p: p.name.lower()):
        key = _local_font_variant(candidate)
        if wanted is not None and key not in wanted:
            continue
        selected.setdefault(key, candidate)

    variants: list[FontVariant] = []
    used_names: set[str] = set()
    for (weight, italic), cached in sorted(selected.items()):
        target_name = _unique_name(
            _safe_font_name(family, cached.suffix, weight=weight, italic=italic),
            used_names,
        )
        try:
            target_name = _copy_file_smart(cached, fonts_dir, target_name)
        except OSError:
            continue
        relpath = f"{project_subdir}/{target_name}"
        variants.append(FontVariant(path=relpath, weight=weight, italic=italic, source="cache"))
        report.fonts_downloaded.append((family, relpath))
    return variants


def _store_font_cache(source: Path, target_name: str) -> None:
    try:
        cache_dir = _font_cache_dir()
        cache_dir.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, cache_dir / _safe_name(target_name))
    except OSError:
        pass


def _copy_local_font_variants(
    family: str,
    *,
    fonts_dir: Path,
    project_subdir: str,
    report: AssetReport,
    wanted: set[tuple[int, bool]] | None = None,
) -> list[FontVariant]:
    local_fonts = _find_local_font_files(family)
    variants: list[FontVariant] = []
    used_names: set[str] = set()
    for local_font in local_fonts:
        weight, italic = _local_font_variant(local_font)
        if wanted is not None and (weight, italic) not in wanted:
            continue
        target_name = _unique_name(
            _safe_font_name(family, local_font.suffix, weight=weight, italic=italic),
            used_names,
        )
        try:
            target_name = _copy_file_smart(local_font, fonts_dir, target_name)
        except OSError:
            continue
        _store_font_cache(fonts_dir / target_name, target_name)
        relpath = f"{project_subdir}/{target_name}"
        variants.append(FontVariant(path=relpath, weight=weight, italic=italic, source="local"))
        report.fonts_downloaded.append((family, relpath))
    return variants


def _google_font_css_urls(family: str) -> list[str]:
    spec = family.replace(" ", "+")
    weights = "400;500;600;700;800;900"
    italic_weights = "0,400;0,500;0,600;0,700;0,800;0,900;1,400;1,500;1,600;1,700;1,800;1,900"
    return [
        f"https://fonts.googleapis.com/css2?family={spec}:ital,wght@{italic_weights}&display=swap",
        f"https://fonts.googleapis.com/css2?family={spec}:wght@{weights}&display=swap",
        f"https://fonts.googleapis.com/css2?family={spec}&display=swap",
    ]


def _select_google_ttf_url(css: str) -> str | None:
    faces = _google_ttf_faces(css)
    if not faces:
        return None
    faces.sort(key=lambda f: (int(f["weight"]), int(bool(f["italic"]))), reverse=True)
    return str(faces[0]["url"])


def _preferred_google_faces(faces: list[dict[str, object]]) -> list[dict[str, object]]:
    selected: dict[tuple[int, bool], dict[str, object]] = {}
    for face in faces:
        key = (int(face["weight"]), bool(face["italic"]))
        current = selected.get(key)
        if current is None or _google_face_score(face) > _google_face_score(current):
            selected[key] = face
    return sorted(selected.values(), key=lambda f: (int(f["weight"]), int(bool(f["italic"]))))


def _google_face_score(face: dict[str, object]) -> int:
    unicode_range = str(face.get("unicode_range", "")).lower()
    if not unicode_range:
        return 5
    if "u+0000-00ff" in unicode_range or "u+0020-007f" in unicode_range:
        return 20
    if "u+0100-02af" in unicode_range:
        return 10
    return 1


def _google_ttf_faces(css: str) -> list[dict[str, object]]:
    faces = re.findall(r"@font-face\s*{([^}]*)}", css, re.IGNORECASE | re.DOTALL)
    out: list[dict[str, object]] = []
    for face in faces:
        url_match = re.search(r"url\((https?://[^\)]+\.ttf)\)", face)
        if not url_match:
            continue
        weight = _font_face_weight(face)
        italic = _font_face_italic(face)
        out.append({
            "url": url_match.group(1),
            "weight": weight,
            "italic": italic,
            "unicode_range": _font_face_unicode_range(face),
        })
    if out:
        return out
    for m in re.finditer(r"url\((https?://[^\)]+\.ttf)\)", css):
        out.append({"url": m.group(1), "weight": 400, "italic": False})
    return out


def _font_face_weight(face: str) -> int:
    m = re.search(r"font-weight\s*:\s*([^;]+)", face, re.IGNORECASE)
    if not m:
        return 400
    value = m.group(1).strip().lower()
    if value == "normal":
        return 400
    if value == "bold":
        return 700
    nums = [int(float(n)) for n in re.findall(r"\d+(?:\.\d+)?", value)]
    return max(nums) if nums else 400


def _font_face_italic(face: str) -> bool:
    m = re.search(r"font-style\s*:\s*([^;]+)", face, re.IGNORECASE)
    if not m:
        return False
    return m.group(1).strip().lower() in ("italic", "oblique")


def _font_face_unicode_range(face: str) -> str:
    m = re.search(r"unicode-range\s*:\s*([^;]+)", face, re.IGNORECASE)
    return m.group(1).strip() if m else ""


def _font_suffix_from_url(url: str) -> str:
    suffix = Path(url.split("?", 1)[0]).suffix.lower()
    return suffix if suffix in (".ttf", ".otf") else ".ttf"


_LOCAL_FONT_ALIASES = {
    "arial": ("arial",),
    "barlowcondensed": ("barlowcondensed", "barlow"),
    "calibri": ("calibri",),
    "cambria": ("cambria",),
    "comic sans ms": ("comic", "comicsansms"),
    "consolas": ("consola", "consolas"),
    "courier new": ("cour", "couriernew"),
    "georgia": ("georgia",),
    "impact": ("impact",),
    "inter": ("inter",),
    "saira condensed": ("sairacondensed", "saira"),
    "segoe ui": ("segoeui", "segui", "segoe"),
    "tahoma": ("tahoma",),
    "times new roman": ("times", "timesnewroman"),
    "trebuchet ms": ("trebuc", "trebuchetms"),
    "verdana": ("verdana",),
}


def _find_local_font_files(family: str) -> list[Path]:
    prefixes = _font_search_prefixes(family)
    candidates: list[Path] = []
    for directory in _local_font_dirs():
        if not directory.is_dir():
            continue
        try:
            for child in directory.iterdir():
                if not child.is_file() or child.suffix.lower() not in (".ttf", ".otf"):
                    continue
                stem = _normalise_font_name(child.stem)
                if any(stem.startswith(prefix) or prefix in stem for prefix in prefixes):
                    candidates.append(child)
        except OSError:
            continue
    if not candidates:
        return []
    candidates.sort(key=lambda p: _local_font_score(p, prefixes), reverse=True)
    selected: dict[tuple[int, bool], Path] = {}
    for candidate in candidates:
        key = _local_font_variant(candidate)
        selected.setdefault(key, candidate)
    return list(selected.values())


def _font_search_prefixes(family: str) -> tuple[str, ...]:
    key = family.strip().lower()
    normalized = _normalise_font_name(family)
    aliases = _LOCAL_FONT_ALIASES.get(key, ())
    out: list[str] = []
    for item in (normalized, *aliases):
        n = _normalise_font_name(item)
        if n and n not in out:
            out.append(n)
    return tuple(out)


def _local_font_dirs() -> list[Path]:
    out: list[Path] = []
    env = os.environ.get("H2U_FONT_DIRS", "")
    for raw in env.split(os.pathsep):
        raw = raw.strip()
        if raw:
            out.append(Path(raw))

    windir = os.environ.get("WINDIR") or os.environ.get("SystemRoot")
    if windir:
        out.append(Path(windir) / "Fonts")
    local_appdata = os.environ.get("LOCALAPPDATA")
    if local_appdata:
        out.append(Path(local_appdata) / "Microsoft" / "Windows" / "Fonts")

    home = Path.home()
    out.extend([
        home / ".fonts",
        home / ".local" / "share" / "fonts",
        Path("/Library/Fonts"),
        Path("/System/Library/Fonts"),
        home / "Library" / "Fonts",
        Path("/usr/share/fonts"),
        Path("/usr/local/share/fonts"),
    ])

    seen: set[str] = set()
    unique: list[Path] = []
    for path in out:
        key = str(path).lower()
        if key not in seen:
            seen.add(key)
            unique.append(path)
    return unique


def _local_font_score(path: Path, prefixes: tuple[str, ...]) -> tuple[int, int, str]:
    stem = _normalise_font_name(path.stem)
    exact = 2 if stem in prefixes else 1 if any(stem.startswith(p) for p in prefixes) else 0
    style = 0
    if any(token in stem for token in ("black", "heavy", "extrabold", "semibold", "bold", "bd")):
        style += 10
    if any(token in stem for token in ("italic", "oblique", "bi", "z")):
        style += 6
    if path.suffix.lower() == ".ttf":
        style += 1
    return exact, style, path.name.lower()


def _local_font_variant(path: Path) -> tuple[int, bool]:
    stem = _normalise_font_name(path.stem)
    italic = any(token in stem for token in ("italic", "oblique")) or stem.endswith(("i", "bi", "z"))
    numeric = [int(n) for n in re.findall(r"(?<!\d)([1-9]00)(?!\d)", stem)]
    if numeric:
        weight = max(n for n in numeric if 100 <= n <= 900)
    elif any(token in stem for token in ("black", "heavy")):
        weight = 900
    elif any(token in stem for token in ("extrabold", "xbold")):
        weight = 800
    elif any(token in stem for token in ("bold", "semibold")) or stem.endswith(("bd", "bi", "b")):
        weight = 700
    elif any(token in stem for token in ("medium", "med")):
        weight = 500
    elif any(token in stem for token in ("light", "thin")):
        weight = 300
    else:
        weight = 400
    return weight, italic


def _normalise_font_name(value: str) -> str:
    return re.sub(r"[^a-z0-9]+", "", value.lower())


def _safe_font_name(family: str, suffix: str, *, weight: int | None = None, italic: bool = False) -> str:
    suffix = suffix if suffix.lower() in (".ttf", ".otf") else ".ttf"
    style = ""
    if weight is not None:
        style += f"-{weight}"
    if italic:
        style += "-Italic"
    return _safe_name(f"{family}{style}{suffix}")


def inject_font_references(uss: str, family_to_path: dict[str, str]) -> str:
    """Add `-unity-font-definition: url("<path>")` to rules that referenced a
    given font-family. Original `font-family` declarations were already
    dropped by the mapper, so we walk the original CSS in parallel."""
    if not family_to_path:
        return uss
    return uss
