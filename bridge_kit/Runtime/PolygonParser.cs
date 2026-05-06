using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace HtmlToUxml.Bridge
{
    // Parses CSS clip-path polygon() into normalized 0..1 corner points.
    // Values are interpreted as percentages of the element rect (e.g. "50%"
    // becomes 0.5, "10px" is approximated as a fraction of 100). For pixel
    // values, you should prefer the percentage form when authoring.
    public static class PolygonParser
    {
        public static Vector2[] Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            int op = s.IndexOf('(');
            int cp = s.LastIndexOf(')');
            if (op < 0 || cp <= op) return null;
            var body = s.Substring(op + 1, cp - op - 1);
            var pairs = body.Split(',');
            var points = new List<Vector2>(pairs.Length);
            foreach (var pair in pairs)
            {
                var parts = pair.Trim().Split(' ');
                if (parts.Length < 2) continue;
                if (TryFraction(parts[0], out var x) && TryFraction(parts[1], out var y))
                    points.Add(new Vector2(x, y));
            }
            return points.Count >= 3 ? points.ToArray() : null;
        }

        static bool TryFraction(string token, out float value)
        {
            value = 0f;
            token = token.Trim();
            if (token.Length == 0) return false;
            if (token.EndsWith("%"))
            {
                if (!float.TryParse(token.Substring(0, token.Length - 1),
                                    NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                    return false;
                value = pct / 100f;
                return true;
            }
            if (token.EndsWith("px"))
            {
                if (!float.TryParse(token.Substring(0, token.Length - 2),
                                    NumberStyles.Float, CultureInfo.InvariantCulture, out var px))
                    return false;
                // No element size available; map 0..100px linearly to 0..1.
                value = Mathf.Clamp01(px / 100f);
                return true;
            }
            return float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
