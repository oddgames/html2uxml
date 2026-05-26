using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // Per-Label sizing heuristics. Mirrors converter.py:
    //   _compact_nowrap_label_decls,
    //   _compact_small_display_label_decls,
    //   _compact_explicit_line_height_label_decls,
    //   _compact_computed_height_label_decls (subset),
    //   _resolved_font_px, _resolved_line_height_px,
    //   _style_is_italic, _font_weight_is_bold
    //
    // UI Toolkit Labels reserve a taller line box than browser CSS for most
    // imported fonts. Stacked HUD rows visibly drift unless the converter
    // pins the label box to the source line height. The detectors below
    // mirror Python's targeted patterns; each returns extra decls or empty.
    public static class LabelHeuristics
    {
        public sealed class StyleView
        {
            public Func<string, string> Get;
        }

        // Single-line clipped labels (`white-space:nowrap` + overflow/clip
        // OR `text-overflow:ellipsis`) get an explicit font-fit box.
        public static List<KeyValuePair<string, string>> CompactNowrapLabelDecls(StyleView s)
        {
            double? fontPx = ResolvedFontPx(s);
            if (!fontPx.HasValue || fontPx.Value > 24) return Empty();
            if (s.Get("height") != null || s.Get("min-height") != null || s.Get("line-height") != null) return Empty();
            string whiteSpace = (s.Get("white-space") ?? "").ToLowerInvariant();
            string overflow   = (s.Get("overflow") ?? "").ToLowerInvariant();
            string textOver   = (s.Get("text-overflow") ?? "").ToLowerInvariant();
            if (whiteSpace != "nowrap" && textOver != "ellipsis") return Empty();
            if (overflow != "hidden" && overflow != "clip" && textOver != "ellipsis") return Empty();

            int linePx = Math.Max(1, (int)Math.Ceiling(fontPx.Value + 3));
            var decls = BoxDecls(linePx);
            decls.Add(KV("margin-top", "0"));
            decls.Add(KV("margin-bottom", "0"));
            decls.Add(KV("padding", "0"));
            decls.Add(KV("-unity-paragraph-spacing", "0"));
            if (StyleIsItalic(s))
            {
                int guard = Math.Max(1, (int)Math.Ceiling(fontPx.Value * 0.2));
                decls.Add(KV("margin-left",  "-" + guard + "px"));
                decls.Add(KV("padding-left",   guard + "px"));
                decls.Add(KV("padding-right",  guard + "px"));
            }
            if (s.Get("text-align") == null && s.Get("-unity-text-align") == null)
                decls.Add(KV("-unity-text-align", "middle-left"));
            return decls;
        }

        // Small (≤12px) display-styled labels (italic / bold / letter-spacing
        // / text-shadow / uppercase) get a tight line box.
        public static List<KeyValuePair<string, string>> CompactSmallDisplayLabelDecls(StyleView s)
        {
            double? fontPx = ResolvedFontPx(s);
            if (!fontPx.HasValue || fontPx.Value > 12) return Empty();
            if (s.Get("height") != null || s.Get("min-height") != null || s.Get("line-height") != null) return Empty();
            if (s.Get("padding") != null || s.Get("padding-top") != null || s.Get("padding-bottom") != null) return Empty();
            if (!HasDisplayTrait(s)) return Empty();

            int linePx = Math.Max(1, (int)Math.Ceiling(fontPx.Value * 1.25));
            var decls = BoxDecls(linePx);
            decls.Add(KV("padding", "0"));
            decls.Add(KV("-unity-paragraph-spacing", "0"));
            if (s.Get("margin-top") == null && s.Get("margin") == null) decls.Add(KV("margin-top", "0"));
            if (s.Get("margin-bottom") == null && s.Get("margin") == null) decls.Add(KV("margin-bottom", "0"));
            if (s.Get("text-align") == null && s.Get("-unity-text-align") == null)
                decls.Add(KV("-unity-text-align", "middle-left"));
            return decls;
        }

        // line-height on display-styled labels turned into a real line box.
        public static List<KeyValuePair<string, string>> CompactExplicitLineHeightLabelDecls(StyleView s)
        {
            double? fontPx = ResolvedFontPx(s);
            if (!fontPx.HasValue || fontPx.Value > 24) return Empty();
            int? linePx = ResolvedLineHeightPx(s, fontPx.Value);
            if (!linePx.HasValue) return Empty();
            if (s.Get("height") != null || s.Get("min-height") != null) return Empty();
            if (s.Get("padding") != null || s.Get("padding-top") != null || s.Get("padding-bottom") != null) return Empty();
            if (s.Get("border") != null || s.Get("border-top") != null || s.Get("border-bottom") != null) return Empty();
            if (!HasDisplayTrait(s)) return Empty();

            int boxPx = Math.Max(linePx.Value, (int)Math.Ceiling(fontPx.Value + 2));
            var decls = BoxDecls(boxPx);
            decls.Add(KV("padding", "0"));
            decls.Add(KV("-unity-paragraph-spacing", "0"));
            if (s.Get("margin-top") == null && s.Get("margin") == null) decls.Add(KV("margin-top", "0"));
            if (s.Get("margin-bottom") == null && s.Get("margin") == null) decls.Add(KV("margin-bottom", "0"));
            if (StyleIsItalic(s))
            {
                int guard = Math.Max(1, (int)Math.Ceiling(fontPx.Value * 0.12));
                decls.Add(KV("padding-left",  guard + "px"));
                decls.Add(KV("padding-right", guard + "px"));
            }
            if (s.Get("text-align") == null && s.Get("-unity-text-align") == null)
                decls.Add(KV("-unity-text-align", "middle-center"));
            return decls;
        }

        // ---- helpers ----

        public static double? ResolvedFontPx(StyleView s) => CssLengthPx(s.Get("font-size"));

        public static int? ResolvedLineHeightPx(StyleView s, double fontPx)
        {
            string value = s.Get("line-height");
            if (value == null) return null;
            string v = value.Trim().ToLowerInvariant();
            if (v.Length == 0 || v == "normal" || v == "inherit" || v == "initial" || v == "unset") return null;
            if (Regex.IsMatch(v, @"^-?\d*\.?\d+$"))
            {
                if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var mult))
                    return Math.Max(1, (int)Math.Ceiling(fontPx * mult));
                return null;
            }
            double? px = CssLengthPx(v);
            if (px.HasValue) return Math.Max(1, (int)Math.Ceiling(px.Value));
            if (v.EndsWith("%"))
            {
                if (double.TryParse(v.Substring(0, v.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var pc))
                    return Math.Max(1, (int)Math.Ceiling(fontPx * pc / 100.0));
            }
            return null;
        }

        public static bool StyleIsItalic(StyleView s)
        {
            string fontStyle  = (s.Get("font-style") ?? "").ToLowerInvariant();
            string unityStyle = (s.Get("-unity-font-style") ?? "").ToLowerInvariant();
            return fontStyle.Contains("italic") || unityStyle.Contains("italic");
        }

        public static bool FontWeightIsBold(StyleView s)
        {
            string raw = (s.Get("font-weight") ?? "").Trim().ToLowerInvariant();
            if (raw == "bold" || raw == "bolder") return true;
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && n >= 700;
        }

        public static bool HasDisplayTrait(StyleView s)
            => StyleIsItalic(s)
            || s.Get("letter-spacing") != null
            || s.Get("text-shadow") != null
            || FontWeightIsBold(s)
            || (s.Get("text-transform") ?? "").Trim().ToLowerInvariant() == "uppercase";

        public static double? CssLengthPx(string value)
        {
            if (value == null) return null;
            string v = value.Trim().ToLowerInvariant();
            if (v.Length == 0 || v == "auto" || v == "normal" || v == "none") return null;
            var m = Regex.Match(v, @"^(-?\d*\.?\d+)(?:px)?$");
            if (!m.Success) return null;
            return double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : (double?)null;
        }

        static List<KeyValuePair<string, string>> Empty() => new List<KeyValuePair<string, string>>();
        static KeyValuePair<string, string> KV(string k, string v) => new KeyValuePair<string, string>(k, v);
        static List<KeyValuePair<string, string>> BoxDecls(int px)
            => new List<KeyValuePair<string, string>>
            {
                KV("height",     px + "px"),
                KV("min-height", px + "px"),
                KV("max-height", px + "px"),
            };
    }
}
