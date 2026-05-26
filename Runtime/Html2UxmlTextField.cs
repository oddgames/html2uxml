using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    [UxmlElement]
    public partial class Html2UxmlTextField : TextField
    {
        string _placeholder;
        Label _placeholderLabel;
        Button _clearButton;
        bool _focused;
        bool _required;
        bool _readOnly;
        int _minLength = -1;
        int _maxLength = -1;
        string _pattern = string.Empty;
        Regex _patternRegex;
        string _inputType = "text";
        string _htmlName = string.Empty;
        string _datalist = string.Empty;
        string _choices = string.Empty;

        [UxmlAttribute("password")]
        public bool Password
        {
            get => isPasswordField;
            set => isPasswordField = value;
        }

        [UxmlAttribute("placeholder")]
        public string Placeholder
        {
            get => _placeholder;
            set
            {
                _placeholder = value ?? string.Empty;
                EnsurePlaceholderLabel();
                UpdatePlaceholderVisibility();
            }
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

        [UxmlAttribute("minlength")]
        public int MinLength
        {
            get => _minLength;
            set { _minLength = value; UpdateValidationState(); }
        }

        [UxmlAttribute("maxlength")]
        public int MaxLengthAttr
        {
            get => _maxLength;
            set
            {
                _maxLength = value;
                if (value > 0)
                    maxLength = value;
                UpdateValidationState();
            }
        }

        [UxmlAttribute("pattern")]
        public string Pattern
        {
            get => _pattern;
            set
            {
                _pattern = value ?? string.Empty;
                _patternRegex = string.IsNullOrEmpty(_pattern)
                    ? null
                    : TryCompile(_pattern);
                UpdateValidationState();
            }
        }

        [UxmlAttribute("input-type")]
        public string InputType
        {
            get => _inputType;
            set
            {
                _inputType = string.IsNullOrEmpty(value) ? "text" : value.ToLowerInvariant();
                EnsureClearButton();
                UpdateValidationState();
            }
        }

        [UxmlAttribute("html-name")]
        public string HtmlName
        {
            get => _htmlName;
            set => _htmlName = value ?? string.Empty;
        }

        [UxmlAttribute("datalist")]
        public string Datalist
        {
            get => _datalist;
            set => _datalist = value ?? string.Empty;
        }

        [UxmlAttribute("choices")]
        public string Choices
        {
            get => _choices;
            set => _choices = value ?? string.Empty;
        }

        [UxmlAttribute("disabled")]
        public bool Disabled
        {
            get => !enabledSelf;
            set => SetEnabled(!value);
        }

        public Html2UxmlTextField()
        {
            this.AddManipulator(new Html2UxmlAnimationManipulator());
            this.AddManipulator(new Html2UxmlPaintManipulator());
            AddToClassList("html2uxml-textfield");
            Html2UxmlToggle.AttachControlsStyleSheet(this);
            style.alignSelf = Align.FlexStart;
            this.RegisterValueChangedCallback(_ =>
            {
                UpdatePlaceholderVisibility();
                UpdateValidationState();
                UpdateClearButtonVisibility();
            });
            RegisterCallback<AttachToPanelEvent>(_ => ScheduleFieldSync());
            RegisterCallback<FocusInEvent>(_ => { _focused = true; UpdatePlaceholderVisibility(); });
            RegisterCallback<FocusOutEvent>(_ => { _focused = false; UpdatePlaceholderVisibility(); UpdateValidationState(); });
            schedule.Execute(SyncFieldVisuals).ExecuteLater(0);
        }

        static Regex TryCompile(string pattern)
        {
            try { return new Regex("^(?:" + pattern + ")$"); }
            catch { return null; }
        }

        void ScheduleFieldSync()
        {
            schedule.Execute(SyncFieldVisuals).ExecuteLater(0);
        }

        void SyncFieldVisuals()
        {
            Html2UxmlFieldStyling.ResetTextInputChrome(this);
            EnsurePlaceholderLabel();
            EnsureClearButton();
            UpdatePlaceholderVisibility();
            UpdateValidationState();
        }

        void EnsurePlaceholderLabel()
        {
            if (string.IsNullOrEmpty(_placeholder) || _placeholderLabel != null)
                return;

            _placeholderLabel = new Label
            {
                name = "html2uxml-placeholder",
                pickingMode = PickingMode.Ignore,
                text = _placeholder,
            };
            _placeholderLabel.AddToClassList("html2uxml-placeholder");
            hierarchy.Add(_placeholderLabel);
        }

        void EnsureClearButton()
        {
            if (_inputType != "search" || _clearButton != null)
                return;

            _clearButton = new Button(() => { value = string.Empty; Focus(); })
            {
                name = "html2uxml-clear-button",
                text = "×",
            };
            _clearButton.AddToClassList("html2uxml-clear-button");
            hierarchy.Add(_clearButton);
            UpdateClearButtonVisibility();
        }

        void UpdateClearButtonVisibility()
        {
            if (_clearButton == null)
                return;
            _clearButton.style.display = !string.IsNullOrEmpty(value)
                ? DisplayStyle.Flex
                : DisplayStyle.None;
        }

        void UpdatePlaceholderVisibility()
        {
            if (_placeholderLabel == null)
                return;
            _placeholderLabel.text = _placeholder;
            bool hasValue = !string.IsNullOrEmpty(value);
            _placeholderLabel.style.display =
                !string.IsNullOrEmpty(_placeholder) && !hasValue && !_focused
                    ? DisplayStyle.Flex
                    : DisplayStyle.None;
        }

        void UpdateValidationState()
        {
            EnableInClassList("__h2u-state-invalid", IsInvalid());
        }

        bool IsInvalid()
        {
            string v = value ?? string.Empty;
            if (_required && string.IsNullOrEmpty(v))
                return true;
            if (string.IsNullOrEmpty(v))
                return false;
            if (_minLength > 0 && v.Length < _minLength)
                return true;
            if (_maxLength > 0 && v.Length > _maxLength)
                return true;
            if (_patternRegex != null && !_patternRegex.IsMatch(v))
                return true;
            switch (_inputType)
            {
                case "email":
                    if (!v.Contains("@") || v.IndexOf('.') < v.IndexOf('@'))
                        return true;
                    break;
                case "url":
                    if (!v.Contains("://"))
                        return true;
                    break;
            }
            return false;
        }
    }
}
