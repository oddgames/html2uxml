"""Smoke tests for the converter. Run with `python -m unittest`."""
from __future__ import annotations

import unittest

from html2uxml import convert


class ConvertBasicsTest(unittest.TestCase):
    def test_div_with_inline_style_emits_visualelement_and_uss_rule(self):
        r = convert(
            '<div style="display: flex; flex-direction: column; '
            'background: rgb(255,0,0); padding: 8px;">hi</div>'
        )
        self.assertIn("ui:VisualElement", r.uxml)
        self.assertIn("display: flex", r.uss)
        self.assertIn("background-color: rgb(255,0,0)", r.uss)
        self.assertIn("padding: 8px", r.uss)

    def test_button_text_attribute(self):
        r = convert("<button>Click</button>")
        self.assertIn('<ui:Button text="Click"', r.uxml)

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
            self.assertIn(f"border-{side}-width: 2px", r.uss)
            self.assertIn(f"border-{side}-color: #f00", r.uss)

    def test_overflow_auto_coerces_to_hidden(self):
        r = convert('<div style="overflow: auto;"></div>')
        self.assertIn("overflow: hidden", r.uss)

    def test_position_fixed_coerces_to_absolute(self):
        r = convert('<div style="position: fixed;"></div>')
        self.assertIn("position: absolute", r.uss)

    def test_cursor_pointer_maps_to_link(self):
        r = convert('<div style="cursor: pointer;"></div>')
        self.assertIn("cursor: link", r.uss)

    def test_transform_translatex_becomes_translate(self):
        r = convert('<div style="transform: translateX(-50%);"></div>')
        self.assertIn("translate: -50% 0", r.uss)

    def test_box_shadow_emits_bridge_props(self):
        r = convert(
            '<div style="box-shadow: rgba(0,0,0,0.5) 0px 4px 10px;"></div>'
        )
        self.assertIn("--gg-shadow-offset-x: 0px", r.uss)
        self.assertIn("--gg-shadow-offset-y: 4px", r.uss)
        self.assertIn("--gg-shadow-blur: 10px", r.uss)
        self.assertIn("--gg-shadow-color: rgba(0,0,0,0.5)", r.uss)
        self.assertIn("gg:BridgeBox", r.uxml)
        self.assertIn('xmlns:gg="HtmlToUxml.Bridge"', r.uxml)

    def test_linear_gradient_bridged_with_balanced_parens(self):
        r = convert(
            '<div style="background: linear-gradient(160deg, '
            'rgb(200, 200, 200) 0%, rgb(42, 42, 42) 100%);"></div>'
        )
        self.assertIn(
            "--gg-gradient: linear-gradient(160deg, "
            "rgb(200, 200, 200) 0%, rgb(42, 42, 42) 100%)",
            r.uss,
        )

    def test_rem_em_coerced_to_px(self):
        r = convert('<div style="font-size: 1.5rem; padding: 0.5em;"></div>')
        self.assertIn("font-size: 24px", r.uss)
        self.assertIn("padding: 8px", r.uss)

    def test_modern_rgb_slash_alpha_normalised(self):
        r = convert('<div style="color: rgb(255 0 0 / 0.5);"></div>')
        self.assertIn("color: rgba(255, 0, 0, 0.5)", r.uss)

    def test_class_selector_in_style_block(self):
        html = (
            "<style>.btn { color: red; padding: 4px; }</style>"
            '<button class="btn">go</button>'
        )
        r = convert(html)
        # Inline class is preserved on the element so the .btn rule applies,
        # but the converter consolidates resolved styles onto a generated class.
        self.assertIn('class="btn ', r.uxml)
        self.assertIn("color: red", r.uss)
        self.assertIn("padding: 4px", r.uss)

    def test_pseudo_classes_pass_through_with_generated_class(self):
        html = (
            "<style>.btn:hover { color: blue; }</style>"
            '<button class="btn">x</button>'
        )
        r = convert(html)
        self.assertIn(":hover", r.uss)
        self.assertIn("color: blue", r.uss)

    def test_nbsp_in_tag_doesnt_break_parsing(self):
        # Sources from docx text extraction often substitute NBSP for spaces.
        html = "<div class=\"a\" style=\"color:red;\">hi</div>"
        r = convert(html)
        self.assertIn("ui:VisualElement", r.uxml)
        self.assertIn("color: red", r.uss)

    def test_dropped_props_recorded_in_stats(self):
        r = convert('<div style="float: left; clip-path: circle(50%);"></div>')
        self.assertIn("float", r.stats.dropped_props)
        self.assertIn("clip-path", r.stats.dropped_props)


if __name__ == "__main__":
    unittest.main()
