using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ODDGames.Html2Uxml.Editor.Converter.Css
{
    // Background-layer + gradient parsing. Mirrors mappings.py:
    //   _map_background_layers, _map_gradient,
    //   _extract_background_pattern_decls, _is_tiled_background_size,
    //   _looks_like_radial_dot_pattern, _split_color_stop_positions,
    //   _is_gradient_stop_position, _small_px_stop
    public static class CssBackground
    {
        static readonly Regex StopPositionRe = new Regex(@"^-?\d*\.?\d+(?:px|%|em|rem)?$");
        static readonly Regex TiledHasDigit  = new Regex(@"\d");
        static readonly Regex UrlOrResourceRe = new Regex(@"url\([^)]+\)|resource\([^)]+\)");
        static readonly Regex AlphaZeroRe    = new Regex(@"rgba?\([^)]*,\s*0(?:\.0+)?\s*\)", RegexOptions.IgnoreCase);

        // Map a multi-layer `background:` / `background-image:` value into the
        // subset Unity + Html2UxmlPanel can draw. Returns null when nothing maps.
        public static List<KeyValuePair<string, string>> MapBackgroundLayers(string value, List<string> warnings)
        {
            var outDecls = new List<KeyValuePair<string, string>>();
            string linear = null;
            string repeatingLinear = null;
            var radials = new List<string>();
            var otherBits = new List<string>();
            var fallbackLayers = new List<string>();

            foreach (var rawLayer in CssText.SplitTopLevelCommas(value))
            {
                string layer = rawLayer;
                var grad = CssText.FindGradient(layer);
                if (grad == null)
                {
                    string trimmed = layer.Trim();
                    if (trimmed.Length > 0) otherBits.Add(trimmed);
                    continue;
                }
                var (start, end, text) = grad.Value;
                string fn = text.Substring(0, text.IndexOf('(')).Trim().ToLowerInvariant();
                if (fn == "linear-gradient")
                {
                    if (linear == null) linear = text;
                    fallbackLayers.Add(text);
                }
                else if (fn == "repeating-linear-gradient")
                {
                    if (repeatingLinear == null) repeatingLinear = text;
                    else warnings.Add("extra repeating-linear-gradient background layers ignored after 1");
                }
                else if (fn == "radial-gradient" || fn == "repeating-radial-gradient")
                {
                    if (radials.Count < 2) radials.Add(text);
                    else warnings.Add("extra radial-gradient background layers ignored after 2");
                    if (fn == "radial-gradient") fallbackLayers.Add(text);
                }
                else
                {
                    warnings.Add((string.IsNullOrEmpty(fn) ? "gradient" : fn) + " not bridged; use linear/radial gradients");
                }
                string rest = (layer.Substring(0, start) + layer.Substring(end)).Trim().Trim(',', ' ');
                if (rest.Length > 0) otherBits.Add(rest);
            }

            if (linear != null)
                outDecls.Add(new KeyValuePair<string, string>("--odd-gradient", CssText.QuoteForUss(linear)));
            if (repeatingLinear != null)
                outDecls.Add(new KeyValuePair<string, string>("--odd-repeating-linear-gradient", CssText.QuoteForUss(repeatingLinear)));
            if (radials.Count > 0)
                outDecls.Add(new KeyValuePair<string, string>("--odd-radial-gradient", CssText.QuoteForUss(radials[0])));
            if (radials.Count > 1)
                outDecls.Add(new KeyValuePair<string, string>("--odd-radial-gradient-2", CssText.QuoteForUss(radials[1])));

            string color = null;
            for (int i = otherBits.Count - 1; i >= 0; i--)
            {
                color = CssColor.ExtractColor(otherBits[i]);
                if (color != null) break;
            }
            if (color == null && fallbackLayers.Count > 0)
                color = CssColor.ExtractColor(fallbackLayers[fallbackLayers.Count - 1]);
            if (color != null)
                outDecls.Add(new KeyValuePair<string, string>("background-color", color));

            foreach (var bit in otherBits)
            {
                var m = UrlOrResourceRe.Match(bit);
                if (m.Success)
                {
                    outDecls.Add(new KeyValuePair<string, string>("background-image", m.Value));
                    break;
                }
            }

            return outDecls.Count > 0 ? outDecls : null;
        }

        // Single-layer gradient mapper (legacy path; map_background_layers covers shorthand).
        public static List<KeyValuePair<string, string>> MapGradient(string value, List<string> warnings)
        {
            string fn = value.Substring(0, value.IndexOf('(')).Trim().ToLowerInvariant();
            switch (fn)
            {
                case "linear-gradient":
                    return new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("--odd-gradient", CssText.QuoteForUss(value)) };
                case "repeating-linear-gradient":
                    return new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("--odd-repeating-linear-gradient", CssText.QuoteForUss(value)) };
                case "radial-gradient":
                case "repeating-radial-gradient":
                    return new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("--odd-radial-gradient", CssText.QuoteForUss(value)) };
            }
            warnings.Add((string.IsNullOrEmpty(fn) ? "gradient" : fn) + " not bridged; use linear/radial gradients");
            return null;
        }

        // ---- Tiled radial dot pattern detection ----

        public static (List<KeyValuePair<string, string>> emit, HashSet<int> skip, Dictionary<int, string> rewrites)
            ExtractBackgroundPatternDecls(IList<KeyValuePair<string, string>> decls, List<string> warnings)
        {
            string sizeValue = null;
            string positionValue = null;
            string repeatValue = null;
            foreach (var kv in decls)
            {
                string low = kv.Key.ToLowerInvariant();
                if (low == "background-size") sizeValue = kv.Value;
                else if (low == "background-position") positionValue = kv.Value;
                else if (low == "background-repeat") repeatValue = kv.Value;
            }
            var emit = new List<KeyValuePair<string, string>>();
            var skip = new HashSet<int>();
            var rewrites = new Dictionary<int, string>();
            if (sizeValue == null || !IsTiledBackgroundSize(sizeValue)) return (emit, skip, rewrites);
            if (repeatValue != null)
            {
                string r = repeatValue.Trim().ToLowerInvariant();
                if (r == "no-repeat" || r == "round" || r == "space") return (emit, skip, rewrites);
            }

            bool emitted = false;
            for (int idx = 0; idx < decls.Count; idx++)
            {
                var kv = decls[idx];
                string p = kv.Key.ToLowerInvariant();
                if (p != "background-image" && p != "background") continue;
                var layers = CssText.SplitTopLevelCommas(kv.Value);
                if (layers.Count == 0) continue;

                var keptLayers = new List<string>();
                foreach (var rawLayer in layers)
                {
                    string layer = rawLayer;
                    var grad = CssText.FindGradient(layer);
                    if (grad == null) { keptLayers.Add(layer); continue; }
                    var (start, end, text) = grad.Value;
                    string fn = text.Substring(0, text.IndexOf('(')).Trim().ToLowerInvariant();
                    if (fn == "radial-gradient" && LooksLikeRadialDotPattern(text))
                    {
                        if (!emitted)
                        {
                            emit.Add(new KeyValuePair<string, string>("--odd-tiled-radial-gradient", CssText.QuoteForUss(text)));
                            emit.Add(new KeyValuePair<string, string>("--odd-background-pattern-size", CssText.QuoteForUss(sizeValue)));
                            if (positionValue != null)
                                emit.Add(new KeyValuePair<string, string>("--odd-background-pattern-position", CssText.QuoteForUss(positionValue)));
                            warnings.Add("tiled radial-gradient background mapped to Html2UxmlPanel pattern renderer");
                            emitted = true;
                        }
                        string rest = (layer.Substring(0, start) + layer.Substring(end)).Trim().Trim(',', ' ');
                        if (rest.Length > 0) keptLayers.Add(rest);
                    }
                    else { keptLayers.Add(layer); }
                }

                if (keptLayers.Count == layers.Count) continue;
                if (keptLayers.Count > 0) rewrites[idx] = string.Join(", ", keptLayers);
                else skip.Add(idx);
            }
            return (emit, skip, rewrites);
        }

        public static bool IsTiledBackgroundSize(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string v = value.Trim().ToLowerInvariant();
            if (v.Length == 0) return false;
            if (v == "auto" || v == "cover" || v == "contain" || v == "initial" || v == "inherit" || v == "unset") return false;
            if (!TiledHasDigit.IsMatch(v)) return false;
            return v.Contains("px") || v.Contains("%") || v.Contains("em") || v.Contains("rem");
        }

        public static bool LooksLikeRadialDotPattern(string value)
        {
            string body = CssText.ExtractGradientBody(value);
            if (body == null) return false;
            var parts = CssText.SplitTopLevelCommas(body);
            if (parts.Count < 2) return false;
            var (firstColor, firstPositions) = SplitColorStopPositions(parts[0]);
            var (secondColor, secondPositions) = SplitColorStopPositions(parts[1]);
            if (string.IsNullOrEmpty(firstColor) || string.IsNullOrEmpty(secondColor)) return false;
            if (firstPositions.Count == 0 || secondPositions.Count == 0) return false;
            if (!secondColor.ToLowerInvariant().Contains("transparent") && !AlphaZeroRe.IsMatch(secondColor)) return false;
            double? firstPx  = SmallPxStop(firstPositions[firstPositions.Count - 1]);
            double? secondPx = SmallPxStop(secondPositions[secondPositions.Count - 1]);
            return firstPx.HasValue && secondPx.HasValue && System.Math.Abs(firstPx.Value - secondPx.Value) <= 1.0;
        }

        public static (string color, List<string> positions) SplitColorStopPositions(string stop)
        {
            var tokens = CssText.SplitWhitespaceTopLevel((stop ?? "").Trim());
            if (tokens.Count == 0) return ("", new List<string>());
            var colorTokens = new List<string>();
            var positions = new List<string>();
            foreach (var tok in tokens)
            {
                if (positions.Count > 0 || IsGradientStopPosition(tok)) positions.Add(tok);
                else colorTokens.Add(tok);
            }
            return (string.Join(" ", colorTokens).Trim(), positions);
        }

        public static bool IsGradientStopPosition(string token)
            => StopPositionRe.IsMatch((token ?? "").Trim().ToLowerInvariant());

        public static double? SmallPxStop(string token)
        {
            string t = (token ?? "").Trim().ToLowerInvariant();
            double factor = 1.0;
            if (t.EndsWith("px")) t = t.Substring(0, t.Length - 2);
            else if (t.EndsWith("rem")) { factor = 16.0; t = t.Substring(0, t.Length - 3); }
            else if (t.EndsWith("em")) { factor = 16.0; t = t.Substring(0, t.Length - 2); }
            else if (t.EndsWith("%")) return null;
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return null;
            double px = n * factor;
            return (px >= 0.0 && px <= 16.0) ? px : (double?)null;
        }
    }
}
