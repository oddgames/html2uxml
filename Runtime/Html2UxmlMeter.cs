using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    [UxmlElement]
    public partial class Html2UxmlMeter : VisualElement
    {
        [UxmlAttribute("low-value")]
        public float LowValue
        {
            get => _min;
            set { _min = value; RefreshVisuals(); }
        }

        [UxmlAttribute("high-value")]
        public float HighValue
        {
            get => _max;
            set { _max = value; RefreshVisuals(); }
        }

        [UxmlAttribute("value")]
        public float Value
        {
            get => _value;
            set { _value = value; RefreshVisuals(); }
        }

        [UxmlAttribute("low")]
        public float Low
        {
            get => _low;
            set { _low = value; _hasLow = true; RefreshVisuals(); }
        }

        [UxmlAttribute("high")]
        public float High
        {
            get => _high;
            set { _high = value; _hasHigh = true; RefreshVisuals(); }
        }

        [UxmlAttribute("optimum")]
        public float Optimum
        {
            get => _optimum;
            set { _optimum = value; _hasOptimum = true; RefreshVisuals(); }
        }

        static readonly CustomStyleProperty<Color> GoodFillProp =
            new CustomStyleProperty<Color>("--odd-meter-good-fill");
        static readonly CustomStyleProperty<Color> SuboptimalFillProp =
            new CustomStyleProperty<Color>("--odd-meter-suboptimal-fill");
        static readonly CustomStyleProperty<Color> BadFillProp =
            new CustomStyleProperty<Color>("--odd-meter-bad-fill");

        readonly Html2UxmlShaderFill _track;
        readonly Html2UxmlShaderFill _fill;
        readonly Html2UxmlShaderFill _border;
        bool _trackStyleCaptured;
        bool _nativeTrackHidden;
        Color _trackColor;
        Color _borderColor;
        float _borderWidth;
        float _trackRadius;
        float _min;
        float _max = 1f;
        float _value;
        float _low;
        float _high;
        float _optimum;
        bool _hasLow;
        bool _hasHigh;
        bool _hasOptimum;
        Color _goodFill = new Color(0.467f, 0.667f, 0.196f, 1f);       // Chrome-like native meter green
        Color _suboptimalFill = new Color(0.933f, 0.600f, 0.000f, 1f); // #ee9900
        Color _badFill = new Color(0.863f, 0.149f, 0.149f, 1f);        // #dc2626

        public Html2UxmlMeter()
        {
            this.AddManipulator(new Html2UxmlPaintManipulator());
            this.AddManipulator(new Html2UxmlAnimationManipulator());
            _track = new Html2UxmlShaderFill { name = "html2uxml-meter-track" };
            _track.AddToClassList("html2uxml-meter-track");
            hierarchy.Add(_track);

            _fill = new Html2UxmlShaderFill { name = "html2uxml-meter-fill" };
            _fill.AddToClassList("html2uxml-meter-fill");
            hierarchy.Add(_fill);

            _border = new Html2UxmlShaderFill { name = "html2uxml-meter-border" };
            _border.AddToClassList("html2uxml-meter-border");
            hierarchy.Add(_border);

            RegisterCallback<CustomStyleResolvedEvent>(OnStylesResolved);
            RegisterCallback<GeometryChangedEvent>(_ => RefreshVisuals());
            RegisterCallback<AttachToPanelEvent>(_ => RefreshVisuals());
        }

        void OnStylesResolved(CustomStyleResolvedEvent evt)
        {
            if (evt.customStyle.TryGetValue(GoodFillProp, out var good))
                _goodFill = good;
            if (evt.customStyle.TryGetValue(SuboptimalFillProp, out var suboptimal))
                _suboptimalFill = suboptimal;
            if (evt.customStyle.TryGetValue(BadFillProp, out var bad))
                _badFill = bad;
            RefreshVisuals();
        }

        void RefreshVisuals()
        {
            if (_fill == null || _track == null || _border == null)
                return;

            float w = layout.width;
            float h = layout.height;
            if (w <= 0f || h <= 0f)
            {
                _fill.style.display = DisplayStyle.None;
                return;
            }

            float span = Mathf.Max(0.0001f, _max - _min);
            float t = Mathf.Clamp01((_value - _min) / span);
            if (CaptureTrackStyle())
                HideNativeTrack();

            var trackRect = MeterTrackRect(w, h);
            float radius = Mathf.Min(_trackRadius, trackRect.height * 0.5f);
            float borderWidth = _borderWidth;
            Color trackColor = _trackColor;
            Color borderColor = _borderColor;
            ConfigureBox(_track, trackRect, trackColor, null, radius, Html2UxmlFillMode.Fill, 0f);
            if (borderWidth > 0.001f && borderColor.a > 0.001f)
                ConfigureBox(_border, trackRect, borderColor, null, radius, Html2UxmlFillMode.Stroke, borderWidth);
            else
                _border.style.display = DisplayStyle.None;

            float insetX = Mathf.Max(0.6f, borderWidth * 0.6f);
            float fillH = Mathf.Clamp(trackRect.height * 0.58f, 6f, Mathf.Max(6f, trackRect.height - 2f));
            float fillY = trackRect.y + (trackRect.height - fillH) * 0.5f + 0.35f;
            float available = Mathf.Max(0f, trackRect.width - insetX * 2f - 1.2f);
            float fillW = available * t;
            if (fillW <= 0.001f)
            {
                _fill.style.display = DisplayStyle.None;
                return;
            }

            _fill.style.display = DisplayStyle.Flex;
            _fill.style.left = trackRect.x + insetX;
            _fill.style.top = fillY;
            _fill.style.width = fillW;
            _fill.style.height = fillH;

            Color fill = FillColorForValue();
            var corners = fillW >= available - 0.5f ? Html2UxmlFillCorners.All : Html2UxmlFillCorners.Leading;
            _fill.Configure(fill, FillGradient(fill), fillH * 0.5f, corners);
        }

        static void ConfigureBox(Html2UxmlShaderFill element, Rect rect, Color color, LinearGradient gradient, float radius, Html2UxmlFillMode mode, float strokeWidth)
        {
            element.style.display = DisplayStyle.Flex;
            element.style.left = rect.x;
            element.style.top = rect.y;
            element.style.width = rect.width;
            element.style.height = rect.height;
            element.Configure(color, gradient, radius, Html2UxmlFillCorners.All, true, mode, strokeWidth);
        }

        bool CaptureTrackStyle()
        {
            Color background = resolvedStyle.backgroundColor;
            Color border = resolvedStyle.borderTopColor;
            float borderWidth = ResolvedBorderWidth();
            float radius = ResolvedRadius();
            bool hasResolvedCss = background.a > 0.001f || border.a > 0.001f || borderWidth > 0.001f || radius > 0.001f;
            if (!hasResolvedCss && _trackStyleCaptured)
                return true;
            if (!hasResolvedCss)
            {
                _trackColor = new Color(0.898f, 0.906f, 0.922f, 1f);
                _borderColor = new Color(0.612f, 0.639f, 0.686f, 1f);
                _borderWidth = 0f;
                _trackRadius = 0f;
                return false;
            }

            _trackColor = background.a > 0f ? ResolvedColorForShader(background) : new Color(0.898f, 0.906f, 0.922f, 1f);
            _borderColor = border.a > 0f ? ResolvedColorForShader(border) : new Color(0.612f, 0.639f, 0.686f, 1f);
            _borderWidth = borderWidth;
            _trackRadius = radius;
            _trackStyleCaptured = true;
            return true;
        }

        void HideNativeTrack()
        {
            if (_nativeTrackHidden)
                return;
            _nativeTrackHidden = true;
            style.backgroundColor = Color.clear;
            style.borderTopColor = Color.clear;
            style.borderRightColor = Color.clear;
            style.borderBottomColor = Color.clear;
            style.borderLeftColor = Color.clear;
        }

        static Color ResolvedColorForShader(Color color)
        {
            if (QualitySettings.activeColorSpace != ColorSpace.Linear)
                return color;
            Color converted = color.gamma;
            converted.a = color.a;
            return converted;
        }

        float ResolvedBorderWidth()
        {
            float width = resolvedStyle.borderTopWidth;
            width = Mathf.Max(width, resolvedStyle.borderRightWidth);
            width = Mathf.Max(width, resolvedStyle.borderBottomWidth);
            width = Mathf.Max(width, resolvedStyle.borderLeftWidth);
            return width;
        }

        float ResolvedRadius()
        {
            float radius = resolvedStyle.borderTopLeftRadius;
            radius = Mathf.Max(radius, resolvedStyle.borderTopRightRadius);
            radius = Mathf.Max(radius, resolvedStyle.borderBottomRightRadius);
            radius = Mathf.Max(radius, resolvedStyle.borderBottomLeftRadius);
            return radius;
        }


        static Rect MeterTrackRect(float width, float height)
        {
            return new Rect(0f, 0f, width, height);
        }
        static LinearGradient FillGradient(Color color)
        {
            return new LinearGradient
            {
                AngleDegrees = 180f,
                Stops = new List<GradientStop>
                {
                    new GradientStop(AdjustHsv(color, 0.58f, 1.30f), 0f),
                    new GradientStop(AdjustHsv(color, 0.46f, 1.39f), 0.18f),
                    new GradientStop(AdjustHsv(color, 0.50f, 1.34f), 0.26f),
                    new GradientStop(AdjustHsv(color, 0.92f, 0.98f), 0.43f),
                    new GradientStop(color, 0.54f),
                    new GradientStop(AdjustHsv(color, 0.72f, 1.27f), 1f),
                }
            };
        }

        static Color AdjustHsv(Color color, float saturationFactor, float valueFactor)
        {
            Color.RGBToHSV(color, out float h, out float s, out float v);
            Color adjusted = Color.HSVToRGB(h, Mathf.Clamp01(s * saturationFactor), Mathf.Clamp01(v * valueFactor));
            adjusted.a = color.a;
            return adjusted;
        }

        Color FillColorForValue()
        {
            float low = _hasLow ? _low : _min;
            float high = _hasHigh ? _high : _max;
            if (!_hasOptimum)
            {
                if (_value < low || _value > high)
                    return _suboptimalFill;
                return _goodFill;
            }

            if (_optimum <= low)
            {
                if (_value <= low)
                    return _goodFill;
                return _value <= high ? _suboptimalFill : _badFill;
            }

            if (_optimum >= high)
            {
                if (_value >= high)
                    return _goodFill;
                return _value >= low ? _suboptimalFill : _badFill;
            }

            return _value >= low && _value <= high ? _goodFill : _suboptimalFill;
        }
    }
}


