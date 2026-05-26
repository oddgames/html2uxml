using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ODDGames.Html2Uxml.Editor.Converter.Css
{
    // Color parsing + extraction. Mirrors mappings.py:
    //   _coerce_modern_color, _extract_color, _css_color_to_hex, _css_color_alpha
    //   plus _MODERN_RGB_RE / _HSL_RE
    //
    // Modern slash-syntax `rgb(R G B / A)` and `hsl(H S% L% / A)` are normalised
    // back to the legacy comma form Unity's USS parser accepts.
    public static class CssColor
    {
        static readonly Regex ModernRgbRe = new Regex(
            @"\brgba?\(\s*"
          + @"(\d+(?:\.\d+)?%?)\s+(\d+(?:\.\d+)?%?)\s+(\d+(?:\.\d+)?%?)"
          + @"(?:\s*/\s*(\d+(?:\.\d+)?%?))?"
          + @"\s*\)",
            RegexOptions.IgnoreCase);

        static readonly Regex HslRe = new Regex(
            @"\bhsla?\(\s*"
          + @"([+-]?\d*\.?\d+)(?:deg)?"
          + @"(?:\s*,\s*|\s+)"
          + @"(\d*\.?\d+)%"
          + @"(?:\s*,\s*|\s+)"
          + @"(\d*\.?\d+)%"
          + @"(?:(?:\s*,\s*|\s*/\s*)(\d*\.?\d+%?))?"
          + @"\s*\)",
            RegexOptions.IgnoreCase);

        static readonly Regex ColorFnRe = new Regex(@"(rgba?|hsla?)\s*\([^)]*\)", RegexOptions.IgnoreCase);
        static readonly Regex HexRe = new Regex(@"#[0-9a-fA-F]{3,8}\b");
        static readonly Regex RgbBodyRe = new Regex(@"rgba?\s*\((.*)\)", RegexOptions.IgnoreCase);

        // Used by the box-shadow custom-filter encoder (limited known names).
        public static readonly Dictionary<string, string> FilterNamedColorHex =
            new Dictionary<string, string>
            {
                { "transparent", "#00000000" },
                { "black",       "#000000" },
                { "white",       "#ffffff" },
                { "red",         "#ff0000" },
                { "green",       "#008000" },
                { "blue",        "#0000ff" },
                { "yellow",      "#ffff00" },
                { "gray",        "#808080" },
                { "grey",        "#808080" },
            };

        public static string CoerceModernColor(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            value = ModernRgbRe.Replace(value, m =>
            {
                string r = m.Groups[1].Value, g = m.Groups[2].Value, b = m.Groups[3].Value;
                string a = m.Groups[4].Success ? m.Groups[4].Value : null;
                return a == null
                    ? $"rgb({r}, {g}, {b})"
                    : $"rgba({r}, {g}, {b}, {a})";
            });
            value = HslRe.Replace(value, m =>
            {
                double h = ((double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) % 360 + 360) % 360;
                double s = Math.Max(0.0, Math.Min(100.0, double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))) / 100.0;
                double l = Math.Max(0.0, Math.Min(100.0, double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture))) / 100.0;
                HlsToRgb(h / 360.0, l, s, out double rd, out double gd, out double bd);
                int ri = (int)Math.Round(rd * 255), gi = (int)Math.Round(gd * 255), bi = (int)Math.Round(bd * 255);
                if (!m.Groups[4].Success)
                    return $"rgb({ri}, {gi}, {bi})";
                string alpha = m.Groups[4].Value;
                double a = alpha.EndsWith("%")
                    ? double.Parse(alpha.Substring(0, alpha.Length - 1), CultureInfo.InvariantCulture) / 100.0
                    : double.Parse(alpha, CultureInfo.InvariantCulture);
                return $"rgba({ri}, {gi}, {bi}, {a.ToString("g", CultureInfo.InvariantCulture)})";
            });
            return value;
        }

        // Return the LAST color-like token in a shorthand value, if any.
        public static string ExtractColor(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            var fns = ColorFnRe.Matches(value);
            if (fns.Count > 0) return fns[fns.Count - 1].Value;
            var hexes = HexRe.Matches(value);
            if (hexes.Count > 0) return hexes[hexes.Count - 1].Value;
            var toks = value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            for (int i = toks.Length - 1; i >= 0; i--)
                if (StyleTables.NAMED_COLORS.Contains(toks[i].ToLowerInvariant()))
                    return toks[i];
            return null;
        }

        public static string CssColorToHex(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string text = value.Trim().ToLowerInvariant();
            if (FilterNamedColorHex.TryGetValue(text, out var named)) return named;
            if (text.StartsWith("#"))
            {
                string raw = text.Substring(1);
                if (raw.Length == 3 || raw.Length == 4)
                {
                    var sb = new System.Text.StringBuilder("#");
                    foreach (char c in raw) { sb.Append(c); sb.Append(c); }
                    return sb.ToString();
                }
                if (raw.Length == 6 || raw.Length == 8) return "#" + raw;
                return null;
            }
            var m = RgbBodyRe.Match(text);
            if (!m.Success) return null;
            string body = m.Groups[1].Value.Replace("/", ",");
            var parts = Regex.Split(body, @"[\s,]+");
            var clean = new List<string>();
            foreach (var p in parts) if (!string.IsNullOrWhiteSpace(p)) clean.Add(p.Trim());
            if (clean.Count < 3) return null;
            int[] rgb = new int[3];
            for (int i = 0; i < 3; i++)
            {
                string p = clean[i];
                double channel;
                try
                {
                    channel = p.EndsWith("%")
                        ? Math.Round(double.Parse(p.Substring(0, p.Length - 1), CultureInfo.InvariantCulture) * 2.55)
                        : Math.Round(double.Parse(p, CultureInfo.InvariantCulture));
                }
                catch (FormatException) { return null; }
                rgb[i] = Math.Max(0, Math.Min(255, (int)channel));
            }
            int alpha = 255;
            bool hasExplicitAlpha = clean.Count >= 4;
            if (hasExplicitAlpha)
            {
                string a = clean[3];
                try
                {
                    alpha = a.EndsWith("%")
                        ? (int)Math.Round(double.Parse(a.Substring(0, a.Length - 1), CultureInfo.InvariantCulture) * 2.55)
                        : (int)Math.Round(double.Parse(a, CultureInfo.InvariantCulture) * 255);
                }
                catch (FormatException) { return null; }
                alpha = Math.Max(0, Math.Min(255, alpha));
            }
            string suffix = (alpha < 255 || hasExplicitAlpha) ? alpha.ToString("x2", CultureInfo.InvariantCulture) : "";
            return $"#{rgb[0]:x2}{rgb[1]:x2}{rgb[2]:x2}{suffix}";
        }

        public static double? CssColorAlpha(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string color = value.Trim().ToLowerInvariant();
            if (color == "transparent") return 0.0;
            if (StyleTables.NAMED_COLORS.Contains(color)) return 1.0;
            if (color.StartsWith("#"))
            {
                string hex = color.Substring(1);
                if (hex.Length == 4) return Convert.ToInt32(new string(hex[3], 2), 16) / 255.0;
                if (hex.Length == 8) return Convert.ToInt32(hex.Substring(6, 2), 16) / 255.0;
                return 1.0;
            }
            var m = RgbBodyRe.Match(color);
            if (m.Success)
            {
                var parts = CssText.SplitTopLevelCommas(m.Groups[1].Value);
                if (parts.Count >= 4)
                {
                    string a = parts[3].Trim();
                    try
                    {
                        return a.EndsWith("%")
                            ? double.Parse(a.Substring(0, a.Length - 1), CultureInfo.InvariantCulture) / 100.0
                            : double.Parse(a, CultureInfo.InvariantCulture);
                    }
                    catch (FormatException) { return null; }
                }
                return 1.0;
            }
            return null;
        }

        // Python colorsys.hls_to_rgb. CSS HSL uses h ∈ [0,1].
        static void HlsToRgb(double h, double l, double s, out double r, out double g, out double b)
        {
            if (s == 0) { r = g = b = l; return; }
            double m2 = l <= 0.5 ? l * (1.0 + s) : l + s - l * s;
            double m1 = 2.0 * l - m2;
            r = HueToRgb(m1, m2, h + 1.0 / 3.0);
            g = HueToRgb(m1, m2, h);
            b = HueToRgb(m1, m2, h - 1.0 / 3.0);
        }

        static double HueToRgb(double m1, double m2, double h)
        {
            h = h - Math.Floor(h);
            if (h * 6.0 < 1.0) return m1 + (m2 - m1) * 6.0 * h;
            if (h * 2.0 < 1.0) return m2;
            if (h * 3.0 < 2.0) return m1 + (m2 - m1) * (2.0 / 3.0 - h) * 6.0;
            return m1;
        }
    }
}
