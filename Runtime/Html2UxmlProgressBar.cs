using UnityEngine;
using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    // Replacement for `<progress>` that paints its fill with a shader-backed
    // child instead of rebuilding Painter2D geometry every repaint. Mirrors
    // `ProgressBar`'s `value`/`low-value`/`high-value` API so existing UXML/USS
    // rules keep targeting it.
    [UxmlElement]
    public partial class Html2UxmlProgressBar : VisualElement
    {
        [UxmlAttribute("low-value")]
        public float LowValue
        {
            get => _lowValue;
            set { _lowValue = value; RefreshVisuals(); }
        }

        [UxmlAttribute("high-value")]
        public float HighValue
        {
            get => _highValue;
            set { _highValue = value; RefreshVisuals(); }
        }

        [UxmlAttribute("value")]
        public float Value
        {
            get => _value;
            set { _value = value; _hasValue = true; RefreshVisuals(); }
        }

        [UxmlAttribute("indeterminate")]
        public bool Indeterminate
        {
            get => !_hasValue;
            set { _hasValue = !value; ScheduleIndeterminateTick(); RefreshVisuals(); }
        }

        // Falls back to `color` when --odd-progress-fill isn't set, so
        // CSS like `.progress-bar-fill { background: #2563eb }` still
        // colours the fill via the resolved colour cascade.
        static readonly CustomStyleProperty<Color> FillColorProp =
            new CustomStyleProperty<Color>("--odd-progress-fill");

        readonly Html2UxmlShaderFill _track;
        readonly Html2UxmlShaderFill _fill;
        readonly Html2UxmlShaderFill _border;
        bool _trackStyleCaptured;
        bool _nativeTrackHidden;
        Color _trackColor;
        Color _borderColor;
        float _trackRadius;
        float _lowValue;
        float _highValue = 100f;
        float _value;
        bool _hasValue = true;
        float _indeterminatePhase;
        bool _indeterminateScheduled;
        Color _fillColor = new Color(0.145f, 0.388f, 0.922f, 1f); // #2563eb default

        public Html2UxmlProgressBar()
        {
            this.AddManipulator(new Html2UxmlPaintManipulator());
            this.AddManipulator(new Html2UxmlAnimationManipulator());
            style.alignSelf = Align.FlexStart;
            _track = new Html2UxmlShaderFill { name = "html2uxml-progress-track" };
            _track.AddToClassList("html2uxml-progress-track");
            hierarchy.Add(_track);

            _fill = new Html2UxmlShaderFill { name = "html2uxml-progress-fill" };
            _fill.AddToClassList("html2uxml-progress-fill");
            hierarchy.Add(_fill);

            _border = new Html2UxmlShaderFill { name = "html2uxml-progress-border" };
            _border.AddToClassList("html2uxml-progress-border");
            hierarchy.Add(_border);

            RegisterCallback<CustomStyleResolvedEvent>(OnStylesResolved);
            RegisterCallback<AttachToPanelEvent>(_ => { ScheduleIndeterminateTick(); RefreshVisuals(); });
            RegisterCallback<GeometryChangedEvent>(_ => RefreshVisuals());
        }

        void ScheduleIndeterminateTick()
        {
            if (_indeterminateScheduled || _hasValue || panel == null)
                return;
            _indeterminateScheduled = true;
            schedule.Execute(() =>
            {
                if (_hasValue)
                {
                    _indeterminateScheduled = false;
                    return;
                }
                _indeterminatePhase = (_indeterminatePhase + 0.04f) % 1f;
                RefreshVisuals();
            }).Every(33);
        }

        void OnStylesResolved(CustomStyleResolvedEvent evt)
        {
            // Only an explicit `--odd-progress-fill` overrides the
            // browser-blue default. Inheriting `color` is risky because
            // every paragraph/label in the page sets it, so a plain
            // `<progress>` would otherwise paint dark grey.
            if (evt.customStyle.TryGetValue(FillColorProp, out var c))
                _fillColor = c;
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

            if (CaptureTrackStyle(h))
                HideNativeTrack();

            float radius = _trackRadius;
            Color trackColor = _trackColor;
            Color borderColor = _borderColor;
            ConfigureBox(_track, new Rect(0f, 0f, w, h), trackColor, radius, Html2UxmlFillMode.Fill, 0f);

            float leftInset = resolvedStyle.borderLeftWidth;
            float rightInset = resolvedStyle.borderRightWidth;
            float topInset = resolvedStyle.borderTopWidth;
            float bottomInset = resolvedStyle.borderBottomWidth;
            float borderWidth = Mathf.Max(Mathf.Max(leftInset, rightInset), Mathf.Max(topInset, bottomInset));
            if (borderWidth > 0.001f && borderColor.a > 0.001f)
                ConfigureBox(_border, new Rect(0f, 0f, w, h), borderColor, radius, Html2UxmlFillMode.Stroke, borderWidth);
            else
                _border.style.display = DisplayStyle.None;

            float innerW = Mathf.Max(0f, w - leftInset - rightInset);
            float innerH = Mathf.Max(0f, h - topInset - bottomInset);
            if (innerW <= 0f || innerH <= 0f)
            {
                _fill.style.display = DisplayStyle.None;
                return;
            }

            float left = leftInset;
            float fillW;
            if (!_hasValue)
            {
                fillW = innerW * 0.3f;
                float travel = innerW + fillW;
                left = leftInset - fillW + travel * _indeterminatePhase;
                fillW = Mathf.Min(fillW, leftInset + innerW - left);
                if (left < leftInset)
                {
                    fillW -= leftInset - left;
                    left = leftInset;
                }
            }
            else
            {
                float span = Mathf.Max(0.0001f, _highValue - _lowValue);
                float t = Mathf.Clamp01((_value - _lowValue) / span);
                fillW = innerW * t;
            }

            if (fillW <= 0.001f)
            {
                _fill.style.display = DisplayStyle.None;
                return;
            }

            _fill.style.display = DisplayStyle.Flex;
            _fill.style.left = left;
            _fill.style.top = topInset;
            _fill.style.width = fillW;
            _fill.style.height = innerH;

            float fillRadius = Mathf.Max(0f, radius - Mathf.Max(leftInset, topInset));
            var corners = fillW >= innerW - 0.5f ? Html2UxmlFillCorners.All : Html2UxmlFillCorners.Leading;
            _fill.Configure(_fillColor, null, fillRadius, corners);
        }

        static void ConfigureBox(Html2UxmlShaderFill element, Rect rect, Color color, float radius, Html2UxmlFillMode mode, float strokeWidth)
        {
            element.style.display = DisplayStyle.Flex;
            element.style.left = rect.x;
            element.style.top = rect.y;
            element.style.width = rect.width;
            element.style.height = rect.height;
            element.Configure(color, null, radius, Html2UxmlFillCorners.All, true, mode, strokeWidth);
        }

        bool CaptureTrackStyle(float height)
        {
            Color background = resolvedStyle.backgroundColor;
            Color border = resolvedStyle.borderTopColor;
            float radius = ResolvedRadius(height);
            float borderWidth = Mathf.Max(
                Mathf.Max(resolvedStyle.borderLeftWidth, resolvedStyle.borderRightWidth),
                Mathf.Max(resolvedStyle.borderTopWidth, resolvedStyle.borderBottomWidth));
            bool hasResolvedCss = background.a > 0.001f || border.a > 0.001f || borderWidth > 0.001f || radius > 0.001f;
            if (!hasResolvedCss && _trackStyleCaptured)
                return true;
            if (!hasResolvedCss)
            {
                _trackColor = new Color(0.898f, 0.906f, 0.922f, 1f);
                _borderColor = new Color(0.612f, 0.639f, 0.686f, 1f);
                _trackRadius = 0f;
                return false;
            }

            _trackColor = background.a > 0f ? ResolvedColorForShader(background) : new Color(0.898f, 0.906f, 0.922f, 1f);
            _borderColor = border.a > 0f ? ResolvedColorForShader(border) : new Color(0.612f, 0.639f, 0.686f, 1f);
            _trackRadius = radius;
            _trackStyleCaptured = true;
            return true;
        }

        static Color ResolvedColorForShader(Color color)
        {
            if (QualitySettings.activeColorSpace != ColorSpace.Linear)
                return color;
            Color converted = color.gamma;
            converted.a = color.a;
            return converted;
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

        float ResolvedRadius(float height)
        {
            float radius = resolvedStyle.borderTopLeftRadius;
            radius = Mathf.Max(radius, resolvedStyle.borderTopRightRadius);
            radius = Mathf.Max(radius, resolvedStyle.borderBottomRightRadius);
            radius = Mathf.Max(radius, resolvedStyle.borderBottomLeftRadius);
            return Mathf.Min(radius, height * 0.5f);
        }
    }
}

