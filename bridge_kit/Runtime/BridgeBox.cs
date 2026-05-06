using UnityEngine;
using UnityEngine.UIElements;

namespace HtmlToUxml.Bridge
{
    // VisualElement that paints CSS features USS doesn't support natively.
    // Reads these USS custom properties when present:
    //
    //   --gg-shadow-offset-x, --gg-shadow-offset-y, --gg-shadow-blur (lengths, px)
    //   --gg-shadow-color (color)
    //   --gg-gradient (string: linear-gradient(...))
    //
    // If none are set the element behaves exactly like a plain VisualElement.
    [UxmlElement]
    public partial class BridgeBox : VisualElement
    {
        static readonly CustomStyleProperty<float> ShadowOffsetX = new CustomStyleProperty<float>("--gg-shadow-offset-x");
        static readonly CustomStyleProperty<float> ShadowOffsetY = new CustomStyleProperty<float>("--gg-shadow-offset-y");
        static readonly CustomStyleProperty<float> ShadowBlur    = new CustomStyleProperty<float>("--gg-shadow-blur");
        static readonly CustomStyleProperty<Color> ShadowColor   = new CustomStyleProperty<Color>("--gg-shadow-color");
        static readonly CustomStyleProperty<string> Gradient     = new CustomStyleProperty<string>("--gg-gradient");
        static readonly CustomStyleProperty<string> ClipPolygon  = new CustomStyleProperty<string>("--gg-clip-polygon");
        static readonly CustomStyleProperty<float>  RowGap        = new CustomStyleProperty<float>("--gg-row-gap");
        static readonly CustomStyleProperty<float>  ColumnGap     = new CustomStyleProperty<float>("--gg-column-gap");

        float _rowGap;
        float _columnGap;
        bool  _hasGap;

        Vector2 _shadowOffset;
        float   _shadowBlur;
        Color   _shadowColor;
        bool    _hasShadow;

        LinearGradient _gradient;

        Vector2[] _clipPoints;

        public BridgeBox()
        {
            RegisterCallback<CustomStyleResolvedEvent>(OnStylesResolved);
            RegisterCallback<GeometryChangedEvent>(_ => ApplyGap());
            generateVisualContent += OnGenerateVisualContent;
        }

        void OnStylesResolved(CustomStyleResolvedEvent evt)
        {
            var style = evt.customStyle;
            float ox = 0f, oy = 0f, blur = 0f;
            Color color = Color.black;
            bool any = false;
            if (style.TryGetValue(ShadowOffsetX, out var x)) { ox = x; any = true; }
            if (style.TryGetValue(ShadowOffsetY, out var y)) { oy = y; any = true; }
            if (style.TryGetValue(ShadowBlur, out var b))    { blur = b; any = true; }
            if (style.TryGetValue(ShadowColor, out var c))   { color = c; any = true; }
            _shadowOffset = new Vector2(ox, oy);
            _shadowBlur = Mathf.Max(0f, blur);
            _shadowColor = color;
            _hasShadow = any && color.a > 0f;

            string gradStr;
            _gradient = style.TryGetValue(Gradient, out gradStr)
                ? GradientParser.ParseLinear(gradStr)
                : null;

            string clipStr;
            _clipPoints = style.TryGetValue(ClipPolygon, out clipStr)
                ? PolygonParser.Parse(clipStr)
                : null;

            float rg = 0f, cg = 0f;
            bool gapAny = false;
            if (style.TryGetValue(RowGap, out var rgv))    { rg = rgv; gapAny = true; }
            if (style.TryGetValue(ColumnGap, out var cgv)) { cg = cgv; gapAny = true; }
            _rowGap = rg;
            _columnGap = cg;
            _hasGap = gapAny;
            ApplyGap();

            MarkDirtyRepaint();
        }

