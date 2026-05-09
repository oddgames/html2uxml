"""Command-line interface."""
from __future__ import annotations

import argparse
import base64
import difflib
import hashlib
import importlib.util
import json
import math
import os
import re
import shutil
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request
from dataclasses import dataclass, field, replace
from pathlib import Path
from urllib.parse import urljoin

from .assets import (
    AssetReport,
    EmbeddedFontReport,
    FontVariant,
    collect_and_rewrite,
    download_google_fonts,
    extract_embedded_font_faces,
    inject_image_aspect_ratios,
)
from .converter import convert
from .css_parser import parse_selector
from .html_parser import parse_html
from .resolver import selector_matches


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
    p.add_argument("--render-browser", choices=("auto", "always", "never"), default="auto",
                   help=(
                       "For URL inputs, render the page in headless Chrome/Edge before conversion. "
                       "auto renders when the selector is missing from the initial HTML shell."
                   ))
    p.add_argument("--render-wait-ms", type=int, default=3000,
                   help="Virtual time budget for browser rendering in milliseconds (default: 3000).")
    p.add_argument("--rendered-html-out", default=None,
                   help="Optional path to write the browser-rendered DOM snapshot.")
    p.add_argument("--css", action="append", default=[],
                   help="Additional CSS file(s) to include. Linked stylesheets in the input HTML are loaded automatically.")
    p.add_argument("--bundle-assets", action="store_true",
                   help="Copy referenced images into <out>/UI/Images and rewrite url() in USS.")
    p.add_argument("--download-assets", action="store_true",
                   help="Also download remote http(s) image URLs (implies --bundle-assets).")
    p.set_defaults(rasterize_svg=True)
    p.add_argument("--rasterize-svg", dest="rasterize_svg", action="store_true",
                   help="Rasterize generated inline SVG assets to PNG using CairoSVG, ImageMagick, or browser fallback (default).")
    p.add_argument("--no-rasterize-svg", dest="rasterize_svg", action="store_false",
                   help="Keep generated inline SVG assets as SVG files for Unity Vector Graphics import.")
    p.add_argument("--svg-raster-scale", type=int, default=4,
                   help="Pixel scale for baked inline SVG PNGs (default: 4, suitable for UI scaled up to 4x).")
    p.add_argument("--svg-raster-min-px", type=int, default=256,
                   help="Minimum baked PNG size for generated inline SVGs on the largest side (default: 256).")
    p.set_defaults(download_fonts=True)
    p.add_argument("--download-fonts", dest="download_fonts", action="store_true",
                   help="Bundle referenced fonts into <out>/UI/Fonts (default). Google Fonts is tried first; local/system TTF/OTF files are used as fallback.")
    p.add_argument("--no-download-fonts", dest="download_fonts", action="store_false",
                   help="Do not bundle font files; leave --odd-font-family markers in USS.")
    p.set_defaults(textcore_font_assets=True)
    p.add_argument("--textcore-font-assets", dest="textcore_font_assets", action="store_true",
                   help="Reference auto-generated Unity TextCore FontAsset .asset files in USS (default).")
    p.add_argument("--no-textcore-font-assets", dest="textcore_font_assets", action="store_false",
                   help="Reference source TTF/OTF files directly instead of generated TextCore FontAsset assets.")
    p.add_argument("--fail-on-inline-styles", action="store_true",
                   help="Fail if source HTML contains style=\"...\" attributes.")
    p.add_argument("--timeout", type=float, default=10.0,
                   help="Network timeout in seconds for URL fetches (default: 10).")
    p.add_argument("-q", "--quiet", action="store_true",
                   help="Suppress the conversion report.")
    p.add_argument("--list-selectors", action="store_true",
                   help="Print available selectors (ids and screen-* candidates) as JSON and exit, "
                        "without converting.")
    p.add_argument("--list-families", action="store_true",
                   help="Print detected font-family usage as JSON and exit, "
                        "without writing any files. Each entry includes the family name, "
                        "used (weight, italic) variants, and a sample of characters.")
    p.add_argument("--font-alias", action="append", default=[], metavar="OLD=NEW",
                   help="Merge font family OLD into NEW. Pass once per merge. "
                        "Applied before font download so unused families are skipped.")
    args = p.parse_args(argv)

    is_url = bool(re.match(r"^https?://", args.input))
    rendered_by_browser = False
    rendered_html_path: Path | None = None
    if is_url:
        try:
            raw_html = _fetch_url_text(args.input, timeout=args.timeout)
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

        html = raw_html
        if _should_render_url_html(raw_html, args.selector, args.render_browser):
            try:
                html = _render_url_with_browser(
                    args.input,
                    wait_ms=max(0, args.render_wait_ms),
                    timeout=args.timeout,
                )
                rendered_by_browser = True
            except BrowserRenderError as e:
                if args.render_browser == "always":
                    print(f"error: browser render failed: {e}", file=sys.stderr)
                    return 2
                print(f"warning: browser render failed, using initial HTML: {e}", file=sys.stderr)
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

    if args.list_selectors:
        print(_emit_selector_list(html))
        return 0

    ui_dir = out_dir / "UI"
    ui_dir.mkdir(parents=True, exist_ok=True)
    uxml_path = ui_dir / f"{base}.uxml"
    uss_path = ui_dir / f"{base}.uss"

    if rendered_by_browser and args.rendered_html_out:
        rendered_html_path = Path(args.rendered_html_out)
        if not rendered_html_path.is_absolute():
            rendered_html_path = out_dir / rendered_html_path
        rendered_html_path.parent.mkdir(parents=True, exist_ok=True)
        rendered_html_path.write_text(html, encoding="utf-8")

    extra_css_parts: list[str] = []

    # When the input is a URL we resolve linked stylesheets against the page
    # URL and fetch each one over HTTP, since `convert()`'s file-based
    # base_dir can't reach them. If a browser snapshot was used, inspect the
    # rendered DOM's links; SPAs can inject or rewrite stylesheet tags.
    if is_url:
        for href in _linked_stylesheet_hrefs(raw_html, html):
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

    if args.fail_on_inline_styles:
        inline_style_count = _count_inline_styles(html)
        if inline_style_count:
            suffix = "" if inline_style_count == 1 else "s"
            print(
                f"error: found {inline_style_count} inline style attribute{suffix}; "
                "move presentation into class rules or omit "
                "--fail-on-inline-styles for rendered DOM snapshots",
                file=sys.stderr,
            )
            return 2

    if args.selector and not _selector_matches_html(html, args.selector):
        print(_selector_missing_message(html, args.selector), file=sys.stderr)
        return 2

    result = convert(
        html,
        extra_css=extra_css,
        uss_filename=uss_path.name,
        base_dir=local_base_dir,
        select=args.selector,
        svg_assets_subdir="Images",
        text_gradients_assets_subdir="TextGradients",
    )
    _prefix_text_gradient_assets(result, base)
    result.uss = _drop_size_when_anchored_to_parent(result.uss, result.uxml)

    alias_map = _parse_font_aliases(args.font_alias)
    if alias_map:
        _apply_font_aliases(result, alias_map)

    if args.list_families:
        referenced = _all_referenced_families(html, extra_css)
        print(_emit_family_list(result, referenced=referenced))
        return 0

    images_dir = ui_dir / "Images"
    svg_report: SvgRasterReport | None = None
    if result.svg_files:
        images_dir.mkdir(parents=True, exist_ok=True)
        _clean_generated_svg_assets(images_dir)
        result.svg_files = _write_svg_files_smart(result, images_dir, "Images")
        if args.rasterize_svg:
            svg_report = _rasterize_svg_assets(
                result,
                images_dir,
                scale=max(1, args.svg_raster_scale),
                min_px=max(1, args.svg_raster_min_px),
            )

    text_gradients_dir = ui_dir / "TextGradients"
    if result.text_gradient_files:
        text_gradients_dir.mkdir(parents=True, exist_ok=True)
        for filename, raw in result.text_gradient_files:
            _write_text_asset_smart(text_gradients_dir, filename, raw)

    if result.data_uri_files:
        images_dir.mkdir(parents=True, exist_ok=True)
        for filename, data in result.data_uri_files:
            written = _write_bytes_asset_smart(images_dir, filename, data)
            _write_unity_png_meta_if_png(images_dir / written)

    asset_report: AssetReport | None = None
    if args.bundle_assets or args.download_assets:
        result.uss, asset_report = collect_and_rewrite(
            result.uss,
            base_dir=local_base_dir,
            assets_dir=images_dir,
            project_subdir="Images",
            download_remote=args.download_assets,
        )
        _write_unity_png_metas(images_dir)
        result.uss = inject_image_aspect_ratios(
            result.uss,
            assets_dir=images_dir,
            project_subdir="Images",
        )

    font_report: AssetReport | None = None
    embedded_report: EmbeddedFontReport | None = None
    if args.download_fonts:
        wanted = _wanted_variants_from_usages(result)
        families = _used_families(result.uss, wanted)
        fonts_dir = ui_dir / "Fonts"

        embedded_seed: dict[str, list[FontVariant]] | None = None
        raw_css = _collect_raw_css(html, extra_css)
        if raw_css:
            referenced = set(_all_referenced_families(html, extra_css))
            referenced.update(families)
            embedded_seed, embedded_report = extract_embedded_font_faces(
                raw_css,
                base_dir=local_base_dir,
                fonts_dir=fonts_dir,
                project_subdir="Fonts",
                timeout=args.timeout,
                download_remote=True,
                families_filter=referenced or None,
            )
            for family in embedded_seed:
                if family not in families:
                    families.append(family)

        family_to_path, font_report = download_google_fonts(
            families,
            assets_dir=fonts_dir,
            project_subdir="Fonts",
            wanted=wanted,
            seed=embedded_seed,
        )
        family_to_path = _dedupe_font_files(family_to_path, fonts_dir)
        result.uss = _inject_font_definitions(
            result.uss,
            family_to_path,
            use_textcore_font_assets=args.textcore_font_assets,
        )
        if args.textcore_font_assets:
            _write_font_asset_manifest(result, family_to_path, fonts_dir)

    # Stash a copy of the source HTML in a sibling Src/ folder so re-
    # conversion or visual diffs against the browser have a stable
    # filename. Reference it from both files via an in-band comment, so
    # opening the .uxml/.uss in any editor leaves a breadcrumb back to
    # the original input.
    src_dir = out_dir / "Src"
    src_dir.mkdir(parents=True, exist_ok=True)
    source_path = src_dir / f"{base}.html"
    source_path.write_text(html, encoding="utf-8")
    source_ref = f"Src/{source_path.name}"
    if not result.uxml.startswith("<?xml"):
        result.uxml = f"<!-- source: {source_ref} -->\n" + result.uxml
    else:
        nl = result.uxml.find("\n")
        if nl >= 0:
            result.uxml = (
                result.uxml[: nl + 1]
                + f"<!-- source: {source_ref} -->\n"
                + result.uxml[nl + 1 :]
            )
        else:
            result.uxml = result.uxml + f"\n<!-- source: {source_ref} -->\n"
    result.uss = f"/* source: {source_ref} */\n" + result.uss

    uxml_path.write_text(result.uxml, encoding="utf-8")
    uss_path.write_text(result.uss, encoding="utf-8")

    print(f"wrote {uxml_path}")
    print(f"wrote {uss_path}")
    print(f"wrote {source_path}")  # Src/<base>.html
    if not args.quiet:
        _print_report(
            result,
            asset_report,
            font_report,
            svg_report,
            embedded_report=embedded_report,
            rendered_by_browser=rendered_by_browser,
            rendered_html_path=rendered_html_path,
            file=sys.stderr,
        )
    return 0


