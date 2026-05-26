using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ODDGames.Html2Uxml.Editor.Converter.Css
{
    // CSS filter / backdrop-filter / mask-image mapping. Mirrors mappings.py:
    //   _map_filter, _map_backdrop_filter, _parse_function_list,
    //   _parse_filter_amount, _NATIVE_FILTER_FUNCS,
    //   _map_mask_image, _linear_mask_filter_call,
    //   _parse_mask_filter_stop, _split_mask_color_position,
    //   _parse_mask_position_fraction, _mask_angle_from_keyword
    public static class CssFilter
    {
        static readonly HashSet<string> NativeFilterFuncs = new HashSet<string>
        {
            "blur","grayscale","invert","opacity","sepia",
            "tint","hue-rotate","contrast","filter",
        };

        static readonly Regex ColorFnRe = new Regex(@"(rgba?|hsla?)\s*\([^)]*\)", RegexOptions.IgnoreCase);
        static readonly Regex HexColorRe = new Regex(@"#[0-9a-fA-F]{3,8}\b");

        public static List<KeyValuePair<string, string>> MapFilter(string value, List<string> warnings)
        {
            var funcs = ParseFunctionList(value);
            if (funcs == null)
            {
                warnings.Add($"filter: {value} -- unsupported filter syntax");
                return null;
            }
            var outDecls = new List<KeyValuePair<string, string>>();
            var nativeParts = new List<string>();
            var unsupported = new List<string>();
            var dropShadowRecords = new List<string>();
            bool usedCustomAssets = false;
            int dropShadowCount = 0;

            foreach (var (name, text, body) in funcs)
            {
                string low = name.ToLowerInvariant();
                if (low == "drop-shadow")
                {
                    var shadow = CssShadow.ParseBoxShadowFirstOuter(body);
                    if (shadow != null)
                    {
                        var (ox, oy, blur, color) = shadow.Value;
                        if (dropShadowCount < 8)
                        {
                            if (CssShadow.CUSTOM_FILTER_BRIDGES_ENABLED)
                            {
                                nativeParts.Add(CssShadow.BoxShadowFilterCall(ox, oy, blur, "0", color));
                                usedCustomAssets = true;
                            }
                            else
                            {
                                dropShadowRecords.Add(CssShadow.ShadowRecord("outset", ox, oy, blur, "0", color));
                            }
                            dropShadowCount++;
                        }
                        else warnings.Add("filter: extra drop-shadow() functions ignored after 8 layers");
                    }
                    else warnings.Add($"filter: drop-shadow({body}) -- complex shadow not bridged");
                }
                else if (NativeFilterFuncs.Contains(low))
                {
                    nativeParts.Add(text);
                }
                else if (low == "brightness" || low == "saturate")
                {
                    double? amount = ParseFilterAmount(body);
                    if (!amount.HasValue || !CssShadow.CUSTOM_FILTER_BRIDGES_ENABLED)
                    {
                        unsupported.Add(name);
                        continue;
                    }
                    string a = amount.Value.ToString("g", CultureInfo.InvariantCulture);
                    nativeParts.Add(low == "brightness"
                        ? $"filter(\"{CssShadow.ODDGamesFilterAsset("ODDGamesColorAdjust")}\" {a} 1)"
                        : $"filter(\"{CssShadow.ODDGamesFilterAsset("ODDGamesColorAdjust")}\" 1 {a})");
                    usedCustomAssets = true;
                }
                else
                {
                    unsupported.Add(name);
                }
            }

            outDecls.AddRange(CssShadow.EmitShadowProps(dropShadowRecords));
            if (nativeParts.Count > 0)
                outDecls.Add(new KeyValuePair<string, string>("filter", string.Join(" ", nativeParts)));
            if (usedCustomAssets)
                warnings.Add("filter uses ODDGames package custom filter assets; install au.com.oddgames.html2uxml before loading the USS");
            if (unsupported.Count > 0)
                warnings.Add("filter functions not supported by Unity USS without custom filter assets, dropped: " + string.Join(", ", unsupported));
            return outDecls.Count > 0 ? outDecls : null;
        }

        public static List<KeyValuePair<string, string>> MapBackdropFilter(string value, List<string> warnings)
        {
            var funcs = ParseFunctionList(value);
            if (funcs == null)
            {
                warnings.Add($"backdrop-filter: {value} -- unsupported filter syntax");
                return null;
            }
            var nativeParts = new List<string>();
            var unsupported = new List<string>();
            bool usedCustomAssets = false;
            foreach (var (name, text, body) in funcs)
            {
                string low = name.ToLowerInvariant();
                if (NativeFilterFuncs.Contains(low)) { nativeParts.Add(text); continue; }
                if (low == "brightness" || low == "saturate")
                {
                    double? amount = ParseFilterAmount(body);
                    if (!amount.HasValue || !CssShadow.CUSTOM_FILTER_BRIDGES_ENABLED) { unsupported.Add(name); continue; }
                    string a = amount.Value.ToString("g", CultureInfo.InvariantCulture);
                    nativeParts.Add(low == "brightness"
                        ? $"filter(\"{CssShadow.ODDGamesFilterAsset("ODDGamesColorAdjust")}\" {a} 1)"
                        : $"filter(\"{CssShadow.ODDGamesFilterAsset("ODDGamesColorAdjust")}\" 1 {a})");
                    usedCustomAssets = true;
                }
                else unsupported.Add(name);
            }
            if (unsupported.Count > 0)
                warnings.Add("backdrop-filter functions not supported by Unity USS without custom filter assets, dropped: " + string.Join(", ", unsupported));
            if (nativeParts.Count == 0) return null;
            if (usedCustomAssets)
                warnings.Add("backdrop-filter uses ODDGames package custom filter assets; install au.com.oddgames.html2uxml before loading the USS");
            warnings.Add("backdrop-filter approximated as filter; Unity blurs the element subtree, not the backdrop");
            return new List<KeyValuePair<string, string>> {
                new KeyValuePair<string, string>("filter", string.Join(" ", nativeParts)),
            };
        }

        public static double? ParseFilterAmount(string value)
        {
            string v = (value ?? "").Trim().ToLowerInvariant();
            if (v.Length == 0) return null;
            try
            {
                if (v.EndsWith("%"))
                    return double.Parse(v.Substring(0, v.Length - 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) / 100.0;
                return double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            catch (System.FormatException) { return null; }
        }

        // Parse `name1(args) name2(args) ...` into a list. Returns null if any
        // function has unbalanced parens or a missing `(`.
        public static List<(string name, string text, string body)> ParseFunctionList(string value)
        {
            var funcs = new List<(string, string, string)>();
            if (value == null) return funcs;
            int i = 0, n = value.Length;
            while (i < n)
            {
                while (i < n && char.IsWhiteSpace(value[i])) i++;
                if (i >= n) break;
                int nameStart = i;
                while (i < n && (char.IsLetter(value[i]) || value[i] == '-')) i++;
                string name = value.Substring(nameStart, i - nameStart);
                while (i < n && char.IsWhiteSpace(value[i])) i++;
                if (name.Length == 0 || i >= n || value[i] != '(') return null;
                i++;
                int bodyStart = i;
                int depth = 1;
                while (i < n && depth > 0)
                {
                    if (value[i] == '(') depth++;
                    else if (value[i] == ')') depth--;
                    i++;
                }
                if (depth != 0) return null;
                string body = value.Substring(bodyStart, i - 1 - bodyStart);
                funcs.Add((name, value.Substring(nameStart, i - nameStart), body));
            }
            return funcs;
        }

        // ---- mask-image ----

        public static List<KeyValuePair<string, string>> MapMaskImage(string prop, string value, List<string> warnings)
        {
            var grad = CssText.FindGradient(value);
            if (grad == null)
            {
                warnings.Add($"{prop}: {value} -- only linear-gradient mask fades are bridged");
                return null;
            }
            string text = grad.Value.text;
            string fn = text.Substring(0, text.IndexOf('(')).Trim().ToLowerInvariant();
            if (fn != "linear-gradient" && fn != "repeating-linear-gradient")
            {
                warnings.Add($"{prop}: {value} -- only linear-gradient mask fades are bridged");
                return null;
            }
            string filterCall = CssShadow.CUSTOM_FILTER_BRIDGES_ENABLED ? LinearMaskFilterCall(text) : null;
            if (filterCall != null)
            {
                warnings.Add($"{prop} uses ODDGames package custom filter asset; install au.com.oddgames.html2uxml before loading the USS");
                return new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("filter", filterCall) };
            }
            warnings.Add($"{prop}: {value} -- complex linear mask uses Html2UxmlPanel fallback");
            return new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("--odd-mask-image", CssText.QuoteForUss(text)) };
        }

        public static string LinearMaskFilterCall(string value)
        {
            string fnName = value.Substring(0, value.IndexOf('(')).Trim();
            string body = CssText.ExtractCallBody(value, fnName);
            if (body == null) return null;
            var parts = CssText.SplitTopLevelCommas(body);
            if (parts.Count < 2) return null;
            double angle = 180.0;
            List<string> stopParts = parts;
            string first = parts[0].Trim().ToLowerInvariant();
            if (first.StartsWith("to ") || first.EndsWith("deg"))
            {
                double? parsed = MaskAngleFromKeyword(first);
                if (!parsed.HasValue) return null;
                angle = parsed.Value;
                stopParts = parts.GetRange(1, parts.Count - 1);
            }
            if (stopParts.Count != 2) return null;
            var start = ParseMaskFilterStop(stopParts[0], 0.0);
            var end = ParseMaskFilterStop(stopParts[1], 1.0);
            if (start == null || end == null) return null;
            var (startAlpha, startPos) = start.Value;
            var (endAlpha, endPos) = end.Value;
            if (!CssLength.NearZero(startPos) || System.Math.Abs(endPos - 1.0) >= 0.0001) return null;
            return CssShadow.CustomFilterCall("ODDGamesLinearMask",
                angle.ToString("g", CultureInfo.InvariantCulture),
                startAlpha.ToString("g", CultureInfo.InvariantCulture),
                endAlpha.ToString("g", CultureInfo.InvariantCulture));
        }

        public static (double alpha, double pos)? ParseMaskFilterStop(string value, double defaultPosition)
        {
            var (color, position) = SplitMaskColorPosition(value);
            if (color == null) return null;
            double? alpha = CssColor.CssColorAlpha(color);
            if (!alpha.HasValue) return null;
            double? pos = ParseMaskPositionFraction(position);
            if (!pos.HasValue && position == null) pos = defaultPosition;
            if (!pos.HasValue) return null;
            return (alpha.Value, pos.Value);
        }

        public static (string color, string position) SplitMaskColorPosition(string value)
        {
            string text = (value ?? "").Trim();
            if (text.Length == 0) return (null, null);
            var func = ColorFnRe.Match(text);
            if (func.Success && func.Index == 0)
            {
                string c = func.Value;
                string rest = text.Substring(func.Index + func.Length).Trim();
                string pos = rest.Length > 0 ? rest.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries)[0] : null;
                return (c, pos);
            }
            var hex = HexColorRe.Match(text);
            if (hex.Success && hex.Index == 0)
            {
                string c = hex.Value;
                string rest = text.Substring(hex.Index + hex.Length).Trim();
                string pos = rest.Length > 0 ? rest.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries)[0] : null;
                return (c, pos);
            }
            var parts = text.Split(new[] { ' ' }, 2);
            string color = parts[0];
            if (!StyleTables.NAMED_COLORS.Contains(color.ToLowerInvariant())) return (null, null);
            string position = null;
            if (parts.Length > 1)
            {
                string rest = parts[1].Trim();
                if (rest.Length > 0)
                    position = rest.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries)[0];
            }
            return (color, position);
        }

        public static double? ParseMaskPositionFraction(string value)
        {
            if (value == null) return null;
            string text = value.Trim().ToLowerInvariant();
            if (text.Length == 0) return null;
            try
            {
                if (text.EndsWith("%"))
                {
                    double v = double.Parse(text.Substring(0, text.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture) / 100.0;
                    return System.Math.Max(0.0, System.Math.Min(1.0, v));
                }
                if (text == "0" || text == "0.0" || text == "1" || text == "1.0")
                    return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
            catch (System.FormatException) { return null; }
            return null;
        }

        public static double? MaskAngleFromKeyword(string value)
        {
            string text = (value ?? "").Trim().ToLowerInvariant();
            if (text.EndsWith("deg"))
            {
                if (double.TryParse(text.Substring(0, text.Length - 3).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    return ((d % 360.0) + 360.0) % 360.0;
                return null;
            }
            if (!text.StartsWith("to ")) return null;
            var words = new HashSet<string>(text.Substring(3).Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries));
            double x = 0, y = 0;
            if (words.Contains("right")) x += 1.0;
            if (words.Contains("left"))  x -= 1.0;
            if (words.Contains("bottom")) y += 1.0;
            if (words.Contains("top"))    y -= 1.0;
            if (x == 0.0 && y == 0.0) return null;
            return ((System.Math.Atan2(x, -y) * 180.0 / System.Math.PI) % 360.0 + 360.0) % 360.0;
        }
    }
}
