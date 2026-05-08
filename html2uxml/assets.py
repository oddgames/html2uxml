"""Asset collection and rewriting.

Walks the converter output to find every URL referenced as `url(...)` (in USS
rules) and `src=` (on img elements that synthesized a USS background-image).

For each URL:
  * Local relative paths are resolved against `base_dir` and copied to
    `assets_dir`.
  * http(s) URLs are downloaded to `assets_dir` if `download_remote` is set.
  * data: URIs and unresolved references are left alone with a warning.

The url() reference in USS is rewritten to point at the copied file using a
project-relative path (for generated output, usually `Images/star.png`).

Fonts are surfaced separately so the CLI can turn each font-family into a
Unity `-unity-font-definition`. Google Fonts is tried first; installed local
TTF/OTF files are used as a fallback.
"""
from __future__ import annotations

import hashlib
import os
import re
import shutil
import struct
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
        if not raw or raw.startswith("data:"):
            return m.group(0)
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


def download_google_fonts(
    families: list[str],
    *,
    assets_dir: Path,
    project_subdir: str = "Fonts",
    timeout: float = 8.0,
) -> tuple[dict[str, list[FontVariant]], AssetReport]:
    """Best-effort download of TTF/OTF files for each family.

    Google Fonts is preferred. If Google does not have a TTF for a family,
    the converter falls back to installed local fonts, such as Windows Fonts.
    Returns (mapping family -> variants, report).
    """
    report = AssetReport()
    out: dict[str, list[FontVariant]] = {}
    fonts_dir = Path(assets_dir)
    fonts_dir.mkdir(parents=True, exist_ok=True)
    for family in families:
        report.fonts_seen.append(family)
        variants = _copy_cached_font_variants(
            family,
            fonts_dir=fonts_dir,
            project_subdir=project_subdir,
            report=report,
        )
        if _has_common_font_axis_coverage(variants):
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
        selected.setdefault(_local_font_variant(candidate), candidate)

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
) -> list[FontVariant]:
    local_fonts = _find_local_font_files(family)
    variants: list[FontVariant] = []
    used_names: set[str] = set()
    for local_font in local_fonts:
        weight, italic = _local_font_variant(local_font)
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