@dataclass
class SvgRasterReport:
    rasterized: list[tuple[str, str]] = field(default_factory=list)
    failed: list[tuple[str, str]] = field(default_factory=list)
    renderers: dict[str, int] = field(default_factory=dict)


_SVG_DIM_RE = re.compile(
    r'(?<![-A-Za-z0-9_])width\s*=\s*["\']([^"\']+)["\']|'
    r'(?<![-A-Za-z0-9_])height\s*=\s*["\']([^"\']+)["\']|'
    r'(?<![-A-Za-z0-9_])viewBox\s*=\s*["\']([^"\']+)["\']'
)


def _clean_generated_svg_assets(images_dir: Path) -> None:
    for old in images_dir.glob("svg-*.*"):
        if old.suffix.lower() in (".svg", ".png", ".meta"):
            try:
                old.unlink()
            except OSError:
                pass


def _prefix_text_gradient_assets(result, screen_name: str) -> None:
    """Avoid shared UI/TextGradients name collisions across screen exports."""
    if not result.text_gradient_files:
        return
    prefix = re.sub(r"[^A-Za-z0-9._-]+", "-", screen_name).strip("-._") or "screen"
    renamed: list[tuple[str, str]] = []
    replacements: dict[str, str] = {}
    for filename, raw in result.text_gradient_files:
        old_name = Path(filename).stem
        new_name = f"{prefix}-{old_name}"
        replacements[old_name] = new_name
        new_raw = raw.replace(f'"name": "{old_name}"', f'"name": "{new_name}"')
        renamed.append((f"{new_name}.h2utg.json", new_raw))
    for old_name, new_name in replacements.items():
        result.uxml = result.uxml.replace(old_name, new_name)
    result.text_gradient_files = renamed


def _write_svg_files_smart(result, images_dir: Path, project_subdir: str) -> list[tuple[str, str]]:
    """Write generated SVG files without clobbering shared UI/Images assets."""
    written: list[tuple[str, str]] = []
    rename_map: dict[str, str] = {}
    for filename, raw in result.svg_files:
        target_name = _write_text_asset_smart(images_dir, filename, raw)
        written.append((target_name, raw))
        if target_name != filename:
            rename_map[filename] = target_name
    if rename_map:
        for old, new in rename_map.items():
            result.uss = result.uss.replace(
                f'url("{project_subdir}/{old}")',
                f'url("{project_subdir}/{new}")',
            )
            result.uss = result.uss.replace(
                f"url('{project_subdir}/{old}')",
                f"url('{project_subdir}/{new}')",
            )
            result.uss = result.uss.replace(
                f"url({project_subdir}/{old})",
                f"url({project_subdir}/{new})",
            )
    return written


def _write_bytes_asset_smart(dest_dir: Path, target_name: str, data: bytes) -> str:
    safe_name = re.sub(r"[^A-Za-z0-9._-]+", "_", target_name).strip("._") or "asset"
    stem = Path(safe_name).stem
    suffix = Path(safe_name).suffix
    candidate = safe_name
    i = 2
    dest_dir.mkdir(parents=True, exist_ok=True)
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


def _write_text_asset_smart(dest_dir: Path, target_name: str, text: str) -> str:
    data = text.encode("utf-8")
    safe_name = re.sub(r"[^A-Za-z0-9._-]+", "_", target_name).strip("._") or "asset"
    stem = Path(safe_name).stem
    suffix = Path(safe_name).suffix
    candidate = safe_name
    i = 2
    dest_dir.mkdir(parents=True, exist_ok=True)
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


def _rasterize_svg_assets(
    result,
    images_dir: Path,
    *,
    scale: int = 4,
    min_px: int = 256,
) -> SvgRasterReport:
    report = SvgRasterReport()
    has_cairosvg = _has_cairosvg()
    magick = _find_magick_executable()
    browser: str | None = None
    if not has_cairosvg and not magick:
        browser = _find_browser_executable()
    if not has_cairosvg and not magick and not browser:
        result.warnings.append(
            "SVG rasterization skipped: could not find CairoSVG, ImageMagick, Chrome, Edge, or Chromium; "
            "Unity will need com.unity.vectorgraphics for generated SVG icons"
        )
        report.failed.extend((name, "rasterizer not found") for name, _raw in result.svg_files)
        return report

    for filename, raw in result.svg_files:
        png_name = f"{Path(filename).stem}.png"
        png_path = images_dir / png_name
        errors: list[str] = []
        renderer_used: str | None = None
        # Figma / Onlook exports stuff every SVG element with a massive
        # inline `style=` attribute (every computed CSS prop). CairoSVG
        # interprets that as authoritative and silently renders an empty
        # PNG — the presentation attrs (`fill`, `stroke`, `stroke-width`)
        # are what we actually want. Strip the inline `style=` blocks
        # before rasterising so the icons come through.
        raw_clean = _strip_svg_inline_styles(raw)
        try:
            if has_cairosvg:
                _rasterize_one_svg_with_cairosvg(raw_clean, png_path, scale=scale, min_px=min_px)
                renderer_used = "CairoSVG"
            elif magick:
                _rasterize_one_svg_with_magick(magick, raw_clean, png_path, scale=scale, min_px=min_px)
                renderer_used = "ImageMagick"
            else:
                _rasterize_one_svg(browser, raw_clean, png_path, scale=scale, min_px=min_px)
                renderer_used = "browser"
        except Exception as e:  # pragma: no cover - exact browser failures are platform-specific
            errors.append(f"CairoSVG: {e}" if has_cairosvg else str(e))
            if magick:
                try:
                    _rasterize_one_svg_with_magick(magick, raw_clean, png_path, scale=scale, min_px=min_px)
                except Exception as magick_e:  # pragma: no cover - exact ImageMagick failures are platform-specific
                    errors.append(f"ImageMagick: {magick_e}")
                else:
                    errors.clear()
                    renderer_used = "ImageMagick"
            if errors and browser is None:
                browser = _find_browser_executable()
            if errors and browser:
                try:
                    _rasterize_one_svg(browser, raw_clean, png_path, scale=scale, min_px=min_px)
                except Exception as browser_e:  # pragma: no cover - exact browser failures are platform-specific
                    errors.append(f"browser: {browser_e}")
                else:
                    errors.clear()
                    renderer_used = "browser"
            if errors:
                report.failed.append((filename, "; ".join(errors)))
                continue
        try:
            (images_dir / filename).unlink()
        except OSError:
            pass
        report.rasterized.append((filename, png_name))
        if renderer_used:
            report.renderers[renderer_used] = report.renderers.get(renderer_used, 0) + 1
        _write_unity_png_meta(png_path)

    if report.rasterized:
        result.uss = _replace_svg_urls_with_png(result.uss, [name for name, _png in report.rasterized])
        if not report.failed:
            result.warnings = [
                w for w in result.warnings
                if not w.startswith("SVG assets emitted; Unity requires SVG import support")
            ]
    if report.failed:
        result.warnings.append(
            f"SVG rasterization failed for {len(report.failed)} asset(s); "
            "remaining SVGs require Unity Vector Graphics support"
        )
    return report


