using UnityEngine;
using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    internal sealed class Html2UxmlPaintManipulator : Manipulator
    {
        static readonly CustomStyleProperty<string> VectorIcon = new CustomStyleProperty<string>("--odd-vector-icon");
        static readonly CustomStyleProperty<Color> SolidBackground = new CustomStyleProperty<Color>("--odd-background-color");

        bool _hasSolidBackground;
        Color _solidBackgroundColor;
        string _icon;
        Color _iconColor = Color.black;

        protected override void RegisterCallbacksOnTarget()
        {
            target.RegisterCallback<CustomStyleResolvedEvent>(OnStylesResolved);
            target.generateVisualContent += OnGenerateVisualContent;
        }

        protected override void UnregisterCallbacksFromTarget()
        {
            target.UnregisterCallback<CustomStyleResolvedEvent>(OnStylesResolved);
            target.generateVisualContent -= OnGenerateVisualContent;
        }

        void OnStylesResolved(CustomStyleResolvedEvent evt)
        {
            var style = evt.customStyle;
            _hasSolidBackground = style.TryGetValue(SolidBackground, out _solidBackgroundColor)
                && _solidBackgroundColor.a > 0.001f;

            _icon = style.TryGetValue(VectorIcon, out var iconStr)
                ? NormalizeQuotedString(iconStr)
                : null;
            _iconColor = target.resolvedStyle.color;
        }

        void OnGenerateVisualContent(MeshGenerationContext ctx)
        {
            var rect = BorderBoxRect(target);
            if (rect.width <= 0f || rect.height <= 0f)
                return;

            var painter = ctx.painter2D;
            if (_hasSolidBackground)
            {
                painter.fillColor = _solidBackgroundColor;
                painter.BeginPath();
                RoundedRect(painter, rect, target.resolvedStyle.borderTopLeftRadius);
                painter.Fill();
            }

            if (_icon == "star")
                PaintStar(painter, rect, _iconColor.a > 0.001f ? _iconColor : Color.black);
        }

        static void PaintStar(Painter2D painter, Rect rect, Color color)
        {
            var center = rect.center;
            float outer = Mathf.Min(rect.width, rect.height) * 0.29f;
            float inner = outer * 0.45f;

            painter.fillColor = color;
            painter.BeginPath();
            for (int i = 0; i < 10; i++)
            {
                float radius = (i % 2 == 0) ? outer : inner;
                float angle = (-90f + i * 36f) * Mathf.Deg2Rad;
                var pt = new Vector2(
                    center.x + Mathf.Cos(angle) * radius,
                    center.y + Mathf.Sin(angle) * radius);
                if (i == 0) painter.MoveTo(pt);
                else painter.LineTo(pt);
            }
            painter.ClosePath();
            painter.Fill();
        }

        static Rect BorderBoxRect(VisualElement element)
        {
            var w = element.layout.width;
            var h = element.layout.height;
            if (w > 0f && h > 0f)
                return new Rect(0f, 0f, w, h);
            return element.contentRect;
        }

        static void RoundedRect(Painter2D p, Rect r, float radius)
        {
            radius = Mathf.Max(0f, Mathf.Min(radius, Mathf.Min(r.width, r.height) * 0.5f));
            if (radius <= 0.01f)
            {
                p.MoveTo(new Vector2(r.xMin, r.yMin));
                p.LineTo(new Vector2(r.xMax, r.yMin));
                p.LineTo(new Vector2(r.xMax, r.yMax));
                p.LineTo(new Vector2(r.xMin, r.yMax));
                p.ClosePath();
                return;
            }

            const float k = 0.55228475f;
            float c = radius * k;
            p.MoveTo(new Vector2(r.xMin + radius, r.yMin));
            p.LineTo(new Vector2(r.xMax - radius, r.yMin));
            p.BezierCurveTo(
                new Vector2(r.xMax - radius + c, r.yMin),
                new Vector2(r.xMax, r.yMin + radius - c),
                new Vector2(r.xMax, r.yMin + radius));
            p.LineTo(new Vector2(r.xMax, r.yMax - radius));
            p.BezierCurveTo(
                new Vector2(r.xMax, r.yMax - radius + c),
                new Vector2(r.xMax - radius + c, r.yMax),
                new Vector2(r.xMax - radius, r.yMax));
            p.LineTo(new Vector2(r.xMin + radius, r.yMax));
            p.BezierCurveTo(
                new Vector2(r.xMin + radius - c, r.yMax),
                new Vector2(r.xMin, r.yMax - radius + c),
                new Vector2(r.xMin, r.yMax - radius));
            p.LineTo(new Vector2(r.xMin, r.yMin + radius));
            p.BezierCurveTo(
                new Vector2(r.xMin, r.yMin + radius - c),
                new Vector2(r.xMin + radius - c, r.yMin),
                new Vector2(r.xMin + radius, r.yMin));
            p.ClosePath();
        }

        static string NormalizeQuotedString(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            value = value.Trim();
            if (value.Length >= 2 &&
                ((value[0] == '"' && value[value.Length - 1] == '"') ||
                 (value[0] == '\'' && value[value.Length - 1] == '\'')))
                return value.Substring(1, value.Length - 2);
            return value;
        }
    }
}
