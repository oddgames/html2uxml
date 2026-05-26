using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    [UxmlElement]
    public partial class Html2UxmlCanvas : Html2UxmlPanel
    {
        struct FillRect
        {
            public Rect Bounds;
            public Color Color;
        }

        int _width = 300;
        int _height = 150;
        Color _defaultFill = new Color(0, 0, 0, 0);
        readonly List<FillRect> _rects = new List<FillRect>();

        public event Action<MeshGenerationContext> Painter;

        [UxmlAttribute("canvas-width")]
        public int CanvasWidth
        {
            get => _width;
            set { _width = Mathf.Max(1, value); style.width = _width; MarkDirtyRepaint(); }
        }

        [UxmlAttribute("canvas-height")]
        public int CanvasHeight
        {
            get => _height;
            set { _height = Mathf.Max(1, value); style.height = _height; MarkDirtyRepaint(); }
        }

        // CSS-style color string: #rrggbb, #rrggbbaa, rgb(), rgba(), or named.
        [UxmlAttribute("canvas-fill")]
        public string CanvasFill
        {
            get => ColorToCss(_defaultFill);
            set { _defaultFill = ParseCssColor(value, _defaultFill); MarkDirtyRepaint(); }
        }

        public Html2UxmlCanvas()
        {
            AddToClassList("html2uxml-canvas");
            style.width = _width;
            style.height = _height;
            generateVisualContent += OnPaint;
        }

        public void Clear()
        {
            _rects.Clear();
            MarkDirtyRepaint();
        }

        public void FillRectPx(float x, float y, float w, float h, Color color)
        {
            _rects.Add(new FillRect { Bounds = new Rect(x, y, w, h), Color = color });
            MarkDirtyRepaint();
        }

        public void RequestRepaint() => MarkDirtyRepaint();

        void OnPaint(MeshGenerationContext ctx)
        {
            float w = layout.width;
            float h = layout.height;
            if (w > 0 && h > 0 && _defaultFill.a > 0)
            {
                var p = ctx.painter2D;
                p.fillColor = _defaultFill;
                p.BeginPath();
                p.MoveTo(new Vector2(0, 0));
                p.LineTo(new Vector2(w, 0));
                p.LineTo(new Vector2(w, h));
                p.LineTo(new Vector2(0, h));
                p.ClosePath();
                p.Fill();
            }
            foreach (var r in _rects)
            {
                var p = ctx.painter2D;
                p.fillColor = r.Color;
                p.BeginPath();
                p.MoveTo(new Vector2(r.Bounds.xMin, r.Bounds.yMin));
                p.LineTo(new Vector2(r.Bounds.xMax, r.Bounds.yMin));
                p.LineTo(new Vector2(r.Bounds.xMax, r.Bounds.yMax));
                p.LineTo(new Vector2(r.Bounds.xMin, r.Bounds.yMax));
                p.ClosePath();
                p.Fill();
            }
            Painter?.Invoke(ctx);
        }

        static Color ParseCssColor(string s, Color fallback)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            s = s.Trim();
            // rgb(r,g,b) / rgba(r,g,b,a) — Unity's TryParseHtmlString doesn't.
            if (s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
            {
                int open = s.IndexOf('(');
                int close = s.LastIndexOf(')');
                if (open > 0 && close > open)
                {
                    var parts = s.Substring(open + 1, close - open - 1)
                        .Split(new[] { ',', '/' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3
                        && TryParseChannel(parts[0], 255f, out var r)
                        && TryParseChannel(parts[1], 255f, out var g)
                        && TryParseChannel(parts[2], 255f, out var b))
                    {
                        float a = 1f;
                        if (parts.Length >= 4)
                            TryParseChannel(parts[3], 1f, out a);
                        return new Color(r, g, b, a);
                    }
                }
            }
            if (ColorUtility.TryParseHtmlString(s, out var c)) return c;
            return fallback;
        }

        static bool TryParseChannel(string raw, float maxIfPlain, out float value)
        {
            raw = raw.Trim();
            if (raw.EndsWith("%"))
            {
                if (float.TryParse(raw.Substring(0, raw.Length - 1), out var pct))
                {
                    value = Mathf.Clamp01(pct / 100f);
                    return true;
                }
                value = 0;
                return false;
            }
            if (float.TryParse(raw, out var v))
            {
                value = Mathf.Clamp01(maxIfPlain > 1 ? v / maxIfPlain : v);
                return true;
            }
            value = 0;
            return false;
        }

        static string ColorToCss(Color c)
        {
            return $"#{(byte)(c.r * 255):X2}{(byte)(c.g * 255):X2}{(byte)(c.b * 255):X2}{(byte)(c.a * 255):X2}";
        }
    }
}
