using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    [UxmlElement]
    public partial class Html2UxmlDropdownField : Html2UxmlPanel
    {
        readonly Label _label;
        readonly VisualElement _arrow;
        readonly VisualElement _menu;
        string _choices = string.Empty;
        readonly System.Collections.Generic.List<string> _choiceList = new System.Collections.Generic.List<string>();
        int _highlightedIndex = -1;
        string _value = string.Empty;
        bool _disabled;
        bool _menuOpen;
        string _htmlName = string.Empty;
        string _typeAheadBuffer = string.Empty;
        double _lastTypeAheadAt;

        [UxmlAttribute("choices")]
        public string choices
        {
            get => _choices;
            set
            {
                _choices = value ?? string.Empty;
                ParseChoices();
                if (string.IsNullOrEmpty(_value) && _choiceList.Count > 0)
                    _value = _choiceList[0];
                RebuildMenu();
                HideMenu();
                UpdateDropdownVisuals();
            }
        }

        [UxmlAttribute("value")]
        public string value
        {
            get => _value;
            set
            {
                _value = value ?? string.Empty;
                UpdateDropdownVisuals();
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
                HideMenu();
            }
        }

        [UxmlAttribute("html-name")]
        public string htmlName
        {
            get => _htmlName;
            set => _htmlName = value ?? string.Empty;
        }

        public Html2UxmlDropdownField()
        {
            focusable = true;
            AddToClassList("html2uxml-dropdown");
            Html2UxmlToggle.AttachControlsStyleSheet(this);

            _label = new Label
            {
                name = "html2uxml-dropdown-label",
                pickingMode = PickingMode.Ignore,
            };
            _label.AddToClassList("html2uxml-dropdown-label");

            _arrow = new VisualElement
            {
                name = "html2uxml-dropdown-arrow",
                pickingMode = PickingMode.Ignore,
            };
            _arrow.AddToClassList("html2uxml-dropdown-arrow");

            _menu = new VisualElement
            {
                name = "html2uxml-dropdown-menu",
            };
            _menu.AddToClassList("html2uxml-dropdown-menu");
            _menu.style.display = DisplayStyle.None;

            Add(_label);
            Add(_arrow);
            Add(_menu);
            RegisterCallback<GeometryChangedEvent>(_ => PositionMenu());
            RegisterCallback<ClickEvent>(evt =>
            {
                Focus();
                ToggleMenu();
                evt.StopPropagation();
            });
            RegisterCallback<PointerDownEvent>(_ => { if (!_disabled) AddToClassList("__h2u-state-pressed"); });
            RegisterCallback<PointerUpEvent>(_ => RemoveFromClassList("__h2u-state-pressed"));
            RegisterCallback<PointerLeaveEvent>(_ => RemoveFromClassList("__h2u-state-pressed"));
            RegisterCallback<KeyDownEvent>(OnKeyDown);
            RegisterCallback<FocusOutEvent>(_ => HideMenu());
            schedule.Execute(UpdateDropdownVisuals).ExecuteLater(0);
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
                    if (_menuOpen)
                        SelectHighlighted();
                    else
                        ShowMenu();
                    evt.StopPropagation();
                    break;
                case KeyCode.Escape:
                    if (_menuOpen)
                    {
                        HideMenu();
                        evt.StopPropagation();
                    }
                    break;
                case KeyCode.Tab:
                    if (_menuOpen)
                        HideMenu();
                    break;
                case KeyCode.DownArrow:
                    if (!_menuOpen)
                        ShowMenu();
                    MoveHighlight(1);
                    evt.StopPropagation();
                    break;
                case KeyCode.UpArrow:
                    if (!_menuOpen)
                        ShowMenu();
                    MoveHighlight(-1);
                    evt.StopPropagation();
                    break;
                case KeyCode.Home:
                    if (_menuOpen && _choiceList.Count > 0)
                    {
                        SetHighlight(0);
                        evt.StopPropagation();
                    }
                    break;
                case KeyCode.End:
                    if (_menuOpen && _choiceList.Count > 0)
                    {
                        SetHighlight(_choiceList.Count - 1);
                        evt.StopPropagation();
                    }
                    break;
                default:
                    char ch = evt.character;
                    if (!char.IsControl(ch) && ch != 0)
                        TypeAhead(ch);
                    break;
            }
        }

        void TypeAhead(char ch)
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (now - _lastTypeAheadAt > 0.6)
                _typeAheadBuffer = string.Empty;
            _lastTypeAheadAt = now;
            _typeAheadBuffer += char.ToLowerInvariant(ch);
            for (int i = 0; i < _choiceList.Count; i++)
            {
                if (_choiceList[i].ToLowerInvariant().StartsWith(_typeAheadBuffer))
                {
                    if (_menuOpen)
                        SetHighlight(i);
                    else
                        value = _choiceList[i];
                    return;
                }
            }
        }

        void MoveHighlight(int direction)
        {
            if (_choiceList.Count == 0)
                return;
            int next = _highlightedIndex < 0
                ? (direction > 0 ? 0 : _choiceList.Count - 1)
                : (_highlightedIndex + direction + _choiceList.Count) % _choiceList.Count;
            SetHighlight(next);
        }

        void SetHighlight(int index)
        {
            _highlightedIndex = Mathf.Clamp(index, 0, _choiceList.Count - 1);
            for (int i = 0; i < _menu.childCount; i++)
                _menu[i].EnableInClassList("__h2u-state-highlighted", i == _highlightedIndex);
        }

        void SelectHighlighted()
        {
            if (_highlightedIndex < 0 || _highlightedIndex >= _choiceList.Count)
                return;
            value = _choiceList[_highlightedIndex];
            HideMenu();
        }

        void ParseChoices()
        {
            _choiceList.Clear();
            if (string.IsNullOrEmpty(_choices))
                return;
            foreach (var choice in _choices.Split(','))
            {
                var trimmed = choice.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    _choiceList.Add(trimmed);
            }
        }

        void RebuildMenu()
        {
            _menu.Clear();
            foreach (var choice in _choiceList)
            {
                var optionValue = choice;
                var option = new Label(optionValue)
                {
                    name = "html2uxml-dropdown-option",
                };
                option.AddToClassList("html2uxml-dropdown-option");
                option.RegisterCallback<ClickEvent>(evt =>
                {
                    value = optionValue;
                    HideMenu();
                    evt.StopPropagation();
                });
                _menu.Add(option);
            }
            PositionMenu();
        }

        void PositionMenu()
        {
            var height = resolvedStyle.height;
            if (height > 0)
                _menu.style.top = height;
        }

        void ToggleMenu()
        {
            if (_menuOpen)
                HideMenu();
            else
                ShowMenu();
        }

        void ShowMenu()
        {
            if (_disabled || _choiceList.Count == 0)
                return;
            PositionMenu();
            _menuOpen = true;
            _menu.style.display = DisplayStyle.Flex;
            int idx = _choiceList.IndexOf(_value);
            if (idx < 0) idx = 0;
            SetHighlight(idx);
        }

        void HideMenu()
        {
            _menuOpen = false;
            _menu.style.display = DisplayStyle.None;
            _highlightedIndex = -1;
            for (int i = 0; i < _menu.childCount; i++)
                _menu[i].RemoveFromClassList("__h2u-state-highlighted");
        }

        void UpdateDropdownVisuals()
        {
            _label.text = !string.IsNullOrEmpty(_value)
                ? _value
                : (_choiceList.Count > 0 ? _choiceList[0] : string.Empty);
        }
    }
}
