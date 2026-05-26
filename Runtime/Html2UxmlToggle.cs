using UnityEngine;
using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    [UxmlElement]
    public partial class Html2UxmlToggle : Html2UxmlPanel
    {
        const string ControlsStyleSheetPath = "ODDGames/html2uxml/Html2UxmlControls";
        static StyleSheet s_controlsStyleSheet;

        readonly VisualElement _box;
        readonly VisualElement _tick;
        readonly VisualElement _dash;
        readonly Label _label;
        bool _value;
        bool _indeterminate;
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
                _indeterminate = false;
                UpdateToggleVisuals();
            }
        }

        [UxmlAttribute("indeterminate")]
        public bool indeterminate
        {
            get => _indeterminate;
            set
            {
                _indeterminate = value;
                UpdateToggleVisuals();
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
                UpdateToggleVisuals();
            }
        }

        [UxmlAttribute("html-name")]
        public string htmlName
        {
            get => _htmlName;
            set => _htmlName = value ?? string.Empty;
        }

        public Html2UxmlToggle()
        {
            focusable = true;
            AddToClassList("html2uxml-toggle");
            AttachControlsStyleSheet(this);

            _box = new VisualElement
            {
                name = "html2uxml-toggle-box",
                pickingMode = PickingMode.Ignore,
            };
            _box.AddToClassList("html2uxml-toggle-box");

            _tick = new VisualElement
            {
                name = "html2uxml-toggle-tick",
                pickingMode = PickingMode.Ignore,
            };
            _tick.AddToClassList("html2uxml-toggle-tick");
            _box.Add(_tick);

            _dash = new VisualElement
            {
                name = "html2uxml-toggle-dash",
                pickingMode = PickingMode.Ignore,
            };
            _dash.AddToClassList("html2uxml-toggle-dash");
            _box.Add(_dash);

            _label = new Label
            {
                name = "html2uxml-toggle-label",
                pickingMode = PickingMode.Ignore,
            };
            _label.AddToClassList("html2uxml-choice-label");

            Add(_box);
            Add(_label);
            RegisterCallback<ClickEvent>(_ => Activate());
            RegisterCallback<KeyDownEvent>(OnKeyDown);
            RegisterCallback<PointerDownEvent>(_ => { if (!_disabled) AddToClassList("__h2u-state-pressed"); });
            RegisterCallback<PointerUpEvent>(_ => RemoveFromClassList("__h2u-state-pressed"));
            RegisterCallback<PointerLeaveEvent>(_ => RemoveFromClassList("__h2u-state-pressed"));
            schedule.Execute(UpdateToggleVisuals).ExecuteLater(0);
        }

        void Activate()
        {
            if (_disabled)
                return;
            Focus();
            value = !_value;
        }

        void OnKeyDown(KeyDownEvent evt)
        {
            if (_disabled)
                return;
            if (evt.keyCode == KeyCode.Space || evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
            {
                value = !_value;
                evt.StopPropagation();
            }
        }

        void UpdateToggleVisuals()
        {
            EnableInClassList("__h2u-state-checked", _value && !_indeterminate);
            EnableInClassList("__h2u-state-indeterminate", _indeterminate);
            EnableInClassList("__h2u-state-invalid", _required && !_value && !_indeterminate);
        }

        internal static void AttachControlsStyleSheet(VisualElement target)
        {
            if (target == null)
                return;
            if (s_controlsStyleSheet == null)
                s_controlsStyleSheet = Resources.Load<StyleSheet>(ControlsStyleSheetPath);
            if (s_controlsStyleSheet == null)
                return;
            // Attach at the panel ROOT, not the element itself. Element-level
            // stylesheets in UI Toolkit win over inherited root sheets, which
            // means our package defaults beat user CSS even at the same
            // specificity. Attaching at root puts both at the same cascade
            // level so user CSS wins on tie via load order — same behaviour
            // browsers have between UA and author stylesheets.
            target.RegisterCallback<AttachToPanelEvent>(_ =>
            {
                var root = target.panel?.visualTree;
                if (root == null) return;
                if (!root.styleSheets.Contains(s_controlsStyleSheet))
                    root.styleSheets.Add(s_controlsStyleSheet);
            });
        }
    }
}
