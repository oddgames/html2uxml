using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ODDGames.Html2Uxml
{
    // Parses CSS clip-path polygon() into a list of corners that retain their
    // original units (pixel or percent). Values are resolved against the
    // element rect at draw time via Resolve, so authoring can mix forms freely
    // (e.g. "polygon(0 0, 80% 0, 73% 100%, 18px 100%)").
    public struct PolygonAxis
    {
        public float Value;
        public bool IsPercent;
    }

    public struct PolygonCorner
    {
        public PolygonAxis X;
        public PolygonAxis Y;
    }

    public static class PolygonParser
    {
        public static PolygonCorner[] Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            int op = s.IndexOf('(');
            int cp = s.LastIndexOf(')');
            if (op < 0 || cp <= op) return null;
            var body = s.Substring(op + 1, cp - op - 1);
            var pairs = body.Split(',');
            var corners = new List<PolygonCorner>(pairs.Length);
            foreach (var pair in pairs)
            {
                var parts = pair.Trim().Split(' ');
                if (parts.Length < 2) continue;
                if (TryAxis(parts[0], out var x) && TryAxis(parts[1], out var y))
                    corners.Add(new PolygonCorner { X = x, Y = y });
            }
            return corners.Count >= 3 ? corners.ToArray() : null;
        }

        public static Vector2 Resolve(PolygonCorner corner, Rect rect)
        {
            float x = corner.X.IsPercent
                ? rect.xMin + corner.X.Value * rect.width
                : rect.xMin + corner.X.Value;
            float y = corner.Y.IsPercent
                ? rect.yMin + corner.Y.Value * rect.height
                : rect.yMin + corner.Y.Value;
            return new Vector2(x, y);
        }

        static bool TryAxis(string token, out PolygonAxis axis)
        {
            axis = default;
            token = token.Trim();
            if (token.Length == 0) return false;
            if (token.EndsWith("%"))
            {
                if (!float.TryParse(token.Substring(0, token.Length - 1),
                                    NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                    return false;
                axis = new PolygonAxis { Value = pct / 100f, IsPercent = true };
                return true;
            }
            if (token.EndsWith("px"))
            {
                if (!float.TryParse(token.Substring(0, token.Length - 2),
                                    NumberStyles.Float, CultureInfo.InvariantCulture, out var px))
                    return false;
                axis = new PolygonAxis { Value = px, IsPercent = false };
                return true;
            }
            if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                return false;
            // Unitless 0 is the only common case; treat any unitless value as px.
            axis = new PolygonAxis { Value = v, IsPercent = false };
            return true;
        }
    }
}
