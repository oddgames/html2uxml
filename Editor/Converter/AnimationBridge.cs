using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ODDGames.Html2Uxml.Editor.Converter.Css;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // CSS @keyframes → Html2UxmlPanel `--odd-animation-*` bridge. Mirrors:
    //   _ANIMATION_PROPS, _ANIMATION_TIMING_KEYWORDS, …,
    //   _extract_animation_keyframes, _find_matching_brace,
    //   _parse_keyframe_body, _parse_keyframe_offset,
    //   _animation_custom_decls, _animation_spec_from_pairs,
    //   _first_animation_layer, _parse_animation_shorthand,
    //   _split_animation_tokens, _looks_like_duration, _looks_like_number,
    //   _duration_to_ms, _encode_animation_keyframes
    public static class AnimationBridge
    {
        public sealed class KeyframeFrame
        {
            public double Offset;
            public List<KeyValuePair<string, string>> Decls;
        }

        public sealed class KeyframeBlock
        {
            public string Name;
            public List<KeyframeFrame> Frames;
        }

        static readonly HashSet<string> AnimationProps = new HashSet<string>
        {
            "opacity","translate","rotate","scale","color","background-color",
        };

        static readonly HashSet<string> TimingKeywords = new HashSet<string>
        {
            "linear","ease","ease-in","ease-out","ease-in-out",
            "ease-in-sine","ease-out-sine","ease-in-out-sine",
            "ease-in-quad","ease-out-quad","ease-in-out-quad",
            "ease-in-cubic","ease-out-cubic","ease-in-out-cubic",
        };

        static readonly HashSet<string> DirectionKeywords = new HashSet<string>
        {
            "normal","reverse","alternate","alternate-reverse",
        };

        static readonly HashSet<string> FillKeywords = new HashSet<string>
        {
            "none","forwards","backwards","both",
        };

        static readonly HashSet<string> PlayStateKeywords = new HashSet<string>
        {
            "running","paused",
        };

        static readonly Regex KeyframesRe =
            new Regex(@"@(?:-[A-Za-z]+-)?keyframes\s+([A-Za-z_][\w-]*)\s*\{", RegexOptions.IgnoreCase);
        static readonly Regex DurationRe =
            new Regex(@"^-?\d*\.?\d+(ms|s)$", RegexOptions.IgnoreCase);
        static readonly Regex NumberRe = new Regex(@"^\d*\.?\d+$");

        // ---- @keyframes extraction ----

        public static Dictionary<string, KeyframeBlock> ExtractKeyframes(string cssText)
        {
            var keyframes = new Dictionary<string, KeyframeBlock>();
            if (string.IsNullOrEmpty(cssText)) return keyframes;
            int pos = 0;
            while (true)
            {
                var m = KeyframesRe.Match(cssText, pos);
                if (!m.Success) break;
                int bodyStart = m.Index + m.Length - 1;
                int bodyEnd = FindMatchingBrace(cssText, bodyStart);
                if (bodyEnd < 0) break;
                string name = m.Groups[1].Value;
                var frames = ParseKeyframeBody(cssText.Substring(bodyStart + 1, bodyEnd - bodyStart - 1));
                if (frames.Count > 0)
                    keyframes[name] = new KeyframeBlock { Name = name, Frames = frames };
                pos = bodyEnd + 1;
            }
            return keyframes;
        }

        static int FindMatchingBrace(string source, int openIndex)
        {
            int depth = 0;
            char? quote = null;
            bool escaped = false;
            for (int idx = openIndex; idx < source.Length; idx++)
            {
                char c = source[idx];
                if (quote.HasValue)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == quote.Value) quote = null;
                    continue;
                }
                if (c == '\'' || c == '"') quote = c;
                else if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return idx;
                }
            }
            return -1;
        }

        static List<KeyframeFrame> ParseKeyframeBody(string body)
        {
            var frames = new List<KeyframeFrame>();
            int pos = 0;
            while (pos < body.Length)
            {
                int brace = body.IndexOf('{', pos);
                if (brace < 0) break;
                string selectorText = body.Substring(pos, brace - pos).Trim();
                int end = FindMatchingBrace(body, brace);
                if (end < 0) break;
                var decls = ParseDeclarations(body.Substring(brace + 1, end - brace - 1));
                foreach (var rawSel in SplitTopLevelArgs(selectorText))
                {
                    double? offset = ParseKeyframeOffset(rawSel.Trim());
                    if (offset.HasValue)
                        frames.Add(new KeyframeFrame { Offset = offset.Value, Decls = decls });
                }
                pos = end + 1;
            }
            frames.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            return frames;
        }

        static double? ParseKeyframeOffset(string value)
        {
            string v = (value ?? "").Trim().ToLowerInvariant();
            if (v == "from") return 0.0;
            if (v == "to")   return 1.0;
            if (v.EndsWith("%"))
            {
                if (!double.TryParse(v.Substring(0, v.Length - 1).Trim(),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return null;
                return Math.Max(0.0, Math.Min(1.0, n / 100.0));
            }
            return null;
        }

        // Naive `prop: value;` splitter — keyframe bodies are simple in
        // practice. Mirrors css_parser._parse_declarations on its happy path.
        static List<KeyValuePair<string, string>> ParseDeclarations(string body)
        {
            var outDecls = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(body)) return outDecls;
            foreach (var raw in body.Split(';'))
            {
                int colon = raw.IndexOf(':');
                if (colon <= 0) continue;
                string prop = raw.Substring(0, colon).Trim();
                string value = raw.Substring(colon + 1).Trim();
                if (prop.Length == 0 || value.Length == 0) continue;
                outDecls.Add(new KeyValuePair<string, string>(prop, value));
            }
            return outDecls;
        }

        // Top-level comma split — reuses CssText.SplitTopLevelCommas semantics.
        static List<string> SplitTopLevelArgs(string s) => CssText.SplitTopLevelCommas(s);

        // ---- bridge decl emission ----

        public static List<KeyValuePair<string, string>> AnimationCustomDecls(
            IList<KeyValuePair<string, string>> pairs,
            Dictionary<string, KeyframeBlock> keyframesByName,
            List<string> warnings)
        {
            var spec = AnimationSpecFromPairs(pairs);
            spec.TryGetValue("name", out var nameRaw);
            string name = (nameRaw ?? "").Trim();
            if (string.IsNullOrEmpty(name) || name.ToLowerInvariant() == "none")
                return new List<KeyValuePair<string, string>>();
            if (!keyframesByName.TryGetValue(name, out var keyframes))
            {
                warnings.Add($"animation '{name}' has no matching @keyframes; dropped");
                return new List<KeyValuePair<string, string>>();
            }
            string encoded = EncodeKeyframes(keyframes, warnings);
            if (string.IsNullOrEmpty(encoded))
            {
                warnings.Add($"animation '{name}' has no supported keyframe properties; dropped");
                return new List<KeyValuePair<string, string>>();
            }
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("--odd-animation-name",            UssString(name)),
                new KeyValuePair<string, string>("--odd-animation-duration-ms",     DurationToMs(GetSpec(spec, "duration", "0s")).ToString("g", CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("--odd-animation-delay-ms",        DurationToMs(GetSpec(spec, "delay",    "0s")).ToString("g", CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("--odd-animation-timing",          UssString(GetSpec(spec, "timing", "linear"))),
                new KeyValuePair<string, string>("--odd-animation-iteration-count", UssString(GetSpec(spec, "iteration-count", "1"))),
                new KeyValuePair<string, string>("--odd-animation-direction",       UssString(GetSpec(spec, "direction", "normal"))),
                new KeyValuePair<string, string>("--odd-animation-fill-mode",       UssString(GetSpec(spec, "fill-mode", "none"))),
                new KeyValuePair<string, string>("--odd-animation-play-state",      UssString(GetSpec(spec, "play-state", "running"))),
                new KeyValuePair<string, string>("--odd-animation-keyframes",       UssString(encoded)),
            };
        }

        static string GetSpec(Dictionary<string, string> spec, string key, string fallback)
            => spec.TryGetValue(key, out var v) ? v : fallback;

        public static Dictionary<string, string> AnimationSpecFromPairs(IList<KeyValuePair<string, string>> pairs)
        {
            var spec = new Dictionary<string, string>();
            foreach (var kv in pairs)
            {
                string low = kv.Key.ToLowerInvariant();
                if (low == "animation")
                {
                    foreach (var pair in ParseAnimationShorthand(FirstAnimationLayer(kv.Value)))
                        spec[pair.Key] = pair.Value;
                }
                else if (low.StartsWith("animation-"))
                {
                    string key = low.Substring("animation-".Length);
                    spec[key] = FirstAnimationLayer(kv.Value).Trim();
                }
            }
            return spec;
        }

        static string FirstAnimationLayer(string value)
        {
            var parts = SplitTopLevelArgs(value);
            return parts.Count > 0 ? parts[0] : value;
        }

        public static Dictionary<string, string> ParseAnimationShorthand(string value)
        {
            var spec = new Dictionary<string, string>();
            foreach (var token in SplitAnimationTokens(value))
            {
                string low = token.ToLowerInvariant();
                if (LooksLikeDuration(low))
                {
                    if (!spec.ContainsKey("duration")) spec["duration"] = token;
                    else if (!spec.ContainsKey("delay")) spec["delay"] = token;
                }
                else if (TimingKeywords.Contains(low) || low.StartsWith("cubic-bezier(") || low.StartsWith("steps("))
                    spec["timing"] = token;
                else if (DirectionKeywords.Contains(low)) spec["direction"] = token;
                else if (FillKeywords.Contains(low))     spec["fill-mode"] = token;
                else if (PlayStateKeywords.Contains(low)) spec["play-state"] = token;
                else if (low == "infinite" || LooksLikeNumber(low)) spec["iteration-count"] = token;
                else if (low != "normal" && low != "none") spec["name"] = token;
            }
            return spec;
        }

        static List<string> SplitAnimationTokens(string value)
        {
            var outList = new List<string>();
            if (string.IsNullOrEmpty(value)) return outList;
            var buf = new StringBuilder();
            int depth = 0;
            char? quote = null;
            bool escaped = false;
            foreach (char c in value.Trim())
            {
                if (quote.HasValue)
                {
                    buf.Append(c);
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == quote.Value) quote = null;
                    continue;
                }
                if (c == '\'' || c == '"') { quote = c; buf.Append(c); }
                else if (c == '(') { depth++; buf.Append(c); }
                else if (c == ')') { depth = Math.Max(0, depth - 1); buf.Append(c); }
                else if (char.IsWhiteSpace(c) && depth == 0)
                {
                    if (buf.Length > 0) { outList.Add(buf.ToString()); buf.Clear(); }
                }
                else buf.Append(c);
            }
            if (buf.Length > 0) outList.Add(buf.ToString());
            return outList;
        }

        static bool LooksLikeDuration(string value) => DurationRe.IsMatch((value ?? "").Trim());
        static bool LooksLikeNumber(string value) => NumberRe.IsMatch((value ?? "").Trim());

        public static double DurationToMs(string value)
        {
            string v = (value ?? "").Trim().ToLowerInvariant();
            try
            {
                if (v.EndsWith("ms"))
                    return Math.Max(0.0, double.Parse(v.Substring(0, v.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture));
                if (v.EndsWith("s"))
                    return Math.Max(0.0, double.Parse(v.Substring(0, v.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture) * 1000.0);
                return Math.Max(0.0, double.Parse(v, NumberStyles.Float, CultureInfo.InvariantCulture));
            }
            catch (FormatException) { return 0.0; }
        }

        // Encode keyframes as `offset|prop=urlenc(value)&prop=…;…` matching
        // the runtime panel's --odd-animation-keyframes parser.
        public static string EncodeKeyframes(KeyframeBlock keyframes, List<string> warnings)
        {
            var records = new List<string>();
            var unsupported = new HashSet<string>();
            foreach (var frame in keyframes.Frames)
            {
                var mapped = StyleMapper.MapDeclarations(frame.Decls);
                warnings.AddRange(mapped.Warnings);
                var props = new List<string>();
                foreach (var kv in mapped.Decls)
                {
                    if (AnimationProps.Contains(kv.Key))
                        props.Add(kv.Key + "=" + Uri.EscapeDataString(kv.Value));
                    else if (!kv.Key.StartsWith("--"))
                        unsupported.Add(kv.Key);
                }
                if (props.Count > 0)
                    records.Add(frame.Offset.ToString("g", CultureInfo.InvariantCulture) + "|" + string.Join("&", props));
            }
            if (unsupported.Count > 0)
            {
                var sorted = new List<string>(unsupported);
                sorted.Sort(StringComparer.Ordinal);
                warnings.Add("animation keyframe properties ignored: " + string.Join(", ", sorted));
            }
            return string.Join(";", records);
        }

        static string UssString(string value)
        {
            string inner = (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
            return "\"" + inner + "\"";
        }
    }
}
