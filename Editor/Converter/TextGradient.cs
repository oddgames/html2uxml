using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // background-clip: text + linear-gradient → rich-text <gradient="…"> wrap.
    // Mirrors converter.py:
    //   _preprocess_text_gradients, _extract_text_gradient,
    //   _find_text_linear_gradient, _find_first_linear_gradient,
    //   _strip_text_gradient_decls, _remove_linear_gradient,
    //   _parse_text_gradient_for_text, _split_top_level_args,
    //   _parse_gradient_angle, _parse_color_stop, _color_to_rgba,
    //   _color_channel, _sample_gradient, _classify_text_gradient,
    //   _register_text_gradient, _text_gradient_json,
    //   _node_text_gradient_name, _maybe_wrap_text_gradient
    public static class TextGradient
    {
        public sealed class Spec
        {
            public string Mode;
            public double[] Tl, Tr, Bl, Br;
        }

        public sealed class State
        {
            // selector text (raw author CSS) -> registered gradient name
            public Dictionary<string, string> ByAuthorSelector = new Dictionary<string, string>();
            // dedup by spec; (mode + 4 corner colours) → name
            public Dictionary<string, string> Dedup = new Dictionary<string, string>();
            // bundle-relative file -> JSON content
            public Dictionary<string, string> Files = new Dictionary<string, string>();
        }

        // Walk parsed rules, detect background-clip:text + linear-gradient,
        // register a gradient, mutate the rule to drop the bg paint props
        // (text wrap consumes them). Returns map node-selector → gradient name.
        public static void Preprocess(List<CssLoader.CssRule> rules, State state, List<string> warnings)
        {
            if (rules == null) return;
            foreach (var rule in rules)
            {
                var (newDecls, gradientName) = ExtractTextGradient(rule.Decls, state);
                if (gradientName == null) continue;
                state.ByAuthorSelector[rule.SelectorText] = gradientName;
                rule.Decls = newDecls;
            }
        }

        static (List<KeyValuePair<string, string>> decls, string name)
            ExtractTextGradient(List<KeyValuePair<string, string>> decls, State state)
        {
            bool bgClipText = false;
            foreach (var kv in decls)
            {
                string p = kv.Key.ToLowerInvariant();
                if (p == "background-clip" || p == "-webkit-background-clip")
                {
                    if (kv.Value.Trim().ToLowerInvariant() == "text") { bgClipText = true; break; }
                }
            }
            if (!bgClipText) return (decls, null);

            string gradText = FindTextLinearGradient(decls);
            if (gradText == null) return (decls, null);

            var parsed = ParseTextGradient(gradText);
            if (parsed == null) return (decls, null);

            string name = Register(state, parsed.Value.angle, parsed.Value.stops);
            return (StripTextGradientDecls(decls), name);
        }

        static string FindTextLinearGradient(IList<KeyValuePair<string, string>> decls)
        {
            foreach (var kv in decls)
            {
                string p = kv.Key.ToLowerInvariant();
                if (p != "background" && p != "background-image") continue;
                string g = FindFirstLinearGradient(kv.Value);
                if (g != null) return g;
            }
            return null;
        }

        static string FindFirstLinearGradient(string value)
        {
            string s = (value ?? "").Trim();
            string low = s.ToLowerInvariant();
            int idx = low.IndexOf("linear-gradient(");
            if (idx < 0) idx = low.IndexOf("repeating-linear-gradient(");
            if (idx < 0) return null;
            int openParen = s.IndexOf('(', idx);
            if (openParen < 0) return null;
            int depth = 0, j = openParen;
            while (j < s.Length)
            {
                char c = s[j];
                if (c == '(') depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0) return s.Substring(idx, j - idx + 1);
                }
                j++;
            }
            return null;
        }

        static List<KeyValuePair<string, string>> StripTextGradientDecls(List<KeyValuePair<string, string>> decls)
        {
            var outDecls = new List<KeyValuePair<string, string>>();
            foreach (var kv in decls)
            {
                string p = kv.Key.ToLowerInvariant();
                if (p == "background-clip" || p == "-webkit-background-clip") continue;
                if (p == "color")
                {
                    string vlow = kv.Value.Trim().ToLowerInvariant();
                    if (vlow == "transparent" || vlow == "rgba(0, 0, 0, 0)" || vlow == "rgba(0,0,0,0)") continue;
                }
                if (p == "background" || p == "background-image")
                {
                    string replaced = RemoveLinearGradient(kv.Value);
                    if (replaced == null) continue;
                    outDecls.Add(new KeyValuePair<string, string>(kv.Key, replaced));
                    continue;
                }
                outDecls.Add(kv);
            }
            return outDecls;
        }

        static string RemoveLinearGradient(string value)
        {
            string g = FindFirstLinearGradient(value);
            if (g == null) return value;
            string s = value.Replace(g, "").Trim();
            s = Regex.Replace(s, @"\s*,\s*,\s*", ", ").Trim(',', ' ');
            return s.Length == 0 ? null : s;
        }

        static (double angle, List<(double pos, double[] rgba)> stops)? ParseTextGradient(string grad)
        {
            int open = grad.IndexOf('(');
            if (open < 0 || !grad.EndsWith(")")) return null;
            string body = grad.Substring(open + 1, grad.Length - open - 2).Trim();
            var parts = SplitTopLevelArgs(body);
            if (parts.Count == 0) return null;
            double angleDeg = 180.0;
            string first = parts[0].Trim();
            double? angle = ParseGradientAngle(first);
            List<string> stopStrs;
            if (angle.HasValue) { angleDeg = angle.Value; stopStrs = parts.GetRange(1, parts.Count - 1); }
            else stopStrs = parts;
            if (stopStrs.Count < 2) return null;
            var stops = new List<(double pos, double[] rgba)>();
            for (int i = 0; i < stopStrs.Count; i++)
            {
                var (col, pos) = ParseColorStop(stopStrs[i].Trim());
                if (col == null) return null;
                double p = pos ?? (double)i / Math.Max(1, stopStrs.Count - 1);
                stops.Add((p, col));
            }
            stops.Sort((a, b) => a.pos.CompareTo(b.pos));
            return (angleDeg, stops);
        }

        static List<string> SplitTopLevelArgs(string s)
        {
            var outList = new List<string>();
            if (string.IsNullOrEmpty(s)) return outList;
            int depth = 0;
            var buf = new StringBuilder();
            foreach (char c in s)
            {
                if (c == ',' && depth == 0) { outList.Add(buf.ToString()); buf.Clear(); continue; }
                if (c == '(') depth++;
                else if (c == ')') depth--;
                buf.Append(c);
            }
            if (buf.Length > 0) outList.Add(buf.ToString());
            return outList;
        }

        static double? ParseGradientAngle(string token)
        {
            string t = (token ?? "").Trim().ToLowerInvariant();
            if (t.EndsWith("deg"))
                return double.TryParse(t.Substring(0, t.Length - 3).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : (double?)null;
            if (t.EndsWith("turn"))
                return double.TryParse(t.Substring(0, t.Length - 4).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d * 360.0 : (double?)null;
            if (t.StartsWith("to "))
            {
                string rest = Regex.Replace(t.Substring(3).Trim(), @"\s+", " ");
                switch (rest)
                {
                    case "top": return 0.0;
                    case "bottom": return 180.0;
                    case "right": return 90.0;
                    case "left": return 270.0;
                    case "top right": case "right top": return 45.0;
                    case "bottom right": case "right bottom": return 135.0;
                    case "bottom left": case "left bottom": return 225.0;
                    case "top left": case "left top": return 315.0;
                }
            }
            return null;
        }

        static (double[] rgba, double? pos) ParseColorStop(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return (null, null);
            double? pos = null;
            string colorPart = s;
            int sp = s.LastIndexOf(' ');
            if (sp > 0)
            {
                string suffix = s.Substring(sp + 1);
                if (suffix.EndsWith("%"))
                {
                    if (double.TryParse(suffix.Substring(0, suffix.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
                    {
                        pos = p / 100.0;
                        colorPart = s.Substring(0, sp);
                    }
                }
            }
            return (ColorToRgba(colorPart.Trim()), pos);
        }

        static double[] ColorToRgba(string value)
        {
            string v = (value ?? "").Trim().ToLowerInvariant();
            if (v.Length == 0) return null;
            if (v == "transparent") return new[] { 0.0, 0.0, 0.0, 0.0 };
            var hex = Regex.Match(v, @"^#([0-9a-f]{3,8})$");
            if (hex.Success)
            {
                string h = hex.Groups[1].Value;
                if (h.Length == 3) { var sb = new StringBuilder(); foreach (var c in h) { sb.Append(c); sb.Append(c); } sb.Append("ff"); h = sb.ToString(); }
                else if (h.Length == 4) { var sb = new StringBuilder(); foreach (var c in h) { sb.Append(c); sb.Append(c); } h = sb.ToString(); }
                else if (h.Length == 6) h = h + "ff";
                else if (h.Length != 8) return null;
                return new[]
                {
                    Convert.ToInt32(h.Substring(0, 2), 16) / 255.0,
                    Convert.ToInt32(h.Substring(2, 2), 16) / 255.0,
                    Convert.ToInt32(h.Substring(4, 2), 16) / 255.0,
                    Convert.ToInt32(h.Substring(6, 2), 16) / 255.0,
                };
            }
            var fn = Regex.Match(v, @"^rgba?\(\s*([^)]+)\)");
            if (fn.Success)
            {
                var items = new List<string>();
                foreach (var x in Regex.Split(fn.Groups[1].Value, @"[,/]"))
                    if (!string.IsNullOrWhiteSpace(x)) items.Add(x.Trim());
                if (items.Count < 3) return null;
                try
                {
                    double r = ColorChannel(items[0]);
                    double g = ColorChannel(items[1]);
                    double b = ColorChannel(items[2]);
                    double a = items.Count >= 4 ? double.Parse(items[3], NumberStyles.Float, CultureInfo.InvariantCulture) : 1.0;
                    return new[] { r, g, b, a };
                }
                catch (FormatException) { return null; }
            }
            switch (v)
            {
                case "white":  return new[] { 1.0, 1.0, 1.0, 1.0 };
                case "black":  return new[] { 0.0, 0.0, 0.0, 1.0 };
                case "red":    return new[] { 1.0, 0.0, 0.0, 1.0 };
                case "green":  return new[] { 0.0, 0.5, 0.0, 1.0 };
                case "blue":   return new[] { 0.0, 0.0, 1.0, 1.0 };
                case "yellow": return new[] { 1.0, 1.0, 0.0, 1.0 };
            }
            return null;
        }

        static double ColorChannel(string token)
        {
            token = token.Trim();
            if (token.EndsWith("%"))
                return double.Parse(token.Substring(0, token.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture) / 100.0;
            return double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture) / 255.0;
        }

        static double[] SampleGradient(List<(double pos, double[] rgba)> stops, double t)
        {
            if (stops.Count == 0) return new[] { 1.0, 1.0, 1.0, 1.0 };
            t = Math.Max(0.0, Math.Min(1.0, t));
            if (t <= stops[0].pos) return stops[0].rgba;
            if (t >= stops[stops.Count - 1].pos) return stops[stops.Count - 1].rgba;
            for (int i = 0; i < stops.Count - 1; i++)
            {
                var (p0, c0) = stops[i];
                var (p1, c1) = stops[i + 1];
                if (p0 <= t && t <= p1)
                {
                    double span = p1 - p0;
                    double k = span <= 1e-6 ? 0.0 : (t - p0) / span;
                    return new[] { c0[0] + (c1[0] - c0[0]) * k, c0[1] + (c1[1] - c0[1]) * k,
                                   c0[2] + (c1[2] - c0[2]) * k, c0[3] + (c1[3] - c0[3]) * k };
                }
            }
            return stops[stops.Count - 1].rgba;
        }

        static Spec Classify(double angleDeg, List<(double pos, double[] rgba)> stops)
        {
            double a = ((angleDeg % 360.0) + 360.0) % 360.0;
            double[] firstC = stops[0].rgba, lastC = stops[stops.Count - 1].rgba;
            if (Math.Abs(a) < 0.5 || Math.Abs(a - 360.0) < 0.5)
                return new Spec { Mode = "Vertical",   Tl = lastC,  Tr = lastC,  Bl = firstC, Br = firstC };
            if (Math.Abs(a - 180.0) < 0.5)
                return new Spec { Mode = "Vertical",   Tl = firstC, Tr = firstC, Bl = lastC,  Br = lastC };
            if (Math.Abs(a - 90.0) < 0.5)
                return new Spec { Mode = "Horizontal", Tl = firstC, Tr = lastC,  Bl = firstC, Br = lastC };
            if (Math.Abs(a - 270.0) < 0.5)
                return new Spec { Mode = "Horizontal", Tl = lastC,  Tr = firstC, Bl = lastC,  Br = firstC };
            double rad = a * Math.PI / 180.0;
            double dx = Math.Sin(rad), dy = -Math.Cos(rad);
            (string k, double x, double y)[] corners = {
                ("tl", -0.5, -0.5), ("tr", 0.5, -0.5), ("bl", -0.5, 0.5), ("br", 0.5, 0.5),
            };
            double pmin = double.MaxValue, pmax = double.MinValue;
            var projs = new Dictionary<string, double>();
            foreach (var (k, x, y) in corners)
            {
                double pp = dx * x + dy * y;
                projs[k] = pp;
                if (pp < pmin) pmin = pp;
                if (pp > pmax) pmax = pp;
            }
            double span = pmax - pmin;
            double[] At(string k)
            {
                double t = span <= 1e-6 ? 0.5 : (projs[k] - pmin) / span;
                return SampleGradient(stops, t);
            }
            return new Spec { Mode = "FourCornersGradient", Tl = At("tl"), Tr = At("tr"), Bl = At("bl"), Br = At("br") };
        }

        static string Register(State state, double angleDeg, List<(double pos, double[] rgba)> stops)
        {
            var spec = Classify(angleDeg, stops);
            string key = spec.Mode + "|" + Round4(spec.Tl) + "|" + Round4(spec.Tr) + "|" + Round4(spec.Bl) + "|" + Round4(spec.Br);
            if (state.Dedup.TryGetValue(key, out var existing)) return existing;
            string name = "h2u-tg-" + (state.Dedup.Count + 1);
            state.Dedup[key] = name;
            state.Files[name + ".h2utg.json"] = TextGradientJson(name, spec);
            return name;
        }

        static string Round4(double[] c)
            => string.Join(",", new[] {
                Math.Round(c[0], 4).ToString(CultureInfo.InvariantCulture),
                Math.Round(c[1], 4).ToString(CultureInfo.InvariantCulture),
                Math.Round(c[2], 4).ToString(CultureInfo.InvariantCulture),
                Math.Round(c[3], 4).ToString(CultureInfo.InvariantCulture),
            });

        static string TextGradientJson(string name, Spec spec)
        {
            string Corner(string label, double[] c)
                => $"  \"{label}\": {{ \"r\": {F(c[0])}, \"g\": {F(c[1])}, \"b\": {F(c[2])}, \"a\": {F(c[3])} }}";
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"name\": \"").Append(name).Append("\",\n");
            sb.Append("  \"mode\": \"").Append(spec.Mode).Append("\",\n");
            sb.Append(Corner("topLeft",     spec.Tl)).Append(",\n");
            sb.Append(Corner("topRight",    spec.Tr)).Append(",\n");
            sb.Append(Corner("bottomLeft",  spec.Bl)).Append(",\n");
            sb.Append(Corner("bottomRight", spec.Br)).Append("\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        static string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

        public static string MaybeWrapTextGradient(string text, string gradientName)
        {
            if (string.IsNullOrEmpty(gradientName) || string.IsNullOrEmpty(text)) return text;
            if (text.Contains("<gradient=")) return text;
            return "<gradient=\"" + gradientName + "\">" + text + "</gradient>";
        }
    }
}
