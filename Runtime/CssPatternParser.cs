using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ODDGames.Html2Uxml
{
    internal readonly struct RepeatingPatternStop
    {
        public readonly Color Color;
        public readonly float Position;
        public readonly bool IsPercent;

        public RepeatingPatternStop(Color color, float position, bool isPercent)
        {
            Color = color;
            Position = position;
            IsPercent = isPercent;
        }

        public float Resolve(float axisLength)
        {
            return IsPercent ? Mathf.Clamp01(Position) * axisLength : Mathf.Max(0f, Position);
        }
    }

    internal readonly struct ResolvedPatternStop
    {
        public readonly Color Color;
        public readonly float PositionPx;

        public ResolvedPatternStop(Color color, float positionPx)
        {
            Color = color;
            PositionPx = positionPx;
        }
    }

    internal sealed class RepeatingLinearPattern
    {
        public float AngleDegrees = 180f;
        public readonly List<RepeatingPatternStop> Stops = new List<RepeatingPatternStop>();

        public bool IsValid => Stops.Count >= 2;

        public List<ResolvedPatternStop> ResolveStops(float axisLength)
        {
            var resolved = new List<ResolvedPatternStop>(Stops.Count + 2);
            for (int i = 0; i < Stops.Count; i++)
                resolved.Add(new ResolvedPatternStop(Stops[i].Color, Stops[i].Resolve(axisLength)));
            resolved.Sort((a, b) => a.PositionPx.CompareTo(b.PositionPx));
            if (resolved.Count == 0)
                return resolved;
            if (resolved[0].PositionPx > 0f)
                resolved.Insert(0, new ResolvedPatternStop(resolved[0].Color, 0f));
            for (int i = 1; i < resolved.Count; i++)
            {
                if (resolved[i].PositionPx < resolved[i - 1].PositionPx)
                    resolved[i] = new ResolvedPatternStop(resolved[i].Color, resolved[i - 1].PositionPx);
            }
            return resolved;
        }
    }

    internal sealed class TiledRadialPattern
    {
        public Color Color = Color.clear;
        public float RadiusPx = 1f;
        public Vector2 Center = new Vector2(0.5f, 0.5f);
    }

    internal static class CssPatternParser
    {
        public static RepeatingLinearPattern ParseRepeatingLinear(string value)
        {
            value = NormalizeQuotedString(value);
            if (string.IsNullOrWhiteSpace(value))
                return null;

            int open = value.IndexOf('(');
            int close = value.LastIndexOf(')');
            if (open < 0 || close <= open)
                return null;

            var fn = value.Substring(0, open).Trim().ToLowerInvariant();
            if (fn != "repeating-linear-gradient")
                return null;

            var args = SplitTopLevel(value.Substring(open + 1, close - open - 1));
            if (args.Count < 2)
                return null;

            var pattern = new RepeatingLinearPattern();
            int firstStop = 0;
            string head = args[0].Trim().ToLowerInvariant();
            if (head.EndsWith("deg"))
            {
                if (float.TryParse(head.Substring(0, head.Length - 3), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var deg))
                {
                    pattern.AngleDegrees = deg;
                    firstStop = 1;
                }
            }
            else if (head.StartsWith("to "))
            {
                pattern.AngleDegrees = AngleFromKeyword(head.Substring(3).Trim());
                firstStop = 1;
            }

            float fallback = 0f;
            for (int i = firstStop; i < args.Count; i++)
            {
                if (!ParseRepeatingStop(args[i], fallback, pattern.Stops, out fallback))
                    return null;
            }
            return pattern.IsValid ? pattern : null;
        }

        public static TiledRadialPattern ParseTiledRadial(string value)
        {
            value = NormalizeQuotedString(value);
            if (string.IsNullOrWhiteSpace(value))
                return null;

            int open = value.IndexOf('(');
            int close = value.LastIndexOf(')');
            if (open < 0 || close <= open)
                return null;

            var fn = value.Substring(0, open).Trim().ToLowerInvariant();
            if (fn != "radial-gradient")
                return null;

            var args = SplitTopLevel(value.Substring(open + 1, close - open - 1));
            if (args.Count < 2)
                return null;

            int firstStop = 0;
            Vector2 center = new Vector2(0.5f, 0.5f);
            string head = args[0].Trim().ToLowerInvariant();
            if (head.StartsWith("at "))
            {
                center = ParsePosition(head.Substring(3).Trim(), center);
                firstStop = 1;
            }
            else if (head.Contains(" at "))
            {
                int at = head.IndexOf(" at ", StringComparison.Ordinal);
                center = ParsePosition(head.Substring(at + 4).Trim(), center);
                firstStop = 1;
            }

            if (firstStop >= args.Count)
                return null;

            if (!SplitColorAndPositions(args[firstStop], out var colorText, out var positions))
                return null;
            if (!ColorParser.TryParse(colorText, out var color))
                return null;
            float radius = 1f;
            if (positions.Count > 0)
                radius = ParseLengthPx(positions[positions.Count - 1], 1f);

            return new TiledRadialPattern
            {
                Color = color,
                RadiusPx = Mathf.Max(0.25f, radius),
                Center = center
            };
        }

        public static Vector2 ParseSize(string value, Vector2 fallback)
        {
            value = NormalizeQuotedString(value);
            var parts = SplitWhitespaceTopLevel(value);
            if (parts.Count == 0)
                return fallback;
            float x = ParseLengthPx(parts[0], fallback.x);
            float y = parts.Count > 1 ? ParseLengthPx(parts[1], fallback.y) : x;
            return new Vector2(Mathf.Max(1f, x), Mathf.Max(1f, y));
        }

        public static Vector2 ParsePosition(string value)
        {
            return ParsePosition(NormalizeQuotedString(value), Vector2.zero);
        }

        static Vector2 ParsePosition(string value, Vector2 fallback)
        {
            var parts = SplitWhitespaceTopLevel(value);
            if (parts.Count == 0)
                return fallback;
            float x = ParseLengthPx(parts[0], fallback.x);
            float y = parts.Count > 1 ? ParseLengthPx(parts[1], fallback.y) : fallback.y;
            return new Vector2(x, y);
        }

        static bool ParseRepeatingStop(
            string token,
            float fallback,
            List<RepeatingPatternStop> stops,
            out float nextFallback)
        {
            nextFallback = fallback;
            if (!SplitColorAndPositions(token, out var colorText, out var positions))
                return false;
            if (!ColorParser.TryParse(colorText, out var color))
                return false;

            if (positions.Count == 0)
            {
                stops.Add(new RepeatingPatternStop(color, fallback, false));
                nextFallback = fallback;
                return true;
            }

            for (int i = 0; i < positions.Count; i++)
            {
                if (!TryParsePatternPosition(positions[i], out var value, out var isPercent))
                    return false;
                stops.Add(new RepeatingPatternStop(color, value, isPercent));
                if (!isPercent)
                    nextFallback = value;
            }
            return true;
        }

        static bool SplitColorAndPositions(string token, out string color, out List<string> positions)
        {
            color = "";
            positions = new List<string>();
            var tokens = SplitWhitespaceTopLevel(token.Trim());
            if (tokens.Count == 0)
                return false;

            var colorTokens = new List<string>();
            bool sawPosition = false;
            for (int i = 0; i < tokens.Count; i++)
            {
                if (sawPosition || IsPositionToken(tokens[i]))
                {
                    sawPosition = true;
                    positions.Add(tokens[i]);
                }
                else
                {
                    colorTokens.Add(tokens[i]);
                }
            }
            color = string.Join(" ", colorTokens);
            return !string.IsNullOrWhiteSpace(color);
        }

        static bool TryParsePatternPosition(string value, out float position, out bool isPercent)
        {
            position = 0f;
            isPercent = false;
            value = value.Trim().ToLowerInvariant();
            if (value.EndsWith("%"))
            {
                if (!float.TryParse(value.Substring(0, value.Length - 1), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var pct))
                    return false;
                position = pct / 100f;
                isPercent = true;
                return true;
            }
            position = ParseLengthPx(value, 0f);
            return true;
        }

        static bool IsPositionToken(string value)
        {
            return System.Text.RegularExpressions.Regex.IsMatch(
                value.Trim(),
                @"^-?\d*\.?\d+(px|%|em|rem)?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        static float ParseLengthPx(string value, float fallback)
        {
            value = value.Trim().ToLowerInvariant();
            float multiplier = 1f;
            if (value.EndsWith("rem"))
            {
                multiplier = 16f;
                value = value.Substring(0, value.Length - 3);
            }
            else if (value.EndsWith("em"))
            {
                multiplier = 16f;
                value = value.Substring(0, value.Length - 2);
            }
            else if (value.EndsWith("px"))
            {
                value = value.Substring(0, value.Length - 2);
            }
            else if (value.EndsWith("%"))
            {
                return fallback;
            }
            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw)
                ? raw * multiplier
                : fallback;
        }

        static List<string> SplitWhitespaceTopLevel(string value)
        {
            var parts = new List<string>();
            int depth = 0;
            char quote = '\0';
            int start = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (quote != '\0')
                {
                    if (c == quote)
                        quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    quote = c;
                    continue;
                }
                if (c == '(')
                    depth++;
                else if (c == ')')
                    depth = Mathf.Max(0, depth - 1);
                else if (char.IsWhiteSpace(c) && depth == 0)
                {
                    if (i > start)
                        parts.Add(value.Substring(start, i - start));
                    start = i + 1;
                }
            }
            if (start < value.Length)
                parts.Add(value.Substring(start));
            return parts;
        }

        static List<string> SplitTopLevel(string value)
        {
            var parts = new List<string>();
            int depth = 0;
            char quote = '\0';
            int start = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (quote != '\0')
                {
                    if (c == quote)
                        quote = '\0';
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    quote = c;
                    continue;
                }
                if (c == '(')
                    depth++;
                else if (c == ')')
                    depth = Mathf.Max(0, depth - 1);
                else if (c == ',' && depth == 0)
                {
                    parts.Add(value.Substring(start, i - start));
                    start = i + 1;
                }
            }
            if (start <= value.Length)
                parts.Add(value.Substring(start));
            return parts;
        }

        static string NormalizeQuotedString(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            value = value.Trim();
            if (value.Length >= 2
                && ((value[0] == '"' && value[value.Length - 1] == '"')
                    || (value[0] == '\'' && value[value.Length - 1] == '\'')))
                return value.Substring(1, value.Length - 2);
            return value;
        }

        static float AngleFromKeyword(string value)
        {
            switch (value)
            {
                case "top": return 0f;
                case "right": return 90f;
                case "bottom": return 180f;
                case "left": return 270f;
            }
            return 180f;
        }
    }
}