def _write_unity_png_meta(png_path: Path) -> None:
    """Write UI-friendly TextureImporter settings for baked SVG PNGs.

    Unity's default import settings enable mipmaps and compression. That can
    make 18px toolbar glyphs look clipped, dim, or partially missing in UI
    Toolkit, even when the PNG itself is correct. The generated meta keeps the
    texture uncompressed, transparent, clamped, and non-mipped.
    """
    guid = hashlib.md5(f"html2uxml:{png_path.as_posix().lower()}".encode("utf-8")).hexdigest()
    meta = f"""fileFormatVersion: 2
guid: {guid}
TextureImporter:
  internalIDToNameTable: []
  externalObjects: {{}}
  serializedVersion: 13
  mipmaps:
    mipMapMode: 0
    enableMipMap: 0
    sRGBTexture: 1
    linearTexture: 0
    fadeOut: 0
    borderMipMap: 0
    mipMapsPreserveCoverage: 0
    alphaTestReferenceValue: 0.5
    mipMapFadeDistanceStart: 1
    mipMapFadeDistanceEnd: 3
  bumpmap:
    convertToNormalMap: 0
    externalNormalMap: 0
    heightScale: 0.25
    normalMapFilter: 0
    flipGreenChannel: 0
  isReadable: 0
  streamingMipmaps: 0
  streamingMipmapsPriority: 0
  vTOnly: 0
  ignoreMipmapLimit: 0
  grayScaleToAlpha: 0
  generateCubemap: 6
  cubemapConvolution: 0
  seamlessCubemap: 0
  textureFormat: 1
  maxTextureSize: 2048
  textureSettings:
    serializedVersion: 2
    filterMode: 1
    aniso: 1
    mipBias: 0
    wrapU: 1
    wrapV: 1
    wrapW: 1
  nPOTScale: 0
  lightmap: 0
  compressionQuality: 100
  spriteMode: 0
  spriteExtrude: 1
  spriteMeshType: 1
  alignment: 0
  spritePivot: {{x: 0.5, y: 0.5}}
  spritePixelsToUnits: 100
  spriteBorder: {{x: 0, y: 0, z: 0, w: 0}}
  spriteGenerateFallbackPhysicsShape: 0
  alphaUsage: 1
  alphaIsTransparency: 1
  spriteTessellationDetail: -1
  textureType: 0
  textureShape: 1
  singleChannelComponent: 0
  flipbookRows: 1
  flipbookColumns: 1
  maxTextureSizeSet: 0
  compressionQualitySet: 1
  textureFormatSet: 0
  ignorePngGamma: 0
  applyGammaDecoding: 0
  swizzle: 50462976
  cookieLightType: 0
  platformSettings:
  - serializedVersion: 4
    buildTarget: DefaultTexturePlatform
    maxTextureSize: 2048
    resizeAlgorithm: 0
    textureFormat: -1
    textureCompression: 0
    compressionQuality: 100
    crunchedCompression: 0
    allowsAlphaSplitting: 0
    overridden: 0
    ignorePlatformSupport: 0
    androidETC2FallbackOverride: 0
    forceMaximumCompressionQuality_BC6H_BC7: 0
  spriteSheet:
    serializedVersion: 2
    sprites: []
    outline: []
    physicsShape: []
    bones: []
    spriteID:
    internalID: 0
    vertices: []
    indices:
    edges: []
    weights: []
    secondaryTextures: []
    nameFileIdTable: {{}}
  mipmapLimitGroupName:
  pSDRemoveMatte: 0
  userData:
  assetBundleName:
  assetBundleVariant:
"""
    png_path.with_suffix(png_path.suffix + ".meta").write_text(meta, encoding="utf-8")


def _write_unity_png_meta_if_png(path: Path) -> None:
    if path.suffix.lower() == ".png" and path.is_file():
        _write_unity_png_meta(path)


def _write_unity_png_metas(images_dir: Path) -> None:
    if not images_dir.is_dir():
        return
    for png_path in images_dir.glob("*.png"):
        _write_unity_png_meta(png_path)


def _has_cairosvg() -> bool:
    return importlib.util.find_spec("cairosvg") is not None


def _find_magick_executable() -> str | None:
    candidate = os.environ.get("H2U_MAGICK")
    if candidate and Path(candidate).is_file():
        return candidate
    found = shutil.which("magick")
    if found:
        return found
    candidates = [
        r"C:\Program Files\ImageMagick-7.1.2-Q16\magick.exe",
        r"C:\Program Files\ImageMagick-7.1.1-Q16\magick.exe",
    ]
    for path in candidates:
        if Path(path).is_file():
            return path
    return None


_SVG_INLINE_STYLE_RE = re.compile(r'\sstyle\s*=\s*"[^"]*"', re.IGNORECASE)


def _strip_svg_inline_styles(raw: str) -> str:
    """Remove `style="..."` attributes from every tag in an SVG payload.

    Figma/Onlook exports drop a multi-kilobyte computed-CSS dump on every
    `<svg>` and `<path>` element. CairoSVG (and several other rasterisers)
    treat that inline style as authoritative and end up rendering empty
    pixels — the icons' actual fill / stroke information lives in the
    presentation attributes (`fill`, `stroke`, `stroke-width`, etc.) which
    survive the strip.
    """
    return _SVG_INLINE_STYLE_RE.sub("", raw)


def _rasterize_one_svg_with_cairosvg(
    raw_svg: str,
    png_path: Path,
    *,
    scale: int = 4,
    min_px: int = 256,
) -> None:
    import cairosvg

    dims = _svg_dimensions(raw_svg) or (24.0, 24.0)
    css_w = max(1, int(math.ceil(dims[0])))
    css_h = max(1, int(math.ceil(dims[1])))
    effective_scale = max(float(scale), float(min_px) / float(max(css_w, css_h)))
    pixel_w = max(1, int(math.ceil(css_w * effective_scale)))
    pixel_h = max(1, int(math.ceil(css_h * effective_scale)))
    png_path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="html2uxml-svg-", ignore_cleanup_errors=True) as td:
        out_path = Path(td) / "icon.png"
        cairosvg.svg2png(
            bytestring=raw_svg.encode("utf-8"),
            write_to=str(out_path),
            output_width=pixel_w,
            output_height=pixel_h,
        )
        if not out_path.is_file() or out_path.stat().st_size <= 0:
            raise RuntimeError("CairoSVG produced no PNG")
        _copy_or_crop_png(out_path, png_path, pixel_w, pixel_h)


def _replace_svg_urls_with_png(
    uss: str,
    filenames: list[str],
    *,
    project_subdir: str = "Images",
) -> str:
    out = uss
    prefix = project_subdir.replace("\\", "/").strip("/")
    for filename in filenames:
        png_name = f"{Path(filename).stem}.png"
        out = out.replace(f'url("{prefix}/{filename}")', f'url("{prefix}/{png_name}")')
        out = out.replace(f"url('{prefix}/{filename}')", f"url('{prefix}/{png_name}')")
        out = out.replace(f"url({prefix}/{filename})", f"url({prefix}/{png_name})")
    return out


