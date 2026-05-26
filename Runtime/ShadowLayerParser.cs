using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ODDGames.Html2Uxml
{
    internal struct ShadowLayer
    {
        public bool Inset;
        public Vector2 Offset;
        public float Blur;
        public float Spread;
        public Color Color;
    }

    internal static class ShadowLayerParser
    {
        public static List<ShadowLayer> Parse(string value, Color currentColor)
        {
            var layers = new List<ShadowLayer>();
            if (string.IsNullOrWhiteSpace(value)) return layers;

            var records = value.Split(';');
            foreach (var raw in records)
            {
                if (layers.Count >= 8) break;
                var record = raw.Trim();
                if (record.Length == 0) continue;
                var fields = record.Split(new[] { '|' }, 6);
                if (fields.Length < 6) continue;
                if (!TryFloat(fields[1], out var x)) continue;
                if (!TryFloat(fields[2], out var y)) continue;
                if (!TryFloat(fields[3], out var blur)) continue;
                if (!TryFloat(fields[4], out var spread)) continue;

                Color color;
                if (fields[5].Trim() == "currentColor")
                    color = currentColor;
                else if (!ColorParser.TryParse(fields[5].Trim(), out color))
                    continue;

                layers.Add(new ShadowLayer
                {
                    Inset = fields[0].Trim() == "inset",
                    Offset = new Vector2(x, y),
                    Blur = Mathf.Max(0f, blur),
                    Spread = spread,
                    Color = color
                });
            }
            return layers;
        }

        static bool TryFloat(string value, out float result)
        {
            return float.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out result);
        }
    }
}
