using System.Globalization;
using System.Text.RegularExpressions;

namespace ODDGames.Html2Uxml.Editor.Converter.Css
{
    // Length parsing + 1-to-4 box-shorthand expansion. Mirrors the helpers
    // _length_px / _format_px / _css_px_value / _expand_box / _coerce_units
    // from html2uxml/mappings.py.
    public static class CssLength
    {
        // CSS length token (with optional length unit). Used by _split_border etc.
        public static readonly Regex LenRe =
            new Regex(@"^-?\d*\.?\d+(px|em|rem|%|vw|vh|)$", RegexOptions.IgnoreCase);

        static readonly Regex PxLengthRe = new Regex(@"^(-?\d*\.?\d+)(?:px)?$");
        static readonly Regex UnitLenRe = new Regex(@"(-?\d*\.?\d+)(rem|em|vw|vh)\b", RegexOptions.IgnoreCase);

        // Parse `auto`/`none`/`normal` → null; else return the px magnitude
        // (unit-less and px both accepted; em/rem/% return null).
        public static double? LengthPx(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string raw = value.Trim().ToLowerInvariant();
            if (raw.Length == 0 || raw == "auto" || raw == "none" || raw == "normal") return null;
            var m = PxLengthRe.Match(raw);
            if (!m.Success) return null;
            if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                return n;
            return null;
        }

        // Same as LengthPx but rejects fractional / unitless values.
        public static double? CssPxValue(string value)
        {
            if (value == null) return null;
            string text = value.Trim().ToLowerInvariant();
            if (text == "0") return 0.0;
            if (text.EndsWith("px")) text = text.Substring(0, text.Length - 2);
            else if (!Regex.IsMatch(text, @"^-?\d+(?:\.\d+)?$")) return null;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : (double?)null;
        }

        public static string FormatPx(double value)
        {
            if (System.Math.Abs(value) < 0.0001) value = 0.0;
            // Python's f"{value:g}" — general format, trim trailing zeros.
            return value.ToString("g", CultureInfo.InvariantCulture) + (value == 0.0 ? "" : "px");
        }

        // Helper variant matching `_format_px` semantics in border-radius clamp
        // path (always emits unit, even on zero, with the same {value:g} format).
        public static string FormatPxAlways(double value)
        {
            if (System.Math.Abs(value) < 0.0001) value = 0.0;
            return value.ToString("g", CultureInfo.InvariantCulture) + "px";
        }

        // CSS 1-4 token shorthand → (top, right, bottom, left).
        public static (string top, string right, string bottom, string left)? ExpandBox(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            var parts = value.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
            switch (parts.Length)
            {
                case 1: return (parts[0], parts[0], parts[0], parts[0]);
                case 2: return (parts[0], parts[1], parts[0], parts[1]);
                case 3: return (parts[0], parts[1], parts[2], parts[1]);
                case 4: return (parts[0], parts[1], parts[2], parts[3]);
                default: return null;
            }
        }

        // em/rem -> px (16px base), vw/vh -> %.
        public static string CoerceUnits(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return UnitLenRe.Replace(value, m =>
            {
                double n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                string unit = m.Groups[2].Value.ToLowerInvariant();
                if (unit == "em" || unit == "rem")
                    return (n * 16).ToString("g", CultureInfo.InvariantCulture) + "px";
                return n.ToString("g", CultureInfo.InvariantCulture) + "%";
            });
        }

        public static bool Near(double a, double b) => System.Math.Abs(a - b) <= 0.0001;
        public static bool NearZero(double v)        => System.Math.Abs(v)     <= 0.0001;
    }
}