def _rasterize_one_svg(
    browser: str,
    raw_svg: str,
    png_path: Path,
    *,
    scale: int = 4,
    min_px: int = 256,
) -> None:
    dims = _svg_dimensions(raw_svg) or (24.0, 24.0)
    css_w = max(1, int(math.ceil(dims[0])))
    css_h = max(1, int(math.ceil(dims[1])))
    effective_scale = max(float(scale), float(min_px) / float(max(css_w, css_h)))
    pixel_w = max(1, int(math.ceil(css_w * effective_scale)))
    pixel_h = max(1, int(math.ceil(css_h * effective_scale)))
    svg_data = base64.b64encode(raw_svg.encode("utf-8")).decode("ascii")
    html = (
        "<!doctype html><meta charset=\"utf-8\">"
        "<style>"
        f"html,body{{margin:0;width:{pixel_w}px;height:{pixel_h}px;"
        "background:transparent;overflow:hidden;}}"
        f"img{{display:block;width:{pixel_w}px;height:{pixel_h}px;}}"
        "*{box-sizing:border-box;}"
        "</style>"
        f'<img src="data:image/svg+xml;base64,{svg_data}" alt="">'
    )
    png_path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="html2uxml-svg-", ignore_cleanup_errors=True) as td:
        td_path = Path(td)
        html_path = td_path / "icon.html"
        shot_path = td_path / "icon.png"
        html_path.write_text(html, encoding="utf-8")
        attempts: list[str] = []
        for headless_flag in ("--headless=new", "--headless"):
            profile = td_path / f"profile-{len(attempts)}"
            cmd = [
                browser,
                headless_flag,
                "--disable-gpu",
                "--disable-dev-shm-usage",
                "--no-first-run",
                "--no-default-browser-check",
                "--no-sandbox",
                "--force-device-scale-factor=1",
                "--default-background-color=00000000",
                f"--user-data-dir={profile}",
                f"--window-size={pixel_w},{pixel_h}",
                f"--screenshot={shot_path}",
                html_path.as_uri(),
            ]
            proc = subprocess.run(
                cmd,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                encoding="utf-8",
                errors="replace",
                timeout=20,
            )
            if proc.returncode == 0 and shot_path.is_file() and shot_path.stat().st_size > 0:
                _copy_or_crop_png(shot_path, png_path, pixel_w, pixel_h)
                return
            attempts.append((proc.stderr or proc.stdout or f"exit code {proc.returncode}").strip()[:300])
        raise RuntimeError("; ".join(a for a in attempts if a) or "browser produced no PNG")


def _rasterize_one_svg_with_magick(
    magick: str,
    raw_svg: str,
    png_path: Path,
    *,
    scale: int = 4,
    min_px: int = 256,
) -> None:
    dims = _svg_dimensions(raw_svg) or (24.0, 24.0)
    css_w = max(1, int(math.ceil(dims[0])))
    css_h = max(1, int(math.ceil(dims[1])))
    effective_scale = max(float(scale), float(min_px) / float(max(css_w, css_h)))
    pixel_w = max(1, int(math.ceil(css_w * effective_scale)))
    pixel_h = max(1, int(math.ceil(css_h * effective_scale)))
    png_path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="html2uxml-svg-", ignore_cleanup_errors=True) as td:
        td_path = Path(td)
        svg_path = td_path / "icon.svg"
        out_path = td_path / "icon.png"
        svg_path.write_text(raw_svg, encoding="utf-8")
        cmd = [
            magick,
            "-background", "none",
            "-density", "1024",
            str(svg_path),
            "-resize", f"{pixel_w}x{pixel_h}",
            str(out_path),
        ]
        proc = subprocess.run(
            cmd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=20,
        )
        if proc.returncode != 0 or not out_path.is_file() or out_path.stat().st_size <= 0:
            detail = (proc.stderr or proc.stdout or f"exit code {proc.returncode}").strip()
            raise RuntimeError(detail[:500] or "ImageMagick produced no PNG")
        _copy_or_crop_png(out_path, png_path, pixel_w, pixel_h)


def _copy_or_crop_png(src: Path, dst: Path, width: int, height: int) -> None:
    try:
        from PIL import Image
    except Exception:
        shutil.copyfile(src, dst)
        return
    with Image.open(src) as im:
        if im.size != (width, height):
            im = im.crop((0, 0, min(width, im.size[0]), min(height, im.size[1])))
        im.save(dst)


def _resize_svg_root(raw_svg: str, width: int, height: int) -> str:
    match = re.match(r"(?is)(\s*<svg\b)([^>]*)(>.*)", raw_svg)
    if not match:
        return raw_svg
    prefix, attrs, rest = match.groups()
    attrs = re.sub(r'\swidth\s*=\s*["\'][^"\']*["\']', "", attrs, flags=re.IGNORECASE)
    attrs = re.sub(r'\sheight\s*=\s*["\'][^"\']*["\']', "", attrs, flags=re.IGNORECASE)
    return f'{prefix}{attrs} width="{width}" height="{height}"{rest}'


def _svg_dimensions(raw: str) -> tuple[float, float] | None:
    width: float | None = None
    height: float | None = None
    view_w: float | None = None
    view_h: float | None = None
    head_end = raw.find(">")
    head = raw[:head_end] if head_end > 0 else raw
    for m in _SVG_DIM_RE.finditer(head):
        if m.group(1) is not None:
            width = _to_float(m.group(1))
        elif m.group(2) is not None:
            height = _to_float(m.group(2))
        elif m.group(3) is not None:
            parts = m.group(3).replace(",", " ").split()
            if len(parts) == 4:
                view_w = _to_float(parts[2])
                view_h = _to_float(parts[3])
    w = width if width is not None else view_w
    h = height if height is not None else view_h
    if w is None or h is None:
        return None
    return w, h


def _to_float(value: str) -> float | None:
    try:
        return float(value.rstrip("px").strip())
    except ValueError:
        return None


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


def _linked_stylesheet_hrefs(*html_sources: str) -> list[str]:
    seen: set[str] = set()
    out: list[str] = []
    for html in html_sources:
        parsed = parse_html(html)
        for href in parsed.linked_stylesheets:
            if href not in seen:
                seen.add(href)
                out.append(href)
    return out


class BrowserRenderError(RuntimeError):
    pass


def _should_render_url_html(html: str, selector: str | None, mode: str) -> bool:
    if mode == "never":
        return False
    if mode == "always":
        return True
    if selector and not _selector_matches_html(html, selector):
        return True
    return _looks_like_framework_shell(html)


def _selector_matches_html(html: str, selector_raw: str) -> bool:
    selector = parse_selector(selector_raw)
    if selector is None:
        return False
    parsed = parse_html(html)
    found = False

    def walk(node, ancestors) -> None:
        nonlocal found
        if found or node.is_text:
            return
        if node.tag != "__root__":
            if ancestors:
                parent = ancestors[-1]
                siblings = [c for c in parent.children if not c.is_text]
                try:
                    sibling_index = siblings.index(node)
                except ValueError:
                    sibling_index = -1
            else:
                siblings = [node]
                sibling_index = 0
            if selector_matches(node, selector, ancestors, sibling_index, siblings):
                found = True
                return
        for child in node.children:
            walk(child, ancestors + [node])

    walk(parsed.root, [])
    return found


def _selector_missing_message(html: str, selector_raw: str) -> str:
    lines = [f"error: selector matched no element after rendering: {selector_raw!r}"]
    selector_token = _simple_selector_token(selector_raw)
    ids = _collect_attr_values(html, "id")
    classes = _collect_attr_values(html, "class")

    if selector_token and selector_token in html:
        lines.append(
            f"hint: {selector_token!r} appears in the HTML text, script, or label data, "
            "but not on a matching rendered element."
        )

    candidates = ids if selector_raw.strip().startswith("#") else classes
    if selector_token and candidates:
        close = difflib.get_close_matches(selector_token, candidates, n=5, cutoff=0.35)
        if close:
            label = "ids" if selector_raw.strip().startswith("#") else "classes"
            lines.append(f"hint: closest rendered {label}: {', '.join(close)}")

    screen_ids = [value for value in ids if "screen" in value.lower()]
    if screen_ids:
        lines.append(f"hint: rendered screen ids: {', '.join(screen_ids[:12])}")
    return "\n".join(lines)


def _simple_selector_token(selector_raw: str) -> str | None:
    raw = selector_raw.strip()
    if not raw:
        return None
    m = re.fullmatch(r"#([A-Za-z_][\w-]*)", raw)
    if m:
        return m.group(1)
    m = re.fullmatch(r"\.([A-Za-z_][\w-]*)", raw)
    if m:
        return m.group(1)
    return None


