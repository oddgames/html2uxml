using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    internal enum Html2UxmlFillCorners
    {
        None,
        Leading,
        All,
    }

    internal enum Html2UxmlFillMode
    {
        Fill,
        Stroke,
    }

    // One shader-backed fill quad/mesh for controls that previously built
    // line-strip gradients on the CPU. The fallback path exists only for
    // missing shader/material support.
    internal sealed class Html2UxmlShaderFill : VisualElement
    {
        const int MaxStops = 8;
        const string ShaderName = "Hidden/ODDGames/html2uxml/CssGradient";

        static readonly int FallbackColorId = Shader.PropertyToID("_FallbackColor");
        static readonly int LinearEnabledId = Shader.PropertyToID("_LinearEnabled");
        static readonly int LinearAngleId = Shader.PropertyToID("_LinearAngle");
        static readonly int LinearCountId = Shader.PropertyToID("_LinearCount");
        static readonly int LinearWidthId = Shader.PropertyToID("_LinearWidth");
        static readonly int LinearHeightId = Shader.PropertyToID("_LinearHeight");
        static readonly int Radial1EnabledId = Shader.PropertyToID("_Radial1Enabled");
        static readonly int Radial1CountId = Shader.PropertyToID("_Radial1Count");
        static readonly int Radial2EnabledId = Shader.PropertyToID("_Radial2Enabled");
        static readonly int Radial2CountId = Shader.PropertyToID("_Radial2Count");
        static readonly int[] LinearColorIds = StopIds("_LinearColor");
        static readonly int[] LinearPosIds = StopIds("_LinearPos");

        Material _material;
        bool _gpuUnavailable;
        bool _preferGpu = true;
        Color _fallbackColor = Color.clear;
        LinearGradient _linear;
        float _radius;
        float _strokeWidth;
        Html2UxmlFillCorners _corners = Html2UxmlFillCorners.All;
        Html2UxmlFillMode _mode = Html2UxmlFillMode.Fill;

        public Html2UxmlShaderFill()
        {
            pickingMode = PickingMode.Ignore;
            focusable = false;
            style.position = Position.Absolute;
            style.backgroundColor = Color.clear;
            generateVisualContent += OnGenerateVisualContent;
            RegisterCallback<DetachFromPanelEvent>(_ => DisposeMaterial());
        }

        public void Configure(
            Color fallbackColor,
            LinearGradient linear,
            float radius,
            Html2UxmlFillCorners corners,
            bool preferGpu = true,
            Html2UxmlFillMode mode = Html2UxmlFillMode.Fill,
            float strokeWidth = 0f)
        {
            _fallbackColor = fallbackColor;
            _linear = linear;
            _radius = Mathf.Max(0f, radius);
            _strokeWidth = Mathf.Max(0f, strokeWidth);
            _corners = corners;
            _mode = mode;
            _preferGpu = preferGpu;

            if (_preferGpu && EnsureMaterial())
                style.unityMaterial = _material;
            else
                style.unityMaterial = null;
        }

        bool EnsureMaterial()
        {
            if (_material != null)
                return true;
            if (_gpuUnavailable)
                return false;
            var shader = Shader.Find(ShaderName);
            if (shader == null || !shader.isSupported)
            {
                _gpuUnavailable = true;
                return false;
            }
            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            return true;
        }

        public void DisposeMaterial()
        {
            var material = _material;
            _material = null;
            try
            {
                style.unityMaterial = null;
            }
            catch (MissingReferenceException)
            {
            }
            catch (System.NullReferenceException)
            {
            }
            if (material == null)
                return;
            if (Application.isPlaying)
                Object.Destroy(material);
            else
                Object.DestroyImmediate(material);
        }

        void OnGenerateVisualContent(MeshGenerationContext ctx)
        {
            var rect = BorderBoxRect(this);
            if (rect.width <= 0f || rect.height <= 0f)
                return;

            float radius = Mathf.Clamp(_radius, 0f, Mathf.Min(rect.width, rect.height) * 0.5f);
            if (_preferGpu && _material != null)
            {
                ApplyMaterialProperties(rect);
                if (_mode == Html2UxmlFillMode.Stroke)
                    PaintShaderStrokeMesh(ctx, rect, radius, _strokeWidth);
                else
                    PaintShaderMesh(ctx, rect, radius);
                return;
            }

            PaintFallback(ctx, rect, radius);
        }

        void ApplyMaterialProperties(Rect rect)
        {
            _material.SetColor(FallbackColorId, _fallbackColor);
            if (_linear != null && _linear.IsValid)
            {
                _material.SetFloat(LinearEnabledId, 1f);
                _material.SetFloat(LinearAngleId, _linear.AngleDegrees);
                _material.SetFloat(LinearWidthId, Mathf.Max(1f, rect.width));
                _material.SetFloat(LinearHeightId, Mathf.Max(1f, rect.height));
                SetStops(_material, _linear.Stops, LinearCountId, LinearColorIds, LinearPosIds);
            }
            else
            {
                _material.SetFloat(LinearEnabledId, 0f);
                _material.SetFloat(LinearCountId, 0f);
            }
            _material.SetFloat(Radial1EnabledId, 0f);
            _material.SetFloat(Radial1CountId, 0f);
            _material.SetFloat(Radial2EnabledId, 0f);
            _material.SetFloat(Radial2CountId, 0f);
        }

        void PaintShaderMesh(MeshGenerationContext ctx, Rect rect, float radius)
        {
            var points = ShapePoints(rect, radius);
            if (points == null || points.Length < 3)
            {
                var data = ctx.Allocate(4, 6, (Texture)null);
                var tint = (Color32)Color.white;
                data.SetAllVertices(new[]
                {
                    MakeUvVertex(rect.xMin, rect.yMin, 0f, 0f, tint),
                    MakeUvVertex(rect.xMax, rect.yMin, 1f, 0f, tint),
                    MakeUvVertex(rect.xMax, rect.yMax, 1f, 1f, tint),
                    MakeUvVertex(rect.xMin, rect.yMax, 0f, 1f, tint),
                });
                data.SetAllIndices(new ushort[] { 0, 1, 2, 2, 3, 0 });
                return;
            }

            var vertices = new Vertex[points.Length + 1];
            var indices = new ushort[points.Length * 3];
            var white = (Color32)Color.white;
            vertices[0] = MakeUvVertex(rect.center.x, rect.center.y, 0.5f, 0.5f, white);
            for (int i = 0; i < points.Length; i++)
            {
                Vector2 pt = points[i];
                vertices[i + 1] = MakeUvVertex(
                    pt.x,
                    pt.y,
                    Mathf.InverseLerp(rect.xMin, rect.xMax, pt.x),
                    Mathf.InverseLerp(rect.yMin, rect.yMax, pt.y),
                    white);
                indices[i * 3 + 0] = 0;
                indices[i * 3 + 1] = (ushort)(i + 1);
                indices[i * 3 + 2] = (ushort)(((i + 1) % points.Length) + 1);
            }
            var roundedData = ctx.Allocate(vertices.Length, indices.Length, (Texture)null);
            roundedData.SetAllVertices(vertices);
            roundedData.SetAllIndices(indices);
        }


        void PaintShaderStrokeMesh(MeshGenerationContext ctx, Rect rect, float radius, float strokeWidth)
        {
            strokeWidth = Mathf.Min(Mathf.Max(0f, strokeWidth), Mathf.Min(rect.width, rect.height) * 0.5f);
            if (strokeWidth <= 0.001f)
                return;

            int segments = radius <= 0.001f ? 0 : Mathf.Clamp(Mathf.CeilToInt(radius * 0.75f), 8, 48);
            var outer = ShapePoints(rect, radius, segments) ?? RectanglePoints(rect);
            var innerRect = new Rect(
                rect.x + strokeWidth,
                rect.y + strokeWidth,
                Mathf.Max(0f, rect.width - strokeWidth * 2f),
                Mathf.Max(0f, rect.height - strokeWidth * 2f));
            if (innerRect.width <= 0f || innerRect.height <= 0f)
                return;
            var inner = ShapePoints(innerRect, Mathf.Max(0f, radius - strokeWidth), segments) ?? RectanglePoints(innerRect);
            int count = Mathf.Min(outer.Length, inner.Length);
            if (count < 3)
                return;

            var vertices = new Vertex[count * 2];
            var indices = new ushort[count * 6];
            var white = (Color32)Color.white;
            for (int i = 0; i < count; i++)
            {
                Vector2 o = outer[i];
                Vector2 n = inner[i];
                vertices[i] = MakeUvVertex(o.x, o.y, Mathf.InverseLerp(rect.xMin, rect.xMax, o.x), Mathf.InverseLerp(rect.yMin, rect.yMax, o.y), white);
                vertices[count + i] = MakeUvVertex(n.x, n.y, Mathf.InverseLerp(rect.xMin, rect.xMax, n.x), Mathf.InverseLerp(rect.yMin, rect.yMax, n.y), white);
            }
            for (int i = 0; i < count; i++)
            {
                int next = (i + 1) % count;
                int ii = i * 6;
                indices[ii + 0] = (ushort)i;
                indices[ii + 1] = (ushort)next;
                indices[ii + 2] = (ushort)(count + next);
                indices[ii + 3] = (ushort)(count + next);
                indices[ii + 4] = (ushort)(count + i);
                indices[ii + 5] = (ushort)i;
            }
            var data = ctx.Allocate(vertices.Length, indices.Length, (Texture)null);
            data.SetAllVertices(vertices);
            data.SetAllIndices(indices);
        }

        void PaintFallback(MeshGenerationContext ctx, Rect rect, float radius)
        {
            var p = ctx.painter2D;
            p.fillColor = _fallbackColor;
            if (_mode == Html2UxmlFillMode.Stroke)
            {
                p.strokeColor = _fallbackColor;
                p.lineWidth = Mathf.Max(1f, _strokeWidth);
                p.BeginPath();
                RoundedPath(p, rect, radius);
                p.Stroke();
                return;
            }

            if (_linear != null && _linear.IsValid)
            {
                PaintVerticalGradientStrips(ctx, rect, radius, _linear);
                return;
            }

            p.BeginPath();
            RoundedPath(p, rect, radius);
            p.Fill();
        }

        Vector2[] ShapePoints(Rect rect, float radius)
        {
            int segments = radius <= 0.001f ? 0 : Mathf.Clamp(Mathf.CeilToInt(radius * 0.75f), 8, 48);
            return ShapePoints(rect, radius, segments);
        }

        Vector2[] ShapePoints(Rect rect, float radius, int segments)
        {
            if (radius <= 0.001f || _corners == Html2UxmlFillCorners.None)
                return null;
            if (_corners == Html2UxmlFillCorners.Leading)
                return LeadingRoundedPoints(rect, radius, segments);
            return RoundedRectPoints(rect, radius, segments);
        }

        static Vector2[] RectanglePoints(Rect rect)
        {
            return new[]
            {
                new Vector2(rect.xMin, rect.yMin),
                new Vector2(rect.xMax, rect.yMin),
                new Vector2(rect.xMax, rect.yMax),
                new Vector2(rect.xMin, rect.yMax),
            };
        }

        static Vector2[] LeadingRoundedPoints(Rect rect, float radius, int segments)
        {
            var points = new List<Vector2>(segments * 2 + 4)
            {
                new Vector2(rect.xMax, rect.yMin),
                new Vector2(rect.xMax, rect.yMax),
            };
            AddArc(points, new Vector2(rect.xMin + radius, rect.yMax - radius), radius, 90f, 180f, segments);
            AddArc(points, new Vector2(rect.xMin + radius, rect.yMin + radius), radius, 180f, 270f, segments);
            return points.ToArray();
        }

        static Vector2[] RoundedRectPoints(Rect rect, float radius, int segments)
        {
            var points = new List<Vector2>((segments + 1) * 4);
            AddArc(points, new Vector2(rect.xMin + radius, rect.yMin + radius), radius, 180f, 270f, segments);
            AddArc(points, new Vector2(rect.xMax - radius, rect.yMin + radius), radius, 270f, 360f, segments);
            AddArc(points, new Vector2(rect.xMax - radius, rect.yMax - radius), radius, 0f, 90f, segments);
            AddArc(points, new Vector2(rect.xMin + radius, rect.yMax - radius), radius, 90f, 180f, segments);
            return points.ToArray();
        }

        static void AddArc(List<Vector2> points, Vector2 center, float radius, float fromDeg, float toDeg, int segments)
        {
            segments = Mathf.Max(2, segments);
            for (int i = 0; i <= segments; i++)
            {
                float t = i / (float)segments;
                float a = Mathf.Lerp(fromDeg, toDeg, t) * Mathf.Deg2Rad;
                points.Add(new Vector2(center.x + Mathf.Cos(a) * radius, center.y + Mathf.Sin(a) * radius));
            }
        }

        static void RoundedPath(Painter2D p, Rect rect, float radius)
        {
            radius = Mathf.Clamp(radius, 0f, Mathf.Min(rect.width, rect.height) * 0.5f);
            if (radius <= 0.001f)
            {
                p.MoveTo(new Vector2(rect.xMin, rect.yMin));
                p.LineTo(new Vector2(rect.xMax, rect.yMin));
                p.LineTo(new Vector2(rect.xMax, rect.yMax));
                p.LineTo(new Vector2(rect.xMin, rect.yMax));
                p.ClosePath();
                return;
            }
            if (radius > 0f)
            {
                p.MoveTo(new Vector2(rect.xMin + radius, rect.yMin));
                p.LineTo(new Vector2(rect.xMax - radius, rect.yMin));
                p.Arc(new Vector2(rect.xMax - radius, rect.yMin + radius), radius, 270f, 360f);
                p.LineTo(new Vector2(rect.xMax, rect.yMax - radius));
                p.Arc(new Vector2(rect.xMax - radius, rect.yMax - radius), radius, 0f, 90f);
                p.LineTo(new Vector2(rect.xMin + radius, rect.yMax));
                p.Arc(new Vector2(rect.xMin + radius, rect.yMax - radius), radius, 90f, 180f);
                p.LineTo(new Vector2(rect.xMin, rect.yMin + radius));
                p.Arc(new Vector2(rect.xMin + radius, rect.yMin + radius), radius, 180f, 270f);
                p.ClosePath();
            }
        }

        static void PaintVerticalGradientStrips(MeshGenerationContext ctx, Rect rect, float radius, LinearGradient gradient)
        {
            int segments = Mathf.Clamp(Mathf.CeilToInt(rect.height * 2f), 8, 48);
            var vertices = new Vertex[segments * 4];
            var indices = new ushort[segments * 6];
            for (int i = 0; i < segments; i++)
            {
                float u0 = i / (float)segments;
                float u1 = (i + 1) / (float)segments;
                float y0 = Mathf.Lerp(rect.yMin, rect.yMax, u0);
                float y1 = Mathf.Lerp(rect.yMin, rect.yMax, u1);
                float inset0 = RoundedEdgeInset(rect, radius, y0);
                float inset1 = RoundedEdgeInset(rect, radius, y1);
                Color c0 = SampleStops(gradient.Stops, u0);
                Color c1 = SampleStops(gradient.Stops, u1);
                int vi = i * 4;
                int ii = i * 6;
                vertices[vi + 0] = MakeVertex(new Vector2(rect.xMin + inset0, y0), c0);
                vertices[vi + 1] = MakeVertex(new Vector2(rect.xMax, y0), c0);
                vertices[vi + 2] = MakeVertex(new Vector2(rect.xMax, y1), c1);
                vertices[vi + 3] = MakeVertex(new Vector2(rect.xMin + inset1, y1), c1);
                indices[ii + 0] = (ushort)(vi + 0);
                indices[ii + 1] = (ushort)(vi + 1);
                indices[ii + 2] = (ushort)(vi + 2);
                indices[ii + 3] = (ushort)(vi + 2);
                indices[ii + 4] = (ushort)(vi + 3);
                indices[ii + 5] = (ushort)(vi + 0);
            }
            var data = ctx.Allocate(vertices.Length, indices.Length);
            data.SetAllVertices(vertices);
            data.SetAllIndices(indices);
        }

        static float RoundedEdgeInset(Rect rect, float radius, float y)
        {
            if (radius <= 0.001f)
                return 0f;
            float centerY;
            if (y < rect.yMin + radius)
                centerY = rect.yMin + radius;
            else if (y > rect.yMax - radius)
                centerY = rect.yMax - radius;
            else
                return 0f;
            float dy = Mathf.Abs(y - centerY);
            return radius - Mathf.Sqrt(Mathf.Max(0f, radius * radius - dy * dy));
        }

        static Rect BorderBoxRect(VisualElement element)
        {
            var w = element.layout.width;
            var h = element.layout.height;
            if (w > 0f && h > 0f)
                return new Rect(0f, 0f, w, h);
            return element.contentRect;
        }

        static Vertex MakeUvVertex(float x, float y, float u, float v, Color32 tint)
        {
            return new Vertex
            {
                position = new Vector3(x, y, Vertex.nearZ),
                uv = new Vector2(u, v),
                tint = tint,
            };
        }

        static Vertex MakeVertex(Vector2 position, Color color)
        {
            return new Vertex
            {
                position = new Vector3(position.x, position.y, Vertex.nearZ),
                tint = (Color32)color,
                uv = Vector2.zero,
            };
        }

        static void SetStops(Material material, List<GradientStop> sourceStops, int countId, int[] colorIds, int[] posIds)
        {
            var stops = NormalizedStops(sourceStops);
            int count = Mathf.Min(MaxStops, stops.Count);
            material.SetFloat(countId, count);
            for (int i = 0; i < MaxStops; i++)
            {
                Color color = Color.clear;
                float pos = 1f;
                if (i < count)
                {
                    if (stops.Count > MaxStops)
                    {
                        pos = i / Mathf.Max(1f, MaxStops - 1f);
                        color = SampleStops(stops, pos);
                    }
                    else
                    {
                        color = stops[i].Color;
                        pos = stops[i].Position;
                    }
                }
                material.SetColor(colorIds[i], color);
                material.SetFloat(posIds[i], pos);
            }
        }

        static List<GradientStop> NormalizedStops(List<GradientStop> input)
        {
            var stops = input == null ? new List<GradientStop>() : new List<GradientStop>(input);
            stops.Sort((a, b) => a.Position.CompareTo(b.Position));
            if (stops.Count == 0)
                return stops;
            if (stops[0].Position > 0f)
                stops.Insert(0, new GradientStop(stops[0].Color, 0f));
            if (stops[stops.Count - 1].Position < 1f)
                stops.Add(new GradientStop(stops[stops.Count - 1].Color, 1f));
            return stops;
        }

        static Color SampleStops(List<GradientStop> stops, float t)
        {
            stops = NormalizedStops(stops);
            if (stops.Count == 0)
                return Color.clear;
            t = Mathf.Clamp01(t);
            for (int i = 1; i < stops.Count; i++)
            {
                if (t <= stops[i].Position)
                {
                    GradientStop a = stops[i - 1];
                    GradientStop b = stops[i];
                    float span = Mathf.Max(0.0001f, b.Position - a.Position);
                    float u = (t - a.Position) / span;
                    return LerpPremultiplied(a.Color, b.Color, u);
                }
            }
            return stops[stops.Count - 1].Color;
        }

        static Color LerpPremultiplied(Color a, Color b, float u)
        {
            float ar = a.r * a.a;
            float ag = a.g * a.a;
            float ab = a.b * a.a;
            float aa = a.a;
            float br = b.r * b.a;
            float bg = b.g * b.a;
            float bb = b.b * b.a;
            float ba = b.a;
            float r = Mathf.Lerp(ar, br, u);
            float g = Mathf.Lerp(ag, bg, u);
            float bl = Mathf.Lerp(ab, bb, u);
            float al = Mathf.Lerp(aa, ba, u);
            if (al <= 0.0001f)
                return Color.clear;
            return new Color(r / al, g / al, bl / al, al);
        }

        static int[] StopIds(string prefix)
        {
            var ids = new int[MaxStops];
            for (int i = 0; i < MaxStops; i++)
                ids[i] = Shader.PropertyToID(prefix + i);
            return ids;
        }
    }
}

