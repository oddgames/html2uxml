using System.Collections.Generic;
using System.Globalization;
using ODDGames.Html2Uxml.Editor.Converter.Css;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // Pixel-arithmetic helpers used by gap-baking + z-index reorder. Mirrors:
    //   _flex_row_gap_px, _flex_column_gap_px, _box_px,
    //   _static_gap_decls, _parse_z_index, _format_px (subset)
    public static class LayoutHelpers
    {
        public static double BoxPx(System.Func<string, string> resolved, string prop)
        {
            string v = resolved(prop);
            if (string.IsNullOrEmpty(v) || v.Trim().ToLowerInvariant() == "auto") return 0.0;
            return CssLength.LengthPx(v) ?? 0.0;
        }

        public static double FlexColumnGapPx(System.Func<string, string> resolved)
        {
            string columnGap = resolved("column-gap");
            if (!string.IsNullOrEmpty(columnGap)) return CssLength.LengthPx(columnGap) ?? 0.0;
            string gap = resolved("gap");
            if (string.IsNullOrEmpty(gap)) return 0.0;
            var parts = gap.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
            string value = parts.Length > 1 ? parts[1] : parts[0];
            return CssLength.LengthPx(value) ?? 0.0;
        }

        public static double FlexRowGapPx(System.Func<string, string> resolved)
        {
            string rowGap = resolved("row-gap");
            if (!string.IsNullOrEmpty(rowGap)) return CssLength.LengthPx(rowGap) ?? 0.0;
            string gap = resolved("gap");
            if (string.IsNullOrEmpty(gap)) return 0.0;
            var parts = gap.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
            return CssLength.LengthPx(parts[0]) ?? 0.0;
        }

        // Mirrors _static_gap_decls. visualIndex 0 (first child) returns empty.
        public static List<KeyValuePair<string, string>> StaticGapDecls(
            System.Func<string, string> parentResolved,
            System.Func<string, string> childResolved,
            int visualIndex)
        {
            var outDecls = new List<KeyValuePair<string, string>>();
            if (visualIndex <= 0) return outDecls;
            string flexDir = (parentResolved("flex-direction") ?? "row").Trim().ToLowerInvariant();
            double gap;
            string prop;
            if (flexDir == "column" || flexDir == "column-reverse")
            {
                gap = FlexRowGapPx(parentResolved);
                prop = flexDir == "column-reverse" ? "margin-bottom" : "margin-top";
            }
            else
            {
                gap = FlexColumnGapPx(parentResolved);
                prop = flexDir == "row-reverse" ? "margin-right" : "margin-left";
            }
            if (gap <= 0.0001) return outDecls;
            double existing = BoxPx(childResolved, prop);
            outDecls.Add(new KeyValuePair<string, string>(prop, FormatPx(existing + gap)));
            return outDecls;
        }

        // Mirrors converter._normal_flow_child_decls (subset). Adds
        // `flex-shrink: 0` to children of non-flex/non-grid parents so
        // browser-style block flow doesn't collapse them when the body is
        // a column flex container.
        public static List<KeyValuePair<string, string>> NormalFlowChildDecls(
            HtmlLoader.HtmlNode node,
            HtmlLoader.HtmlNode parent,
            System.Func<string, string> nodeResolved,
            System.Func<string, string> parentResolved)
        {
            if (parent == null || node == null || node.IsText) return new List<KeyValuePair<string, string>>();
            string tag = (parent.Tag ?? "").ToLowerInvariant();
            if (tag == "button" || tag == "a") return new List<KeyValuePair<string, string>>();
            if (nodeResolved("flex-shrink") != null) return new List<KeyValuePair<string, string>>();
            string position = (nodeResolved("position") ?? "static").Trim().ToLowerInvariant();
            if (position == "absolute" || position == "fixed") return new List<KeyValuePair<string, string>>();
            // Only emit when the parent has NO display rule at all (typical
            // for top-level <body> children before any author CSS sets one).
            // The resolver currently doesn't carry inheritance, so checking
            // parentResolved("display") may return null even for flex parents
            // that derived `display:flex` from a class rule. Restrict to the
            // body-direct-child case to avoid over-applying.
            if (tag != "body") return new List<KeyValuePair<string, string>>();
            string parentDisplay = (parentResolved("display") ?? "").Trim().ToLowerInvariant();
            if (parentDisplay == "flex" || parentDisplay == "inline-flex"
                || parentDisplay == "grid" || parentDisplay == "inline-grid")
                return new List<KeyValuePair<string, string>>();
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("flex-shrink", "0"),
            };
        }

        public static int? ParseZIndex(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string v = value.Trim().ToLowerInvariant();
            if (v == "auto" || v == "initial" || v == "inherit") return null;
            return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : (int?)null;
        }

        static string FormatPx(double v)
        {
            if (System.Math.Abs(v) < 0.0001) return "0";
            return v.ToString("g", CultureInfo.InvariantCulture) + "px";
        }
    }
}