def _emit_selector_list(html: str) -> str:
    """Walk the rendered DOM and produce a JSON list of pickable selectors.

    Each entry: {selector, kind, tag, text}. ``kind`` is "id" or "class".
    Ids come first; screen-prefixed ids float to the top so the importer's UI
    can highlight them as the obvious picks.
    """
    parsed = parse_html(html)
    entries: list[dict] = []
    seen: set[str] = set()

    def add(selector: str, kind: str, tag: str, text: str) -> None:
        if selector in seen:
            return
        seen.add(selector)
        entries.append({
            "selector": selector,
            "kind": kind,
            "tag": tag,
            "text": text[:80].strip(),
        })

    def gather_text(node, limit: int = 80) -> str:
        out: list[str] = []
        def walk(n):
            if n.is_text:
                t = (n.text or "").strip()
                if t:
                    out.append(t)
                return
            for c in n.children:
                walk(c)
                if sum(len(s) for s in out) >= limit:
                    return
        walk(node)
        return " ".join(out)

    def visit(node) -> None:
        if node.is_text:
            return
        attr_id = node.attrs.get("id")
        if attr_id:
            add(f"#{attr_id}", "id", node.tag, gather_text(node))
        for child in node.children:
            visit(child)

    visit(parsed.root)

    classes_seen: set[str] = set()
    def visit_classes(node) -> None:
        if node.is_text:
            return
        cls = node.attrs.get("class") or ""
        for token in cls.split():
            if token in classes_seen:
                continue
            classes_seen.add(token)
            add(f".{token}", "class", node.tag, gather_text(node))
        for child in node.children:
            visit_classes(child)
    visit_classes(parsed.root)

    # Sort: screen-* ids first, then other ids, then classes.
    def sort_key(item: dict) -> tuple[int, str]:
        sel = item["selector"]
        if sel.startswith("#screen-") or "screen" in sel.lower() and item["kind"] == "id":
            rank = 0
        elif item["kind"] == "id":
            rank = 1
        else:
            rank = 2
        return (rank, sel.lower())
    entries.sort(key=sort_key)

    # ensure_ascii=True so Windows consoles (cp1252) can print this without
    # tripping on emoji or wide unicode in the gathered text snippets.
    return json.dumps({"selectors": entries}, ensure_ascii=True, indent=2)


def _collect_attr_values(html: str, attr_name: str) -> list[str]:
    parsed = parse_html(html)
    seen: set[str] = set()
    values: list[str] = []

    def add(value: str) -> None:
        if value and value not in seen:
            seen.add(value)
            values.append(value)

    def walk(node) -> None:
        if node.is_text:
            return
        raw = node.attrs.get(attr_name)
        if raw:
            if attr_name == "class":
                for bit in raw.split():
                    add(bit)
            else:
                add(raw)
        for child in node.children:
            walk(child)

    walk(parsed.root)
    return values


def _looks_like_framework_shell(html: str) -> bool:
    low = html.lower()
    if "<script" not in low:
        return False
    framework_markers = (
        'id="root"', "id='root'",
        'id="app"', "id='app'",
        "__next", "__nuxt", "data-reactroot", "vite",
    )
    if not any(marker in low for marker in framework_markers):
        return False
    parsed = parse_html(html)
    return _count_elements(parsed.root) <= 3


def _count_elements(node) -> int:
    if node.is_text:
        return 0
    count = 0 if node.tag == "__root__" else 1
    for child in node.children:
        count += _count_elements(child)
    return count


def _render_url_with_browser(url: str, *, wait_ms: int, timeout: float) -> str:
    browser = _find_browser_executable()
    if not browser:
        raise BrowserRenderError(
            "could not find Chrome, Edge, or Chromium. Set H2U_BROWSER to the browser executable."
        )

    render_timeout = max(15.0, timeout + wait_ms / 1000.0 + 10.0)
    attempts: list[str] = []
    for headless_flag in ("--headless=new", "--headless"):
        with tempfile.TemporaryDirectory(
            prefix="html2uxml-browser-",
            ignore_cleanup_errors=True,
        ) as profile:
            cmd = [
                browser,
                headless_flag,
                "--disable-gpu",
                "--disable-dev-shm-usage",
                "--no-first-run",
                "--no-default-browser-check",
                "--no-sandbox",
                f"--user-data-dir={profile}",
                f"--virtual-time-budget={wait_ms}",
                "--dump-dom",
                url,
            ]
            try:
                proc = subprocess.run(
                    cmd,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    text=True,
                    encoding="utf-8",
                    errors="replace",
                    timeout=render_timeout,
                )
            except (OSError, subprocess.TimeoutExpired) as e:
                attempts.append(str(e))
                continue
        stdout = proc.stdout or ""
        if proc.returncode == 0 and "<html" in stdout.lower():
            return stdout
        detail = (proc.stderr or stdout or f"exit code {proc.returncode}").strip()
        attempts.append(detail[:500])
    raise BrowserRenderError("; ".join(a for a in attempts if a) or "browser produced no DOM")


