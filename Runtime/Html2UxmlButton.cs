using UnityEngine;
using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    [UxmlElement]
    public partial class Html2UxmlButton : Button
    {
        string _href = string.Empty;
        string _buttonType = "button";
        string _formName = string.Empty;
        string _formValue = string.Empty;
        string _accept = string.Empty;
        bool _multiple;
        string _text = string.Empty;
        Label _textLabel;

        public override string text
        {
            get => _text;
            set
            {
                _text = value ?? string.Empty;
                base.text = string.Empty;
                SyncTextLabel();
            }
        }

        [UxmlAttribute("href")]
        public string Href
        {
            get => _href;
            set => _href = value ?? string.Empty;
        }

        [UxmlAttribute("button-type")]
        public string ButtonType
        {
            get => _buttonType;
            set => _buttonType = string.IsNullOrEmpty(value) ? "button" : value.ToLowerInvariant();
        }

        [UxmlAttribute("form-name")]
        public string FormName
        {
            get => _formName;
            set => _formName = value ?? string.Empty;
        }

        [UxmlAttribute("form-value")]
        public string FormValue
        {
            get => _formValue;
            set => _formValue = value ?? string.Empty;
        }

        [UxmlAttribute("accept")]
        public string Accept
        {
            get => _accept;
            set => _accept = value ?? string.Empty;
        }

        [UxmlAttribute("multiple")]
        public bool Multiple
        {
            get => _multiple;
            set => _multiple = value;
        }

        [UxmlAttribute("disabled")]
        public bool Disabled
        {
            get => !enabledSelf;
            set => SetEnabled(!value);
        }

        public Html2UxmlButton()
        {
            this.AddManipulator(new Html2UxmlAnimationManipulator());
            this.AddManipulator(new Html2UxmlPaintManipulator());
            AddToClassList("html2uxml-button");
            Html2UxmlToggle.AttachControlsStyleSheet(this);
            // Inline style wins over USS class rules — setting align-self
            // here blocks the cascade from honouring the author's parent
            // align-items:stretch (e.g. cam-buttons inside a column flex
            // container). Drop the inline override and let USS decide.
            EnsureTextLabel();
            clicked += OnClicked;
            RegisterCallback<PointerDownEvent>(_ => AddToClassList("__h2u-state-pressed"));
            RegisterCallback<PointerUpEvent>(_ => RemoveFromClassList("__h2u-state-pressed"));
            RegisterCallback<PointerLeaveEvent>(_ => RemoveFromClassList("__h2u-state-pressed"));
        }

        void EnsureTextLabel()
        {
            if (_textLabel != null)
                return;

            _textLabel = new Label
            {
                name = "__h2u-button-text",
                pickingMode = PickingMode.Ignore
            };
            _textLabel.AddToClassList("html2uxml-button-text");
            _textLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
            _textLabel.style.whiteSpace = WhiteSpace.NoWrap;
            _textLabel.style.flexShrink = 0f;
            hierarchy.Add(_textLabel);
            SyncTextLabel();
        }

        void SyncTextLabel()
        {
            if (_textLabel == null)
                return;
            _textLabel.text = _text;
            // Empty label still claims layout space (default Label margin
            // 2px each side), which shifts an icon-only button's child
            // off-center inside its flex parent. Hide when there's no text.
            _textLabel.style.display = string.IsNullOrEmpty(_text)
                ? DisplayStyle.None
                : DisplayStyle.Flex;
        }

        void OnClicked()
        {
            OpenHref();
            if (_buttonType == "reset")
                ResetEnclosingForm();
        }

        void OpenHref()
        {
            var href = _href.Trim();
            if (string.IsNullOrEmpty(href) || href == "#")
                return;
            Application.OpenURL(href);
        }

        void ResetEnclosingForm()
        {
            VisualElement form = null;
            for (var p = parent; p != null; p = p.parent)
            {
                if (p.ClassListContains("h2u-tag-form") || p.ClassListContains("html2uxml-form"))
                {
                    form = p;
                    break;
                }
            }
            if (form == null)
                return;
            form.Query<Html2UxmlToggle>().ForEach(t => t.value = false);
            form.Query<Html2UxmlRadioButton>().ForEach(r => r.value = false);
            form.Query<Html2UxmlTextField>().ForEach(t => t.value = string.Empty);
            form.Query<Html2UxmlFloatField>().ForEach(f => f.value = 0f);
        }
    }
}
