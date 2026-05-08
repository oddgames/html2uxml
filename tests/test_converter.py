"""Smoke tests for the converter. Run with `python -m unittest`."""
from __future__ import annotations

import contextlib
import io
import json
import os
import unittest
from unittest import mock
from pathlib import Path
from tempfile import TemporaryDirectory

from html2uxml import convert
from html2uxml.assets import (
    AssetReport,
    FontVariant,
    collect_and_rewrite,
    download_google_fonts,
    inject_image_aspect_ratios,
    _copy_cached_font_variants,
    _font_search_prefixes,
    _preferred_google_faces,
    _local_font_variant,
    _safe_font_name,
    _select_google_ttf_url,
)
from html2uxml.cli import (
    _inject_font_definitions,
    _linked_stylesheet_hrefs,
    _rasterize_one_svg,
    _rasterize_svg_assets,
    _replace_svg_urls_with_png,
    _resize_svg_root,
    _selector_matches_html,
    _should_render_url_html,
    main as cli_main,
)


class ConvertBasicsTest(unittest.TestCase):
    def test_div_with_inline_style_emits_visualelement_and_uss_rule(self):
        r = convert(
            '<div style="display: flex; flex-direction: column; '
            'background: rgb(255,0,0); padding: 8px;">hi</div>'
        )
        self.assertIn("ui:Label", r.uxml)
        self.assertIn("display: flex", r.uss)
        self.assertIn("background-color: rgb(255,0,0)", r.uss)
        self.assertIn("padding: 8px", r.uss)

    def test_button_text_attribute(self):
        r = convert("<button>Click</button>")
        self.assertIn('<ui:Button text="Click"', r.uxml)

    def test_button_with_styled_inline_children_preserves_child_labels(self):
        r = convert(
            '<button class="cam"><span class="cam-icon">◉</span><span class="cam-label">CHASE</span></button>',
            ".cam { display: flex; flex-direction: row; font-family: 'Barlow Condensed', sans-serif; }"
            ".cam-icon { font-weight: 900; font-size: 10px; }"
            ".cam-label { font-style: italic; font-weight: 800; font-size: 9px; letter-spacing: 1.2px; }",
        )
        self.assertNotIn('text="◉CHASE"', r.uxml)
        self.assertIn('<ui:Button class="cam">', r.uxml)
        self.assertRegex(r.uxml, r'<ui:Label class="cam-icon(?: h2u-\d+)*" text="◉"')
        self.assertRegex(r.uxml, r'<ui:Label class="cam-label(?: h2u-\d+)*" text="CHASE"')
        self.assertIn('--odd-font-family: "Barlow Condensed"', r.uss)
        self.assertIn("--odd-font-weight: 800", r.uss)

    def test_button_with_svg_child_preserves_svg_asset(self):
        r = convert(
            '<button id="chat"><svg width="16" height="16" viewBox="0 0 24 24">'
            '<path stroke-width="2" d="M0 0h1"/></svg></button>'
        )
        self.assertIn('<ui:Button name="chat">', r.uxml)
        self.assertIn("<ui:VisualElement", r.uxml)
        self.assertEqual(1, len(r.svg_files))
        self.assertEqual("chat.svg", r.svg_files[0][0])
        self.assertIn('background-image: url("Images/chat.svg")', r.uss)
        self.assertIn("width: 16px", r.uss)
        self.assertIn("height: 16px", r.uss)
        self.assertIn("SVG assets emitted", "\n".join(r.warnings))

    def test_svg_asset_names_only_add_counter_on_conflict(self):
        r = convert(
            '<button id="chat"><svg width="16" height="16"></svg></button>'
            '<button id="chat"><svg width="16" height="16"></svg></button>'
            '<button id="camera"><svg width="16" height="16"></svg></button>'
        )
        self.assertEqual(
            ["chat.svg", "chat-2.svg", "camera.svg"],
            [name for name, _ in r.svg_files],
        )

    def test_svg_child_in_rounded_overflow_parent_keeps_svg_art_unclipped(self):
        r = convert(
            '<div class="disc"><svg width="44" height="44" viewBox="0 0 78 78">'
            '<rect width="78" height="78" fill="#5a4030"/></svg></div>',
            ".disc { width:48px; height:48px; overflow:hidden; border-radius:999px; }",
        )
        self.assertIn("overflow: hidden", r.uss)
        self.assertEqual(1, r.uss.count("border-top-left-radius: 999px"))
        self.assertEqual(1, r.uss.count("border-bottom-right-radius: 999px"))

    def test_button_reset_emits_before_authored_button_class(self):
        r = convert(
            "<style>.btn { border: 1px solid red; padding: 4px; }</style>"
            '<button class="btn">Go</button>'
        )
        self.assertIn("Button {", r.uss)
        self.assertIn(".unity-button {", r.uss)
        self.assertLess(r.uss.index(".unity-button {"), r.uss.index(".btn {"))
        self.assertIn("border-top-width: 0.5px", r.uss)
        self.assertIn("padding: 4px", r.uss)

    def test_text_only_div_keeps_class_on_label(self):
        r = convert('<div class="title">Detroit Lobby</div>')
        self.assertIn('<ui:Label class="title" text="Detroit Lobby"', r.uxml)
        self.assertNotIn('<ui:VisualElement class="title"', r.uxml)

    def test_mixed_inline_text_run_gets_horizontal_generated_class(self):
        r = convert(
            "<style>.title { font-size: 20px; font-style: italic; }</style>"
            '<div class="title">Detroit <span class="accent">Lobby</span></div>'
        )
        self.assertIn('class="title h2u-1"', r.uxml)
        self.assertIn("flex-direction: row", r.uss)
        self.assertRegex(r.uxml, r'<ui:Label class="h2u-\d+" text="Detroit"')
        self.assertRegex(r.uxml, r'class="accent(?: h2u-\d+)+\" text="Lobby"')
        self.assertIn("height: 24px", r.uss)
        self.assertIn("-unity-text-align: middle-left", r.uss)

    def test_mixed_inline_text_run_carries_parent_typography(self):
        r = convert(
            "<style>.title { font-family: 'Saira Condensed', sans-serif; "
            "font-weight: 800; font-size: 11px; letter-spacing: 1px; "
            "color: #ffbf13; text-shadow: 1px 1px 0 #000; }</style>"
            '<div class="title">TA<span>P</span></div>'
        )
        self.assertRegex(r.uxml, r'<ui:Label class="h2u-\d+" text="TA"')
        self.assertIn('--odd-font-family: "Saira Condensed"', r.uss)
        self.assertIn("--odd-font-weight: 800", r.uss)
        self.assertIn("font-size: 11px", r.uss)
        self.assertIn("letter-spacing: 1px", r.uss)
        self.assertIn("color: #ffbf13", r.uss)
        self.assertIn("text-shadow: 1px 1px 0 #000", r.uss)

    def test_inline_child_labels_get_parent_line_height_alignment(self):
        r = convert(
            "<style>.time { display:flex; font-size:16px; line-height:1; }"
            ".num { padding:2px 4px; }</style>"
            '<div class="time"><span class="num">02</span><span>:</span></div>'
        )
        self.assertRegex(r.uxml, r'class="num(?: h2u-\d+)+\" text="02"')
        self.assertRegex(r.uxml, r'<ui:Label class="(?:h2u-\d+ ?)+\" text=":"')
        self.assertIn("min-height: 16px", r.uss)
        self.assertIn("-unity-text-align: middle-center", r.uss)
        self.assertIn("padding: 2px 4px", r.uss)

    def test_line_height_display_label_gets_centered_line_box(self):
        r = convert(
            '<div class="score">6.812</div>',
            ".score { font-family: 'Saira Condensed', sans-serif; font-style: italic; "
            "font-weight: 900; font-size: 15px; line-height: 1; letter-spacing: 0.5px; }",
        )
        self.assertIn("height: 17px", r.uss)
        self.assertIn("min-height: 17px", r.uss)
        self.assertIn("-unity-text-align: middle-center", r.uss)
        self.assertIn("-unity-paragraph-spacing: 0", r.uss)

    def test_generated_direct_text_label_carries_parent_typography(self):
        r = convert(
            '<button class="ready"><span class="dot"></span>TAP TO READY UP</button>',
            ".ready { font-family: 'Saira Condensed', sans-serif; font-style: italic; "
            "font-weight: 800; font-size: 11px; letter-spacing: 1.5px; "
            "color: #ffbf13; text-shadow: 1px 1px 0 #000; }"
            ".dot { width: 5px; height: 5px; background: #ffbf13; }",
        )
        self.assertRegex(r.uxml, r'<ui:Label class="h2u-\d+" text="TAP TO READY UP"')
        self.assertIn('--odd-font-family: "Saira Condensed"', r.uss)
        self.assertIn("--odd-font-weight: 800", r.uss)
        self.assertIn("font-size: 11px", r.uss)
        self.assertIn("letter-spacing: 1.5px", r.uss)
        self.assertIn("color: #ffbf13", r.uss)
        self.assertIn("text-shadow: 1px 1px 0 #000", r.uss)
        self.assertIn("-unity-font-style: bold-and-italic", r.uss)

    def test_direct_display_text_with_large_letter_spacing_uses_layout_spacing(self):
        r = convert(
            '<button class="ready"><span class="dot"></span>TAP TO READY UP</button>',
            ".ready { font-family: 'Saira Condensed', sans-serif; font-style: italic; "
            "font-weight: 800; font-size: 11px; letter-spacing: 3px; "
            "color: #ffbf13; text-shadow: 1px 1px 0 #000; }"
            ".dot { width: 5px; height: 5px; background: #ffbf13; }",
        )
        self.assertNotIn('text="TAP TO READY UP"', r.uxml)
        self.assertRegex(r.uxml, r'<ui:Label class="h2u-\d+" text="T"')
        self.assertRegex(r.uxml, r'<ui:Label class="h2u-\d+" text="A"')
        self.assertIn("margin-right: 1.35px", r.uss)
        self.assertIn("width: 3.77px", r.uss)
        self.assertIn('--odd-font-family: "Saira Condensed"', r.uss)
        self.assertIn("text-shadow: 1px 1px 0 #000", r.uss)

    def test_button_with_flex_gap_uses_gap_bridge_button(self):
        r = convert(
            '<button class="ready"><span class="dot"></span><span>TAP TO READY UP</span></button>',
            ".ready { display: flex; flex-direction: row; align-items: center; "
            "column-gap: 8px; font-family: 'Saira Condensed', sans-serif; "
            "font-style: italic; font-weight: 800; font-size: 11px; "
            "letter-spacing: 3px; color: #ffbf13; }"
            ".dot { width: 5px; height: 5px; border-radius: 999px; background: #ffbf13; }",
        )
        self.assertIn('xmlns:odd="ODDGames.Html2Uxml"', r.uxml)
        self.assertIn('<odd:Html2UxmlButton class="ready">', r.uxml)
        self.assertIn("--odd-column-gap: 8", r.uss)
        self.assertIn('<ui:VisualElement class="dot"', r.uxml)
        self.assertNotIn('text="TAP TO READY UP"', r.uxml)

    def test_static_flex_gap_bakes_child_margin_without_panel_promotion(self):
        r = convert(
            '<div class="row"><div class="a"></div><div class="b"></div></div>',
            ".row { display: flex; flex-direction: row; gap: 8px; }"
            ".a, .b { width: 10px; height: 10px; }",
        )
        self.assertNotIn("odd:Html2UxmlPanel", r.uxml)
        self.assertIn("margin-left: 8px", r.uss)

    def test_inline_child_label_carries_inherited_letter_spacing(self):
        r = convert(
            '<div class="ready"><span>TAP</span></div>',
            ".ready { font-family: 'Saira Condensed', sans-serif; font-style: italic; "
            "font-weight: 800; font-size: 11px; letter-spacing: 1.5px; color: #ffbf13; }",
        )
        self.assertRegex(r.uxml, r'<ui:Label class="(?:h2u-\d+ ?)+" text="TAP"')
        self.assertIn('--odd-font-family: "Saira Condensed"', r.uss)
        self.assertIn("--odd-font-weight: 800", r.uss)
        self.assertIn("letter-spacing: 1.5px", r.uss)
        self.assertIn("color: #ffbf13", r.uss)

    def test_text_only_display_label_with_large_letter_spacing_uses_layout_spacing(self):
        r = convert(
            '<div class="ready"><span>TAP</span></div>',
            ".ready { font-family: 'Saira Condensed', sans-serif; font-style: italic; "
            "font-weight: 800; font-size: 11px; letter-spacing: 3px; color: #ffbf13; }",
        )
        self.assertNotIn('text="TAP"', r.uxml)
        self.assertRegex(r.uxml, r'<ui:Label class="h2u-\d+" text="T"')
        self.assertRegex(r.uxml, r'<ui:Label class="h2u-\d+" text="A"')
        self.assertIn("margin-right: 1.35px", r.uss)
        self.assertIn("color: #ffbf13", r.uss)

    def test_pseudo_content_carries_inherited_typography(self):
        r = convert(
            "<style>.badge { font-family: 'Saira Condensed', sans-serif; "
            "font-weight: 800; font-size: 11px; letter-spacing: 3px; "
            "color: #ffbf13; text-transform: uppercase; }"
            ".badge::before { content: 'go'; }</style>"
            '<div class="badge"></div>'
        )
        self.assertRegex(r.uxml, r'<ui:Label class="h2u-\d+" text="GO"')
        self.assertIn('--odd-font-family: "Saira Condensed"', r.uss)
        self.assertIn("--odd-font-weight: 800", r.uss)
        self.assertIn("font-size: 11px", r.uss)
        self.assertIn("letter-spacing: 3px", r.uss)
        self.assertIn("color: #ffbf13", r.uss)

    def test_list_marker_carries_inherited_typography(self):
        r = convert(
            "<style>.list { font-size: 14px; letter-spacing: 2px; color: #ffbf13; }</style>"
            '<ul class="list"><li>Ready</li></ul>'
        )
        self.assertRegex(r.uxml, r'<ui:Label class="h2u-\d+" text="•"')
        self.assertIn("font-size: 14px", r.uss)
        self.assertIn("letter-spacing: 2px", r.uss)
        self.assertIn("color: #ffbf13", r.uss)

    def test_text_align_renamed_and_value_remapped(self):
        r = convert('<p style="text-align: center;">x</p>')
        self.assertIn("-unity-text-align: middle-center", r.uss)
        self.assertNotIn("\ntext-align:", r.uss)

    def test_font_weight_and_style_fold_into_unity_font_style(self):
        r = convert(
            '<span style="font-weight: 700; font-style: italic;">yo</span>'
        )
        self.assertIn("-unity-font-style: bold-and-italic", r.uss)

    def test_border_shorthand_split_per_side(self):
        r = convert('<div style="border: 2px solid #f00;"></div>')
        for side in ("top", "right", "bottom", "left"):
            self.assertIn(f"border-{side}-width: 1px", r.uss)
            self.assertIn(f"border-{side}-color: #f00", r.uss)

    def test_border_width_longhand_scaled_and_expanded(self):
        r = convert('<div style="border-width: 1px 2px 0 1.5px;"></div>')
        self.assertIn("border-top-width: 0.5px", r.uss)
        self.assertIn("border-right-width: 1px", r.uss)
        self.assertIn("border-bottom-width: 0", r.uss)
        self.assertIn("border-left-width: 0.75px", r.uss)

    def test_border_radius_shorthand_expands_for_unity_corners(self):
        r = convert('<div style="border-radius: 999px;"></div>')
        self.assertIn("border-top-left-radius: 999px", r.uss)
        self.assertIn("border-top-right-radius: 999px", r.uss)
        self.assertIn("border-bottom-right-radius: 999px", r.uss)
        self.assertIn("border-bottom-left-radius: 999px", r.uss)
        self.assertNotIn("border-radius: 999px", r.uss)

    def test_border_radius_box_shorthand_keeps_css_corner_order(self):
        r = convert('<div style="border-radius: 1px 2px 3px 4px;"></div>')
        self.assertIn("border-top-left-radius: 1px", r.uss)
        self.assertIn("border-top-right-radius: 2px", r.uss)
        self.assertIn("border-bottom-right-radius: 3px", r.uss)
        self.assertIn("border-bottom-left-radius: 4px", r.uss)

    def test_overflow_auto_coerces_to_hidden(self):
        r = convert('<div style="overflow: auto;"></div>')
        self.assertIn("overflow: hidden", r.uss)

    def test_position_fixed_coerces_to_absolute(self):
        r = convert('<div style="position: fixed;"></div>')
        self.assertIn("position: absolute", r.uss)

    def test_cursor_pointer_maps_to_link(self):
        r = convert('<div style="cursor: pointer;"></div>')
        self.assertIn("cursor: link", r.uss)

    def test_pointer_events_none_maps_to_picking_mode(self):
        r = convert('<div style="pointer-events: none;"></div>')
        self.assertIn('picking-mode="Ignore"', r.uxml)
        self.assertNotIn("pointer-events", r.uss)
        self.assertNotIn("__picking-mode__", r.uss)

    def test_pointer_events_none_in_pseudo_does_not_emit_internal_uss_marker(self):
        r = convert(
            "<style>.panel::before { content: 'x'; pointer-events: none; }</style>"
            '<div class="panel"></div>'
        )
        self.assertIn('picking-mode="Ignore"', r.uxml)
        self.assertNotIn("__picking-mode__", r.uss)

    def test_pointer_events_none_propagates_to_direct_text_label(self):
        r = convert(
            "<style>.row { pointer-events: none; }</style>"
            '<div class="row"><span>icon</span> Tap to ready</div>'
        )
        # Parent and the synthetic Label for the direct text node both ignore
        # picks; the real <span> child is not a synthetic and stays default.
        self.assertRegex(
            r.uxml,
            r'<ui:Label picking-mode="Ignore"[^/]*text="Tap to ready"',
        )
        self.assertNotRegex(
            r.uxml,
            r'<ui:Label picking-mode="Ignore"[^/]*text="icon"',
        )

    def test_pointer_events_none_propagates_to_pseudo_label(self):
        r = convert(
            "<style>.badge { pointer-events: none; }"
            ".badge::before { content: '>'; }</style>"
            '<div class="badge">Item</div>'
        )
        # Both the parent (or its label form) and the pseudo Label ignore picks.
        self.assertEqual(r.uxml.count('picking-mode="Ignore"'), 2)

    def test_pointer_events_none_propagates_to_list_marker(self):
        r = convert(
            "<style>li { pointer-events: none; }</style>"
            "<ul><li>Ready</li></ul>"
        )
        self.assertRegex(
            r.uxml,
            r'<ui:Label picking-mode="Ignore"[^/]*text="\xb7"|'
            r'<ui:Label picking-mode="Ignore"[^/]*text="•"',
        )

    def test_text_gradient_vertical_emits_sidecar_and_wraps_label(self):
        r = convert(
            '<div class="hero">CHAMPION</div>',
            ".hero { background-image: linear-gradient(180deg, #FFD550 0%, #CC990F 100%); "
            "-webkit-background-clip: text; color: transparent; font-size: 24px; }",
        )
        self.assertIn('text="&lt;gradient=&quot;h2u-tg-1&quot;&gt;CHAMPION&lt;/gradient&gt;"', r.uxml)
        self.assertNotIn("--odd-gradient", r.uss)
        self.assertNotIn("color: transparent", r.uss)
        self.assertEqual(1, len(r.text_gradient_files))
        filename, body = r.text_gradient_files[0]
        self.assertEqual("h2u-tg-1.h2utg.json", filename)
        import json
        data = json.loads(body)
        self.assertEqual("Vertical", data["mode"])
        self.assertAlmostEqual(data["topLeft"]["r"], 1.0, places=2)
        self.assertAlmostEqual(data["bottomLeft"]["r"], 0.8, places=2)

    def test_text_gradient_horizontal_90deg(self):
        r = convert(
            '<div class="hero">X</div>',
            ".hero { background: linear-gradient(90deg, #ff0000, #0000ff); "
            "background-clip: text; color: transparent; }",
        )
        import json
        data = json.loads(r.text_gradient_files[0][1])
        self.assertEqual("Horizontal", data["mode"])
        self.assertAlmostEqual(data["topLeft"]["r"], 1.0, places=2)
        self.assertAlmostEqual(data["topRight"]["b"], 1.0, places=2)

    def test_text_gradient_dedupes_identical_gradients(self):
        r = convert(
            '<div class="a">A</div><div class="b">B</div>',
            ".a, .b { background: linear-gradient(180deg, #fff, #000); "
            "-webkit-background-clip: text; color: transparent; }",
        )
        self.assertEqual(1, len(r.text_gradient_files))
        self.assertIn('text="&lt;gradient=&quot;h2u-tg-1&quot;&gt;A&lt;/gradient&gt;"', r.uxml)
        self.assertIn('text="&lt;gradient=&quot;h2u-tg-1&quot;&gt;B&lt;/gradient&gt;"', r.uxml)

    def test_text_gradient_no_promote_to_html2uxml_panel(self):
        r = convert(
            '<div class="hero">HI</div>',
            ".hero { background: linear-gradient(180deg, #fff, #000); "
            "-webkit-background-clip: text; color: transparent; }",
        )
        self.assertNotIn("odd:Html2UxmlPanel", r.uxml)

    def test_empty_pseudo_without_visible_output_is_skipped(self):
        r = convert(
            "<style>.panel::before { content: ''; position: absolute; pointer-events: none; }</style>"
            '<div class="panel"></div>'
        )
        self.assertNotIn('text=""', r.uxml)
        self.assertNotIn("__picking-mode__", r.uss)

    def test_z_index_reorders_siblings_for_unity_paint_order(self):
        r = convert(
            "<style>#front { z-index: 10; } #back { z-index: 1; }</style>"
            '<div><div id="front">front</div><div id="back">back</div></div>'
        )
        self.assertLess(r.uxml.find('name="back"'), r.uxml.find('name="front"'))
        self.assertNotIn("z-index", r.uss)
        self.assertNotIn("z-index", r.stats.dropped_props)

    def test_z_index_flow_overlap_uses_named_overlay_clone(self):
        r = convert(
            "<style>"
            ".lower { display: flex; align-items: flex-end; height: 54px; }"
            ".portrait { width: 48px; height: 48px; margin-left: 8px; "
            "margin-right: -18px; z-index: 2; }"
            ".plate { width: 203px; height: 48px; }"
            "</style>"
            '<div class="lower"><div class="portrait"></div><div class="plate"></div></div>'
        )
        first_portrait = r.uxml.find('class="portrait"')
        plate = r.uxml.find('class="plate"')
        clone = r.uxml.find("h2u-z-overlay-clone")
        self.assertLess(first_portrait, plate)
        self.assertLess(plate, clone)
        self.assertIn('name="h2u-z-overlay-clone-portrait"', r.uxml)
        self.assertIn('tooltip="html2uxml z-index overlay clone of .portrait"', r.uxml)
        self.assertIn("left: 8px", r.uss)
        self.assertIn("top: 6px", r.uss)
        self.assertIn("margin-right: -18px", r.uss)

    def test_transform_translatex_becomes_translate(self):
        r = convert('<div style="transform: translateX(-50%);"></div>')
        self.assertIn("translate: -50% 0", r.uss)

    def test_transform_translate3d_and_axis_scale_flatten_to_2d(self):
        r = convert(
            '<div style="transform: translate3d(12px, 8px, 4px) scaleX(-1);"></div>'
        )
        self.assertIn("translate: 12px 8px", r.uss)
        self.assertIn("scale: -1 1", r.uss)
        self.assertIn("translate3d() flattened to 2D", "\n".join(r.warnings))

    def test_box_shadow_emits_bridge_props(self):
        r = convert(
            '<div style="box-shadow: rgba(0,0,0,0.5) 0px 4px 10px;"></div>'
        )
        self.assertIn("--odd-shadow-offset-x: 0", r.uss)
        self.assertIn("--odd-shadow-offset-y: 4", r.uss)
        self.assertIn("--odd-shadow-blur: 10", r.uss)
        self.assertIn("--odd-shadow-color: rgba(0,0,0,0.5)", r.uss)
        self.assertIn("odd:Html2UxmlPanel", r.uxml)
        self.assertIn('xmlns:odd="ODDGames.Html2Uxml"', r.uxml)

    def test_inset_box_shadow_emits_inner_shadow_bridge_props(self):
        r = convert(
            '<div style="box-shadow: inset 0 1px 0 rgba(255,255,255,0.2);"></div>'
        )
        self.assertIn("--odd-inner-shadow-offset-x: 0", r.uss)
        self.assertIn("--odd-inner-shadow-offset-y: 1", r.uss)
        self.assertIn("--odd-inner-shadow-blur: 0", r.uss)
        self.assertIn("--odd-inner-shadow-color: rgba(255,255,255,0.2)", r.uss)
        self.assertIn("odd:Html2UxmlPanel", r.uxml)

    def test_box_shadow_keeps_one_outer_and_one_inner_layer(self):
        r = convert(
            '<div style="box-shadow: 0 8px 20px rgba(0,0,0,0.4), '
            'inset 0 1px 0 rgba(255,255,255,0.2);"></div>'
        )
        self.assertIn(
            '--odd-box-shadows: "outset|0|8|20|0|rgba(0,0,0,0.4);'
            'inset|0|1|0|0|rgba(255,255,255,0.2)"',
            r.uss,
        )
        self.assertIn("--odd-shadow-offset-y: 8", r.uss)
        self.assertIn("--odd-inner-shadow-offset-y: 1", r.uss)

    def test_multiple_outer_box_shadows_emit_layer_list(self):
        r = convert(
            '<div style="box-shadow: 0 8px 20px rgba(0,0,0,0.4), '
            '0 0 18px #ffcc00;"></div>'
        )
        self.assertIn(
            '--odd-box-shadows: "outset|0|8|20|0|rgba(0,0,0,0.4);'
            'outset|0|0|18|0|#ffcc00"',
            r.uss,
        )
        self.assertNotIn("complex shadow not bridged", "\n".join(r.warnings))
        self.assertIn("odd:Html2UxmlPanel", r.uxml)

    def test_linear_gradient_bridged_with_balanced_parens(self):
        r = convert(
            '<div style="background: linear-gradient(160deg, '
            'rgb(200, 200, 200) 0%, rgb(42, 42, 42) 100%);"></div>'
        )
        self.assertIn(
            '--odd-gradient: "linear-gradient(160deg, '
            'rgb(200, 200, 200) 0%, rgb(42, 42, 42) 100%)"',
            r.uss,
        )

    def test_radial_gradient_bridged(self):
        r = convert(
            '<div style="background: radial-gradient(circle at 35% 35%, '
            '#fff 0%, #ffbf13 35%, #2a0000 100%);"></div>'
        )
        self.assertIn('--odd-radial-gradient: "radial-gradient(circle at 35% 35%', r.uss)
        self.assertIn("odd:Html2UxmlPanel", r.uxml)

    def test_two_radial_background_layers_are_bridged(self):
        r = convert(
            '<div style="background: '
            'radial-gradient(ellipse 80% 70% at 0% 50%, blue 0%, transparent 60%), '
            'radial-gradient(ellipse 80% 70% at 100% 50%, red 0%, transparent 60%), '
            'linear-gradient(180deg, #111 0%, #000 100%);"></div>'
        )
        self.assertIn('--odd-radial-gradient: "radial-gradient(ellipse 80% 70% at 0% 50%', r.uss)
        self.assertIn('--odd-radial-gradient-2: "radial-gradient(ellipse 80% 70% at 100% 50%', r.uss)
        self.assertIn('--odd-gradient: "linear-gradient(180deg, #111 0%, #000 100%)"', r.uss)
        self.assertIn("odd:Html2UxmlPanel", r.uxml)

    def test_repeating_linear_gradient_uses_pattern_bridge(self):
        r = convert(
            '<div style="background-image: repeating-linear-gradient(0deg, '
            'transparent 0 2px, rgba(255,255,255,0.4) 2px 3px);"></div>'
        )
        self.assertIn('--odd-repeating-linear-gradient: "repeating-linear-gradient(0deg, ', r.uss)
        self.assertNotIn("--odd-gradient", r.uss)
        self.assertIn("odd:Html2UxmlPanel", r.uxml)

    def test_repeating_linear_background_does_not_emit_solid_fallback(self):
        r = convert(
            '<div style="background: repeating-linear-gradient(90deg, '
            'transparent 0 18px, rgba(255,191,19,0.05) 18px 20px);"></div>'
        )
        self.assertIn("--odd-repeating-linear-gradient", r.uss)
        self.assertNotIn("background-color: rgba(255,191,19,0.05)", r.uss)

    def test_tiled_radial_dot_background_uses_pattern_bridge(self):
        r = convert(
            '<div style="background-image: radial-gradient(rgba(255,255,255,0.04) 1px, '
            'transparent 1px); background-size: 6px 6px;"></div>'
        )
        self.assertIn('--odd-tiled-radial-gradient: "radial-gradient(rgba(255,255,255,0.04) 1px, transparent 1px)"', r.uss)
        self.assertIn('--odd-background-pattern-size: "6px 6px"', r.uss)
        self.assertNotIn("--odd-radial-gradient", r.uss)
        self.assertIn("background-size: 6px 6px", r.uss)
        self.assertIn("odd:Html2UxmlPanel", r.uxml)

    def test_non_tiled_radial_gradient_stays_normal_gradient(self):
        r = convert(
            '<div style="background-image: radial-gradient(rgba(255,255,255,0.04) 1px, '
            'transparent 1px);"></div>'
        )
        self.assertIn("--odd-radial-gradient", r.uss)
        self.assertNotIn("--odd-tiled-radial-gradient", r.uss)

    def test_text_only_bridged_element_stays_panel(self):
        r = convert(
            '<div class="chip">★</div>',
            ".chip { background: linear-gradient(180deg, #fff, #000); "
            "display: flex; align-items: center; justify-content: center; }",
        )
        self.assertIn('<odd:Html2UxmlPanel class="chip">', r.uxml)
        self.assertIn('<ui:Label text="★"', r.uxml)
        self.assertIn("--odd-gradient", r.uss)

    def test_decorative_star_bridge_uses_vector_icon(self):
        r = convert(
            '<div class="chip">★</div>',
            ".chip { width: 16px; height: 16px; border-radius: 999px; "
            "background: linear-gradient(180deg, #fff, #000); }",
        )
        self.assertIn('<odd:Html2UxmlPanel class="chip h2u-', r.uxml)
        self.assertIn("border-top-left-radius: 999px", r.uss)
        self.assertIn('--odd-vector-icon: "star"', r.uss)
        self.assertNotIn('<ui:Label text="★"', r.uxml)

    def test_compact_nowrap_hud_labels_get_unity_line_boxes(self):
        r = convert(
            '<div class="row"><div class="text">'
            '<div class="name">GREAT CLIPS MW</div>'
            '<div class="driver">BRYCE KENNY</div>'
            '</div></div>',
            ".row { display: flex; flex-direction: row; height: 22px; }"
            ".text { display: flex; flex-direction: column; justify-content: center; }"
            ".name { font-size: 9px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }"
            ".driver { font-size: 7px; margin-top: 2px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }",
        )
        self.assertIn('class="row h2u-', r.uxml)
        self.assertIn('class="text h2u-', r.uxml)
        self.assertIn('class="name h2u-', r.uxml)
        self.assertIn('class="driver h2u-', r.uxml)
        self.assertIn("overflow: hidden", r.uss)
        self.assertIn("min-height: 0", r.uss)
        self.assertIn("max-height: 12px", r.uss)
        self.assertIn("max-height: 10px", r.uss)
        self.assertIn("margin-top: 0", r.uss)
        self.assertIn("-unity-text-align: middle-left", r.uss)

    def test_compact_italic_labels_get_overhang_guard(self):
        r = convert(
            '<div class="name">GREAT CLIPS MW</div>',
            ".name { font-style: italic; font-size: 9px; white-space: nowrap; "
            "overflow: hidden; text-overflow: ellipsis; }",
        )
        self.assertIn("margin-left: -2px", r.uss)
        self.assertIn("padding-left: 2px", r.uss)

    def test_small_display_label_gets_tight_line_box_without_nowrap(self):
        r = convert(
            '<div class="timer-label">EVENT STARTS IN</div>',
            ".timer-label { font-style: italic; font-weight: 800; font-size: 8px; "
            "letter-spacing: 2px; color: white; text-shadow: 1px 1px 0 #000; }",
        )
        self.assertIn('class="timer-label h2u-', r.uxml)
        self.assertIn("height: 10px", r.uss)
        self.assertIn("max-height: 10px", r.uss)
        self.assertIn("-unity-paragraph-spacing: 0", r.uss)

    def test_inherited_display_label_gets_tight_line_box(self):
        r = convert(
            '<div class="ticker"><span class="item">RAYNES vs BLOOD</span></div>',
            ".ticker { display: flex; flex-direction: row; align-items: center; "
            "font-style: italic; font-weight: 800; font-size: 12px; "
            "letter-spacing: 0.9px; color: white; }"
            ".item { display: inline; }",
        )
        self.assertRegex(r.uxml, r'class="item(?: h2u-\d+)+" text="RAYNES vs BLOOD"')
        self.assertIn("height: 15px", r.uss)
        self.assertIn("max-height: 15px", r.uss)
        self.assertIn("-unity-text-align: middle-left", r.uss)
        self.assertIn("--odd-font-weight: 800", r.uss)

    def test_plain_small_body_label_keeps_default_line_box(self):
        r = convert('<div class="caption">small copy</div>', ".caption { font-size: 8px; }")
        self.assertIn('class="caption"', r.uxml)
        self.assertNotIn("height: 10px", r.uss)

    def test_explicit_text_height_skips_compact_line_box(self):
        r = convert(
            '<div class="name">GREAT CLIPS MW</div>',
            ".name { font-size: 9px; height: 14px; white-space: nowrap; "
            "overflow: hidden; text-overflow: ellipsis; }",
        )
        self.assertIn("height: 14px", r.uss)
        self.assertNotIn("max-height: 10px", r.uss)

    def test_midsize_nameplate_label_gets_compact_line_box(self):
        # Nameplate-sized 16px label with the explicit single-line
        # nowrap+overflow:hidden+ellipsis triple. Without a constraint, Unity's
        # default line box is taller than the source CSS expects, which
        # pushes a stacked sibling out of an absolutely-positioned plate.
        r = convert(
            '<div class="plate">'
            '<div class="name">KAYLA BLOOD</div>'
            '<div class="truck">SOLDIER FORTUNE</div>'
            '</div>',
            ".plate { position: absolute; top: 6px; height: 48px; }"
            ".name { font-size: 16px; font-style: italic; font-weight: 900; "
            "white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }"
            ".truck { font-size: 10px; margin-top: 3px; "
            "white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }",
        )
        # 16px font -> 19px line box (font-size + 3).
        self.assertIn("height: 19px", r.uss)
        self.assertIn("max-height: 19px", r.uss)
        # Existing 10px small-label path still runs.
        self.assertIn("max-height: 13px", r.uss)

    def test_empty_styled_span_emits_visual_element_not_label(self):
        r = convert(
            '<button><span class="dot"></span><span>Ready</span></button>',
            ".dot { width: 5px; height: 5px; border-radius: 999px; background: #ffbf13; }",
        )
        self.assertIn('<ui:VisualElement class="dot"', r.uxml)
        self.assertNotIn('<ui:Label class="dot"', r.uxml)

    def test_rem_em_coerced_to_px(self):
        r = convert('<div style="font-size: 1.5rem; padding: 0.5em;"></div>')
        self.assertIn("font-size: 24px", r.uss)
        self.assertIn("padding: 8px", r.uss)

    def test_modern_rgb_slash_alpha_normalised(self):
        r = convert('<div style="color: rgb(255 0 0 / 0.5);"></div>')
        self.assertIn("color: rgba(255, 0, 0, 0.5)", r.uss)

    def test_hsl_color_normalised_for_unity(self):
        r = convert('<div style="color: hsl(120, 100%, 25%);"></div>')
        self.assertIn("color: rgb(0, 128, 0)", r.uss)

    def test_class_selector_in_style_block(self):
        html = (
            "<style>.btn { color: red; padding: 4px; }</style>"
            '<button class="btn">go</button>'
        )
        r = convert(html)
        # Original class name preserved; the .btn rule emitted verbatim.
        self.assertIn('class="btn"', r.uxml)
        self.assertIn(".btn {", r.uss)
        self.assertIn("color: red", r.uss)
        self.assertIn("padding: 4px", r.uss)
        # No h2u-N rule for an element without inline style overrides.
        self.assertNotIn(".h2u-1", r.uss)

    def test_img_uses_background_visual_element_for_unity_preview(self):
        r = convert('<img class="logo" src="mtd-logo.png" alt="MTD" />')
        self.assertIn('<ui:VisualElement class="logo h2u-1" tooltip="MTD"', r.uxml)
        self.assertIn('background-image: url("mtd-logo.png")', r.uss)
        self.assertIn("-unity-background-scale-mode: scale-to-fit", r.uss)

    def test_bundled_image_aspect_ratio_is_injected(self):
        with TemporaryDirectory() as td:
            assets_dir = Path(td)
            (assets_dir / "logo.png").write_bytes(
                b"\x89PNG\r\n\x1a\n"
                b"\x00\x00\x00\rIHDR"
                b"\x00\x00\x00\x64\x00\x00\x00\x32"
                b"\x08\x06\x00\x00\x00"
            )
            uss = '.logo {\n    background-image: url("Images/logo.png");\n}\n'
            rewritten = inject_image_aspect_ratios(uss, assets_dir=assets_dir)
        self.assertIn("aspect-ratio: 2", rewritten)

    def test_asset_copy_reuses_identical_names_and_suffixes_different_bytes(self):
        with TemporaryDirectory() as td:
            base = Path(td)
            src_a = base / "a"
            src_b = base / "b"
            src_c = base / "c"
            src_a.mkdir()
            src_b.mkdir()
            src_c.mkdir()
            (src_a / "icon.png").write_bytes(b"same")
            (src_b / "icon.png").write_bytes(b"same")
            (src_c / "icon.png").write_bytes(b"different")
            assets_dir = base / "out" / "UI" / "Images"
            uss = (
                f'.a {{ background-image: url("{(src_a / "icon.png").as_posix()}"); }}\n'
                f'.b {{ background-image: url("{(src_b / "icon.png").as_posix()}"); }}\n'
                f'.c {{ background-image: url("{(src_c / "icon.png").as_posix()}"); }}\n'
            )
            rewritten, _report = collect_and_rewrite(
                uss,
                base_dir=None,
                assets_dir=assets_dir,
                project_subdir="Images",
            )
            self.assertEqual(2, len(list(assets_dir.glob("icon*.png"))))
            self.assertIn('url("Images/icon.png")', rewritten)
            self.assertIn('url("Images/icon-2.png")', rewritten)

    def test_project_relative_asset_urls_are_not_rebundled(self):
        with TemporaryDirectory() as td:
            assets_dir = Path(td) / "out" / "UI" / "Images"
            assets_dir.mkdir(parents=True)
            (assets_dir / "chat-toggle-button.png").write_bytes(b"png")
            uss = '.icon { background-image: url("Images/chat-toggle-button.png"); }\n'
            rewritten, report = collect_and_rewrite(
                uss,
                base_dir=Path(td),
                assets_dir=assets_dir,
                project_subdir="Images",
            )
            self.assertEqual(uss, rewritten)
            self.assertEqual([], report.failed)
            self.assertEqual([], report.copied)

    def test_google_font_ttf_selection_prefers_heaviest_weight(self):
        css = """
        @font-face { font-family: 'X'; font-weight: 400; src: url(https://example.com/400.ttf); }
        @font-face { font-family: 'X'; font-weight: 900; src: url(https://example.com/900.ttf); }
        @font-face { font-family: 'X'; font-weight: 700; src: url(https://example.com/700.ttf); }
        """
        self.assertEqual("https://example.com/900.ttf", _select_google_ttf_url(css))

    def test_google_font_face_selection_prefers_latin_subset(self):
        faces = [
            {
                "url": "https://example.com/cyrillic.ttf",
                "weight": 900,
                "italic": False,
                "unicode_range": "U+0400-045F",
            },
            {
                "url": "https://example.com/latin.ttf",
                "weight": 900,
                "italic": False,
                "unicode_range": "U+0000-00FF",
            },
        ]
        self.assertEqual("https://example.com/latin.ttf", _preferred_google_faces(faces)[0]["url"])

    def test_safe_font_name_uses_family_weight_and_style(self):
        self.assertEqual(
            "Saira_Condensed-900-Italic.ttf",
            _safe_font_name("Saira Condensed", ".ttf", weight=900, italic=True),
        )

    def test_cached_normal_fonts_do_not_block_missing_italic_downloads(self):
        css = """
        @font-face { font-family: 'Saira Condensed'; font-style: italic; font-weight: 800; src: url(https://example.com/saira-800i.ttf); }
        """
        with TemporaryDirectory() as td:
            base = Path(td)
            cache = base / "fonts"
            cache.mkdir()
            (cache / "Saira_Condensed-800.ttf").write_bytes(b"normal")
            fonts_dir = base / "Fonts"
            with mock.patch.dict(os.environ, {"H2U_CACHE_DIR": str(base)}, clear=False):
                with mock.patch("html2uxml.assets._google_font_css_urls", return_value=["https://example.com/css"]):
                    with mock.patch("html2uxml.assets.urllib.request.urlopen") as mocked_open:
                        mocked_resp = mock.Mock()
                        mocked_resp.read.return_value = css.encode("utf-8")
                        mocked_open.return_value.__enter__.return_value = mocked_resp
                        with mock.patch("html2uxml.assets._download") as mocked_download:
                            def fake_download(_url, dest, _report, _timeout, target_name=None):
                                (dest / target_name).write_bytes(b"italic")
                                return target_name

                            mocked_download.side_effect = fake_download
                            mapping, _report = download_google_fonts(
                                ["Saira Condensed"],
                                assets_dir=fonts_dir,
                                project_subdir="Fonts",
                            )

        variants = mapping["Saira Condensed"]
        self.assertIn((800, False), {(v.weight, v.italic) for v in variants})
        self.assertIn((800, True), {(v.weight, v.italic) for v in variants})

    def test_font_injection_uses_real_weight_without_synthetic_bold(self):
        uss = (
            '.title {\n'
            '    --odd-font-family: "Saira Condensed";\n'
            '    --odd-font-weight: 900;\n'
            '    -unity-font-style: bold-and-italic;\n'
            '}\n'
        )
        out = _inject_font_definitions(
            uss,
            {
                "Saira Condensed": [
                    FontVariant("Assets/UI/Fonts/SairaCondensed-900.ttf", 900, False),
                ],
            },
        )
        self.assertIn(
            '-unity-font-definition: url("Assets/UI/Fonts/SairaCondensed-900 SDF.asset")',
            out,
        )
        self.assertIn("-unity-font-style: italic", out)
        self.assertNotIn("bold-and-italic", out)
        self.assertNotIn("--odd-font-weight", out)

    def test_font_injection_applies_inherited_family_to_child_weights(self):
        uss = (
            '.root {\n'
            '    --odd-font-family: "Saira Condensed";\n'
            '}\n'
            '.title {\n'
            '    --odd-font-weight: 900;\n'
            '    -unity-font-style: bold-and-italic;\n'
            '}\n'
        )
        out = _inject_font_definitions(
            uss,
            {
                "Saira Condensed": [
                    FontVariant("Assets/UI/Fonts/SairaCondensed-400.ttf", 400, False),
                    FontVariant("Assets/UI/Fonts/SairaCondensed-900.ttf", 900, False),
                ],
            },
        )
        self.assertIn(
            '-unity-font-definition: url("Assets/UI/Fonts/SairaCondensed-400 SDF.asset")',
            out,
        )
        self.assertIn(
            '.title {\n    -unity-font-definition: url("Assets/UI/Fonts/SairaCondensed-900 SDF.asset");\n'
            '    -unity-font-style: italic;',
            out,
        )
        self.assertNotIn("bold-and-italic", out)
        self.assertNotIn("--odd-font-weight", out)

    def test_font_injection_uses_real_italic_without_synthetic_style(self):
        uss = (
            '.time {\n'
            '    --odd-font-family: "Courier New";\n'
            '    --odd-font-weight: 700;\n'
            '    -unity-font-style: bold-and-italic;\n'
            '}\n'
        )
        out = _inject_font_definitions(
            uss,
            {
                "Courier New": [
                    FontVariant("Assets/UI/Fonts/CourierNew-700-Italic.ttf", 700, True),
                ],
            },
        )
        self.assertIn(
            '-unity-font-definition: url("Assets/UI/Fonts/CourierNew-700-Italic SDF.asset")',
            out,
        )
        self.assertNotIn("-unity-font-style", out)

    def test_font_injection_can_reference_source_font_files(self):
        uss = (
            '.time {\n'
            '    --odd-font-family: "Courier New";\n'
            '    --odd-font-weight: 700;\n'
            '    -unity-font-style: bold-and-italic;\n'
            '}\n'
        )
        out = _inject_font_definitions(
            uss,
            {
                "Courier New": [
                    FontVariant("Assets/UI/Fonts/CourierNew-700-Italic.ttf", 700, True),
                ],
            },
            use_textcore_font_assets=False,
        )
        self.assertIn(
            '-unity-font-definition: url("Assets/UI/Fonts/CourierNew-700-Italic.ttf")',
            out,
        )

    def test_windows_font_aliases_find_courier_variants(self):
        self.assertIn("cour", _font_search_prefixes("Courier New"))
        self.assertEqual((700, True), _local_font_variant(Path("courbi.ttf")))
        self.assertEqual((900, True), _local_font_variant(Path("Barlow_Condensed-900-Italic.ttf")))

    def test_font_cache_reuses_stable_variant_names(self):
        with TemporaryDirectory() as td:
            base = Path(td)
            cache_root = base / "cache"
            cache_fonts = cache_root / "fonts"
            cache_fonts.mkdir(parents=True)
            (cache_fonts / "Inter-700.ttf").write_bytes(b"font")
            fonts_dir = base / "out-fonts"
            fonts_dir.mkdir()
            report = AssetReport()
            with mock.patch.dict(os.environ, {"H2U_CACHE_DIR": str(cache_root)}):
                variants = _copy_cached_font_variants(
                    "Inter",
                    fonts_dir=fonts_dir,
                    project_subdir="Assets/UI/Fonts",
                    report=report,
                )
            self.assertEqual([FontVariant("Assets/UI/Fonts/Inter-700.ttf", 700, False, "cache")], variants)
            self.assertTrue((fonts_dir / "Inter-700.ttf").is_file())

    def test_inline_style_only_hoists_to_h2u(self):
        r = convert(
            '<style>.card { padding: 8px; }</style>'
            '<div class="card" style="background: red;"></div>'
        )
        self.assertIn(".card {", r.uss)
        self.assertIn("padding: 8px", r.uss)
        self.assertIn(".h2u-1", r.uss)
        self.assertIn("background-color: red", r.uss)
        self.assertIn('class="card h2u-1"', r.uxml)

    def test_identical_generated_styles_reuse_h2u_class(self):
        r = convert(
            '<div style="color: red; padding: 4px;">a</div>'
            '<span style="padding: 4px; color: red;">b</span>'
        )
        self.assertEqual(1, r.stats.inline_overrides)
        self.assertEqual(1, r.uss.count(".h2u-"))
        self.assertEqual(2, r.uxml.count('class="h2u-1"'))

    def test_pseudo_classes_pass_through_with_generated_class(self):
        html = (
            "<style>.btn:hover { color: blue; }</style>"
            '<button class="btn">x</button>'
        )
        r = convert(html)
        self.assertIn(":hover", r.uss)
        self.assertIn("color: blue", r.uss)

    def test_selector_includes_positioned_overlay_sibling(self):
        html = (
            '<div class="stage" style="position:relative;width:100px;height:100px;">'
            '<section id="screen" style="width:100px;height:100px;"></section>'
            '<div id="hud" style="position:absolute;top:4px;right:4px;z-index:10;">'
            '<button id="chat">chat</button>'
            "</div>"
            "</div>"
        )
        r = convert(html, select="#screen")
        self.assertIn('name="screen"', r.uxml)
        self.assertIn('name="hud"', r.uxml)
        self.assertIn('name="chat"', r.uxml)
        self.assertIn("h2u-selection-viewport", r.uxml)
        self.assertIn("width: 100px", r.uss)
        self.assertIn("height: 100px", r.uss)
        self.assertIn("positioned overlay sibling", "\n".join(r.warnings))

    def test_selector_does_not_include_normal_sibling(self):
        html = (
            '<div class="stage">'
            '<section id="screen"></section>'
            '<div id="sidebar"><button id="outside">outside</button></div>'
            "</div>"
        )
        r = convert(html, select="#screen")
        self.assertIn('name="screen"', r.uxml)
        self.assertNotIn('name="sidebar"', r.uxml)
        self.assertNotIn('name="outside"', r.uxml)

    def test_opacity_is_not_propagated_as_text_inheritance(self):
        html = (
            '<button style="position:relative; opacity:0.55;">'
            '<svg width="18" height="18" viewBox="0 0 24 24"><path d="M1 1h2"/></svg>'
            '<span style="position:absolute; top:-3px; right:-5px; '
            'background:#E63A1F; color:#fff; font-size:8px;">2</span>'
            "</button>"
        )
        r = convert(html)
        self.assertEqual(1, r.uss.count("opacity: 0.55"))
        self.assertIn("opacity: 1", r.uss)
        self.assertRegex(r.uxml, r'<ui:Label class="h2u-\d+" text="2" />')
        self.assertIn("CSS opacity group approximated", "\n".join(r.warnings))

    def test_direct_label_opacity_still_maps(self):
        r = convert('<span style="opacity:0.4;">x</span>')
        self.assertIn("opacity: 0.4", r.uss)

    def test_nbsp_in_tag_doesnt_break_parsing(self):
        # Sources from docx text extraction often substitute NBSP for spaces.
        html = "<div class=\"a\" style=\"color:red;\">hi</div>"
        r = convert(html)
        self.assertIn("ui:Label", r.uxml)
        self.assertIn("color: red", r.uss)

    def test_dropped_props_recorded_in_stats(self):
        r = convert('<div style="float: left; mask: url(x.png);"></div>')
        self.assertIn("float", r.stats.dropped_props)
        self.assertIn("mask", r.stats.dropped_props)

    def test_clip_path_polygon_bridged(self):
        r = convert(
            '<div style="clip-path: polygon(0% 0%, 100% 0%, 50% 100%);"></div>'
        )
        self.assertIn("--odd-clip-polygon", r.uss)
        self.assertIn("polygon(0% 0%, 100% 0%, 50% 100%)", r.uss)
        self.assertIn("odd:Html2UxmlPanel", r.uxml)

    def test_filter_drop_shadow_bridged(self):
        r = convert(
            '<div style="filter: drop-shadow(rgba(0,0,0,0.5) 2px 4px 6px);"></div>'
        )
        self.assertIn("--odd-shadow-offset-x: 2", r.uss)
        self.assertIn("--odd-shadow-offset-y: 4", r.uss)
        self.assertIn("--odd-shadow-blur: 6", r.uss)

    def test_unity_64_native_filter_functions_pass_through(self):
        r = convert('<div style="filter: blur(4px) invert(100%);"></div>')
        self.assertIn("filter: blur(4px) invert(100%)", r.uss)
        self.assertNotIn("odd:Html2UxmlPanel", r.uxml)

    def test_brightness_and_saturate_use_oddgames_custom_filter_assets(self):
        r = convert('<div style="filter: brightness(120%) saturate(1.8);"></div>')
        self.assertIn(
            'filter: filter("Packages/au.com.oddgames.html2uxml/Runtime/Filters/ODDGamesColorAdjust.asset" 1.2 1) '
            'filter("Packages/au.com.oddgames.html2uxml/Runtime/Filters/ODDGamesColorAdjust.asset" 1 1.8)',
            r.uss,
        )
        self.assertIn("ODDGames package custom filter assets", "\n".join(r.warnings))
        self.assertNotIn("odd:Html2UxmlPanel", r.uxml)

    def test_backdrop_filter_blur_is_approximated_as_native_filter(self):
        r = convert(
            '<div style="backdrop-filter: blur(12px) saturate(180%);"></div>'
        )
        self.assertIn(
            'filter: blur(12px) filter("Packages/au.com.oddgames.html2uxml/Runtime/Filters/ODDGamesColorAdjust.asset" 1 1.8)',
            r.uss,
        )
        self.assertIn("backdrop-filter approximated as filter", "\n".join(r.warnings))

    def test_webkit_backdrop_filter_uses_same_blur_approximation(self):
        r = convert('<div style="-webkit-backdrop-filter: blur(12px);"></div>')
        self.assertIn("filter: blur(12px)", r.uss)

    def test_drop_shadow_can_combine_with_native_filter(self):
        r = convert(
            '<div style="filter: drop-shadow(#000 1px 2px 3px) blur(4px);"></div>'
        )
        self.assertIn("--odd-shadow-offset-x: 1", r.uss)
        self.assertIn("filter: blur(4px)", r.uss)
        self.assertIn("odd:Html2UxmlPanel", r.uxml)

    def test_multiple_drop_shadow_filters_emit_layer_list(self):
        r = convert(
            '<div style="filter: drop-shadow(#000 1px 2px 3px) '
            'drop-shadow(rgba(255,255,255,0.5) 0 0 8px) blur(4px);"></div>'
        )
        self.assertIn(
            '--odd-box-shadows: "outset|1|2|3|0|#000;'
            'outset|0|0|8|0|rgba(255,255,255,0.5)"',
            r.uss,
        )
        self.assertIn("filter: blur(4px)", r.uss)
        self.assertIn("odd:Html2UxmlPanel", r.uxml)

    def test_linear_mask_image_emits_bridge_fade(self):
        r = convert(
            '<div style="mask-image: linear-gradient(to bottom, '
            'black 0%, transparent 100%);"></div>'
        )
        self.assertIn(
            '--odd-mask-image: "linear-gradient(to bottom, black 0%, transparent 100%)"',
            r.uss,
        )
        self.assertIn("odd:Html2UxmlPanel", r.uxml)

    def test_webkit_linear_mask_image_emits_bridge_fade(self):
        r = convert(
            '<div style="-webkit-mask-image: linear-gradient(90deg, '
            'transparent 0%, black 24px);"></div>'
        )
        self.assertIn(
            '--odd-mask-image: "linear-gradient(90deg, transparent 0%, black 24px)"',
            r.uss,
        )
        self.assertIn("odd:Html2UxmlPanel", r.uxml)

    def test_radial_mask_image_is_reported_as_gap(self):
        r = convert(
            '<div style="mask-image: radial-gradient(circle, black, transparent);"></div>'
        )
        self.assertNotIn("--odd-mask-image", r.uss)
        self.assertIn("only linear-gradient mask fades are bridged", "\n".join(r.warnings))

    def test_css_variables_are_resolved_for_bridge_effects(self):
        r = convert(
            "<style>"
            ".card { --glow: 0 0 18px rgba(255,204,0,0.8); "
            "box-shadow: var(--glow); }"
            "</style>"
            '<div class="card"></div>'
        )
        self.assertIn("--glow: 0 0 18px rgba(255,204,0,0.8)", r.uss)
        self.assertIn(
            '--odd-box-shadows: "outset|0|0|18|0|rgba(255,204,0,0.8)"',
            r.uss,
        )
        self.assertIn('class="card h2u-1"', r.uxml)
        self.assertIn("odd:Html2UxmlPanel", r.uxml)

    def test_unity_specific_properties_pass_through(self):
        r = convert(
            '<div style="-unity-text-auto-size: best-fit 12px 28px; '
            '-unity-material: url(&quot;Assets/UI/m.mat&quot;);"></div>'
        )
        self.assertIn("-unity-text-auto-size: best-fit 12px 28px", r.uss)
        self.assertIn('-unity-material: url("Assets/UI/m.mat")', r.uss)

    def test_unity_64_background_longhands_pass_through(self):
        r = convert(
            '<div style="background-size: cover; background-repeat: no-repeat; '
            'background-position: center;"></div>'
        )
        self.assertIn("background-size: cover", r.uss)
        self.assertIn("background-repeat: no-repeat", r.uss)
        self.assertIn("background-position: center", r.uss)

    def test_outline_approximated_as_border(self):
        r = convert('<div style="outline: 2px solid red;"></div>')
        self.assertIn("border-top-width: 1px", r.uss)
        self.assertIn("border-top-color: red", r.uss)

    def test_white_space_pre_wrap_passes_through(self):
        r = convert('<div style="white-space: pre-wrap;"></div>')
        self.assertIn("white-space: pre-wrap", r.uss)

    def test_overflow_auto_promotes_to_scrollview(self):
        r = convert('<div style="overflow: auto;"><span>x</span></div>')
        self.assertIn("ui:ScrollView", r.uxml)

    def test_details_summary_to_foldout(self):
        r = convert("<details><summary>Title</summary><span>body</span></details>")
        self.assertIn('<ui:Foldout', r.uxml)
        self.assertIn('text="Title"', r.uxml)
        self.assertIn("body", r.uxml)
        self.assertNotIn("ui:Label text=\"Title\"", r.uxml)  # summary consumed

    def test_progress_to_progressbar(self):
        r = convert('<progress value="40" max="100"></progress>')
        self.assertIn("ui:ProgressBar", r.uxml)
        self.assertIn('value="40"', r.uxml)
        self.assertIn('high-value="100"', r.uxml)

    def test_text_decoration_underline_in_label(self):
        r = convert(
            '<style>.u { text-decoration: underline; }</style>'
            '<span class="u">hi</span>'
        )
        self.assertIn("&lt;u&gt;hi&lt;/u&gt;", r.uxml)
        self.assertEqual([], r.warnings)

    def test_attribute_selector_hoisted(self):
        r = convert(
            '<style>input[type="text"] { color: red; }</style>'
            '<input type="text" />'
        )
        # Attribute selector won't survive into USS verbatim, but the
        # declarations are hoisted onto the matched element's h2u-N rule.
        self.assertIn(".h2u-1", r.uss)
        self.assertIn("color: red", r.uss)
        self.assertNotIn('input[type="text"]', r.uss)

    def test_nth_child_selector_hoists_only_matching_elements(self):
        r = convert(
            "<style>.item:nth-child(2) { color: red; }</style>"
            '<span class="item">a</span><span class="item">b</span>'
        )
        self.assertIn(".h2u-1", r.uss)
        self.assertIn("color: red", r.uss)
        self.assertEqual(1, r.uxml.count("h2u-"))
        self.assertNotIn(":nth-child", r.uss)

    def test_not_selector_hoists_only_matching_elements(self):
        r = convert(
            "<style>.item:not(.active) { color: red; }</style>"
            '<span class="item active">a</span><span class="item">b</span>'
        )
        self.assertIn(".h2u-1", r.uss)
        self.assertIn("color: red", r.uss)
        self.assertEqual(1, r.uxml.count("h2u-"))
        self.assertNotIn(":not", r.uss)

    def test_unsupported_selector_with_runtime_pseudo_is_not_hoisted_as_static(self):
        r = convert(
            '<style>input[type="text"]:focus { color: red; }</style>'
            '<input type="text" />'
        )
        self.assertNotIn("color: red", r.uss)
        self.assertNotIn("h2u-", r.uxml)

    def test_font_family_custom_prop_does_not_require_html2uxml_panel(self):
        r = convert(
            '<style>.panel { font-family: Inter, sans-serif; }</style>'
            '<div class="panel">x</div>'
        )
        self.assertIn('--odd-font-family: "Inter"', r.uss)
        self.assertNotIn("odd:Html2UxmlPanel", r.uxml)
        self.assertNotIn('xmlns:odd="ODDGames.Html2Uxml"', r.uxml)

    def test_self_closing_stylesheet_link_is_loaded(self):
        with TemporaryDirectory() as td:
            base = Path(td)
            (base / "style.css").write_text(".x { color: red; }", encoding="utf-8")
            r = convert(
                '<link rel="stylesheet" href="style.css" /><div class="x">x</div>',
                base_dir=base,
            )
        self.assertIn(".x", r.uss)
        self.assertIn("color: red", r.uss)

    def test_stylesheet_link_rel_tokens_are_loaded(self):
        with TemporaryDirectory() as td:
            base = Path(td)
            (base / "style.css").write_text(".x { color: red; }", encoding="utf-8")
            r = convert(
                '<link rel="preload stylesheet" href="style.css"><div class="x">x</div>',
                base_dir=base,
            )
        self.assertIn(".x", r.uss)
        self.assertIn("color: red", r.uss)

    def test_linked_stylesheet_hrefs_dedup_raw_and_rendered_html(self):
        self.assertEqual(
            ["base.css", "runtime.css"],
            _linked_stylesheet_hrefs(
                '<link rel="stylesheet" href="base.css">',
                '<link rel="stylesheet" href="base.css"><link rel="stylesheet" href="runtime.css">',
            ),
        )

    def test_head_void_tags_do_not_suppress_body_text(self):
        r = convert(
            "<html><head>"
            '<meta charset="utf-8">'
            '<link rel="preconnect" href="https://example.invalid">'
            "<title>ignored</title>"
            "</head><body><div><span>LIVE</span></div></body></html>"
        )
        self.assertIn('text="LIVE"', r.uxml)
        self.assertNotIn("ignored", r.uxml)

    def test_cli_can_fail_on_inline_styles(self):
        with TemporaryDirectory() as td:
            base = Path(td)
            source = base / "inline.html"
            source.write_text('<div style="color: red;">x</div>', encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                rc = cli_main([
                    str(source),
                    "-o", str(base / "out"),
                    "--fail-on-inline-styles",
                    "-q",
                ])
        self.assertEqual(2, rc)

    def test_cli_allows_class_based_source_with_inline_guard(self):
        with TemporaryDirectory() as td:
            base = Path(td)
            source = base / "classed.html"
            source.write_text(
                "<style>.x { color: red; }</style><div class=\"x\">x</div>",
                encoding="utf-8",
            )
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                rc = cli_main([
                    str(source),
                    "-o", str(base / "out"),
                    "--fail-on-inline-styles",
                    "-q",
                ])
            self.assertEqual(0, rc)
            self.assertTrue((base / "out" / "UI" / "classed.uxml").is_file())

    def test_cli_fails_when_selector_only_appears_in_label_text(self):
        with TemporaryDirectory() as td:
            base = Path(td)
            source = base / "screens.html"
            source.write_text(
                '<div id="screen-standings"></div>'
                '<div>06 · Championship · screen-qualifying-standings</div>',
                encoding="utf-8",
            )
            stderr = io.StringIO()
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(stderr):
                rc = cli_main([
                    str(source),
                    "-o", str(base / "out"),
                    "--selector", "#screen-qualifying-standings",
                    "-q",
                ])
            self.assertEqual(2, rc)
            self.assertFalse((base / "out" / "UI" / "screens.uxml").exists())
            message = stderr.getvalue()
            self.assertIn("selector matched no element", message)
            self.assertIn("screen-qualifying-standings", message)
            self.assertIn("screen-standings", message)

    def test_cli_loads_linked_css_without_css_flag(self):
        with TemporaryDirectory() as td:
            base = Path(td)
            (base / "style.css").write_text(".x { color: red; }", encoding="utf-8")
            source = base / "linked.html"
            source.write_text(
                '<link rel="stylesheet" href="style.css"><div class="x">x</div>',
                encoding="utf-8",
            )
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                rc = cli_main([
                    str(source),
                    "-o", str(base / "out"),
                    "--no-download-fonts",
                    "-q",
                ])
            self.assertEqual(0, rc)
            self.assertIn("color: red", (base / "out" / "UI" / "linked.uss").read_text(encoding="utf-8"))

    def test_cli_report_prints_converter_warnings(self):
        with TemporaryDirectory() as td:
            base = Path(td)
            source = base / "warn.html"
            source.write_text(
                '<div style="transform: translate3d(1px, 2px, 3px);">x</div>',
                encoding="utf-8",
            )
            stderr = io.StringIO()
            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(stderr):
                rc = cli_main([
                    str(source),
                    "-o", str(base / "out"),
                    "--no-download-fonts",
                ])
            self.assertEqual(0, rc)
            report = stderr.getvalue()
            self.assertIn("warnings:", report)
            self.assertIn("translate3d() flattened to 2D", report)

    def test_svg_url_rewrite_targets_rasterized_png(self):
        uss = (
            '.icon { background-image: url("Images/svg-1.svg"); }\n'
            ".alt { background-image: url(Images/svg-2.svg); }"
        )
        rewritten = _replace_svg_urls_with_png(uss, ["svg-1.svg", "svg-2.svg"])
        self.assertIn('url("Images/svg-1.png")', rewritten)
        self.assertIn("url(Images/svg-2.png)", rewritten)
        self.assertNotIn("svg-1.svg", rewritten)
        self.assertNotIn("svg-2.svg", rewritten)

    def test_svg_root_resize_replaces_intrinsic_dimensions(self):
        raw = '<svg xmlns="http://www.w3.org/2000/svg" width="18" height="18" viewBox="0 0 24 24"></svg>'
        resized = _resize_svg_root(raw, 72, 72)
        self.assertIn('width="72"', resized)
        self.assertIn('height="72"', resized)
        self.assertNotIn('width="18"', resized)
        self.assertNotIn('height="18"', resized)

    def test_cli_accepts_svg_raster_scale(self):
        with TemporaryDirectory() as td:
            base = Path(td)
            source = base / "icon.html"
            source.write_text(
                '<button><svg width="18" height="18" viewBox="0 0 24 24"></svg></button>',
                encoding="utf-8",
            )
            with mock.patch("html2uxml.cli._rasterize_svg_assets") as mocked_raster:
                mocked_raster.return_value = None
                with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                    rc = cli_main([
                        str(source),
                        "-o", str(base / "out"),
                        "--svg-raster-scale", "4",
                        "--svg-raster-min-px", "256",
                        "--no-download-fonts",
                        "-q",
                    ])
            self.assertEqual(0, rc)
            self.assertEqual(4, mocked_raster.call_args.kwargs["scale"])
            self.assertEqual(256, mocked_raster.call_args.kwargs["min_px"])

    def test_svg_raster_minimum_size_is_applied(self):
        with TemporaryDirectory() as td:
            out = Path(td) / "icon.png"
            with mock.patch("html2uxml.cli.subprocess.run") as mocked_run:
                def fake_run(cmd, **_kwargs):
                    screenshot_arg = next(part for part in cmd if part.startswith("--screenshot="))
                    Path(screenshot_arg.split("=", 1)[1]).write_bytes(b"png")
                    proc = mock.Mock()
                    proc.returncode = 0
                    proc.stdout = ""
                    proc.stderr = ""
                    return proc

                mocked_run.side_effect = fake_run
                with mock.patch("html2uxml.cli._copy_or_crop_png") as mocked_copy:
                    _rasterize_one_svg(
                        "browser",
                        '<svg width="18" height="18" viewBox="0 0 24 24"></svg>',
                        out,
                        scale=4,
                        min_px=256,
                    )
            cmd = mocked_run.call_args.args[0]
            self.assertIn("--window-size=256,256", cmd)
            self.assertEqual((256, 256), mocked_copy.call_args.args[2:4])

    def test_successful_svg_rasterization_removes_intermediate_svg(self):
        with TemporaryDirectory() as td:
            base = Path(td)
            result = mock.Mock()
            result.svg_files = [("svg-1.svg", '<svg width="18" height="18"></svg>')]
            result.uss = 'background-image: url("Images/svg-1.svg");'
            result.warnings = []
            images_dir = base / "UI" / "Images"
            images_dir.mkdir(parents=True)
            (images_dir / "svg-1.svg").write_text(result.svg_files[0][1], encoding="utf-8")
            with mock.patch("html2uxml.cli._has_cairosvg", return_value=False):
                with mock.patch("html2uxml.cli._find_magick_executable", return_value=None):
                    with mock.patch("html2uxml.cli._find_browser_executable", return_value="browser"):
                        with mock.patch("html2uxml.cli._rasterize_one_svg") as mocked_one:
                            mocked_one.side_effect = lambda _browser, _raw, path, scale=4, min_px=256: path.write_bytes(b"png")
                            report = _rasterize_svg_assets(
                                result,
                                images_dir,
                                scale=4,
                                min_px=256,
                            )
            self.assertEqual([("svg-1.svg", "svg-1.png")], report.rasterized)
            self.assertEqual({"browser": 1}, report.renderers)
            self.assertFalse((images_dir / "svg-1.svg").exists())
            self.assertIn("svg-1.png", result.uss)

    def test_svg_rasterization_prefers_imagemagick_over_browser(self):
        with TemporaryDirectory() as td:
            base = Path(td)
            result = mock.Mock()
            result.svg_files = [("svg-1.svg", '<svg width="18" height="18"></svg>')]
            result.uss = 'background-image: url("Images/svg-1.svg");'
            result.warnings = []
            images_dir = base / "UI" / "Images"
            images_dir.mkdir(parents=True)
            (images_dir / "svg-1.svg").write_text(result.svg_files[0][1], encoding="utf-8")
            with mock.patch("html2uxml.cli._has_cairosvg", return_value=False):
                with mock.patch("html2uxml.cli._find_magick_executable", return_value="magick"):
                    with mock.patch("html2uxml.cli._find_browser_executable") as mocked_browser:
                        with mock.patch("html2uxml.cli._rasterize_one_svg_with_magick") as mocked_magick:
                            mocked_magick.side_effect = lambda _magick, _raw, path, scale=4, min_px=256: path.write_bytes(b"png")
                            report = _rasterize_svg_assets(result, images_dir, scale=4, min_px=256)
            self.assertEqual({"ImageMagick": 1}, report.renderers)
            mocked_browser.assert_not_called()

    def test_cli_downloads_fonts_by_default(self):
        with TemporaryDirectory() as td:
            base = Path(td)
            source = base / "fonted.html"
            source.write_text(
                '<style>.x { font-family: Inter, sans-serif; font-weight: 700; }</style>'
                '<div class="x">x</div>',
                encoding="utf-8",
            )
            with mock.patch("html2uxml.cli.download_google_fonts") as mocked_fonts:
                mocked_fonts.return_value = (
                    {"Inter": [FontVariant("Fonts/Inter-700.ttf", 700, False)]},
                    AssetReport(),
                )
                with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                    rc = cli_main([
                        str(source),
                        "-o", str(base / "out"),
                        "-q",
                    ])
            self.assertEqual(0, rc)
            mocked_fonts.assert_called_once()
            self.assertIn(
                '-unity-font-definition: url("Fonts/Inter-700 SDF.asset")',
                (base / "out" / "UI" / "fonted.uss").read_text(encoding="utf-8"),
            )
            manifest = json.loads((base / "out" / "UI" / "Fonts" / "html2uxml-fonts.json").read_text(encoding="utf-8"))
            self.assertIn("ABCDEFGHIJKLMNOPQRSTUVWXYZ", manifest["defaultCharacters"])
            self.assertEqual("static", manifest["fonts"][0]["atlasMode"])
            self.assertEqual("Inter-700.ttf", manifest["fonts"][0]["fontFile"])
            self.assertIn("x", manifest["fonts"][0]["characters"])

    def test_cli_font_manifest_marks_dynamic_text_opt_in(self):
        with TemporaryDirectory() as td:
            base = Path(td)
            source = base / "fonted.html"
            source.write_text(
                '<style>.x { font-family: Inter, sans-serif; --odd-font-atlas-mode: dynamic; }</style>'
                '<div class="x">runtime</div>',
                encoding="utf-8",
            )
            with mock.patch("html2uxml.cli.download_google_fonts") as mocked_fonts:
                mocked_fonts.return_value = (
                    {"Inter": [FontVariant("Fonts/Inter-400.ttf", 400, False)]},
                    AssetReport(),
                )
                with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                    rc = cli_main([
                        str(source),
                        "-o", str(base / "out"),
                        "-q",
                    ])
            self.assertEqual(0, rc)
            manifest = json.loads((base / "out" / "UI" / "Fonts" / "html2uxml-fonts.json").read_text(encoding="utf-8"))
            self.assertEqual("dynamic", manifest["fonts"][0]["atlasMode"])
            self.assertIn("runtime", manifest["fonts"][0]["characters"])

    def test_cli_can_opt_out_of_font_downloads(self):
        with TemporaryDirectory() as td:
            base = Path(td)
            source = base / "fonted.html"
            source.write_text(
                '<style>.x { font-family: Inter, sans-serif; }</style><div class="x">x</div>',
                encoding="utf-8",
            )
            with mock.patch("html2uxml.cli.download_google_fonts") as mocked_fonts:
                with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                    rc = cli_main([
                        str(source),
                        "-o", str(base / "out"),
                        "--no-download-fonts",
                        "-q",
                    ])
            self.assertEqual(0, rc)
            mocked_fonts.assert_not_called()
            self.assertIn(
                '--odd-font-family: "Inter"',
                (base / "out" / "UI" / "fonted.uss").read_text(encoding="utf-8"),
            )

    def test_url_render_auto_triggers_when_selector_missing_from_shell(self):
        html = (
            '<html><body><div id="root"></div>'
            '<script>const id = "screen-bracket-event-select-v1b";</script>'
            "</body></html>"
        )
        self.assertFalse(_selector_matches_html(html, "#screen-bracket-event-select-v1b"))
        self.assertTrue(
            _should_render_url_html(html, "#screen-bracket-event-select-v1b", "auto")
        )

    def test_url_render_auto_skips_when_selector_exists_in_static_html(self):
        html = '<html><body><section id="screen"></section></body></html>'
        self.assertTrue(_selector_matches_html(html, "#screen"))
        self.assertFalse(_should_render_url_html(html, "#screen", "auto"))

    def test_sibling_combinator_matching(self):
        r = convert(
            '<style>.a + .b { color: red; }</style>'
            '<div class="a"></div><div class="b"></div>'
        )
        # USS does not support sibling combinators; matching declarations are
        # hoisted to a generated class on the statically matched element.
        self.assertIn(".h2u-1", r.uss)
        self.assertIn("color: red", r.uss)
        self.assertNotIn(".a + .b", r.uss)

    def test_before_pseudo_synthesizes_label(self):
        r = convert(
            '<style>.tag::before { content: "★ "; color: gold; }</style>'
            '<div class="tag"><span>name</span></div>'
        )
        # Synthetic Label appears as the first child.
        self.assertIn('text="★ "', r.uxml)
        self.assertIn("color: gold", r.uss)

    def test_li_inside_ul_gets_bullet(self):
        r = convert("<ul><li>one</li><li>two</li></ul>")
        self.assertIn('text="•"', r.uxml)

    def test_li_inside_ol_gets_number(self):
        r = convert("<ol><li>a</li><li>b</li></ol>")
        self.assertIn('text="1."', r.uxml)
        self.assertIn('text="2."', r.uxml)

    # ---- subtree selection + pruning -------------------------------------

    def test_select_extracts_subtree(self):
        html = (
            '<div class="outer"><span>outside</span></div>'
            '<div id="card"><span>inside</span></div>'
            '<div class="other"><span>also outside</span></div>'
        )
        r = convert(html, select="#card")
        self.assertIn("inside", r.uxml)
        self.assertNotIn("outside", r.uxml)
        self.assertNotIn("also outside", r.uxml)

    def test_select_prunes_unused_css_rules(self):
        html = (
            "<style>"
            ".kept { color: red; }"
            ".unused { color: blue; }"
            ".outer .kept { padding: 4px; }"
            "</style>"
            '<div class="outer"><span class="kept">x</span></div>'
            '<div class="elsewhere"><span class="unused">y</span></div>'
        )
        r = convert(html, select=".outer")
        self.assertIn(".kept", r.uss)
        self.assertIn(".outer .kept", r.uss)
        self.assertNotIn(".unused", r.uss)

    def test_select_no_match_warns(self):
        r = convert("<div></div>", select=".missing")
        self.assertTrue(any("matched no element" in w for w in r.warnings))

    def test_full_doc_prunes_dead_rules(self):
        # No element matches .ghost, so the rule should not appear in USS.
        r = convert(
            "<style>.alive { color: red; } .ghost { color: blue; }</style>"
            '<div class="alive"></div>'
        )
        self.assertIn(".alive", r.uss)
        self.assertNotIn(".ghost", r.uss)


if __name__ == "__main__":
    unittest.main()
