using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    [UxmlElement]
    public partial class Html2UxmlRadioButton : Html2UxmlPanel
    {
        readonly VisualElement _circle;
        readonly VisualElement _dot;
        readonly Label _label;
        bool _value;
        bool _disabled;
        bool _required;
        string _htmlName = string.Empty;
        string _text = string.Empty;

        [UxmlAttribute("text")]
        public string text
        {
            get => _text;
            set
            {
                _text = value ?? string.Empty;
                _label.text = _text;
            }
        }

        [UxmlAttribute("value")]
        public bool value
        {
            get => _value;
            set
            {
                _value = value;
                UpdateRadioVisuals();
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

        [UxmlAttribute("required")]
        public bool required
        {
            get => _required;
            set
            {
                _required = value;
                UpdateRadioVisuals();
            }
        }

        [UxmlAttribute("html-name")]
        public string htmlName
        {
            get => _htmlName;
            set => _htmlName = value ?? string.Empty;
        }

        public Html2UxmlRadioButton()
        {
            focusable = true;
            AddToClassList("html2uxml-radio");
            Html2UxmlToggle.AttachControlsStyleSheet(this);

            _circle = new VisualElement
            {
                name = "html2uxml-radio-circle",
                pickingMode = PickingMode.Ignore,
            };
            _circle.AddToClassList("html2uxml-radio-circle");

            _dot = new VisualElement
            {
                name = "html2uxml-radio-dot",
                pickingMode = PickingMode.Ignore,
            };
            _dot.AddToClassList("html2uxml-radio-dot");
            _circle.Add(_dot);

            _label = new Label
            {
                name = "html2uxml-radio-label",
                pickingMode = PickingMode.Ignore,
            };
            _label.AddToClassList("html2uxml-choice-label");

            Add(_circle);
            Add(_label);
            RegisterCallback<ClickEvent>(_ => SelectRadio());
            RegisterCallback<KeyDownEvent>(OnKeyDown);
            RegisterCallback<PointerDownEvent>(_ => { if (!_disabled) AddToClassList("__h2u-state-pressed"); });
            RegisterCallback<PointerUpEvent>(_ => RemoveFromClassList("__h2u-state-pressed"));
            RegisterCallback<PointerLeaveEvent>(_ => RemoveFromClassList("__h2u-state-pressed"));
            schedule.Execute(UpdateRadioVisuals).ExecuteLater(0);
        }

        void OnKeyDown(KeyDownEvent evt)
        {
            if (_disabled)
                return;
            switch (evt.keyCode)
            {
                case KeyCode.Space:
                case KeyCode.Return:
                case KeyCode.KeypadEnter:
                    SelectRadio();
                    evt.StopPropagation();
                    break;
                case KeyCode.DownArrow:
                case KeyCode.RightArrow:
                    FocusSiblingInGroup(1, false);
                    evt.StopPropagation();
                    break;
                case KeyCode.UpArrow:
                case KeyCode.LeftArrow:
                    FocusSiblingInGroup(-1, false);
                    evt.StopPropagation();
                    break;
                case KeyCode.Home:
                    FocusSiblingInGroup(0, true);
                    evt.StopPropagation();
                    break;
                case KeyCode.End:
                    FocusSiblingInGroup(0, false, true);
                    evt.StopPropagation();
                    break;
            }
        }

        void FocusSiblingInGroup(int direction, bool toFirst, bool toLast = false)
        {
            var group = CollectGroup();
            if (group.Count == 0)
                return;
            int idx;
            if (toFirst)
                idx = 0;
            else if (toLast)
                idx = group.Count - 1;
            else
            {
                int self = group.IndexOf(this);
                if (self < 0)
                    return;
                idx = (self + direction + group.Count) % group.Count;
            }
            var target = group[idx];
            target.Focus();
            target.SelectRadio();
        }

        List<Html2UxmlRadioButton> CollectGroup()
        {
            var list = new List<Html2UxmlRadioButton>();
            if (parent == null)
                return list;
            foreach (var sibling in parent.Children())
            {
                if (sibling is Html2UxmlRadioButton radio
                    && radio.htmlName == _htmlName
                    && radio.enabledSelf)
                    list.Add(radio);
            }
            return list;
        }

        void SelectRadio()
        {
            if (_disabled)
                return;

            Focus();
            if (!string.IsNullOrEmpty(_htmlName) && parent != null)
            {
                foreach (var sibling in parent.Children())
                {
                    if (sibling is Html2UxmlRadioButton radio
                        && radio != this
                        && radio.htmlName == _htmlName)
                    {
                        radio.value = false;
                    }
                }
            }
            value = true;
        }

        void UpdateRadioVisuals()
        {
            EnableInClassList("__h2u-state-checked", _value);
            EnableInClassList("__h2u-state-invalid", _required && !GroupHasSelection());
        }

        bool GroupHasSelection()
        {
            if (parent == null)
                return _value;
            foreach (var sibling in parent.Children())
            {
                if (sibling is Html2UxmlRadioButton radio
                    && radio.htmlName == _htmlName
                    && radio.value)
                    return true;
            }
            return false;
        }
    }
}
