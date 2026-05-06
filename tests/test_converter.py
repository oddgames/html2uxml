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
        # Original class name preserved; the .btn rule emitted verbatim.
        self.assertIn('class="btn"', r.uxml)
        self.assertIn(".btn {", r.uss)
        self.assertIn("color: red", r.uss)
        self.assertIn("padding: 4px", r.uss)
        # No h2u-N rule for an element without inline style overrides.
        self.assertNotIn(".h2u-1", r.uss)

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
        r = convert('<div style="float: left; mask: url(x.png);"></div>')
        self.assertIn("float", r.stats.dropped_props)
        self.assertIn("mask", r.stats.dropped_props)

    def test_clip_path_polygon_bridged(self):
        r = convert(
            '<div style="clip-path: polygon(0% 0%, 100% 0%, 50% 100%);"></div>'
        )
        self.assertIn("--gg-clip-polygon", r.uss)
        self.assertIn("polygon(0% 0%, 100% 0%, 50% 100%)", r.uss)
        self.assertIn("gg:BridgeBox", r.uxml)

    def test_filter_drop_shadow_bridged(self):
        r = convert(
            '<div style="filter: drop-shadow(rgba(0,0,0,0.5) 2px 4px 6px);"></div>'
        )
        self.assertIn("--gg-shadow-offset-x: 2px", r.uss)
        self.assertIn("--gg-shadow-offset-y: 4px", r.uss)
        self.assertIn("--gg-shadow-blur: 6px", r.uss)

    def test_outline_approximated_as_border(self):
        r = convert('<div style="outline: 2px solid red;"></div>')
        self.assertIn("border-top-width: 2px", r.uss)
        self.assertIn("border-top-color: red", r.uss)

    def test_white_space_pre_wrap_to_pre(self):
        r = convert('<div style="white-space: pre-wrap;"></div>')
        self.assertIn("white-space: pre", r.uss)

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

    def test_attribute_selector_hoisted(self):
        r = convert(
            '<style>input[type="text"] { color: red; }</style>'
            '<input type="text" />'
        )
        # Attribute selector won't survive into USS verbatim, but the
        # declarations are hoisted onto the matched element's h2u-N rule.
        self.assertIn(".h2u-1", r.uss)
        self.assertIn("color: red", r.uss)

    def test_sibling_combinator_matching(self):
        r = convert(
            '<style>.a + .b { color: red; }</style>'
            '<div class="a"></div><div class="b"></div>'
        )
        # The original sibling-combinator rule emits verbatim.
        self.assertIn(".a + .b", r.uss)
        self.assertIn("color: red", r.uss)

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


if __name__ == "__main__":
    unittest.main()
