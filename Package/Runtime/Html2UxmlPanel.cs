using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    // VisualElement that paints CSS features USS doesn't support natively.
    // Reads these USS custom properties when present:
    //
    //   --odd-shadow-offset-x, --odd-shadow-offset-y, --odd-shadow-blur (unitless px floats)
    //   --odd-shadow-color (color)
    //   --odd-box-shadows (string: kind|x|y|blur|spread|color;...)
    //   --odd-inner-shadow-offset-x/-y/-blur/-spread (unitless px floats)
    //   --odd-inner-shadow-color (color)
    //   --odd-gradient (string: linear-gradient(...))
    //   --odd-radial-gradient (string: radial-gradient(...))
    //   --odd-radial-gradient-2 (string: radial-gradient(...))
    //   --odd-repeating-linear-gradient (string: repeating-linear-gradient(...))
    //   --odd-tiled-radial-gradient (string: radial-gradient(...) paired with background-size)
    //   --odd-background-pattern-size / --odd-background-pattern-position (strings)
    //   --odd-mask-image (string: linear-gradient(...))
    //   --odd-mask-fade-color (color)
    //
    // If none are set the element behaves exactly like a plain VisualElement.
    [UxmlElement]
    public partial class Html2UxmlPanel : VisualElement
    {
        static readonly CustomStyleProperty<float> ShadowOffsetX = new CustomStyleProperty<float>("--odd-shadow-offset-x");
        static readonly CustomStyleProperty<float> ShadowOffsetY = new CustomStyleProperty<float>("--odd-shadow-offset-y");
        static readonly CustomStyleProperty<float> ShadowBlur    = new CustomStyleProperty<float>("--odd-shadow-blur");
        static readonly CustomStyleProperty<Color> ShadowColor   = new CustomStyleProperty<Color>("--odd-shadow-color");
        static readonly CustomStyleProperty<string> BoxShadows = new CustomStyleProperty<string>("--odd-box-shadows");
        static readonly CustomStyleProperty<float> InnerShadowOffsetX = new CustomStyleProperty<float>("--odd-inner-shadow-offset-x");
        static readonly CustomStyleProperty<float> InnerShadowOffsetY = new CustomStyleProperty<float>("--odd-inner-shadow-offset-y");
        static readonly CustomStyleProperty<float> InnerShadowBlur    = new CustomStyleProperty<float>("--odd-inner-shadow-blur");
        static readonly CustomStyleProperty<float> InnerShadowSpread  = new CustomStyleProperty<float>("--odd-inner-shadow-spread");
        static readonly CustomStyleProperty<Color> InnerShadowColor   = new CustomStyleProperty<Color>("--odd-inner-shadow-color");
        static readonly CustomStyleProperty<string> Gradient     = new CustomStyleProperty<string>("--odd-gradient");
        static readonly CustomStyleProperty<string> RadialGradient = new CustomStyleProperty<string>("--odd-radial-gradient");
        static readonly CustomStyleProperty<string> RadialGradient2 = new CustomStyleProperty<string>("--odd-radial-gradient-2");
        static readonly CustomStyleProperty<string> RepeatingLinearGradient = new CustomStyleProperty<string>("--odd-repeating-linear-gradient");
        static readonly CustomStyleProperty<string> TiledRadialGradient = new CustomStyleProperty<string>("--odd-tiled-radial-gradient");
        static readonly CustomStyleProperty<string> BackgroundPatternSize = new CustomStyleProperty<string>("--odd-background-pattern-size");
        static readonly CustomStyleProperty<string> BackgroundPatternPosition = new CustomStyleProperty<string>("--odd-background-pattern-position");
        static readonly CustomStyleProperty<Color> SolidBackground = new CustomStyleProperty<Color>("--odd-background-color");
        static readonly CustomStyleProperty<string> MaskImage = new CustomStyleProperty<string>("--odd-mask-image");
        static readonly CustomStyleProperty<Color> MaskFadeColor = new CustomStyleProperty<Color>("--odd-mask-fade-color");
        static readonly CustomStyleProperty<string> ClipPolygon  = new CustomStyleProperty<string>("--odd-clip-polygon");
        static readonly CustomStyleProperty<string> VectorIcon    = new CustomStyleProperty<string>("--odd-vector-icon");


        Vector2 _shadowOffset;
        float   _shadowBlur;
        Color   _shadowColor;
        bool    _hasShadow;

        Vector2 _innerShadowOffset;
        float   _innerShadowBlur;
        float   _innerShadowSpread;
        Color   _innerShadowColor;
        bool    _hasInnerShadow;
        List<ShadowLayer> _shadowLayers = new List<ShadowLayer>();

        LinearGradient _gradient;
        RadialGradient _radialGradient;
        RadialGradient _radialGradient2;
        RepeatingLinearPattern _repeatingLinearPattern;
        TiledRadialPattern _tiledRadialPattern;
        Vector2 _patternSize = new Vector2(8f, 8f);
        Vector2 _patternPosition = Vector2.zero;
        Color _solidBackgroundColor;
        bool _hasSolidBackground;
        bool _hasPaintWork;

        PolygonCorner[] _clipPoints;
        Color _clipCoverColor;
        CssGradientLayer _gradientLayer;
        InsetShadowOverlay _insetShadowOverlay;
        VectorIconOverlay _iconOverlay;
        ClipMaskOverlay _clipOverlay;
        MaskFadeOverlay _maskOverlay;

        public Html2UxmlPanel()
        {
            this.AddManipulator(new Html2UxmlAnimationManipulator());
            RegisterCallback<CustomStyleResolvedEvent>(OnStylesResolved);
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

            float iox = 0f, ioy = 0f, iblur = 0f, ispread = 0f;
            Color icolor = Color.clear;
            bool innerAny = false;
            if (style.TryGetValue(InnerShadowOffsetX, out var ix)) { iox = ix; innerAny = true; }
            if (style.TryGetValue(InnerShadowOffsetY, out var iy)) { ioy = iy; innerAny = true; }
            if (style.TryGetValue(InnerShadowBlur, out var ib))    { iblur = ib; innerAny = true; }
            if (style.TryGetValue(InnerShadowSpread, out var isp)) { ispread = isp; innerAny = true; }
            if (style.TryGetValue(InnerShadowColor, out var ic))   { icolor = ic; innerAny = true; }
            _innerShadowOffset = new Vector2(iox, ioy);
            _innerShadowBlur = Mathf.Max(0f, iblur);
            _innerShadowSpread = ispread;
            _innerShadowColor = icolor;
            _hasInnerShadow = innerAny && icolor.a > 0f;

            string shadowsStr;
            _shadowLayers = style.TryGetValue(BoxShadows, out shadowsStr)
                ? ShadowLayerParser.Parse(shadowsStr, resolvedStyle.color)
                : new List<ShadowLayer>();
            if (_shadowLayers.Count == 0)
            {
                if (_hasShadow)
                {
                    _shadowLayers.Add(new ShadowLayer
                    {
                        Inset = false,
                        Offset = _shadowOffset,
                        Blur = _shadowBlur,
                        Spread = 0f,
                        Color = _shadowColor
                    });
                }
                if (_hasInnerShadow)
                {
                    _shadowLayers.Add(new ShadowLayer
                    {
                        Inset = true,
                        Offset = _innerShadowOffset,
                        Blur = _innerShadowBlur,
                        Spread = _innerShadowSpread,
                        Color = _innerShadowColor
                    });
                }
            }

            string gradStr;
            _gradient = style.TryGetValue(Gradient, out gradStr)
                ? GradientParser.ParseLinear(gradStr)
                : null;

            string radialStr;
            _radialGradient = style.TryGetValue(RadialGradient, out radialStr)
                ? GradientParser.ParseRadial(radialStr)
                : null;

            string radial2Str;
            _radialGradient2 = style.TryGetValue(RadialGradient2, out radial2Str)
                ? GradientParser.ParseRadial(radial2Str)
                : null;

            string repeatingLinearStr;
            _repeatingLinearPattern = style.TryGetValue(RepeatingLinearGradient, out repeatingLinearStr)
                ? CssPatternParser.ParseRepeatingLinear(repeatingLinearStr)
                : null;

            string tiledRadialStr;
            _tiledRadialPattern = style.TryGetValue(TiledRadialGradient, out tiledRadialStr)
                ? CssPatternParser.ParseTiledRadial(tiledRadialStr)
                : null;

            string patternSizeStr;
            _patternSize = style.TryGetValue(BackgroundPatternSize, out patternSizeStr)
                ? CssPatternParser.ParseSize(patternSizeStr, new Vector2(8f, 8f))
                : new Vector2(8f, 8f);

            string patternPositionStr;
            _patternPosition = style.TryGetValue(BackgroundPatternPosition, out patternPositionStr)
                ? CssPatternParser.ParsePosition(patternPositionStr)
                : Vector2.zero;

            _hasSolidBackground = style.TryGetValue(SolidBackground, out _solidBackgroundColor)
                && _solidBackgroundColor.a > 0.001f;

            string clipStr;
            _clipPoints = style.TryGetValue(ClipPolygon, out clipStr)
                ? PolygonParser.Parse(clipStr)
                : null;
            // Cover masks UITK's own background paint that bleeds outside the
            // polygon. It must NOT paint with an ancestor's color: ancestors
            // can sit behind unrelated siblings (e.g. a portrait disc the
            // polygon's notch is meant to reveal). Only mask when this element
            // has its own opaque resolved background.
            _clipCoverColor = resolvedStyle.backgroundColor;
            bool hadPaintWork = _hasPaintWork;
            EnsureClipOverlay();
            EnsureGradientLayer();
            EnsureInsetShadowOverlay();

            string iconStr;
            if (style.TryGetValue(VectorIcon, out iconStr))
                EnsureVectorIconOverlay(iconStr);
            else
                RemoveVectorIconOverlay();

            string maskStr;
            if (style.TryGetValue(MaskImage, out maskStr))
            {
                Color fadeColor;
                if (!style.TryGetValue(MaskFadeColor, out fadeColor))
                {
                    fadeColor = resolvedStyle.backgroundColor;
                    if (fadeColor.a <= 0f && parent != null)
                        fadeColor = parent.resolvedStyle.backgroundColor;
                }
                EnsureMaskOverlay(maskStr, fadeColor);
            }
            else
            {
                RemoveMaskOverlay();
            }

            // CSS gap / row-gap / column-gap is baked into per-child margins at
            // conversion time (see html2uxml._static_gap_decls). No runtime
            // mutation needed, which avoids "VisualElements cannot change render
            // data ... during visual tree rendering" errors that fire when style
            // writes land mid-render in UI Builder previews.

            _hasPaintWork = ComputeHasPaintWork();
            // No explicit MarkDirtyRepaint: Unity's CustomStyleResolvedEvent
            // dispatch already increments the version, and calling it from a
            // style/scheduler callback in Unity 6 throws "cannot change render
            // data ... during visual tree rendering" inside UI Builder previews.
        }

        bool ComputeHasPaintWork()
        {
            return (_shadowLayers != null && _shadowLayers.Count > 0)
                || _hasSolidBackground
                || _gradient != null
                || _radialGradient != null
                || _radialGradient2 != null
                || _tiledRadialPattern != null
                || _repeatingLinearPattern != null
                || (_clipOverlay == null && _clipPoints != null && _clipPoints.Length >= 3 && _clipCoverColor.a > 0.001f);
        }


        void EnsureMaskOverlay(string maskStr, Color fadeColor)
        {
            if (_maskOverlay == null)
            {
                _maskOverlay = new MaskFadeOverlay();
                Add(_maskOverlay);
            }
            _maskOverlay.Configure(maskStr, fadeColor);
            _maskOverlay.BringToFront();
        }

        void RemoveMaskOverlay()
        {
            if (_maskOverlay == null) return;
            if (_maskOverlay.parent == this)
                Remove(_maskOverlay);
            _maskOverlay = null;
        }

        void EnsureGradientLayer()
        {
            // Unity 6.4 only accepts UI Toolkit-compatible materials here,
            // normally authored with the UITK Shader Graph target. The package
            // still ships shader/material assets for opt-in experimentation,
            // but automatic CSS gradients use the mesh fallback to avoid
            // "material is not compatible with UITK" editor errors.
            RemoveGradientLayer();
        }

        void RemoveGradientLayer()
        {
            RemoveInsetShadowOverlay();
            if (_gradientLayer == null) return;
            if (_gradientLayer.parent == this)
                Remove(_gradientLayer);
            _gradientLayer.DisposeMaterial();
            _gradientLayer = null;
        }

        void EnsureInsetShadowOverlay()
        {
            bool shaderGradient = _gradientLayer != null && _gradientLayer.parent == this;
            if (!shaderGradient || _shadowLayers == null || _shadowLayers.Count == 0)
            {
                RemoveInsetShadowOverlay();
                return;
            }

            var insetLayers = new List<ShadowLayer>();
            for (int i = 0; i < _shadowLayers.Count; i++)
            {
                if (_shadowLayers[i].Inset && _shadowLayers[i].Color.a > 0f)
                    insetLayers.Add(_shadowLayers[i]);
            }

            if (insetLayers.Count == 0)
            {
                RemoveInsetShadowOverlay();
                return;
            }

            if (_insetShadowOverlay == null)
            {
                _insetShadowOverlay = new InsetShadowOverlay();
                Add(_insetShadowOverlay);
            }
            else if (_insetShadowOverlay.parent != this)
            {
                Add(_insetShadowOverlay);
            }

            _insetShadowOverlay.Configure(insetLayers, resolvedStyle.borderTopLeftRadius);
            _insetShadowOverlay.SendToBack();
            _gradientLayer.SendToBack();
        }

        void RemoveInsetShadowOverlay()
        {
            if (_insetShadowOverlay == null) return;
            if (_insetShadowOverlay.parent == this)
                Remove(_insetShadowOverlay);
            _insetShadowOverlay = null;
        }

        void EnsureVectorIconOverlay(string iconStr)
        {
            string icon = NormalizeQuotedString(iconStr);
            if (string.IsNullOrEmpty(icon))
            {
                RemoveVectorIconOverlay();
                return;
            }
            if (_iconOverlay == null)
            {
                _iconOverlay = new VectorIconOverlay();
                Add(_iconOverlay);
            }
            _iconOverlay.Configure(icon, resolvedStyle.color);
            _iconOverlay.BringToFront();
        }

        void RemoveVectorIconOverlay()
        {
            if (_iconOverlay == null) return;
            if (_iconOverlay.parent == this)
                Remove(_iconOverlay);
            _iconOverlay = null;
        }

        void EnsureClipOverlay()
        {
            if (_clipPoints == null || _clipPoints.Length < 3 || _clipCoverColor.a <= 0.001f)
            {
                RemoveClipOverlay();
                return;
            }
            if (_clipOverlay == null)
            {
                _clipOverlay = new ClipMaskOverlay();
                Add(_clipOverlay);
            }
            _clipOverlay.Configure(_clipPoints, _clipCoverColor);
            _clipOverlay.BringToFront();
        }

        void RemoveClipOverlay()
        {
            if (_clipOverlay == null) return;
            if (_clipOverlay.parent == this)
                Remove(_clipOverlay);
            _clipOverlay = null;
        }

        void OnGenerateVisualContent(MeshGenerationContext ctx)
        {
            if (!_hasPaintWork)
                return;
            var rect = BorderBoxRect();
            var backgroundRect = BackgroundPaintRect(rect);
            var painter = ctx.painter2D;
            for (int i = _shadowLayers.Count - 1; i >= 0; i--)
                if (!_shadowLayers[i].Inset && _shadowLayers[i].Color.a > 0f)
                    PaintShadow(painter, rect, _shadowLayers[i]);
            bool shaderGradient = _gradientLayer != null && _gradientLayer.parent == this;
            if (_hasSolidBackground && !shaderGradient)
                PaintSolidBackground(painter, backgroundRect);
            if (!shaderGradient)
            {
                if (_gradient != null) PaintGradient(ctx, backgroundRect);
                if (_radialGradient2 != null) PaintRadialGradient(ctx, backgroundRect, _radialGradient2);
                if (_radialGradient != null) PaintRadialGradient(ctx, backgroundRect, _radialGradient);
                if (_tiledRadialPattern != null) PaintTiledRadialPattern(ctx, backgroundRect);
                if (_repeatingLinearPattern != null) PaintRepeatingLinearPattern(ctx, backgroundRect);
            }
            if (_clipOverlay == null && _clipPoints != null && _clipPoints.Length >= 3)
                PaintClipMask(painter, rect);
            bool insetOverlay = _insetShadowOverlay != null && _insetShadowOverlay.parent == this;
            for (int i = _shadowLayers.Count - 1; i >= 0; i--)
                if (!insetOverlay && _shadowLayers[i].Inset && _shadowLayers[i].Color.a > 0f)
                {
                    if (_clipPoints != null && _clipPoints.Length >= 3)
                        PaintClippedInnerShadow(painter, backgroundRect, _shadowLayers[i]);
                    else
                        PaintInnerShadow(painter, backgroundRect, _shadowLayers[i]);
                }
        }

        Rect BorderBoxRect()
        {
            var w = layout.width;
            var h = layout.height;
            if (w > 0f && h > 0f)
                return new Rect(0f, 0f, w, h);
            return contentRect;
        }

        Rect BackgroundPaintRect(Rect borderRect)
        {
            return InsetRect(
                borderRect,
                Mathf.Max(0f, resolvedStyle.borderLeftWidth),
                Mathf.Max(0f, resolvedStyle.borderTopWidth),
                Mathf.Max(0f, resolvedStyle.borderRightWidth),
                Mathf.Max(0f, resolvedStyle.borderBottomWidth));
        }

        static Rect InsetRect(Rect rect, float left, float top, float right, float bottom)
        {
            float xMin = rect.xMin + left;
            float yMin = rect.yMin + top;
            float xMax = rect.xMax - right;
            float yMax = rect.yMax - bottom;
            if (xMax < xMin)
            {
                float x = (xMin + xMax) * 0.5f;
                xMin = xMax = x;
            }
            if (yMax < yMin)
            {
                float y = (yMin + yMax) * 0.5f;
                yMin = yMax = y;
            }
            return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
        }

        void PaintClipMask(Painter2D p, Rect rect)
        {
            // Approximate a clip-path by painting OUTSIDE the polygon with the
            // parent's resolved background color (or transparent black) so the
            // visible area matches the polygon. A proper stencil would need a
            // shader; this produces the right shape against solid backgrounds.
            if (_clipCoverColor.a <= 0.001f) return;
            p.fillColor = _clipCoverColor;
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
                var pt = PolygonParser.Resolve(_clipPoints[i], rect);
                if (i == _clipPoints.Length - 1) p.MoveTo(pt);
                else p.LineTo(pt);
            }
            p.ClosePath();
            p.Fill(FillRule.NonZero);
        }

        Color ResolveAncestorBackgroundColor()
        {
            for (var p = parent; p != null; p = p.parent)
            {
                var c = p.resolvedStyle.backgroundColor;
                if (c.a > 0.001f)
                    return c;
            }
            return Color.clear;
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

        void PaintSolidBackground(Painter2D p, Rect rect)
        {
            p.fillColor = _solidBackgroundColor;
            p.BeginPath();
            if (_clipPoints != null && _clipPoints.Length >= 3)
            {
                for (int i = 0; i < _clipPoints.Length; i++)
                {
                    var pt = PolygonParser.Resolve(_clipPoints[i], rect);
                    if (i == 0) p.MoveTo(pt);
                    else p.LineTo(pt);
                }
                p.ClosePath();
            }
            else
            {
                RoundedRect(p, rect, resolvedStyle.borderTopLeftRadius);
            }
            p.Fill();
        }

        void PaintShadow(Painter2D p, Rect rect, ShadowLayer layer)
        {
            // Approximate a Gaussian shadow with N concentric semi-transparent
            // rounded rectangles. Cheap, no shader needed.
            float radius = resolvedStyle.borderTopLeftRadius;
            float spread = layer.Spread;
            Rect baseRect = new Rect(
                rect.x + layer.Offset.x - spread,
                rect.y + layer.Offset.y - spread,
                rect.width + spread * 2f,
                rect.height + spread * 2f);
            if (layer.Blur <= 0f)
            {
                p.fillColor = layer.Color;
                p.BeginPath();
                RoundedRect(p, baseRect, radius + Mathf.Max(0f, spread));
                RoundedRectReverse(p, rect, radius);
                p.Fill(FillRule.NonZero);
                return;
            }

            int steps = Mathf.Clamp(Mathf.CeilToInt(layer.Blur * 0.5f), 1, 8);
            for (int i = steps; i >= 1; i--)
            {
                float t = i / (float)steps;
                float expand = layer.Blur * t * 0.5f;
                Color c = layer.Color;
                c.a = layer.Color.a * (1f - t) * 0.6f;
                p.fillColor = c;
                p.BeginPath();
                RoundedRect(p,
                    new Rect(
                        baseRect.x - expand,
                        baseRect.y - expand,
                        baseRect.width + expand * 2f,
                        baseRect.height + expand * 2f),
                    radius + Mathf.Max(0f, spread) + expand);
                RoundedRectReverse(p, rect, radius);
                p.Fill(FillRule.NonZero);
            }
        }

        void PaintInnerShadow(Painter2D p, Rect rect, ShadowLayer layer)
        {
            float thickness = Mathf.Max(
                1f,
                Mathf.Abs(layer.Spread)
                + Mathf.Max(Mathf.Abs(layer.Offset.x), Mathf.Abs(layer.Offset.y))
                + layer.Blur);
            int steps = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(1f, thickness) * 0.5f), 1, 8);
            bool offsetH = Mathf.Abs(layer.Offset.x) > 0.001f;
            bool offsetV = Mathf.Abs(layer.Offset.y) > 0.001f;
            // 0/0 inset shadows render as a uniform ring (CSS draws an
            // inward feather on every side). The previous behaviour of
            // falling back to a single top strip produced a hard
            // horizontal bar across small circular elements like
            // border-radius:50% screws.
            bool symmetric = !offsetH && !offsetV;
            float outerRadius = resolvedStyle.borderTopLeftRadius;
            // 0/0 inset shadows render as a uniform inner ring so the
            // shadow follows the element's rounded corners. Painting four
            // axis-aligned strips here would draw a square inside a
            // circular border-radius element (e.g. screws), which leaks
            // sharp corners through the rounded bound.
            if (symmetric)
            {
                float halfMin = Mathf.Min(rect.width, rect.height) * 0.5f;
                if (halfMin <= 0.5f) return;
                // On very small elements (e.g. 4×4 screws) the ring
                // consumes most of the disc when rasterized at element
                // size, but Chrome paints at the scaled-up screen DPI
                // so the 1px feather is barely visible. Skip when the
                // ring would dominate the element.
                if (halfMin < thickness * 3f) return;
                // Cap thickness to leave a visible inner core (>= 1px).
                // Without this, the inner rect collapses to 0×0,
                // RoundedRectReverse clamps its radius to 0, and the
                // NonZero subtraction degenerates into a fully filled
                // circle — the "black dot" artifact.
                float ringThickness = Mathf.Min(thickness, halfMin - 0.5f);
                if (ringThickness <= 0f) return;
                for (int i = steps; i >= 1; i--)
                {
                    float t = i / (float)steps;
                    Color c = layer.Color;
                    c.a = layer.Color.a * t * 0.8f;
                    p.fillColor = c;
                    float band = ringThickness * (1f - (i - 1f) / steps);
                    Rect inner = new Rect(
                        rect.xMin + band,
                        rect.yMin + band,
                        Mathf.Max(0f, rect.width - band * 2f),
                        Mathf.Max(0f, rect.height - band * 2f));
                    p.BeginPath();
                    RoundedRect(p, rect, outerRadius);
                    RoundedRectReverse(p, inner, Mathf.Max(0f, outerRadius - band));
                    p.Fill(FillRule.NonZero);
                }
                return;
            }

            bool top = offsetV && layer.Offset.y >= 0f;
            bool bottom = offsetV && layer.Offset.y < 0f;
            bool left = offsetH && layer.Offset.x >= 0f;
            bool right = offsetH && layer.Offset.x < 0f;
            for (int i = steps; i >= 1; i--)
            {
                float t = i / (float)steps;
                Color c = layer.Color;
                c.a = layer.Color.a * t * 0.8f;
                p.fillColor = c;
                float band = thickness * (1f - (i - 1f) / steps);
                float r = Mathf.Min(outerRadius, band * 0.5f);
                if (top)
                    DrawInsetStrip(p, new Rect(rect.xMin, rect.yMin, rect.width, band), r);
                if (bottom)
                    DrawInsetStrip(p, new Rect(rect.xMin, rect.yMax - band, rect.width, band), r);
                if (left)
                    DrawInsetStrip(p, new Rect(rect.xMin, rect.yMin, band, rect.height), r);
                if (right)
                    DrawInsetStrip(p, new Rect(rect.xMax - band, rect.yMin, band, rect.height), r);
            }
        }

        void DrawInsetStrip(Painter2D p, Rect strip, float radius)
        {
            p.BeginPath();
            RoundedRect(p, strip, radius);
            p.Fill();
        }

        void PaintClippedInnerShadow(Painter2D p, Rect rect, ShadowLayer layer)
        {
            var polygon = ResolveClipPolygon(rect);
            if (polygon.Count < 3)
                return;

            float thickness = Mathf.Max(
                1f,
                Mathf.Abs(layer.Spread)
                + Mathf.Max(Mathf.Abs(layer.Offset.x), Mathf.Abs(layer.Offset.y))
                + layer.Blur);
            int steps = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(1f, thickness) * 0.5f), 1, 8);
            bool horizontal = Mathf.Abs(layer.Offset.x) > 0.001f;
            bool vertical = Mathf.Abs(layer.Offset.y) > 0.001f || !horizontal;
            for (int i = steps; i >= 1; i--)
            {
                float t = i / (float)steps;
                Color c = layer.Color;
                c.a = layer.Color.a * t * 0.8f;
                p.fillColor = c;
                float band = thickness * (1f - (i - 1f) / steps);
                if (vertical)
                {
                    var top = layer.Offset.y >= 0f;
                    Rect strip = top
                        ? new Rect(rect.xMin, rect.yMin, rect.width, band)
                        : new Rect(rect.xMin, rect.yMax - band, rect.width, band);
                    FillClippedPolygonBand(p, polygon, strip);
                }
                if (horizontal)
                {
                    var left = layer.Offset.x >= 0f;
                    Rect strip = left
                        ? new Rect(rect.xMin, rect.yMin, band, rect.height)
                        : new Rect(rect.xMax - band, rect.yMin, band, rect.height);
                    FillClippedPolygonBand(p, polygon, strip);
                }
            }
        }

        List<Vector2> ResolveClipPolygon(Rect rect)
        {
            var polygon = new List<Vector2>(_clipPoints.Length);
            for (int i = 0; i < _clipPoints.Length; i++)
                polygon.Add(PolygonParser.Resolve(_clipPoints[i], rect));
            return polygon;
        }

        static void FillClippedPolygonBand(Painter2D p, List<Vector2> polygon, Rect band)
        {
            var clipped = ClipPolygonToRect(polygon, band);
            if (clipped.Count < 3)
                return;

            p.BeginPath();
            p.MoveTo(clipped[0]);
            for (int i = 1; i < clipped.Count; i++)
                p.LineTo(clipped[i]);
            p.ClosePath();
            p.Fill();
        }

        static List<Vector2> ClipPolygonToRect(List<Vector2> polygon, Rect rect)
        {
            var output = new List<Vector2>(polygon);
            output = ClipPolygonEdge(output, v => v.x >= rect.xMin, (a, b) => IntersectVertical(a, b, rect.xMin));
            output = ClipPolygonEdge(output, v => v.x <= rect.xMax, (a, b) => IntersectVertical(a, b, rect.xMax));
            output = ClipPolygonEdge(output, v => v.y >= rect.yMin, (a, b) => IntersectHorizontal(a, b, rect.yMin));
            output = ClipPolygonEdge(output, v => v.y <= rect.yMax, (a, b) => IntersectHorizontal(a, b, rect.yMax));
            return output;
        }

        static List<Vector2> ClipPolygonEdge(
            List<Vector2> input,
            System.Func<Vector2, bool> inside,
            System.Func<Vector2, Vector2, Vector2> intersect)
        {
            var output = new List<Vector2>();
            if (input.Count == 0)
                return output;

            Vector2 prev = input[input.Count - 1];
            bool prevInside = inside(prev);
            for (int i = 0; i < input.Count; i++)
            {
                Vector2 curr = input[i];
                bool currInside = inside(curr);
                if (currInside)
                {
                    if (!prevInside)
                        output.Add(intersect(prev, curr));
                    output.Add(curr);
                }
                else if (prevInside)
                {
                    output.Add(intersect(prev, curr));
                }
                prev = curr;
                prevInside = currInside;
            }
            return output;
        }

        static Vector2 IntersectVertical(Vector2 a, Vector2 b, float x)
        {
            float dx = b.x - a.x;
            if (Mathf.Abs(dx) <= 0.0001f)
                return new Vector2(x, a.y);
            float t = Mathf.Clamp01((x - a.x) / dx);
            return new Vector2(x, Mathf.Lerp(a.y, b.y, t));
        }

        static Vector2 IntersectHorizontal(Vector2 a, Vector2 b, float y)
        {
            float dy = b.y - a.y;
            if (Mathf.Abs(dy) <= 0.0001f)
                return new Vector2(a.x, y);
            float t = Mathf.Clamp01((y - a.y) / dy);
            return new Vector2(Mathf.Lerp(a.x, b.x, t), y);
        }

        void PaintGradient(MeshGenerationContext ctx, Rect rect)
        {
            if (!_gradient.IsValid || rect.width <= 0f || rect.height <= 0f)
                return;

            if (_clipPoints != null && _clipPoints.Length >= 3)
            {
                PaintPolygonLinearGradient(ctx, rect);
                return;
            }

            float radius = RoundedRadius(rect);
            if (radius > 0.001f)
            {
                PaintRoundedLinearGradient(ctx, rect, radius);
                return;
            }

            int angle = Mathf.RoundToInt(NormalizeAngle(_gradient.AngleDegrees));
            if (angle == 0 || angle == 90 || angle == 180 || angle == 270)
            {
                PaintAxisAlignedLinearGradient(ctx, rect, angle);
                return;
            }

            Vector2[] corners = {
                new Vector2(rect.xMin, rect.yMin),
                new Vector2(rect.xMax, rect.yMin),
                new Vector2(rect.xMax, rect.yMax),
                new Vector2(rect.xMin, rect.yMax),
            };
            float rad = angle * Mathf.Deg2Rad;
            Vector2 dir = new Vector2(Mathf.Sin(rad), -Mathf.Cos(rad));
            float pMin = float.PositiveInfinity, pMax = float.NegativeInfinity;
            foreach (var c in corners)
            {
                float pr = Vector2.Dot(c, dir);
                pMin = Mathf.Min(pMin, pr);
                pMax = Mathf.Max(pMax, pr);
            }

            var data = ctx.Allocate(4, 6);
            var vertices = new Vertex[4];
            for (int i = 0; i < 4; i++)
            {
                float t = Mathf.InverseLerp(pMin, pMax, Vector2.Dot(corners[i], dir));
                vertices[i] = MakeVertex(corners[i], SampleGradient(_gradient, t));
            }
            data.SetAllVertices(vertices);
            data.SetAllIndices(new ushort[] { 0, 1, 2, 2, 3, 0 });
        }

        void PaintRoundedLinearGradient(MeshGenerationContext ctx, Rect rect, float radius)
        {
            var points = RoundedRectPoints(rect, radius);
            var vertices = new Vertex[points.Length + 1];
            var indices = new ushort[points.Length * 3];
            float angle = NormalizeAngle(_gradient.AngleDegrees);
            float rad = angle * Mathf.Deg2Rad;
            Vector2 dir = new Vector2(Mathf.Sin(rad), -Mathf.Cos(rad));
            Vector2[] corners = {
                new Vector2(rect.xMin, rect.yMin),
                new Vector2(rect.xMax, rect.yMin),
                new Vector2(rect.xMax, rect.yMax),
                new Vector2(rect.xMin, rect.yMax),
            };
            float pMin = float.PositiveInfinity;
            float pMax = float.NegativeInfinity;
            foreach (var c in corners)
            {
                float pr = Vector2.Dot(c, dir);
                pMin = Mathf.Min(pMin, pr);
                pMax = Mathf.Max(pMax, pr);
            }

            vertices[0] = MakeVertex(rect.center, SampleLinearGradientAtPoint(rect.center, dir, pMin, pMax));
            for (int i = 0; i < points.Length; i++)
            {
                Vector2 pt = points[i];
                vertices[i + 1] = MakeVertex(pt, SampleLinearGradientAtPoint(pt, dir, pMin, pMax));
                indices[i * 3 + 0] = 0;
                indices[i * 3 + 1] = (ushort)(i + 1);
                indices[i * 3 + 2] = (ushort)(((i + 1) % points.Length) + 1);
            }

            var data = ctx.Allocate(vertices.Length, indices.Length);
            data.SetAllVertices(vertices);
            data.SetAllIndices(indices);
        }

        // Paint the linear gradient inside the clip polygon by fan-triangulating
        // from the polygon's centroid and sampling the gradient at each polygon
        // vertex. Assumes the polygon is convex (all bridged clip-path uses are).
        // The gradient axis still uses the element rect's diagonal extent so the
        // CSS-spec stop positions stay consistent with non-clipped paints.
        void PaintPolygonLinearGradient(MeshGenerationContext ctx, Rect rect)
        {
            int n = _clipPoints.Length;
            float angle = NormalizeAngle(_gradient.AngleDegrees);
            float rad = angle * Mathf.Deg2Rad;
            Vector2 dir = new Vector2(Mathf.Sin(rad), -Mathf.Cos(rad));

            Vector2[] corners = {
                new Vector2(rect.xMin, rect.yMin),
                new Vector2(rect.xMax, rect.yMin),
                new Vector2(rect.xMax, rect.yMax),
                new Vector2(rect.xMin, rect.yMax),
            };
            float pMin = float.PositiveInfinity, pMax = float.NegativeInfinity;
            for (int i = 0; i < corners.Length; i++)
            {
                float pr = Vector2.Dot(corners[i], dir);
                pMin = Mathf.Min(pMin, pr);
                pMax = Mathf.Max(pMax, pr);
            }

            var polyPoints = new Vector2[n];
            Vector2 centroid = Vector2.zero;
            for (int i = 0; i < n; i++)
            {
                polyPoints[i] = PolygonParser.Resolve(_clipPoints[i], rect);
                centroid += polyPoints[i];
            }
            centroid /= n;

            var vertices = new Vertex[n + 1];
            var indices = new ushort[n * 3];
            vertices[0] = MakeVertex(centroid, SampleLinearGradientAtPoint(centroid, dir, pMin, pMax));
            for (int i = 0; i < n; i++)
            {
                Vector2 pt = polyPoints[i];
                vertices[i + 1] = MakeVertex(pt, SampleLinearGradientAtPoint(pt, dir, pMin, pMax));
                indices[i * 3 + 0] = 0;
                indices[i * 3 + 1] = (ushort)(i + 1);
                indices[i * 3 + 2] = (ushort)(((i + 1) % n) + 1);
            }

            var data = ctx.Allocate(vertices.Length, indices.Length);
            data.SetAllVertices(vertices);
            data.SetAllIndices(indices);
        }

        void PaintAxisAlignedLinearGradient(MeshGenerationContext ctx, Rect rect, int angle)
        {
            var stops = NormalizedStops(_gradient.Stops);
            int segments = Mathf.Max(1, stops.Count - 1);
            var vertices = new Vertex[segments * 4];
            var indices = new ushort[segments * 6];
            int vi = 0;
            int ii = 0;
            for (int i = 0; i < segments; i++)
            {
                var a = stops[i];
                var b = stops[i + 1];
                Rect strip;
                Color c0 = a.Color;
                Color c1 = b.Color;
                if (angle == 90 || angle == 270)
                {
                    float xA = GradientX(rect, angle, a.Position);
                    float xB = GradientX(rect, angle, b.Position);
                    float x0 = Mathf.Min(xA, xB);
                    float x1 = Mathf.Max(xA, xB);
                    strip = new Rect(x0, rect.yMin, Mathf.Max(0.001f, x1 - x0), rect.height);
                    if (xA > xB)
                    {
                        c0 = b.Color;
                        c1 = a.Color;
                    }
                    vertices[vi + 0] = MakeVertex(new Vector2(strip.xMin, strip.yMin), c0);
                    vertices[vi + 1] = MakeVertex(new Vector2(strip.xMax, strip.yMin), c1);
                    vertices[vi + 2] = MakeVertex(new Vector2(strip.xMax, strip.yMax), c1);
                    vertices[vi + 3] = MakeVertex(new Vector2(strip.xMin, strip.yMax), c0);
                }
                else
                {
                    float yA = GradientY(rect, angle, a.Position);
                    float yB = GradientY(rect, angle, b.Position);
                    float y0 = Mathf.Min(yA, yB);
                    float y1 = Mathf.Max(yA, yB);
                    strip = new Rect(rect.xMin, y0, rect.width, Mathf.Max(0.001f, y1 - y0));
                    if (yA > yB)
                    {
                        c0 = b.Color;
                        c1 = a.Color;
                    }
                    vertices[vi + 0] = MakeVertex(new Vector2(strip.xMin, strip.yMin), c0);
                    vertices[vi + 1] = MakeVertex(new Vector2(strip.xMax, strip.yMin), c0);
                    vertices[vi + 2] = MakeVertex(new Vector2(strip.xMax, strip.yMax), c1);
                    vertices[vi + 3] = MakeVertex(new Vector2(strip.xMin, strip.yMax), c1);
                }
                indices[ii + 0] = (ushort)(vi + 0);
                indices[ii + 1] = (ushort)(vi + 1);
                indices[ii + 2] = (ushort)(vi + 2);
                indices[ii + 3] = (ushort)(vi + 2);
                indices[ii + 4] = (ushort)(vi + 3);
                indices[ii + 5] = (ushort)(vi + 0);
                vi += 4;
                ii += 6;
            }

            var data = ctx.Allocate(vertices.Length, indices.Length);
            data.SetAllVertices(vertices);
            data.SetAllIndices(indices);
        }

        void PaintRadialGradient(MeshGenerationContext ctx, Rect rect, RadialGradient radialGradient)
        {
            if (radialGradient == null || !radialGradient.IsValid || rect.width <= 0f || rect.height <= 0f)
                return;
            Vector2 center = new Vector2(
                rect.xMin + rect.width * radialGradient.Center.x,
                rect.yMin + rect.height * radialGradient.Center.y);

            float left = Mathf.Abs(center.x - rect.xMin);
            float right = Mathf.Abs(rect.xMax - center.x);
            float top = Mathf.Abs(center.y - rect.yMin);
            float bottom = Mathf.Abs(rect.yMax - center.y);
            float rx = Mathf.Max(left, right);
            float ry = Mathf.Max(top, bottom);
            if (radialGradient.HasExplicitRadius)
            {
                rx = radialGradient.RadiusXIsPercent
                    ? radialGradient.Radius.x * rect.width
                    : radialGradient.Radius.x;
                ry = radialGradient.RadiusYIsPercent
                    ? radialGradient.Radius.y * rect.height
                    : radialGradient.Radius.y;
            }
            else if (radialGradient.IsCircle)
            {
                float farthest = 0f;
                farthest = Mathf.Max(farthest, Vector2.Distance(center, new Vector2(rect.xMin, rect.yMin)));
                farthest = Mathf.Max(farthest, Vector2.Distance(center, new Vector2(rect.xMax, rect.yMin)));
                farthest = Mathf.Max(farthest, Vector2.Distance(center, new Vector2(rect.xMax, rect.yMax)));
                farthest = Mathf.Max(farthest, Vector2.Distance(center, new Vector2(rect.xMin, rect.yMax)));
                rx = ry = farthest;
            }
            else
            {
                // CSS radial-gradient() defaults to ellipse farthest-corner.
                // Using side distances directly makes edge-origin washes end
                // too early and produces a hard visible oval in UITK. Expanding
                // both axes reaches the farthest corner and restores the long
                // browser-like feather.
                const float FarthestCornerEllipse = 1.41421356f;
                rx *= FarthestCornerEllipse;
                ry *= FarthestCornerEllipse;
            }
            rx = Mathf.Max(0.001f, rx);
            ry = Mathf.Max(0.001f, ry);

            if (IsCircleLikePaintRect(rect))
            {
                PaintCircularClippedRadialGradient(ctx, rect, radialGradient, center, rx, ry);
                return;
            }

            PaintRectClippedRadialGradient(ctx, rect, radialGradient, center, rx, ry);
        }

        static void PaintRectClippedRadialGradient(
            MeshGenerationContext ctx,
            Rect rect,
            RadialGradient radialGradient,
            Vector2 center,
            float rx,
            float ry)
        {
            int columns = Mathf.Clamp(Mathf.CeilToInt(rect.width / 24f), 8, 48);
            int rows = Mathf.Clamp(Mathf.CeilToInt(rect.height / 24f), 8, 32);
            int vertexCount = (columns + 1) * (rows + 1);
            int indexCount = columns * rows * 6;
            var vertices = new Vertex[vertexCount];
            var indices = new ushort[indexCount];

            int vi = 0;
            for (int row = 0; row <= rows; row++)
            {
                float v = row / (float)rows;
                float y = Mathf.Lerp(rect.yMin, rect.yMax, v);
                for (int col = 0; col <= columns; col++)
                {
                    float u = col / (float)columns;
                    float x = Mathf.Lerp(rect.xMin, rect.xMax, u);
                    var pt = new Vector2(x, y);
                    vertices[vi++] = MakeVertex(
                        pt,
                        SampleRadialGradientAtPoint(pt, center, rx, ry, radialGradient));
                }
            }

            int ii = 0;
            for (int row = 0; row < rows; row++)
            {
                int rowStart = row * (columns + 1);
                int nextRowStart = (row + 1) * (columns + 1);
                for (int col = 0; col < columns; col++)
                {
                    int a = rowStart + col;
                    int b = a + 1;
                    int c = nextRowStart + col;
                    int d = c + 1;
                    indices[ii++] = (ushort)a;
                    indices[ii++] = (ushort)c;
                    indices[ii++] = (ushort)d;
                    indices[ii++] = (ushort)d;
                    indices[ii++] = (ushort)b;
                    indices[ii++] = (ushort)a;
                }
            }

            var data = ctx.Allocate(vertices.Length, indices.Length);
            data.SetAllVertices(vertices);
            data.SetAllIndices(indices);
        }

        bool IsCircleLikePaintRect(Rect rect)
        {
            float min = Mathf.Min(rect.width, rect.height);
            float max = Mathf.Max(rect.width, rect.height);
            if (min <= 0f || max - min > Mathf.Max(1f, min * 0.08f))
                return false;

            float radius = Mathf.Min(
                Mathf.Min(resolvedStyle.borderTopLeftRadius, resolvedStyle.borderTopRightRadius),
                Mathf.Min(resolvedStyle.borderBottomRightRadius, resolvedStyle.borderBottomLeftRadius));
            return radius >= min * 0.45f;
        }

        static void PaintCircularClippedRadialGradient(
            MeshGenerationContext ctx,
            Rect rect,
            RadialGradient radialGradient,
            Vector2 gradientCenter,
            float rx,
            float ry)
        {
            float circleRadius = Mathf.Min(rect.width, rect.height) * 0.5f;
            if (circleRadius <= 0f)
                return;

            int segments = Mathf.Clamp(Mathf.CeilToInt(circleRadius * 8f), 20, 64);
            int rings = Mathf.Clamp(Mathf.CeilToInt(circleRadius * 2f), 3, 12);
            int vertexCount = 1 + rings * segments;
            int indexCount = segments * 3 + (rings - 1) * segments * 6;
            var vertices = new Vertex[vertexCount];
            var indices = new ushort[indexCount];
            Vector2 shapeCenter = rect.center;
            vertices[0] = MakeVertex(
                shapeCenter,
                SampleRadialGradientAtPoint(shapeCenter, gradientCenter, rx, ry, radialGradient));

            int vi = 1;
            for (int ring = 1; ring <= rings; ring++)
            {
                float r = circleRadius * ring / rings;
                for (int s = 0; s < segments; s++)
                {
                    float a = s / (float)segments * Mathf.PI * 2f;
                    var pt = new Vector2(
                        shapeCenter.x + Mathf.Cos(a) * r,
                        shapeCenter.y + Mathf.Sin(a) * r);
                    vertices[vi++] = MakeVertex(
                        pt,
                        SampleRadialGradientAtPoint(pt, gradientCenter, rx, ry, radialGradient));
                }
            }

            int ii = 0;
            for (int s = 0; s < segments; s++)
            {
                indices[ii++] = 0;
                indices[ii++] = (ushort)(1 + s);
                indices[ii++] = (ushort)(1 + ((s + 1) % segments));
            }
            for (int ring = 2; ring <= rings; ring++)
            {
                int prev = 1 + (ring - 2) * segments;
                int curr = 1 + (ring - 1) * segments;
                for (int s = 0; s < segments; s++)
                {
                    int sn = (s + 1) % segments;
                    indices[ii++] = (ushort)(prev + s);
                    indices[ii++] = (ushort)(curr + s);
                    indices[ii++] = (ushort)(curr + sn);
                    indices[ii++] = (ushort)(curr + sn);
                    indices[ii++] = (ushort)(prev + sn);
                    indices[ii++] = (ushort)(prev + s);
                }
            }

            var data = ctx.Allocate(vertices.Length, indices.Length);
            data.SetAllVertices(vertices);
            data.SetAllIndices(indices);
        }

        static Color SampleRadialGradientAtPoint(
            Vector2 point,
            Vector2 center,
            float rx,
            float ry,
            RadialGradient radialGradient)
        {
            float dx = (point.x - center.x) / Mathf.Max(0.001f, rx);
            float dy = (point.y - center.y) / Mathf.Max(0.001f, ry);
            return SampleGradient(radialGradient, Mathf.Sqrt(dx * dx + dy * dy));
        }

        void PaintTiledRadialPattern(MeshGenerationContext ctx, Rect rect)
        {
            if (_tiledRadialPattern == null || _tiledRadialPattern.Color.a <= 0f)
                return;

            float tileW = Mathf.Max(1f, _patternSize.x);
            float tileH = Mathf.Max(1f, _patternSize.y);
            // CSS radial-dot textures such as
            // radial-gradient(rgba(...) 1px, transparent 1px) are antialiased
            // circular image tiles in the browser. A full 2r square quad reads
            // much larger/heavier in UI Toolkit, so draw a small radial fan
            // with a transparent edge instead.
            float radius = Mathf.Max(0.25f, _tiledRadialPattern.RadiusPx * 0.85f);
            float centerX = tileW * _tiledRadialPattern.Center.x;
            float centerY = tileH * _tiledRadialPattern.Center.y;
            const int DotSegments = 6;
            int verticesPerDot = DotSegments + 1;
            int indicesPerDot = DotSegments * 3;

            int columns = Mathf.CeilToInt(rect.width / tileW) + 3;
            int rows = Mathf.CeilToInt(rect.height / tileH) + 3;
            if (columns <= 0 || rows <= 0)
                return;

            float firstX = rect.xMin + _patternPosition.x + centerX;
            float firstY = rect.yMin + _patternPosition.y + centerY;
            while (firstX > rect.xMin - radius) firstX -= tileW;
            while (firstY > rect.yMin - radius) firstY -= tileH;

            int dotCount = 0;
            for (int row = 0; row < rows; row++)
            {
                float y = firstY + row * tileH;
                if (y < rect.yMin - radius || y > rect.yMax + radius)
                    continue;
                for (int col = 0; col < columns; col++)
                {
                    float x = firstX + col * tileW;
                    if (x < rect.xMin - radius || x > rect.xMax + radius)
                        continue;
                    dotCount++;
                }
            }
            if (dotCount <= 0 || dotCount * verticesPerDot > 65000)
                return;

            var data = ctx.Allocate(dotCount * verticesPerDot, dotCount * indicesPerDot);
            var vertices = new Vertex[dotCount * verticesPerDot];
            var indices = new ushort[dotCount * indicesPerDot];
            int vi = 0;
            int ii = 0;
            Color color = _tiledRadialPattern.Color;
            Color edgeColor = color;
            edgeColor.a = 0f;
            for (int row = 0; row < rows; row++)
            {
                float y = firstY + row * tileH;
                if (y < rect.yMin - radius || y > rect.yMax + radius)
                    continue;
                for (int col = 0; col < columns; col++)
                {
                    float x = firstX + col * tileW;
                    if (x < rect.xMin - radius || x > rect.xMax + radius)
                        continue;
                    int centerIndex = vi;
                    vertices[vi++] = MakeVertex(new Vector2(x, y), color);
                    for (int s = 0; s < DotSegments; s++)
                    {
                        float angle = (s / (float)DotSegments) * Mathf.PI * 2f;
                        vertices[vi++] = MakeVertex(
                            new Vector2(
                                x + Mathf.Cos(angle) * radius,
                                y + Mathf.Sin(angle) * radius),
                            edgeColor);
                    }
                    for (int s = 0; s < DotSegments; s++)
                    {
                        indices[ii++] = (ushort)centerIndex;
                        indices[ii++] = (ushort)(centerIndex + 1 + s);
                        indices[ii++] = (ushort)(centerIndex + 1 + ((s + 1) % DotSegments));
                    }
                }
            }
            data.SetAllVertices(vertices);
            data.SetAllIndices(indices);
        }

        void PaintRepeatingLinearPattern(MeshGenerationContext ctx, Rect rect)
        {
            if (_repeatingLinearPattern == null || !_repeatingLinearPattern.IsValid)
                return;

            int angle = Mathf.RoundToInt(NormalizeAngle(_repeatingLinearPattern.AngleDegrees));
            bool vertical = angle == 90 || angle == 270;
            bool horizontal = angle == 0 || angle == 180;
            if (!vertical && !horizontal)
                return;

            float axisLength = vertical ? rect.width : rect.height;
            if (axisLength <= 0f)
                return;

            var stops = _repeatingLinearPattern.ResolveStops(axisLength);
            if (stops.Count < 2)
                return;

            float period = Mathf.Max(0.001f, stops[stops.Count - 1].PositionPx);
            float offset = vertical ? _patternPosition.x : _patternPosition.y;
            offset %= period;
            if (offset > 0f)
                offset -= period;

            int repeatCount = Mathf.CeilToInt(axisLength / period) + 3;
            int maxQuads = repeatCount * (stops.Count - 1);
            if (maxQuads <= 0 || maxQuads > 4096)
                return;

            var vertices = new List<Vertex>(maxQuads * 4);
            var indices = new List<ushort>(maxQuads * 6);
            for (int repeat = 0; repeat < repeatCount; repeat++)
            {
                float basePos = offset + repeat * period;
                for (int i = 0; i < stops.Count - 1; i++)
                {
                    var a = stops[i];
                    var b = stops[i + 1];
                    float p0 = Mathf.Max(0f, basePos + a.PositionPx);
                    float p1 = Mathf.Min(axisLength, basePos + b.PositionPx);
                    if (p1 <= 0f || p0 >= axisLength || p1 - p0 <= 0.001f)
                        continue;
                    if (a.Color.a <= 0f && b.Color.a <= 0f)
                        continue;
                    AddPatternQuad(vertices, indices, rect, vertical, p0, p1, a.Color, b.Color);
                }
            }

            if (vertices.Count == 0)
                return;
            var data = ctx.Allocate(vertices.Count, indices.Count);
            data.SetAllVertices(vertices.ToArray());
            data.SetAllIndices(indices.ToArray());
        }

        static void AddPatternQuad(
            List<Vertex> vertices,
            List<ushort> indices,
            Rect rect,
            bool vertical,
            float p0,
            float p1,
            Color c0,
            Color c1)
        {
            if (vertices.Count > ushort.MaxValue - 4)
                return;
            ushort vi = (ushort)vertices.Count;
            if (vertical)
            {
                float x0 = rect.xMin + p0;
                float x1 = rect.xMin + p1;
                vertices.Add(MakeVertex(new Vector2(x0, rect.yMin), c0));
                vertices.Add(MakeVertex(new Vector2(x1, rect.yMin), c1));
                vertices.Add(MakeVertex(new Vector2(x1, rect.yMax), c1));
                vertices.Add(MakeVertex(new Vector2(x0, rect.yMax), c0));
            }
            else
            {
                float y0 = rect.yMin + p0;
                float y1 = rect.yMin + p1;
                vertices.Add(MakeVertex(new Vector2(rect.xMin, y0), c0));
                vertices.Add(MakeVertex(new Vector2(rect.xMax, y0), c0));
                vertices.Add(MakeVertex(new Vector2(rect.xMax, y1), c1));
                vertices.Add(MakeVertex(new Vector2(rect.xMin, y1), c1));
            }
            indices.Add(vi);
            indices.Add((ushort)(vi + 1));
            indices.Add((ushort)(vi + 2));
            indices.Add((ushort)(vi + 2));
            indices.Add((ushort)(vi + 3));
            indices.Add(vi);
        }

        static float NormalizeAngle(float angle)
        {
            return (angle % 360f + 360f) % 360f;
        }

        static float GradientX(Rect r, int angle, float t)
        {
            return angle == 270
                ? r.xMax - r.width * t
                : r.xMin + r.width * t;
        }

        static float GradientY(Rect r, int angle, float t)
        {
            return angle == 0
                ? r.yMax - r.height * t
                : r.yMin + r.height * t;
        }

        static List<GradientStop> NormalizedStops(List<GradientStop> input)
        {
            var stops = new List<GradientStop>(input);
            stops.Sort((a, b) => a.Position.CompareTo(b.Position));
            if (stops.Count == 0)
                return stops;
            if (stops[0].Position > 0f)
                stops.Insert(0, new GradientStop(stops[0].Color, 0f));
            if (stops[stops.Count - 1].Position < 1f)
                stops.Add(new GradientStop(stops[stops.Count - 1].Color, 1f));
            return stops;
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

        static Color SampleGradient(LinearGradient g, float t)
        {
            return SampleStops(g.Stops, t);
        }

        Color SampleLinearGradientAtPoint(Vector2 point, Vector2 dir, float pMin, float pMax)
        {
            float t = Mathf.InverseLerp(pMin, pMax, Vector2.Dot(point, dir));
            return SampleGradient(_gradient, t);
        }

        static Color SampleGradient(RadialGradient g, float t)
        {
            return SampleStops(g.Stops, t);
        }

        static Color SampleStops(System.Collections.Generic.List<GradientStop> stops, float t)
        {
            t = Mathf.Clamp01(t);
            for (int i = 1; i < stops.Count; i++)
            {
                if (t <= stops[i].Position)
                {
                    var a = stops[i - 1];
                    var b = stops[i];
                    float span = Mathf.Max(0.0001f, b.Position - a.Position);
                    float u = (t - a.Position) / span;
                    return Color.Lerp(a.Color, b.Color, u);
                }
            }
            return stops[stops.Count - 1].Color;
        }

        static void EllipsePath(Painter2D p, Vector2 center, float rx, float ry)
        {
            int segments = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(rx, ry) * 3f), 64, 192);
            for (int i = 0; i < segments; i++)
            {
                float a = i / (float)segments * Mathf.PI * 2f;
                var pt = new Vector2(
                    center.x + Mathf.Cos(a) * rx,
                    center.y + Mathf.Sin(a) * ry);
                if (i == 0) p.MoveTo(pt);
                else p.LineTo(pt);
            }
            p.ClosePath();
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

        float RoundedRadius(Rect rect)
        {
            return Mathf.Clamp(
                resolvedStyle.borderTopLeftRadius,
                0f,
                Mathf.Min(rect.width, rect.height) * 0.5f);
        }

        static Vector2[] RoundedRectPoints(Rect rect, float radius)
        {
            int segmentsPerCorner = RoundedCornerSegmentCount(radius);
            var points = new Vector2[segmentsPerCorner * 4];
            int i = 0;
            AddArc(points, ref i, new Vector2(rect.xMin + radius, rect.yMin + radius), radius, 180f, 270f, segmentsPerCorner);
            AddArc(points, ref i, new Vector2(rect.xMax - radius, rect.yMin + radius), radius, 270f, 360f, segmentsPerCorner);
            AddArc(points, ref i, new Vector2(rect.xMax - radius, rect.yMax - radius), radius, 0f, 90f, segmentsPerCorner);
            AddArc(points, ref i, new Vector2(rect.xMin + radius, rect.yMax - radius), radius, 90f, 180f, segmentsPerCorner);
            return points;
        }

        static int RoundedCornerSegmentCount(float radius)
        {
            return Mathf.Clamp(Mathf.CeilToInt(radius * 0.75f), 12, 64);
        }

        static void AddArc(Vector2[] points, ref int index, Vector2 center, float radius, float fromDeg, float toDeg, int segments)
        {
            segments = Mathf.Max(2, segments);
            for (int i = 0; i < segments; i++)
            {
                float t = i / (float)(segments - 1);
                float a = Mathf.Lerp(fromDeg, toDeg, t) * Mathf.Deg2Rad;
                points[index++] = new Vector2(center.x + Mathf.Cos(a) * radius, center.y + Mathf.Sin(a) * radius);
            }
        }

        static void RoundedRectReverse(Painter2D p, Rect r, float radius)
        {
            radius = Mathf.Clamp(radius, 0f, Mathf.Min(r.width, r.height) * 0.5f);
            if (radius <= 0f)
            {
                p.MoveTo(new Vector2(r.xMin, r.yMin));
                p.LineTo(new Vector2(r.xMin, r.yMax));
                p.LineTo(new Vector2(r.xMax, r.yMax));
                p.LineTo(new Vector2(r.xMax, r.yMin));
                p.ClosePath();
                return;
            }
            p.MoveTo(new Vector2(r.xMin, r.yMin + radius));
            p.LineTo(new Vector2(r.xMin, r.yMax - radius));
            p.Arc(new Vector2(r.xMin + radius, r.yMax - radius), radius, 180f, 90f);
            p.LineTo(new Vector2(r.xMax - radius, r.yMax));
            p.Arc(new Vector2(r.xMax - radius, r.yMax - radius), radius, 90f, 0f);
            p.LineTo(new Vector2(r.xMax, r.yMin + radius));
            p.Arc(new Vector2(r.xMax - radius, r.yMin + radius), radius, 0f, -90f);
            p.LineTo(new Vector2(r.xMin + radius, r.yMin));
            p.Arc(new Vector2(r.xMin + radius, r.yMin + radius), radius, -90f, -180f);
            p.ClosePath();
        }

        sealed class CssGradientLayer : VisualElement
        {
            const int MaxStops = 8;
            const string ShaderName = "Hidden/ODDGames/html2uxml/CssGradient";

            static readonly int FallbackColorId = Shader.PropertyToID("_FallbackColor");
            static readonly int LinearEnabledId = Shader.PropertyToID("_LinearEnabled");
            static readonly int LinearAngleId = Shader.PropertyToID("_LinearAngle");
            static readonly int LinearCountId = Shader.PropertyToID("_LinearCount");
            static readonly int Radial1EnabledId = Shader.PropertyToID("_Radial1Enabled");
            static readonly int Radial1CountId = Shader.PropertyToID("_Radial1Count");
            static readonly int Radial1CenterId = Shader.PropertyToID("_Radial1Center");
            static readonly int Radial1RadiusId = Shader.PropertyToID("_Radial1Radius");
            static readonly int Radial2EnabledId = Shader.PropertyToID("_Radial2Enabled");
            static readonly int Radial2CountId = Shader.PropertyToID("_Radial2Count");
            static readonly int Radial2CenterId = Shader.PropertyToID("_Radial2Center");
            static readonly int Radial2RadiusId = Shader.PropertyToID("_Radial2Radius");

            static readonly int[] LinearColorIds = StopIds("_LinearColor");
            static readonly int[] LinearPosIds = StopIds("_LinearPos");
            static readonly int[] Radial1ColorIds = StopIds("_Radial1Color");
            static readonly int[] Radial1PosIds = StopIds("_Radial1Pos");
            static readonly int[] Radial2ColorIds = StopIds("_Radial2Color");
            static readonly int[] Radial2PosIds = StopIds("_Radial2Pos");

            LinearGradient _linear;
            RadialGradient _radial1;
            RadialGradient _radial2;
            Color _fallbackColor;
            float _radius;
            PolygonCorner[] _clipPoints;
            Material _material;

            public CssGradientLayer()
            {
                pickingMode = PickingMode.Ignore;
                focusable = false;
                style.position = Position.Absolute;
                style.left = 0f;
                style.top = 0f;
                style.right = 0f;
                style.bottom = 0f;
                style.backgroundColor = Color.clear;
                generateVisualContent += OnGenerateVisualContent;
                RegisterCallback<DetachFromPanelEvent>(_ => DisposeMaterial());
            }

            public bool Configure(
                LinearGradient linear,
                RadialGradient radial1,
                RadialGradient radial2,
                Color fallbackColor,
                float radius,
                PolygonCorner[] clipPoints)
            {
                if (!EnsureMaterial())
                    return false;
                _linear = linear;
                _radial1 = radial1;
                _radial2 = radial2;
                _fallbackColor = fallbackColor;
                _radius = Mathf.Max(0f, radius);
                _clipPoints = clipPoints;
                // MarkDirtyRepaint elided: caller dispatches us from a style
                // or attach path; Unity's own version bump covers the repaint
                // and an explicit call here would throw "cannot change render
                // data during visual tree rendering" inside UI Builder previews.
                return true;
            }

            public void DisposeMaterial()
            {
                if (_material == null)
                    return;
                style.unityMaterial = null;
                if (Application.isPlaying)
                    UnityEngine.Object.Destroy(_material);
                else
                    UnityEngine.Object.DestroyImmediate(_material);
                _material = null;
            }

            bool EnsureMaterial()
            {
                if (_material != null)
                    return true;
                var shader = Shader.Find(ShaderName);
                if (shader == null)
                    return false;
                _material = new Material(shader)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                style.unityMaterial = _material;
                return true;
            }

            void OnGenerateVisualContent(MeshGenerationContext ctx)
            {
                if (_material == null)
                    return;
                var rect = BorderBoxRect(this);
                if (rect.width <= 0f || rect.height <= 0f)
                    return;

                ApplyMaterialProperties(rect);
                if (_clipPoints != null && _clipPoints.Length >= 3)
                {
                    var polygonVertices = new Vertex[_clipPoints.Length + 1];
                    var polygonIndices = new ushort[_clipPoints.Length * 3];
                    var polygonTint = (Color32)Color.white;
                    polygonVertices[0] = MakeUvVertex(rect.center.x, rect.center.y, 0.5f, 0.5f, polygonTint);
                    float invW = rect.width > 0f ? 1f / rect.width : 0f;
                    float invH = rect.height > 0f ? 1f / rect.height : 0f;
                    for (int i = 0; i < _clipPoints.Length; i++)
                    {
                        var pt = PolygonParser.Resolve(_clipPoints[i], rect);
                        float u = (pt.x - rect.xMin) * invW;
                        float v = (pt.y - rect.yMin) * invH;
                        polygonVertices[i + 1] = MakeUvVertex(pt.x, pt.y, u, v, polygonTint);
                        polygonIndices[i * 3 + 0] = 0;
                        polygonIndices[i * 3 + 1] = (ushort)(i + 1);
                        polygonIndices[i * 3 + 2] = (ushort)(((i + 1) % _clipPoints.Length) + 1);
                    }
                    var polygonData = ctx.Allocate(polygonVertices.Length, polygonIndices.Length, (Texture)null);
                    polygonData.SetAllVertices(polygonVertices);
                    polygonData.SetAllIndices(polygonIndices);
                    return;
                }

                float radius = Mathf.Clamp(_radius, 0f, Mathf.Min(rect.width, rect.height) * 0.5f);
                if (radius <= 0.001f)
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

                var points = RoundedRectPoints(rect, radius);
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

            void ApplyMaterialProperties(Rect rect)
            {
                _material.SetColor(FallbackColorId, _fallbackColor);
                if (_linear != null && _linear.IsValid)
                {
                    _material.SetFloat(LinearEnabledId, 1f);
                    _material.SetFloat(LinearAngleId, _linear.AngleDegrees);
                    SetStops(_material, _linear.Stops, LinearCountId, LinearColorIds, LinearPosIds);
                }
                else
                {
                    _material.SetFloat(LinearEnabledId, 0f);
                    _material.SetFloat(LinearCountId, 0f);
                }

                ApplyRadial(_material, _radial1, rect, Radial1EnabledId, Radial1CountId,
                    Radial1CenterId, Radial1RadiusId, Radial1ColorIds, Radial1PosIds);
                ApplyRadial(_material, _radial2, rect, Radial2EnabledId, Radial2CountId,
                    Radial2CenterId, Radial2RadiusId, Radial2ColorIds, Radial2PosIds);
            }

            static void ApplyRadial(
                Material material,
                RadialGradient radial,
                Rect rect,
                int enabledId,
                int countId,
                int centerId,
                int radiusId,
                int[] colorIds,
                int[] posIds)
            {
                if (radial == null || !radial.IsValid)
                {
                    material.SetFloat(enabledId, 0f);
                    material.SetFloat(countId, 0f);
                    return;
                }

                material.SetFloat(enabledId, 1f);
                material.SetVector(centerId, new Vector4(radial.Center.x, radial.Center.y, 0f, 0f));
                material.SetVector(radiusId, RadiusUv(radial, rect));
                SetStops(material, radial.Stops, countId, colorIds, posIds);
            }

            static Vector4 RadiusUv(RadialGradient radial, Rect rect)
            {
                float rx;
                float ry;
                if (radial.HasExplicitRadius)
                {
                    rx = radial.RadiusXIsPercent
                        ? radial.Radius.x
                        : radial.Radius.x / Mathf.Max(0.001f, rect.width);
                    ry = radial.RadiusYIsPercent
                        ? radial.Radius.y
                        : radial.Radius.y / Mathf.Max(0.001f, rect.height);
                }
                else if (radial.IsCircle)
                {
                    Vector2 centerPx = new Vector2(radial.Center.x * rect.width, radial.Center.y * rect.height);
                    float farthest = 0f;
                    farthest = Mathf.Max(farthest, Vector2.Distance(centerPx, new Vector2(0f, 0f)));
                    farthest = Mathf.Max(farthest, Vector2.Distance(centerPx, new Vector2(rect.width, 0f)));
                    farthest = Mathf.Max(farthest, Vector2.Distance(centerPx, new Vector2(rect.width, rect.height)));
                    farthest = Mathf.Max(farthest, Vector2.Distance(centerPx, new Vector2(0f, rect.height)));
                    rx = farthest / Mathf.Max(0.001f, rect.width);
                    ry = farthest / Mathf.Max(0.001f, rect.height);
                }
                else
                {
                    const float FarthestCornerEllipse = 1.41421356f;
                    rx = Mathf.Max(radial.Center.x, 1f - radial.Center.x) * FarthestCornerEllipse;
                    ry = Mathf.Max(radial.Center.y, 1f - radial.Center.y) * FarthestCornerEllipse;
                }

                return new Vector4(Mathf.Max(0.001f, rx), Mathf.Max(0.001f, ry), 0f, 0f);
            }

            static void SetStops(
                Material material,
                List<GradientStop> sourceStops,
                int countId,
                int[] colorIds,
                int[] posIds)
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

            static Vector2[] RoundedRectPoints(Rect rect, float radius)
            {
                int segmentsPerCorner = RoundedCornerSegmentCount(radius);
                var points = new Vector2[(segmentsPerCorner + 1) * 4];
                int i = 0;
                AddArc(points, ref i, new Vector2(rect.xMin + radius, rect.yMin + radius), radius, 180f, 270f, segmentsPerCorner);
                AddArc(points, ref i, new Vector2(rect.xMax - radius, rect.yMin + radius), radius, 270f, 360f, segmentsPerCorner);
                AddArc(points, ref i, new Vector2(rect.xMax - radius, rect.yMax - radius), radius, 0f, 90f, segmentsPerCorner);
                AddArc(points, ref i, new Vector2(rect.xMin + radius, rect.yMax - radius), radius, 90f, 180f, segmentsPerCorner);
                return points;
            }

            static int RoundedCornerSegmentCount(float radius)
            {
                return Mathf.Clamp(Mathf.CeilToInt(radius * 0.75f), 12, 64);
            }

            static void AddArc(Vector2[] points, ref int index, Vector2 center, float radius, float fromDeg, float toDeg, int segments)
            {
                segments = Mathf.Max(2, segments);
                for (int s = 0; s <= segments; s++)
                {
                    float t = s / (float)segments;
                    float a = Mathf.Lerp(fromDeg, toDeg, t) * Mathf.Deg2Rad;
                    points[index++] = new Vector2(center.x + Mathf.Cos(a) * radius, center.y + Mathf.Sin(a) * radius);
                }
            }

            static int[] StopIds(string prefix)
            {
                var ids = new int[MaxStops];
                for (int i = 0; i < MaxStops; i++)
                    ids[i] = Shader.PropertyToID(prefix + i);
                return ids;
            }
        }

        sealed class ClipMaskOverlay : VisualElement
        {
            PolygonCorner[] _points;
            Color _coverColor;

            public ClipMaskOverlay()
            {
                pickingMode = PickingMode.Ignore;
                focusable = false;
                style.position = Position.Absolute;
                style.left = 0f;
                style.top = 0f;
                style.right = 0f;
                style.bottom = 0f;
                generateVisualContent += OnGenerateVisualContent;
            }

            public void Configure(PolygonCorner[] points, Color coverColor)
            {
                _points = points;
                _coverColor = coverColor;
                // MarkDirtyRepaint elided: caller dispatches us from a style
                // or attach path; Unity's own version bump covers the repaint
                // and an explicit call here would throw "cannot change render
                // data during visual tree rendering" inside UI Builder previews.
            }

            void OnGenerateVisualContent(MeshGenerationContext ctx)
            {
                if (_points == null || _points.Length < 3 || _coverColor.a <= 0.001f)
                    return;
                var rect = BorderBoxRect(this);
                if (rect.width <= 0f || rect.height <= 0f)
                    return;

                var p = ctx.painter2D;
                p.fillColor = _coverColor;
                p.BeginPath();
                p.MoveTo(new Vector2(rect.xMin, rect.yMin));
                p.LineTo(new Vector2(rect.xMax, rect.yMin));
                p.LineTo(new Vector2(rect.xMax, rect.yMax));
                p.LineTo(new Vector2(rect.xMin, rect.yMax));
                p.ClosePath();
                for (int i = _points.Length - 1; i >= 0; i--)
                {
                    var pt = PolygonParser.Resolve(_points[i], rect);
                    if (i == _points.Length - 1) p.MoveTo(pt);
                    else p.LineTo(pt);
                }
                p.ClosePath();
                p.Fill(FillRule.NonZero);
            }

            static Rect BorderBoxRect(VisualElement element)
            {
                var w = element.layout.width;
                var h = element.layout.height;
                if (w > 0f && h > 0f)
                    return new Rect(0f, 0f, w, h);
                return element.contentRect;
            }
        }

        sealed class InsetShadowOverlay : VisualElement
        {
            List<ShadowLayer> _layers = new List<ShadowLayer>();
            float _radius;

            public InsetShadowOverlay()
            {
                pickingMode = PickingMode.Ignore;
                focusable = false;
                style.position = Position.Absolute;
                style.left = 0f;
                style.top = 0f;
                style.right = 0f;
                style.bottom = 0f;
                generateVisualContent += OnGenerateVisualContent;
            }

            public void Configure(List<ShadowLayer> layers, float radius)
            {
                _layers = layers ?? new List<ShadowLayer>();
                _radius = Mathf.Max(0f, radius);
                // MarkDirtyRepaint elided: caller dispatches us from a style
                // or attach path; Unity's own version bump covers the repaint
                // and an explicit call here would throw "cannot change render
                // data during visual tree rendering" inside UI Builder previews.
            }

            void OnGenerateVisualContent(MeshGenerationContext ctx)
            {
                if (_layers == null || _layers.Count == 0)
                    return;
                var rect = BorderBoxRect(this);
                if (rect.width <= 0f || rect.height <= 0f)
                    return;
                var painter = ctx.painter2D;
                for (int i = _layers.Count - 1; i >= 0; i--)
                {
                    if (_layers[i].Color.a > 0f)
                        PaintInset(painter, rect, _layers[i], _radius);
                }
            }

            static void PaintInset(Painter2D p, Rect rect, ShadowLayer layer, float radius)
            {
                float thickness = Mathf.Max(
                    1f,
                    Mathf.Abs(layer.Spread)
                    + Mathf.Max(Mathf.Abs(layer.Offset.x), Mathf.Abs(layer.Offset.y))
                    + layer.Blur);
                int steps = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(1f, thickness) * 0.5f), 1, 8);
                bool horizontal = Mathf.Abs(layer.Offset.x) > 0.001f;
                bool vertical = Mathf.Abs(layer.Offset.y) > 0.001f || !horizontal;
                for (int i = steps; i >= 1; i--)
                {
                    float t = i / (float)steps;
                    Color c = layer.Color;
                    c.a = layer.Color.a * t * 0.8f;
                    p.fillColor = c;
                    float band = thickness * (1f - (i - 1f) / steps);
                    if (vertical)
                    {
                        var top = layer.Offset.y >= 0f;
                        Rect strip = top
                            ? new Rect(rect.xMin, rect.yMin, rect.width, band)
                            : new Rect(rect.xMin, rect.yMax - band, rect.width, band);
                        p.BeginPath();
                        RoundedRect(p, strip, Mathf.Min(radius, band * 0.5f));
                        p.Fill();
                    }
                    if (horizontal)
                    {
                        var left = layer.Offset.x >= 0f;
                        Rect strip = left
                            ? new Rect(rect.xMin, rect.yMin, band, rect.height)
                            : new Rect(rect.xMax - band, rect.yMin, band, rect.height);
                        p.BeginPath();
                        RoundedRect(p, strip, Mathf.Min(radius, band * 0.5f));
                        p.Fill();
                    }
                }
            }

            static Rect BorderBoxRect(VisualElement element)
            {
                var w = element.layout.width;
                var h = element.layout.height;
                if (w > 0f && h > 0f)
                    return new Rect(0f, 0f, w, h);
                return element.contentRect;
            }
        }

        sealed class VectorIconOverlay : VisualElement
        {
            string _icon;
            Color _color = Color.black;

            public VectorIconOverlay()
            {
                pickingMode = PickingMode.Ignore;
                focusable = false;
                style.position = Position.Absolute;
                style.left = 0f;
                style.top = 0f;
                style.right = 0f;
                style.bottom = 0f;
                generateVisualContent += OnGenerateVisualContent;
            }

            public void Configure(string icon, Color color)
            {
                _icon = icon;
                _color = color.a > 0.001f ? color : Color.black;
                // MarkDirtyRepaint elided: caller dispatches us from a style
                // or attach path; Unity's own version bump covers the repaint
                // and an explicit call here would throw "cannot change render
                // data during visual tree rendering" inside UI Builder previews.
            }

            void OnGenerateVisualContent(MeshGenerationContext ctx)
            {
                if (_icon != "star")
                    return;
                var rect = BorderBoxRect(this);
                if (rect.width <= 0f || rect.height <= 0f)
                    return;
                var center = rect.center;
                float outer = Mathf.Min(rect.width, rect.height) * 0.29f;
                float inner = outer * 0.45f;

                var p = ctx.painter2D;
                p.fillColor = _color;
                p.BeginPath();
                for (int i = 0; i < 10; i++)
                {
                    float radius = (i % 2 == 0) ? outer : inner;
                    float angle = (-90f + i * 36f) * Mathf.Deg2Rad;
                    var pt = new Vector2(
                        center.x + Mathf.Cos(angle) * radius,
                        center.y + Mathf.Sin(angle) * radius);
                    if (i == 0) p.MoveTo(pt);
                    else p.LineTo(pt);
                }
                p.ClosePath();
                p.Fill();
            }

            static Rect BorderBoxRect(VisualElement element)
            {
                var w = element.layout.width;
                var h = element.layout.height;
                if (w > 0f && h > 0f)
                    return new Rect(0f, 0f, w, h);
                return element.contentRect;
            }
        }

        sealed class MaskFadeOverlay : VisualElement
        {
            MaskGradient _mask;
            Color _fadeColor;

            public MaskFadeOverlay()
            {
                pickingMode = PickingMode.Ignore;
                focusable = false;
                style.position = Position.Absolute;
                style.left = 0f;
                style.top = 0f;
                style.right = 0f;
                style.bottom = 0f;
                generateVisualContent += OnGenerateVisualContent;
            }

            public void Configure(string maskStr, Color fadeColor)
            {
                _mask = MaskGradientParser.Parse(maskStr);
                _fadeColor = fadeColor;
                // MarkDirtyRepaint elided: caller dispatches us from a style
                // or attach path; Unity's own version bump covers the repaint
                // and an explicit call here would throw "cannot change render
                // data during visual tree rendering" inside UI Builder previews.
            }

            void OnGenerateVisualContent(MeshGenerationContext ctx)
            {
                if (_mask == null || !_mask.IsAxisAligned || _fadeColor.a <= 0f)
                    return;
                var rect = contentRect;
                if (rect.width <= 0f || rect.height <= 0f)
                    return;

                var p = ctx.painter2D;
                int angle = Mathf.RoundToInt((_mask.AngleDegrees % 360f + 360f) % 360f);
                bool horizontal = angle == 90 || angle == 270;
                float axisLength = horizontal ? rect.width : rect.height;
                const int Bands = 32;
                for (int i = 0; i < Bands; i++)
                {
                    float t0 = i / (float)Bands;
                    float t1 = (i + 1) / (float)Bands;
                    float sampleT = (t0 + t1) * 0.5f;
                    float alpha = 1f - Mathf.Clamp01(_mask.SampleAlpha(sampleT, axisLength));
                    if (alpha <= 0.001f) continue;
                    Color c = _fadeColor;
                    c.a *= alpha;
                    p.fillColor = c;
                    p.BeginPath();
                    RoundedRect(p, BandRect(rect, angle, t0, t1), 0f);
                    p.Fill();
                }
            }

            static Rect BandRect(Rect rect, int angle, float t0, float t1)
            {
                switch (angle)
                {
                    case 0:
                        return new Rect(rect.x, rect.y + rect.height * (1f - t1), rect.width, rect.height * (t1 - t0));
                    case 90:
                        return new Rect(rect.x + rect.width * t0, rect.y, rect.width * (t1 - t0), rect.height);
                    case 270:
                        return new Rect(rect.x + rect.width * (1f - t1), rect.y, rect.width * (t1 - t0), rect.height);
                    default:
                        return new Rect(rect.x, rect.y + rect.height * t0, rect.width, rect.height * (t1 - t0));
                }
            }
        }
    }
}
