using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ODDGames.Html2Uxml.Editor.Converter.Css
{
    // Border + border-radius + border-image mapping. Mirrors mappings.py:
    //   _split_border, _map_border_width_property, _scale_border_width,
    //   _map_border_radius, _first_radius_component,
    //   _clamp_oversized_border_radii
    public static class CssBorder
    {
        const double BorderHairlineScale = 0.5;
        static readonly Regex FuncWipe = new Regex(@"(rgba?|hsla?)\s*\([^)]*\)", RegexOptions.IgnoreCase);
        static readonly Regex HexWipe  = new Regex(@"#[0-9a-fA-F]{3,8}\b");
        static readonly Regex BorderWidthRe = new Regex(@"(-?\d*\.?\d+)(px)?", RegexOptions.IgnoreCase);

        public static List<KeyValuePair<string, string>> SplitBorder(string value, string[] sides)
        {
            string width = null;
            string color = CssColor.ExtractColor(value);
            string scrubbed = FuncWipe.Replace(value, " ");
            scrubbed = HexWipe.Replace(scrubbed, " ");
            foreach (var tok in scrubbed.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries))
            {
                if (CssLength.LenRe.IsMatch(tok)) { width = tok; break; }
            }
            var outDecls = new List<KeyValuePair<string, string>>();
            foreach (var side in sides)
            {
                if (width != null) outDecls.Add(new KeyValuePair<string, string>($"border-{side}-width", ScaleBorderWidth(width)));
                if (color != null) outDecls.Add(new KeyValuePair<string, string>($"border-{side}-color", color));
            }
            return outDecls.Count > 0 ? outDecls : null;
        }

        public static List<KeyValuePair<string, string>> MapBorderWidthProperty(string prop, string value)
        {
            if (prop != "border-width")
                return new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>(prop, ScaleBorderWidth(value)) };
            var sides = CssLength.ExpandBox(value);
            if (sides == null)
                return new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("border-width", ScaleBorderWidth(value)) };
            var (top, right, bottom, left) = sides.Value;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("border-top-width",    ScaleBorderWidth(top)),
                new KeyValuePair<string, string>("border-right-width",  ScaleBorderWidth(right)),
                new KeyValuePair<string, string>("border-bottom-width", ScaleBorderWidth(bottom)),
                new KeyValuePair<string, string>("border-left-width",   ScaleBorderWidth(left)),
            };
        }

        public static string ScaleBorderWidth(string value)
        {
            string raw = (value ?? "").Trim();
            if (raw == "0" || raw == "0px" || raw == "0.0px" || raw == "0.00px") return "0";
            var m = BorderWidthRe.Match(raw);
            if (!m.Success || m.Index != 0 || m.Length != raw.Length) return value;
            string unit = m.Groups[2].Success ? m.Groups[2].Value : null;
            double n = double.Parse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (unit == null && n != 0) unit = "px";
            if (unit == null) return "0";
            double width = System.Math.Max(0.0, n * BorderHairlineScale);
            if (width == 0) return "0";
            return width.ToString("g", CultureInfo.InvariantCulture) + "px";
        }

        public static List<KeyValuePair<string, string>> MapBorderRadius(string value, List<string> warnings)
        {
            string horizontal = value.Split(new[] { '/' }, 2)[0].Trim();
            if (value.Contains("/"))
                warnings.Add($"border-radius elliptical values approximated with horizontal radii: {value}");
            var sides = CssLength.ExpandBox(horizontal);
            if (sides == null) return null;
            var (tl, tr, br, bl) = sides.Value;
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("border-top-left-radius",     FirstRadiusComponent(tl, warnings)),
                new KeyValuePair<string, string>("border-top-right-radius",    FirstRadiusComponent(tr, warnings)),
                new KeyValuePair<string, string>("border-bottom-right-radius", FirstRadiusComponent(br, warnings)),
                new KeyValuePair<string, string>("border-bottom-left-radius",  FirstRadiusComponent(bl, warnings)),
            };
        }

        public static string FirstRadiusComponent(string value, List<string> warnings)
        {
            if (string.IsNullOrEmpty(value)) return value;
            var parts = value.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1)
                warnings.Add($"border radius elliptical corner approximated with first radius: {value}");
            return parts.Length > 0 ? parts[0] : value;
        }

        // Match browser pill behavior for large fixed radii: clamp `border-radius:
        // 999px` against the smaller half-extent so it doesn't blow into a lens.
        public static void ClampOversizedBorderRadii(Dictionary<string, string> seen, List<string> warnings)
        {
            seen.TryGetValue("width", out var widthRaw);
            seen.TryGetValue("height", out var heightRaw);
            double? width = CssLength.CssPxValue(widthRaw);
            double? height = CssLength.CssPxValue(heightRaw);
            if (width == null && height == null) return;
            var src = new List<double>();
            if (width.HasValue && width.Value > 0) src.Add(width.Value);
            if (height.HasValue && height.Value > 0) src.Add(height.Value);
            if (src.Count == 0) return;
            double minDim = src[0];
            for (int i = 1; i < src.Count; i++) if (src[i] < minDim) minDim = src[i];
            double maxRadius = minDim * 0.5;
            if (maxRadius <= 0) return;

            string[] radiusProps = {
                "border-top-left-radius","border-top-right-radius",
                "border-bottom-right-radius","border-bottom-left-radius",
            };
            bool clamped = false;
            foreach (var prop in radiusProps)
            {
                if (!seen.TryGetValue(prop, out var raw)) continue;
                double? r = CssLength.CssPxValue(raw);
                if (!r.HasValue || r.Value <= maxRadius) continue;
                seen[prop] = CssLength.FormatPxAlways(maxRadius);
                clamped = true;
            }
            if (clamped)
                warnings.Add($"border-radius clamped to {CssLength.FormatPxAlways(maxRadius)} to match CSS pill sizing");
        }
    }
}
