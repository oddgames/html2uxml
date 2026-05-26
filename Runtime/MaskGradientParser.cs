using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ODDGames.Html2Uxml
{
    internal readonly struct MaskStopPosition
    {
        readonly int _mode;
        readonly float _value;

        MaskStopPosition(int mode, float value)
        {
            _mode = mode;
            _value = value;
        }

        public static MaskStopPosition Fraction(float value) => new MaskStopPosition(1, value);
        public static MaskStopPosition Pixels(float value) => new MaskStopPosition(2, value);
        public static MaskStopPosition EndMinusPixels(float value) => new MaskStopPosition(3, value);

        public float Resolve(float axisLength, float fallback)
        {
            axisLength = Mathf.Max(1f, axisLength);
            switch (_mode)
            {
                case 1: return Mathf.Clamp01(_value);
                case 2: return Mathf.Clamp01(_value / axisLength);
                case 3: return Mathf.Clamp01(1f - (_value / axisLength));
            }
            return fallback;
        }
    }

    internal struct MaskGradientStop
    {
        public Color Color;
        public MaskStopPosition Position;
        public bool HasPosition;
    }

    internal sealed class MaskGradient
    {
        public float AngleDegrees = 180f;
        public List<MaskGradientStop> Stops = new List<MaskGradientStop>();

        public bool IsAxisAligned
        {
            get
            {
                var angle = Mathf.RoundToInt((AngleDegrees % 360f + 360f) % 360f);
                return angle == 0 || angle == 90 || angle == 180 || angle == 270;
            }
        }

        public float SampleAlpha(float t, float axisLength)
        {
            if (Stops.Count == 0) return 1f;
            if (Stops.Count == 1) return Stops[0].Color.a;

            t = Mathf.Clamp01(t);
            var positions = new float[Stops.Count];
            for (int i = 0; i < Stops.Count; i++)
            {
                float fallback = i / Mathf.Max(1f, Stops.Count - 1f);
                positions[i] = Stops[i].HasPosition
                    ? Stops[i].Position.Resolve(axisLength, fallback)
                    : fallback;
                if (i > 0 && positions[i] < positions[i - 1])
                    positions[i] = positions[i - 1];
            }

            for (int i = 1; i < Stops.Count; i++)
            {
                if (t <= positions[i])
                {
                    float span = Mathf.Max(0.0001f, positions[i] - positions[i - 1]);
                    float u = (t - positions[i - 1]) / span;
                    return Mathf.Lerp(Stops[i - 1].Color.a, Stops[i].Color.a, u);
                }
            }
            return Stops[Stops.Count - 1].Color.a;
        }
    }

    internal static class MaskGradientParser
    {
        public static MaskGradient Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            int open = value.IndexOf('(');
            int close = value.LastIndexOf(')');
            if (open < 0 || close <= open) return null;

            var fn = value.Substring(0, open).Trim().ToLowerInvariant();
            if (!fn.EndsWith("linear-gradient")) return null;

            var args = SplitTopLevel(value.Substring(open + 1, close - open - 1));
            if (args.Count < 2) return null;

            var gradient = new MaskGradient();
            int firstStop = 0;
            var head = args[0].Trim().ToLowerInvariant();
            if (head.EndsWith("deg"))
            {
                if (!float.TryParse(head.Substring(0, head.Length - 3), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out gradient.AngleDegrees))
                    return null;
                firstStop = 1;
            }
            else if (head.StartsWith("to "))
            {
                gradient.AngleDegrees = AngleFromKeyword(head.Substring(3).Trim());
                firstStop = 1;
            }

            for (int i = firstStop; i < args.Count; i++)
            {
                if (!ParseStop(args[i], out var stop)) return null;
                gradient.Stops.Add(stop);
            }
            return gradient.Stops.Count >= 2 ? gradient : null;
        }

        static bool ParseStop(string token, out MaskGradientStop stop)
        {
            stop = default;
            token = token.Trim();
            if (!SplitColorAndPosition(token, out var colorPart, out var positionPart))
                return false;
            if (!ColorParser.TryParse(colorPart, out var color))
                return false;
            stop.Color = color;
            if (!string.IsNullOrWhiteSpace(positionPart)
                && TryParsePosition(positionPart.Trim(), out var position))
            {
                stop.Position = position;
                stop.HasPosition = true;
            }
            return true;
        }

        static bool SplitColorAndPosition(string token, out string color, out string position)
        {
            color = token;
            position = "";
            if (token.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
            {
                int close = token.IndexOf(')');
                if (close < 0) return false;
                color = token.Substring(0, close + 1).Trim();
                position = token.Substring(close + 1).Trim();
                return true;
            }

            int firstSpace = token.IndexOf(' ');
            if (firstSpace < 0) return true;
            color = token.Substring(0, firstSpace).Trim();
            position = token.Substring(firstSpace + 1).Trim();
            return true;
        }

        static bool TryParsePosition(string value, out MaskStopPosition position)
        {
            position = default;
            value = value.Trim().ToLowerInvariant();
            if (value.StartsWith("calc(100% - ") && value.EndsWith("px)"))
            {
                var inner = value.Substring("calc(100% - ".Length);
                inner = inner.Substring(0, inner.Length - 3).Trim();
                if (float.TryParse(inner, NumberStyles.Float, CultureInfo.InvariantCulture, out var px))
                {
                    position = MaskStopPosition.EndMinusPixels(px);
                    return true;
                }
            }
            if (value.EndsWith("%"))
            {
                if (float.TryParse(value.Substring(0, value.Length - 1), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var pct))
                {
                    position = MaskStopPosition.Fraction(pct / 100f);
                    return true;
                }
            }
            if (value.EndsWith("px"))
                value = value.Substring(0, value.Length - 2);
            if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw))
            {
                position = MaskStopPosition.Pixels(raw);
                return true;
            }
            return false;
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
            return 45f;
        }

        static List<string> SplitTopLevel(string value)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ',' && depth == 0)
                {
                    parts.Add(value.Substring(start, i - start));
                    start = i + 1;
                }
            }
            if (start <= value.Length) parts.Add(value.Substring(start));
            return parts;
        }
    }
}