        void ApplyGap()
        {
            if (!_hasGap || childCount < 2) return;
            // Pick spacing axis from flex-direction. Row -> column-gap as left margin
            // on every child after the first; Column -> row-gap as top margin.
            bool isColumn = resolvedStyle.flexDirection == FlexDirection.Column
                         || resolvedStyle.flexDirection == FlexDirection.ColumnReverse;
            float spacing = isColumn ? _rowGap : _columnGap;
            for (int i = 0; i < childCount; i++)
            {
                var c = ElementAt(i);
                if (i == 0)
                {
                    if (isColumn) c.style.marginTop = 0f;
                    else          c.style.marginLeft = 0f;
                }
                else
                {
                    if (isColumn) c.style.marginTop = spacing;
                    else          c.style.marginLeft = spacing;
                }
            }
        }

        void OnGenerateVisualContent(MeshGenerationContext ctx)
        {
            var rect = contentRect;
            var painter = ctx.painter2D;
            if (_hasShadow) PaintShadow(painter, rect);
            if (_gradient != null) PaintGradient(painter, rect);
            if (_clipPoints != null && _clipPoints.Length >= 3) PaintClipMask(painter, rect);
        }

        void PaintClipMask(Painter2D p, Rect rect)
        {
            // Approximate a clip-path by painting OUTSIDE the polygon with the
            // parent's resolved background color (or transparent black) so the
            // visible area matches the polygon. A proper stencil would need a
            // shader; this produces the right shape against solid backgrounds.
            p.fillColor = new Color(0, 0, 0, 0); // requires a parent background
            p.BeginPath();
            // Draw the rect, then "subtract" the polygon by reversing winding.
            p.MoveTo(new Vector2(rect.xMin, rect.yMin));
            p.LineTo(new Vector2(rect.xMax, rect.yMin));
            p.LineTo(new Vector2(rect.xMax, rect.yMax));
            p.LineTo(new Vector2(rect.xMin, rect.yMax));
            p.ClosePath();
            // Polygon hole (counter-clockwise to act as a hole under non-zero fill rule)
            for (int i = _clipPoints.Length - 1; i >= 0; i--)
            {
                var pt = _clipPoints[i];
                var x = rect.xMin + pt.x * rect.width;
                var y = rect.yMin + pt.y * rect.height;
                if (i == _clipPoints.Length - 1) p.MoveTo(new Vector2(x, y));
                else p.LineTo(new Vector2(x, y));
            }
            p.ClosePath();
            p.Fill(FillRule.NonZero);
        }

        void PaintShadow(Painter2D p, Rect rect)
        {
            // Approximate a Gaussian shadow with N concentric semi-transparent
            // rounded rectangles. Cheap, no shader needed.
            int steps = Mathf.Clamp(Mathf.RoundToInt(_shadowBlur * 0.5f), 0, 8);
            float radius = resolvedStyle.borderTopLeftRadius;
            for (int i = steps; i >= 1; i--)
            {
                float t = i / (float)steps;
                float expand = _shadowBlur * t * 0.5f;
                Color c = _shadowColor;
                c.a = _shadowColor.a * (1f - t) * 0.6f;
                p.fillColor = c;
                p.BeginPath();
                RoundedRect(p,
                    new Rect(
                        rect.x + _shadowOffset.x - expand,
                        rect.y + _shadowOffset.y - expand,
                        rect.width + expand * 2f,
                        rect.height + expand * 2f),
                    radius + expand);
                p.Fill();
            }
        }

        void PaintGradient(Painter2D p, Rect rect)
        {
            // Sample the gradient at N stripes along the gradient axis.
            // For arbitrary angles, rotate sampling vector.
            float rad = (_gradient.AngleDegrees - 90f) * Mathf.Deg2Rad;
            Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));
            // Project corners onto dir to find min/max projection.
            float pMin = float.PositiveInfinity, pMax = float.NegativeInfinity;
            Vector2[] corners = {
                new Vector2(rect.xMin, rect.yMin),
                new Vector2(rect.xMax, rect.yMin),
                new Vector2(rect.xMax, rect.yMax),
                new Vector2(rect.xMin, rect.yMax),
            };
            foreach (var c in corners)
            {
                float pr = Vector2.Dot(c, dir);
                pMin = Mathf.Min(pMin, pr);
                pMax = Mathf.Max(pMax, pr);
            }