def _find_browser_executable() -> str | None:
    env_names = (
        "H2U_BROWSER",
        "CHROME_BIN",
        "PLAYWRIGHT_CHROMIUM_EXECUTABLE_PATH",
    )
    for name in env_names:
        candidate = os.environ.get(name)
        if candidate and Path(candidate).is_file():
            return candidate

    for name in ("chrome", "chrome.exe", "msedge", "msedge.exe", "chromium", "chromium-browser"):
        found = shutil.which(name)
        if found:
            return found

    candidates = [
        r"C:\Program Files\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    ]
    for candidate in candidates:
        if Path(candidate).is_file():
            return candidate
    return None


def _count_inline_styles(html: str) -> int:
    parsed = parse_html(html)

    def walk(node) -> int:
        if node.is_text:
            return 0
        count = 1 if "style" in node.attrs else 0
        for child in node.children:
            count += walk(child)
        return count

    return walk(parsed.root)


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


_FF_RULE_RE = re.compile(r'--odd-font-family\s*:\s*"([^"]+)"')
_FONT_WEIGHT_RE = re.compile(r"--odd-font-weight\s*:\s*([^;]+);?")
_UNITY_FONT_STYLE_RE = re.compile(r"-unity-font-style\s*:\s*([^;]+);?")
_USS_RULE_RE = re.compile(r"(?P<head>[^{]+)\{(?P<body>[^{}]*)\}", re.MULTILINE)


_USS_BLOCK_RE = re.compile(r"(?P<head>[^{]+)\{(?P<body>[^{}]*)\}", re.MULTILINE)
_USS_DECL_RE = re.compile(r"\s*([A-Za-z-][A-Za-z0-9-]*)\s*:\s*([^;}]+);?")


def _drop_size_when_anchored_to_parent(uss: str, uxml: str) -> str:
    """Strip redundant `width:Wpx; height:Hpx` from rules whose four-edge
    anchoring already pins them to their parent's bounds.

    Figma exports give us BOTH `top/right/bottom/left` insets AND an
    explicit `width: Npx; height: Npx;` that matches the design canvas.
    CSS lets the explicit size win, so when the parent grows beyond the
    design size the anchored child stays its design size with empty
    space on the right/bottom. Dropping the size lets the inset
    arithmetic compute it from the parent.

    Only strip when the explicit dims actually match the inset-derived
    box. A decorative element (e.g. a 4×4 screw at `top:290 left:724
    right:6 bottom:6`) has insets that imply a larger box than the
    explicit size — keeping the explicit dims is correct there.

    Parent dimensions come from a child→parent map built from the UXML
    plus a {class → (width,height)} map built from the USS itself.
    """
    if not uss:
        return uss

    rule_dims = _parse_rule_dimensions(uss)
    parent_class = _build_parent_class_map(uxml)

    def parent_dims(cls: str) -> tuple[float | None, float | None]:
        seen: set[str] = set()
        cur = parent_class.get(cls)
        while cur and cur not in seen:
            seen.add(cur)
            w, h = rule_dims.get(cur, (None, None))
            if w is not None or h is not None:
                return w, h
            cur = parent_class.get(cur)
        return None, None

    def repl(m: re.Match) -> str:
        head = m.group("head").strip()
        body = m.group("body")
        if "position" not in body:
            return m.group(0)
        decls = list(_USS_DECL_RE.findall(body))
        if not decls:
            return m.group(0)
        keyed = {prop.strip().lower(): value.strip() for prop, value in decls}
        if keyed.get("position") not in ("absolute", "fixed"):
            return m.group(0)
        edges = ("top", "right", "bottom", "left")
        if not all(_is_pixel_or_zero(keyed.get(edge)) for edge in edges):
            return m.group(0)

        cls = head[1:] if head.startswith(".") else head
        parent_w, parent_h = parent_dims(cls)

        strip_width = _is_pixel_length(keyed.get("width"))
        strip_height = _is_pixel_length(keyed.get("height"))
        if strip_width and parent_w is not None:
            implied_w = parent_w - _px(keyed["left"]) - _px(keyed["right"])
            if abs(implied_w - _px(keyed["width"])) > 0.5:
                strip_width = False
        elif strip_width and parent_w is None:
            # No parent context — fall back to "all zero insets" safety net.
            if not all(_is_zero_length(keyed.get(edge)) for edge in edges):
                strip_width = False
        if strip_height and parent_h is not None:
            implied_h = parent_h - _px(keyed["top"]) - _px(keyed["bottom"])
            if abs(implied_h - _px(keyed["height"])) > 0.5:
                strip_height = False
        elif strip_height and parent_h is None:
            if not all(_is_zero_length(keyed.get(edge)) for edge in edges):
                strip_height = False

        if not strip_width and not strip_height:
            return m.group(0)

        def strip(line: str) -> bool:
            stripped = line.strip()
            if not stripped:
                return False
            mm = re.match(r"([A-Za-z-][A-Za-z0-9-]*)\s*:", stripped)
            if not mm:
                return False
            prop = mm.group(1).lower()
            if prop == "width" and strip_width:
                return True
            if prop == "height" and strip_height:
                return True
            return False

        kept = [line for line in body.splitlines() if not strip(line)]
        new_body = "\n".join(kept)
        return f"{m.group('head')}{{{new_body}}}"

    return _USS_BLOCK_RE.sub(repl, uss)


def _parse_rule_dimensions(uss: str) -> dict[str, tuple[float | None, float | None]]:
    out: dict[str, tuple[float | None, float | None]] = {}
    for m in _USS_BLOCK_RE.finditer(uss):
        head = m.group("head").strip()
        if not head.startswith("."):
            continue
        cls = head.split()[0][1:].split(",", 1)[0]
        body = m.group("body")
        keyed: dict[str, str] = {}
        for prop, val in _USS_DECL_RE.findall(body):
            keyed[prop.strip().lower()] = val.strip()
        w = _px_or_none(keyed.get("width"))
        h = _px_or_none(keyed.get("height"))
        if w is not None or h is not None:
            existing = out.get(cls, (None, None))
            out[cls] = (w if w is not None else existing[0], h if h is not None else existing[1])
    return out


_UXML_OPEN_TAG_RE = re.compile(
    r"<(?:ui|odd):[A-Za-z0-9_-]+\b[^/>]*?\bclass\s*=\s*\"([^\"]+)\"[^/>]*>",
    re.DOTALL,
)
_UXML_SELF_CLOSE_RE = re.compile(
    r"<(?:ui|odd):[A-Za-z0-9_-]+\b[^>]*\bclass\s*=\s*\"([^\"]+)\"[^>]*/>",
    re.DOTALL,
)
_UXML_CLOSE_TAG_RE = re.compile(r"</(?:ui|odd):[A-Za-z0-9_-]+>")


def _build_parent_class_map(uxml: str) -> dict[str, str]:
    """Walk the UXML linearly and emit a child→parent mapping keyed by
    each element's first class name."""
    out: dict[str, str] = {}
    if not uxml:
        return out
    stack: list[str] = []
    pos = 0
    n = len(uxml)
    while pos < n:
        opener = _UXML_OPEN_TAG_RE.search(uxml, pos)
        closer = _UXML_CLOSE_TAG_RE.search(uxml, pos)
        self_close = _UXML_SELF_CLOSE_RE.search(uxml, pos)

        candidates = [(c.start(), c, kind)
                      for c, kind in ((opener, "open"), (closer, "close"), (self_close, "self"))
                      if c is not None]
        if not candidates:
            break
        candidates.sort(key=lambda item: item[0])
        _, m, kind = candidates[0]

        if kind == "self":
            cls = m.group(1).split()[0]
            if stack:
                out.setdefault(cls, stack[-1])
        elif kind == "open":
            cls = m.group(1).split()[0]
            if stack:
                out.setdefault(cls, stack[-1])
            stack.append(cls)
        else:  # close
            if stack:
                stack.pop()
        pos = m.end()
    return out


def _px(value: str) -> float:
    v = value.strip().rstrip(";").strip().lower()
    if v in ("0", "0px"):
        return 0.0
    m = re.match(r"^(-?\d+(?:\.\d+)?)px$", v)
    return float(m.group(1)) if m else 0.0


def _px_or_none(value: str | None) -> float | None:
    if value is None:
        return None
    return _px(value) if _is_pixel_or_zero(value) else None


def _is_zero_length(value: str | None) -> bool:
    if value is None:
        return False
    v = value.strip().rstrip(";").strip().lower()
    return v in ("0", "0px", "0%", "0em", "0rem")


def _is_pixel_or_zero(value: str | None) -> bool:
    if value is None:
        return False
    v = value.strip().rstrip(";").strip().lower()
    if v in ("0", "0px"):
        return True
    return bool(re.match(r"^-?\d+(?:\.\d+)?px$", v))


def _is_pixel_length(value: str | None) -> bool:
    if value is None:
        return False
    v = value.strip().rstrip(";").strip().lower()
    return bool(re.match(r"^-?\d+(?:\.\d+)?px$", v))


_HTML_STYLE_BLOCK_RE = re.compile(r"<style[^>]*>(.*?)</style>", re.IGNORECASE | re.DOTALL)
_HTML_STYLE_ATTR_RE = re.compile(r'\bstyle\s*=\s*"([^"]*)"', re.IGNORECASE)
_FONT_FACE_BLOCK_RE = re.compile(r"@font-face\s*\{[^}]*\}", re.IGNORECASE | re.DOTALL)
_FONT_FAMILY_DECL_RE = re.compile(r"font-family\s*:\s*([^;}]+)", re.IGNORECASE)

# Generic CSS keywords to ignore when collecting referenced families.
_GENERIC_CSS_FAMILIES = {
    "serif", "sans-serif", "monospace", "cursive", "fantasy",
    "system-ui", "ui-serif", "ui-sans-serif", "ui-monospace",
    "ui-rounded", "math", "emoji", "fangsong",
    "-apple-system", "blinkmacsystemfont",
    "inherit", "initial", "unset", "revert", "revert-layer",
}


def _all_referenced_families(html: str, extra_css: str) -> list[str]:
    """Return every non-generic font-family token referenced by a use site.

    Walks `<style>` blocks, `style="..."` attribute values, and any
    already-fetched linked CSS. `@font-face` blocks are stripped so
    declaration-side families are not mistaken for uses. Fallback families
    in a comma list are kept (the converter's FontUsage only tracks the
    primary, so without this pass the manual font-merge UI would never
    see fallbacks)."""
    import html as _html_mod

    chunks: list[str] = []
    if extra_css:
        chunks.append(extra_css)
    if html:
        for m in _HTML_STYLE_BLOCK_RE.finditer(html):
            chunks.append(m.group(1))
        for m in _HTML_STYLE_ATTR_RE.finditer(html):
            chunks.append(_html_mod.unescape(m.group(1)))

    seen: set[str] = set()
    out: list[str] = []
    for css in chunks:
        css = _FONT_FACE_BLOCK_RE.sub("", css)
        for m in _FONT_FAMILY_DECL_RE.finditer(css):
            for tok in m.group(1).split(","):
                name = tok.strip().strip('"').strip("'")
                if not name:
                    continue
                if name.lower() in _GENERIC_CSS_FAMILIES:
                    continue
                if name not in seen:
                    seen.add(name)
                    out.append(name)
    return out


def _collect_raw_css(html: str, extra_css: str) -> str:
    """Concatenate inline `<style>` blocks plus already-fetched linked CSS.

    Used to scan for `@font-face` rules that the css_parser intentionally
    drops (every `@`-rule is skipped during selector parsing)."""
    parts: list[str] = []
    if extra_css:
        parts.append(extra_css)
    if html:
        for m in _HTML_STYLE_BLOCK_RE.finditer(html):
            parts.append(m.group(1))
    return "\n".join(parts)


def _families_from_uss(uss: str) -> list[str]:
    seen: set[str] = set()
    out: list[str] = []
    for m in _FF_RULE_RE.finditer(uss):
        f = m.group(1)
        if f not in seen:
            seen.add(f)
            out.append(f)
    return out


def _used_families(uss: str, wanted: dict[str, set[tuple[int, bool]]]) -> list[str]:
    """Return every family declared by a `--odd-font-family` marker in `uss`.

    Families that do appear in `wanted` with a non-empty variant set are
    narrowed by `download_google_fonts`. Families absent from `wanted`
    (i.e. families the converter declared a marker for but did not emit a
    FontUsage for, typically because they are inherited or referenced
    without an emitted text node) fall back to the broad download path.
    """
    return _families_from_uss(uss)


def _wanted_variants_from_usages(result) -> dict[str, set[tuple[int, bool]]]:
    out: dict[str, set[tuple[int, bool]]] = {}
    for usage in getattr(result, "font_usages", []) or []:
        family = getattr(usage, "family", None)
        if not family:
            continue
        out.setdefault(family, set()).add((
            int(getattr(usage, "weight", 400) or 400),
            bool(getattr(usage, "italic", False)),
        ))
    return out


_FONT_ALIAS_FAMILY_LITERAL_RE = re.compile(r'(?P<lhs>--odd-font-family\s*:\s*)"(?P<name>[^"]*)"')


def _parse_font_aliases(raw_args: list[str]) -> dict[str, str]:
    """Parse --font-alias OLD=NEW pairs into a dict, ignoring no-op self maps."""
    out: dict[str, str] = {}
    for raw in raw_args or []:
        if "=" not in raw:
            print(f"warning: ignoring --font-alias {raw!r} (expected OLD=NEW)", file=sys.stderr)
            continue
        old, new = raw.split("=", 1)
        old = old.strip().strip('"').strip("'")
        new = new.strip().strip('"').strip("'")
        if not old or not new or old == new:
            continue
        out[old] = new
    # Resolve transitive maps: A->B, B->C  =>  A->C, B->C
    resolved: dict[str, str] = {}
    for src in out:
        cur = src
        seen: set[str] = {src}
        while cur in out and out[cur] not in seen:
            cur = out[cur]
            seen.add(cur)
        resolved[src] = out.get(cur, cur)
    return resolved


def _apply_font_aliases(result, aliases: dict[str, str]) -> None:
    """Rewrite family references in the converter result's USS markers and
    font_usages list. Both must agree before download_google_fonts runs."""
    if not aliases:
        return

    def repl(m: re.Match) -> str:
        name = m.group("name")
        new = aliases.get(name)
        return f'{m.group("lhs")}"{new}"' if new else m.group(0)

    result.uss = _FONT_ALIAS_FAMILY_LITERAL_RE.sub(repl, result.uss)

    new_usages = []
    for usage in getattr(result, "font_usages", []) or []:
        family = getattr(usage, "family", None)
        new = aliases.get(family) if family else None
        if new and new != family:
            new_usages.append(replace(usage, family=new))
        else:
            new_usages.append(usage)
    result.font_usages = new_usages


def _emit_family_list(result, *, referenced: list[str] | None = None) -> str:
    """JSON describing detected font usage. Consumed by the Unity importer
    to build the manual font-merge UI.

    `referenced` carries every non-generic family name that appears as a
    font-family token (including comma-separated fallbacks) anywhere in
    the source CSS. Families that show up only as fallbacks have empty
    variants — that's how the importer flags them as "fallback" so the
    user can still pick them as merge targets."""
    families: dict[str, dict] = {}
    for usage in getattr(result, "font_usages", []) or []:
        family = getattr(usage, "family", None)
        if not family:
            continue
        entry = families.setdefault(family, {
            "family": family,
            "variants": set(),
            "characters": "",
            "dynamic": False,
            "role": "primary",
        })
        entry["variants"].add((
            int(getattr(usage, "weight", 400) or 400),
            bool(getattr(usage, "italic", False)),
        ))
        entry["characters"] = _unique_chars(
            entry["characters"] + (getattr(usage, "text", "") or "")
        )
        if getattr(usage, "dynamic", False):
            entry["dynamic"] = True

    if referenced:
        for fam in referenced:
            if fam in families:
                continue
            families[fam] = {
                "family": fam,
                "variants": set(),
                "characters": "",
                "dynamic": False,
                "role": "fallback",
            }

    out = []
    for entry in sorted(families.values(), key=lambda e: e["family"].lower()):
        out.append({
            "family": entry["family"],
            "variants": [
                {"weight": w, "italic": i}
                for (w, i) in sorted(entry["variants"])
            ],
            "characters": entry["characters"][:120],
            "characterCount": len(entry["characters"]),
            "dynamic": entry["dynamic"],
            "role": entry["role"],
        })
    return json.dumps({"families": out}, ensure_ascii=True, indent=2)


def _dedupe_font_files(
    mapping: dict[str, list[FontVariant] | str],
    fonts_dir: Path,
) -> dict[str, list[FontVariant] | str]:
    """Collapse identical TTF/OTF byte-payloads to a single canonical file.

    Different families (e.g. an alias merged into a system match, or two
    families that share a Google Fonts entry) can each pull a copy of the
    same TTF. Unity imports each one separately, doubling atlas memory and
    confusing the FontAsset references. This pass keeps the lex-first
    filename and rewrites every other variant to point at it.
    """
    if not mapping:
        return mapping
    by_hash: dict[str, str] = {}
    redirect: dict[str, str] = {}
    for variants_raw in mapping.values():
        variants = _normalise_font_variants(variants_raw) \
            if not isinstance(variants_raw, list) else variants_raw
        for v in variants:
            rel = v.path.replace("\\", "/") if isinstance(v, FontVariant) else str(v).replace("\\", "/")
            abs_path = fonts_dir / Path(rel).name
            if not abs_path.is_file():
                continue
            try:
                digest = hashlib.sha1(abs_path.read_bytes()).hexdigest()
            except OSError:
                continue
            canonical = by_hash.get(digest)
            if canonical is None or _font_path_rank(rel) < _font_path_rank(canonical):
                if canonical is not None:
                    redirect[canonical] = rel
                by_hash[digest] = rel
            elif rel != canonical:
                redirect[rel] = canonical

    if not redirect:
        return mapping

    final_redirect: dict[str, str] = {}
    for src, dst in redirect.items():
        cur = dst
        seen = {src}
        while cur in redirect and redirect[cur] not in seen:
            cur = redirect[cur]
            seen.add(cur)
        final_redirect[src] = cur

    new_mapping: dict[str, list[FontVariant] | str] = {}
    for family, variants_raw in mapping.items():
        if isinstance(variants_raw, str):
            new_mapping[family] = final_redirect.get(variants_raw, variants_raw)
            continue
        new_variants: list[FontVariant] = []
        for v in variants_raw:
            target = final_redirect.get(v.path)
            new_variants.append(replace(v, path=target) if target else v)
        new_mapping[family] = new_variants

    for old_rel in final_redirect:
        path = fonts_dir / Path(old_rel).name
        try:
            path.unlink()
        except OSError:
            pass

    return new_mapping


def _font_path_rank(rel: str) -> tuple[int, str]:
    """Lower is better when picking a canonical filename."""
    name = Path(rel).name
    return (len(name), name.lower())


_DEFAULT_STATIC_FONT_FALLBACK = (
    "".join(chr(i) for i in range(32, 127))  # printable ASCII
    + "\n\t"
)


def _build_default_charset(result) -> str:
    """Static atlas characters. Union of every FontUsage's text, plus a
    fallback (printable ASCII + whitespace) so fonts still render runtime
    text the converter could not see — input fields, formatted numbers,
    and labels populated from data sources."""
    seen: set[str] = set()
    out: list[str] = []
    for usage in getattr(result, "font_usages", []) or []:
        for ch in (getattr(usage, "text", "") or ""):
            if ch in seen:
                continue
            seen.add(ch)
            out.append(ch)
    for ch in _DEFAULT_STATIC_FONT_FALLBACK:
        if ch in seen:
            continue
        seen.add(ch)
        out.append(ch)
    return "".join(out)


def _write_font_asset_manifest(
    result,
    mapping: dict[str, list[FontVariant] | str],
    fonts_dir: Path,
) -> None:
    entries: dict[str, dict] = {}
    for usage in getattr(result, "font_usages", []) or []:
        variants_raw = mapping.get(usage.family)
        if not variants_raw:
            continue
        selected = _select_font_variant(
            _normalise_font_variants(variants_raw),
            int(getattr(usage, "weight", 400) or 400),
            bool(getattr(usage, "italic", False)),
        )
        if selected is None:
            continue
        font_path = Path(selected.path.replace("\\", "/"))
        font_file = font_path.name
        entry = entries.setdefault(font_file, {
            "fontFile": font_file,
            "fontAsset": f"{font_path.stem} SDF.asset",
            "atlasMode": "static",
            "characters": "",
        })
        if getattr(usage, "dynamic", False):
            entry["atlasMode"] = "dynamic"
        entry["characters"] = _unique_chars(entry["characters"] + (getattr(usage, "text", "") or ""))

    payload = {
        "defaultCharacters": _build_default_charset(result),
        "fonts": sorted(entries.values(), key=lambda item: item["fontFile"].lower()),
    }
    fonts_dir.mkdir(parents=True, exist_ok=True)
    (fonts_dir / "html2uxml-fonts.json").write_text(
        json.dumps(payload, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )


def _unique_chars(text: str) -> str:
    seen: set[str] = set()
    out: list[str] = []
    for ch in text:
        if ch in seen:
            continue
        seen.add(ch)
        out.append(ch)
    return "".join(out)


def _inject_font_definitions(
    uss: str,
    mapping: dict[str, list[FontVariant] | str],
    *,
    use_textcore_font_assets: bool = True,
) -> str:
    if not mapping:
        return uss

    active_family: str | None = None
    active_variants: list[FontVariant] | None = None

    def repl_rule(m: re.Match) -> str:
        nonlocal active_family, active_variants
        body = m.group("body")
        family_match = _FF_RULE_RE.search(body)
        explicit_family = family_match is not None
        if explicit_family:
            family = family_match.group(1)
            variants_raw = mapping.get(family)
            if not variants_raw:
                return m.group(0)
            active_family = family
            active_variants = _normalise_font_variants(variants_raw)
            variants = active_variants
        else:
            if active_family is None or active_variants is None:
                return m.group(0)
            if not _rule_has_font_variant_request(body):
                return m.group(0)
            variants = active_variants

        rewritten = _rewrite_font_rule(
            m,
            variants,
            remove_family_marker=explicit_family,
            use_textcore_font_assets=use_textcore_font_assets,
        )
        if rewritten is None:
            return m.group(0)
        return rewritten

    return _USS_RULE_RE.sub(repl_rule, uss)


def _rewrite_font_rule(
    m: re.Match,
    variants: list[FontVariant],
    *,
    remove_family_marker: bool,
    use_textcore_font_assets: bool,
) -> str | None:
    body = m.group("body")
    desired_weight = _rule_font_weight(body)
    desired_style = _rule_font_style(body)
    desired_italic = "italic" in desired_style
    selected = _select_font_variant(variants, desired_weight, desired_italic)
    if selected is None:
        return None
    font_ref = _font_definition_ref(selected, use_textcore_font_assets=use_textcore_font_assets)

    lines = body.splitlines()
    output_lines: list[str] = []
    inserted = False
    insert_index = _font_definition_insert_index(lines)
    for index, line in enumerate(lines):
        if _FF_RULE_RE.search(line) and remove_family_marker:
            indent_match = re.match(r"^(\s*)", line)
            indent = indent_match.group(1) if indent_match else "    "
            output_lines.append(f'{indent}-unity-font-definition: url("{font_ref}");')
            inserted = True
            continue
        if not inserted and not remove_family_marker and index == insert_index:
            indent = _line_indent(line)
            output_lines.append(f'{indent}-unity-font-definition: url("{font_ref}");')
            inserted = True
        if _FONT_WEIGHT_RE.search(line):
            continue
        if _UNITY_FONT_STYLE_RE.search(line):
            next_style = _synthetic_font_style(desired_weight, desired_italic, selected)
            if next_style:
                indent = _line_indent(line)
                output_lines.append(f"{indent}-unity-font-style: {next_style};")
            continue
        output_lines.append(line)
    if not inserted:
        output_lines.append(f'    -unity-font-definition: url("{font_ref}");')
    return f"{m.group('head')}{{" + "\n".join(output_lines) + "}"


def _font_definition_ref(
    selected: FontVariant,
    *,
    use_textcore_font_assets: bool,
) -> str:
    if not use_textcore_font_assets:
        return selected.path
    path = Path(selected.path.replace("\\", "/"))
    return str(path.with_name(f"{path.stem} SDF.asset")).replace("\\", "/")


def _line_indent(line: str) -> str:
    indent_match = re.match(r"^(\s*)", line)
    return indent_match.group(1) if indent_match else "    "


def _font_definition_insert_index(lines: list[str]) -> int:
    for i, line in enumerate(lines):
        if _FONT_WEIGHT_RE.search(line) or _UNITY_FONT_STYLE_RE.search(line):
            return i
    return 0


def _rule_has_font_variant_request(body: str) -> bool:
    return _FONT_WEIGHT_RE.search(body) is not None or _UNITY_FONT_STYLE_RE.search(body) is not None


def _normalise_font_variants(raw: list[FontVariant] | str) -> list[FontVariant]:
    if isinstance(raw, str):
        return [FontVariant(path=raw, weight=400, italic=False)]
    return list(raw)


def _rule_font_weight(body: str) -> int:
    match = _FONT_WEIGHT_RE.search(body)
    if not match:
        style = _rule_font_style(body)
        return 700 if "bold" in style else 400
    value = match.group(1).strip().lower()
    if value == "bold":
        return 700
    if value == "normal":
        return 400
    try:
        return int(float(value))
    except ValueError:
        return 400


def _rule_font_style(body: str) -> str:
    match = _UNITY_FONT_STYLE_RE.search(body)
    return match.group(1).strip().lower() if match else "normal"


def _select_font_variant(
    variants: list[FontVariant],
    desired_weight: int,
    desired_italic: bool,
) -> FontVariant | None:
    if not variants:
        return None
    return min(
        variants,
        key=lambda v: (
            0 if v.italic == desired_italic else 1,
            abs(v.weight - desired_weight),
            0 if v.weight <= desired_weight else 1,
        ),
    )


def _synthetic_font_style(
    desired_weight: int,
    desired_italic: bool,
    selected: FontVariant,
) -> str | None:
    synth_bold = desired_weight >= 600 and selected.weight < 600
    synth_italic = desired_italic and not selected.italic
    if synth_bold and synth_italic:
        return "bold-and-italic"
    if synth_bold:
        return "bold"
    if synth_italic:
        return "italic"
    return None


def _print_report(
    result,
    asset_report,
    font_report,
    svg_report,
    *,
    embedded_report=None,
    rendered_by_browser: bool = False,
    rendered_html_path: Path | None = None,
    file,
) -> None:
    s = result.stats
    if s is None:
        return
    print("", file=file)
    print("== conversion report ==", file=file)
    if rendered_by_browser:
        suffix = f" ({rendered_html_path})" if rendered_html_path is not None else ""
        print(f"  source: browser-rendered DOM snapshot{suffix}", file=file)
    print(
        f"  elements: VisualElement={s.elements} Html2UxmlPanel={s.html2uxml_panels} "
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
        print(f"  bridged via runtime package: {rows}", file=file)
    if s.dropped_props:
        rows = ", ".join(
            f"{k}={v}" for k, v in sorted(s.dropped_props.items(), key=lambda kv: -kv[1])
        )
        print(f"  dropped (no USS equivalent): {rows}", file=file)
    warnings = _dedupe(result.warnings)
    if warnings:
        print("  warnings:", file=file)
        for warning in warnings[:10]:
            print(f"    - {warning}", file=file)
        if len(warnings) > 10:
            print(f"    - ... {len(warnings) - 10} more", file=file)
    if asset_report is not None:
        print(
            f"  assets: copied={len(asset_report.copied)} "
            f"downloaded={len(asset_report.downloaded)} "
            f"failed={len(asset_report.failed)}",
            file=file,
        )
        for u, why in asset_report.failed[:5]:
            print(f"    - {u}: {why}", file=file)
    if svg_report is not None:
        renderer_rows = ", ".join(f"{k}={v}" for k, v in sorted(svg_report.renderers.items()))
        renderer_text = f" via {renderer_rows}" if renderer_rows else ""
        print(
            f"  svg: rasterized={len(svg_report.rasterized)} "
            f"failed={len(svg_report.failed)}{renderer_text}",
            file=file,
        )
        for u, why in svg_report.failed[:5]:
            print(f"    - {u}: {why}", file=file)
    if font_report is not None:
        rows = ", ".join(f"{f}->{p}" for f, p in font_report.fonts_downloaded[:5])
        if rows:
            print(f"  fonts: {rows}", file=file)
        for f, why in font_report.failed[:5]:
            print(f"    - {f}: {why}", file=file)
    if embedded_report is not None and (
        embedded_report.extracted or embedded_report.failed or embedded_report.skipped
    ):
        print(
            f"  embedded fonts: extracted={len(embedded_report.extracted)} "
            f"failed={len(embedded_report.failed)} "
            f"skipped={len(embedded_report.skipped)}",
            file=file,
        )
        for family, path in embedded_report.extracted[:5]:
            print(f"    + {family} -> {path}", file=file)
        for url, why in embedded_report.skipped[:3]:
            print(f"    ~ {url}: {why}", file=file)
        for url, why in embedded_report.failed[:3]:
            print(f"    - {url}: {why}", file=file)


def _dedupe(values: list[str]) -> list[str]:
    seen: set[str] = set()
    out: list[str] = []
    for value in values:
        if value in seen:
            continue
        seen.add(value)
        out.append(value)
    return out


if __name__ == "__main__":
    raise SystemExit(main())
