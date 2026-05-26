using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    [UxmlElement]
    public partial class Html2UxmlLabel : Label
    {
        string _forControl = string.Empty;
        string _htmlName = string.Empty;
        string _dateTime = string.Empty;

        [UxmlAttribute("for-control")]
        public string ForControl
        {
            get => _forControl;
            set => _forControl = value ?? string.Empty;
        }

        [UxmlAttribute("html-name")]
        public string HtmlName
        {
            get => _htmlName;
            set => _htmlName = value ?? string.Empty;
        }

        [UxmlAttribute("datetime")]
        public string DateTime
        {
            get => _dateTime;
            set => _dateTime = value ?? string.Empty;
        }

        public Html2UxmlLabel()
        {
            this.AddManipulator(new Html2UxmlAnimationManipulator());
            this.AddManipulator(new Html2UxmlPaintManipulator());
            RegisterCallback<ClickEvent>(OnClick);
        }

        void OnClick(ClickEvent evt)
        {
            if (string.IsNullOrEmpty(_forControl))
                return;
            var root = panel?.visualTree;
            if (root == null)
                return;
            var target = root.Q(name: _forControl);
            if (target == null)
                return;
            target.Focus();
            switch (target)
            {
                case Html2UxmlToggle t:
                    t.value = !t.value;
                    break;
                case Html2UxmlRadioButton r:
                    r.value = true;
                    break;
            }
        }
    }
}
