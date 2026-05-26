using UnityEngine;
using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    [UxmlElement]
    public partial class Html2UxmlFloatField : FloatField
    {
        bool _hasMin;
        bool _hasMax;
        float _min;
        float _max;
        float _step;
        bool _required;
        bool _readOnly;

        [UxmlAttribute("min")]
        public float Min
        {
            get => _min;
            set { _min = value; _hasMin = true; UpdateValidationState(); }
        }

        [UxmlAttribute("max")]
        public float Max
        {
            get => _max;
            set { _max = value; _hasMax = true; UpdateValidationState(); }
        }

        [UxmlAttribute("step")]
        public float Step
        {
            get => _step;
            set => _step = value;
        }

        [UxmlAttribute("required")]
        public bool Required
        {
            get => _required;
            set { _required = value; UpdateValidationState(); }
        }

        [UxmlAttribute("readonly")]
        public bool ReadOnly
        {
            get => _readOnly;
            set { _readOnly = value; isReadOnly = value; }
        }

        [UxmlAttribute("disabled")]
        public bool Disabled
        {
            get => !enabledSelf;
            set => SetEnabled(!value);
        }

        public Html2UxmlFloatField()
        {
            this.AddManipulator(new Html2UxmlAnimationManipulator());
            this.AddManipulator(new Html2UxmlPaintManipulator());
            AddToClassList("html2uxml-floatfield");
            Html2UxmlToggle.AttachControlsStyleSheet(this);
            style.alignSelf = Align.FlexStart;
            RegisterCallback<AttachToPanelEvent>(_ => ScheduleFieldSync());
            this.RegisterValueChangedCallback(_ => UpdateValidationState());
            RegisterCallback<FocusOutEvent>(_ => ClampOnBlur());
            schedule.Execute(SyncFieldVisuals).ExecuteLater(0);
        }

        void ClampOnBlur()
        {
            float v = value;
            if (_hasMin && v < _min) v = _min;
            if (_hasMax && v > _max) v = _max;
            if (_step > Mathf.Epsilon && _hasMin)
                v = _min + Mathf.Round((v - _min) / _step) * _step;
            if (!Mathf.Approximately(v, value))
                value = v;
            UpdateValidationState();
        }

        void UpdateValidationState()
        {
            EnableInClassList("__h2u-state-invalid", IsInvalid());
        }

        bool IsInvalid()
        {
            float v = value;
            if (_required && float.IsNaN(v))
                return true;
            if (_hasMin && v < _min)
                return true;
            if (_hasMax && v > _max)
                return true;
            return false;
        }

        void ScheduleFieldSync()
        {
            schedule.Execute(SyncFieldVisuals).ExecuteLater(0);
        }

        void SyncFieldVisuals()
        {
            Html2UxmlFieldStyling.ResetTextInputChrome(this);
            UpdateValidationState();
        }
    }
}
