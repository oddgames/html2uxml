using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ODDGames.Html2Uxml.Editor.Converter.Css
{
    // Content-box → border-box width/height inflation (Yoga has no
    // box-sizing). Plus inert -unity-slice cleanup. Mirrors mappings.py:
    //   _apply_content_box_sizing, _drop_inert_unity_slices,
    //   _inflate_px_dimension, _box_side_px
    public static class CssBoxSizing
    {
        static readonly Regex UrlOrResourceRe = new Regex(@"\b(url|resource)\s*\(", RegexOptions.IgnoreCase);

        public static void ApplyContentBoxSizing(Dictionary<string, string> seen, IList<KeyValuePair<string, string>> normalizedDecls)
        {
            string boxSizing = null;
            foreach (var kv in normalizedDecls)
                if (kv.Key == "box-sizing") boxSizing = kv.Value.Trim().ToLowerInvariant();
            if (boxSizing == "border-box") return;

            double horizontal =
                BoxSidePx(seen, "padding", "left")  + BoxSidePx(seen, "padding", "right") +
                BoxSidePx(seen, "border-width", "left") + BoxSidePx(seen, "border-width", "right");
            double vertical =
                BoxSidePx(seen, "padding", "top")   + BoxSidePx(seen, "padding", "bottom") +
                BoxSidePx(seen, "border-width", "top") + BoxSidePx(seen, "border-width", "bottom");

            if (horizontal > 0) InflatePxDimension(seen, "width", horizontal);
            if (vertical   > 0) InflatePxDimension(seen, "height", vertical);
        }

        public static void DropInertUnitySlices(Dictionary<string, string> seen, IList<KeyValuePair<string, string>> normalizedDecls)
        {
            bool anySlice = false;
            foreach (var prop in seen.Keys) if (prop.StartsWith("-unity-slice-")) { anySlice = true; break; }
            if (!anySlice) return;

            bool hasBorderImageSource = false;
            foreach (var kv in normalizedDecls)
            {
                string p = kv.Key.ToLowerInvariant();
                string v = (kv.Value ?? "").Trim().ToLowerInvariant();
                if (p == "border-image" && UrlOrResourceRe.IsMatch(kv.Value)) { hasBorderImageSource = true; break; }
                if (p == "border-image-source" && v != "" && v != "none" && v != "initial" && v != "unset")
                {
                    if (UrlOrResourceRe.IsMatch(kv.Value)) { hasBorderImageSource = true; break; }
                }
            }
            if (hasBorderImageSource) return;

            seen.Remove("-unity-slice-top");
            seen.Remove("-unity-slice-right");
            seen.Remove("-unity-slice-bottom");
            seen.Remove("-unity-slice-left");
        }

        static void InflatePxDimension(Dictionary<string, string> seen, string prop, double delta)
        {
            seen.TryGetValue(prop, out var raw);
            double? baseV = CssLength.LengthPx(raw);
            if (!baseV.HasValue) return;
            seen[prop] = CssLength.FormatPx(baseV.Value + delta);
        }

        static double BoxSidePx(Dictionary<string, string> seen, string family, string side)
        {
            string longhandProp = family == "border-width" ? $"border-{side}-width" : $"{family}-{side}";
            seen.TryGetValue(longhandProp, out var longhand);
            double? parsed = CssLength.LengthPx(longhand);
            if (parsed.HasValue) return parsed.Value;

            seen.TryGetValue(family, out var shorthand);
            if (string.IsNullOrEmpty(shorthand)) return 0.0;
            var expanded = CssLength.ExpandBox(shorthand);
            if (expanded == null) return CssLength.LengthPx(shorthand) ?? 0.0;
            var (top, right, bottom, left) = expanded.Value;
            string value = side switch
            {
                "top" => top,
                "right" => right,
                "bottom" => bottom,
                "left" => left,
                _ => null,
            };
            return CssLength.LengthPx(value) ?? 0.0;
        }
    }
}