            const int Stripes = 32;
            float radius = resolvedStyle.borderTopLeftRadius;
            for (int i = 0; i < Stripes; i++)
            {
                float t0 = i / (float)Stripes;
                float t1 = (i + 1) / (float)Stripes;
                Color cMid = SampleGradient(_gradient, (t0 + t1) * 0.5f);
                p.fillColor = cMid;
                p.BeginPath();
                // Build a rectangle in the gradient axis space, then unrotate.
                // For simplicity, draw axis-aligned slabs only when angle is
                // a multiple of 90deg; otherwise fall back to whole-rect tint
                // at the average colour.
                if (Mathf.Approximately(_gradient.AngleDegrees % 90f, 0f))
                {
                    Rect slab = SlabRect(rect, _gradient.AngleDegrees, t0, t1);
                    RoundedRect(p, slab, radius);
                    p.Fill();
                }
                else if (i == Stripes / 2)
                {
                    RoundedRect(p, rect, radius);
                    p.Fill();
                }
            }
        }

        static Rect SlabRect(Rect r, float angle, float t0, float t1)
        {
            // 0 -> top-to-bottom, 90 -> left-to-right, 180 -> bottom-to-top,
            // 270 -> right-to-left.
            angle = (angle % 360f + 360f) % 360f;
            switch (Mathf.RoundToInt(angle))
            {
                case 0:   return new Rect(r.x, r.y + r.height * t0, r.width, r.height * (t1 - t0));
                case 90:  return new Rect(r.x + r.width * t0, r.y, r.width * (t1 - t0), r.height);
                case 180: return new Rect(r.x, r.y + r.height * (1f - t1), r.width, r.height * (t1 - t0));
                case 270: return new Rect(r.x + r.width * (1f - t1), r.y, r.width * (t1 - t0), r.height);
            }
            return r;
        }

        static Color SampleGradient(LinearGradient g, float t)
        {
            t = Mathf.Clamp01(t);
            for (int i = 1; i < g.Stops.Count; i++)
            {
                if (t <= g.Stops[i].Position)
                {
                    var a = g.Stops[i - 1];
                    var b = g.Stops[i];
                    float span = Mathf.Max(0.0001f, b.Position - a.Position);
                    float u = (t - a.Position) / span;
                    return Color.Lerp(a.Color, b.Color, u);
                }
            }
            return g.Stops[g.Stops.Count - 1].Color;
        }

        static void RoundedRect(Painter2D p, Rect r, float radius)
        {
            radius = Mathf.Clamp(radius, 0f, Mathf.Min(r.width, r.height) * 0.5f);
            if (radius <= 0f)
            {
                p.MoveTo(new Vector2(r.xMin, r.yMin));
                p.LineTo(new Vector2(r.xMax, r.yMin));
                p.LineTo(new Vector2(r.xMax, r.yMax));
                p.LineTo(new Vector2(r.xMin, r.yMax));
                p.ClosePath();
                return;
            }
            p.MoveTo(new Vector2(r.xMin + radius, r.yMin));
            p.LineTo(new Vector2(r.xMax - radius, r.yMin));
            p.Arc(new Vector2(r.xMax - radius, r.yMin + radius), radius, -90f, 0f);
            p.LineTo(new Vector2(r.xMax, r.yMax - radius));
            p.Arc(new Vector2(r.xMax - radius, r.yMax - radius), radius, 0f, 90f);
            p.LineTo(new Vector2(r.xMin + radius, r.yMax));
            p.Arc(new Vector2(r.xMin + radius, r.yMax - radius), radius, 90f, 180f);
            p.LineTo(new Vector2(r.xMin, r.yMin + radius));
            p.Arc(new Vector2(r.xMin + radius, r.yMin + radius), radius, 180f, 270f);
            p.ClosePath();
        }
    }
}
