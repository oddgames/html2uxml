using UnityEngine;
using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    [UxmlElement]
    public partial class Html2UxmlSlider : Html2UxmlPanel
    {
        readonly VisualElement _track;
        readonly VisualElement _fill;
        readonly VisualElement _thumb;
        float _lowValue = 0f;
        float _highValue = 100f;
        float _step = 1f;
        float _value = 0f;
        bool _disabled;
        bool _dragging;

        [UxmlAttribute("low-value")]
        public float lowValue
        {
            get => _lowValue;
            set
            {
                _lowValue = value;
                this.value = _value;
            }
        }

        [UxmlAttribute("high-value")]
        public float highValue
        {
            get => _highValue;
            set
            {
                _highValue = value;
                this.value = _value;
            }
        }

        [UxmlAttribute("step")]
        public float step
        {
            get => _step;
            set
            {
                _step = value;
                this.value = _value;
            }
        }

        [UxmlAttribute("value")]
        public float value
        {
            get => _value;
            set
            {
                _value = ClampSliderValue(value);
                UpdateSliderVisuals();
            }
        }

        [UxmlAttribute("disabled")]
        public bool disabled
        {
            get => _disabled;
            set
            {
                _disabled = value;
                SetEnabled(!_disabled);
            }
        }

        public Html2UxmlSlider()
        {
            focusable = true;
            AddToClassList("html2uxml-slider");
            Html2UxmlToggle.AttachControlsStyleSheet(this);

            _track = new Html2UxmlElement
            {
                name = "html2uxml-slider-track",
                pickingMode = PickingMode.Ignore,
            };
            _track.AddToClassList("html2uxml-slider-track");

            _fill = new Html2UxmlElement
            {
                name = "html2uxml-slider-fill",
                pickingMode = PickingMode.Ignore,
            };
            _fill.AddToClassList("html2uxml-slider-fill");

            _thumb = new Html2UxmlElement
            {
                name = "html2uxml-slider-thumb",
                pickingMode = PickingMode.Ignore,
            };
            _thumb.AddToClassList("html2uxml-slider-thumb");

            _track.Add(_fill);
            _track.Add(_thumb);
            Add(_track);

            RegisterCallback<GeometryChangedEvent>(_ => UpdateSliderVisuals());
            RegisterCallback<PointerDownEvent>(OnPointerDown);
            RegisterCallback<PointerMoveEvent>(OnPointerMove);
            RegisterCallback<PointerUpEvent>(OnPointerUp);
            RegisterCallback<PointerCancelEvent>(_ => EndDrag());
            RegisterCallback<KeyDownEvent>(OnKeyDown);
            schedule.Execute(UpdateSliderVisuals).ExecuteLater(0);
        }

        void OnKeyDown(KeyDownEvent evt)
        {
            if (_disabled)
                return;
            float small = _step > Mathf.Epsilon ? _step : 1f;
            float large = Mathf.Max(small * 10f, (_highValue - _lowValue) * 0.1f);
            switch (evt.keyCode)
            {
                case KeyCode.LeftArrow:
                case KeyCode.DownArrow:
                    value = _value - small;
                    evt.StopPropagation();
                    break;
                case KeyCode.RightArrow:
                case KeyCode.UpArrow:
                    value = _value + small;
                    evt.StopPropagation();
                    break;
                case KeyCode.PageDown:
                    value = _value - large;
                    evt.StopPropagation();
                    break;
                case KeyCode.PageUp:
                    value = _value + large;
                    evt.StopPropagation();
                    break;
                case KeyCode.Home:
                    value = _lowValue;
                    evt.StopPropagation();
                    break;
                case KeyCode.End:
                    value = _highValue;
                    evt.StopPropagation();
                    break;
            }
        }

        float ClampSliderValue(float raw)
        {
            var min = Mathf.Min(_lowValue, _highValue);
            var max = Mathf.Max(_lowValue, _highValue);
            var clamped = Mathf.Clamp(raw, min, max);
            if (_step > Mathf.Epsilon)
                clamped = _lowValue + Mathf.Round((clamped - _lowValue) / _step) * _step;
            return Mathf.Clamp(clamped, min, max);
        }

        float SliderT()
        {
            var range = _highValue - _lowValue;
            return Mathf.Abs(range) <= Mathf.Epsilon
                ? 0f
                : Mathf.Clamp01((_value - _lowValue) / range);
        }

        void UpdateSliderVisuals()
        {
            var t = SliderT();
            _fill.style.width = Length.Percent(t * 100f);
            _thumb.style.left = Length.Percent(t * 100f);
        }

        void OnPointerDown(PointerDownEvent evt)
        {
            Focus();
            _dragging = true;
            AddToClassList("__h2u-state-pressed");
            SetValueFromWorldX(evt.position.x);
            evt.StopPropagation();
        }

        void OnPointerMove(PointerMoveEvent evt)
        {
            if (!_dragging)
                return;
            SetValueFromWorldX(evt.position.x);
            evt.StopPropagation();
        }

        void OnPointerUp(PointerUpEvent evt)
        {
            SetValueFromWorldX(evt.position.x);
            EndDrag();
            evt.StopPropagation();
        }

        void EndDrag()
        {
            RemoveFromClassList("__h2u-state-pressed");
            _dragging = false;
        }

        void SetValueFromWorldX(float worldX)
        {
            if (_disabled)
                return;
            var trackWidth = _track.resolvedStyle.width;
            if (trackWidth <= Mathf.Epsilon)
                return;
            var localX = worldX - _track.worldBound.x;
            var t = Mathf.Clamp01(localX / trackWidth);
            value = _lowValue + (_highValue - _lowValue) * t;
        }
    }
}
