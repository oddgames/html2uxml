using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ODDGames.Html2Uxml
{
    public readonly struct GradientStop
    {
        public readonly Color Color;
        public readonly float Position; // 0..1

        public GradientStop(Color color, float position)
        {
            Color = color;
            Position = position;
        }
    }

    public sealed class LinearGradient
    {
        public float AngleDegrees;          // CSS-style: 0deg => to top, 90deg => to right
        public List<GradientStop> Stops = new List<GradientStop>();

        public bool IsValid => Stops != null && Stops.Count >= 2;
    }

    public sealed class RadialGradient
    {
        public Vector2 Center = new Vector2(0.5f, 0.5f);
        public bool IsCircle;
        public bool HasExplicitRadius;
        public Vector2 Radius = Vector2.one;
        public bool RadiusXIsPercent = true;
        public bool RadiusYIsPercent = true;
        public List<GradientStop> Stops = new List<GradientStop>();

        public bool IsValid => Stops != null && Stops.Count >= 2;
    }

    public static class GradientParser
    {
        // Parses a CSS linear-gradient(...) declaration. Returns null on failure.
        // Supported forms:
        //   linear-gradient(<angle>deg, <color> [<pos>], <color> [<pos>], ...)
        //   linear-gradient(to <side>, <color>, <color>)
        //   linear-gradient(<color>, <color>)        // implicit 180deg
        public static LinearGradient ParseLinear(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var open = s.IndexOf('(');
            var close = s.LastIndexOf(')');
            if (open < 0 || close <= open) return null;

            var fn = s.Substring(0, open).Trim().ToLowerInvariant();
            if (!fn.EndsWith("linear-gradient")) return null;

            var body = s.Substring(open + 1, close - open - 1);
            var args = SplitTopLevel(body);
            if (args.Count < 2) return null;

            var grad = new LinearGradient { AngleDegrees = 180f };
            int firstStopIdx = 0;
            var head = args[0].Trim().ToLowerInvariant();
            if (head.EndsWith("deg"))
            {
                if (float.TryParse(head.Substring(0, head.Length - 3),
                                   NumberStyles.Float, CultureInfo.InvariantCulture,
                                   out var deg))
                {
                    grad.AngleDegrees = deg;
                    firstStopIdx = 1;
                }
            }
            else if (head.StartsWith("to "))
            {
                grad.AngleDegrees = AngleFromKeyword(head.Substring(3).Trim());
                firstStopIdx = 1;
            }

            int n = args.Count - firstStopIdx;
            for (int i = 0; i < n; i++)
            {
                if (!ParseStop(args[firstStopIdx + i], i / Mathf.Max(1f, n - 1f), out var stop))
                    return null;
                grad.Stops.Add(stop);
            }
            return grad.IsValid ? grad : null;
        }

        // Parses common CSS radial-gradient(...) forms used by generated UI:
        //   radial-gradient(circle at 35% 35%, <color> <pos>, ...)
        //   radial-gradient(ellipse at 50% 50%, <color>, ...)
        //   radial-gradient(<color>, <color>)
        public static RadialGradient ParseRadial(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var open = s.IndexOf('(');
            var close = s.LastIndexOf(')');
            if (open < 0 || close <= open) return null;

            var fn = s.Substring(0, open).Trim().ToLowerInvariant();
            if (!fn.EndsWith("radial-gradient")) return null;

            var body = s.Substring(open + 1, close - open - 1);
            var args = SplitTopLevel(body);
            if (args.Count < 2) return null;

            var grad = new RadialGradient();
            int firstStopIdx = 0;
            var head = args[0].Trim().ToLowerInvariant();
            if (head.StartsWith("at "))
            {
                grad.Center = ParsePositionPair(head.Substring(3).Trim());
                firstStopIdx = 1;
            }
            else if (head.StartsWith("circle") || head.StartsWith("ellipse") || head.Contains(" at "))
            {
                var at = head.IndexOf(" at ", StringComparison.Ordinal);
                var shapePart = head;
                if (at >= 0)
                {
                    shapePart = head.Substring(0, at).Trim();
                    grad.Center = ParsePositionPair(head.Substring(at + 4).Trim());
                }
                ParseRadialShape(shapePart, grad);
                firstStopIdx = 1;
            }

            int n = args.Count - firstStopIdx;
            for (int i = 0; i < n; i++)
            {
                if (!ParseStop(args[firstStopIdx + i], i / Mathf.Max(1f, n - 1f), out var stop))
                    return null;
                grad.Stops.Add(stop);
            }
            return grad.IsValid ? grad : null;
        }

        static void ParseRadialShape(string s, RadialGradient grad)
        {
            if (string.IsNullOrWhiteSpace(s))
                return;

            var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return;

            int i = 0;
            if (parts[0] == "circle")
            {
                grad.IsCircle = true;
                i = 1;
            }
            else if (parts[0] == "ellipse")
            {
                grad.IsCircle = false;
                i = 1;
            }

            if (i >= parts.Length)
                return;

            if (IsRadialSizeKeyword(parts[i]))
                return;

            if (!TryParseRadiusComponent(parts[i], out var rx, out var rxPct))
                return;

            if (grad.IsCircle)
            {
                grad.HasExplicitRadius = true;
                grad.Radius = new Vector2(rx, rx);
                grad.RadiusXIsPercent = rxPct;
                grad.RadiusYIsPercent = rxPct;
                return;
            }

            float ry = rx;
            bool ryPct = rxPct;
            if (i + 1 < parts.Length)
            {
                if (TryParseRadiusComponent(parts[i + 1], out var parsedRy, out var parsedRyPct))
                {
                    ry = parsedRy;
                    ryPct = parsedRyPct;
                }
            }

            grad.HasExplicitRadius = true;
            grad.Radius = new Vector2(rx, ry);
            grad.RadiusXIsPercent = rxPct;
            grad.RadiusYIsPercent = ryPct;
        }

        static bool IsRadialSizeKeyword(string s)
        {
            switch (s)
            {
                case "closest-side":
                case "closest-corner":
                case "farthest-side":
                case "farthest-corner":
                    return true;
                default:
                    return false;
            }
        }

        static bool TryParseRadiusComponent(string s, out float value, out bool isPercent)
        {
            value = 0f;
            isPercent = true;
            if (string.IsNullOrWhiteSpace(s))
                return false;
            s = s.Trim().ToLowerInvariant();
            if (s.EndsWith("%"))
            {
                if (!float.TryParse(s.Substring(0, s.Length - 1), NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out var pct))
                    return false;
                value = Mathf.Max(0f, pct / 100f);
                isPercent = true;
                return true;
            }
            if (s.EndsWith("px"))
                s = s.Substring(0, s.Length - 2);
            if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw))
                return false;
            value = Mathf.Max(0f, raw);
            isPercent = false;
            return true;
        }

        static Vector2 ParsePositionPair(string s)
        {
            var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return new Vector2(0.5f, 0.5f);
            if (parts.Length == 1)
                return new Vector2(ParsePositionComponent(parts[0], true), 0.5f);
            return new Vector2(
                ParsePositionComponent(parts[0], true),
                ParsePositionComponent(parts[1], false));
        }

        static float ParsePositionComponent(string s, bool horizontal)
        {
            s = s.Trim().ToLowerInvariant();
            switch (s)
            {
                case "left":   return 0f;
                case "top":    return 0f;
                case "center": return 0.5f;
                case "right":  return 1f;
                case "bottom": return 1f;
            }
            if (s.EndsWith("%") &&
                float.TryParse(s.Substring(0, s.Length - 1), NumberStyles.Float,
                               CultureInfo.InvariantCulture, out var pct))
                return Mathf.Clamp01(pct / 100f);
            if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw))
                return Mathf.Clamp01(raw);
            return 0.5f;
        }

        static float AngleFromKeyword(string s)
        {
            switch (s)
            {
                case "top":          return 0f;
                case "right":        return 90f;
                case "bottom":       return 180f;
                case "left":         return 270f;
                case "top right":    case "right top":    return 45f;
                case "bottom right": case "right bottom": return 135f;
                case "bottom left":  case "left bottom":  return 225f;
                case "top left":     case "left top":     return 315f;
            }
            return 180f;
        }

        static bool ParseStop(string token, float fallbackPos, out GradientStop stop)
        {
            stop = default;
            token = token.Trim();
            // The position (if any) is the last whitespace-separated chunk that
            // ends with %, px, or is a bare number, AS LONG AS the rest still
            // parses as a color (so we don't strip a number from rgb()).
            var space = token.LastIndexOf(' ');
            string colorPart = token;
            float pos = fallbackPos;
            if (space > 0 && token[token.Length - 1] != ')')
            {
                var posPart = token.Substring(space + 1);
                if (TryParsePosition(posPart, out var parsed))
                {
                    colorPart = token.Substring(0, space).Trim();
                    pos = parsed;
                }
            }
            if (!ColorParser.TryParse(colorPart, out var color)) return false;
            stop = new GradientStop(color, Mathf.Clamp01(pos));
            return true;
        }

        static bool TryParsePosition(string s, out float value)
        {
            value = 0f;
            if (string.IsNullOrEmpty(s)) return false;
            string num = s;
            if (s.EndsWith("%"))     num = s.Substring(0, s.Length - 1);
            else if (s.EndsWith("px"))num = s.Substring(0, s.Length - 2);
            if (!float.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                return false;
            value = s.EndsWith("%") ? f / 100f : f;
            return true;
        }

        // Split on commas that are not inside parentheses.
        static List<string> SplitTopLevel(string s)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ',' && depth == 0)
                {
                    parts.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            if (start <= s.Length) parts.Add(s.Substring(start));
            return parts;
        }
    }

    static class ColorParser
    {
        public static bool TryParse(string s, out Color c)
        {
            c = Color.white;
            if (string.IsNullOrWhiteSpace(s)) return false;
            s = s.Trim();
            if (string.Equals(s, "transparent", StringComparison.OrdinalIgnoreCase))
            {
                c = new Color(0f, 0f, 0f, 0f);
                return true;
            }
            if (s.StartsWith("#")) return ColorUtility.TryParseHtmlString(s, out c);
            if (s.StartsWith("rgb"))
            {
                int op = s.IndexOf('(');
                int cp = s.IndexOf(')');
                if (op < 0 || cp <= op) return false;
                var args = s.Substring(op + 1, cp - op - 1).Replace(',', ' ').Split(
                    new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (args.Length < 3) return false;
                if (!float.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var r)) return false;
                if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var g)) return false;
                if (!float.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) return false;
                float a = 1f;
                if (args.Length >= 4 &&
                    !float.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out a))
                    a = 1f;
                c = new Color(r / 255f, g / 255f, b / 255f, a);
                return true;
            }
            return ColorUtility.TryParseHtmlString(s, out c);
        }
    }
}
