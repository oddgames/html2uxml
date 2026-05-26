using UnityEngine;
using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    // Placeholder for <embed>, <object>, <math>, <iframe> — Unity has no
    // generic plug-in / web-renderer surface. Renders a labeled box so the
    // layout slot is preserved; consumers can subclass to implement bespoke
    // rendering (e.g. native plug-in via DllImport, math rendering with a
    // custom font, image-of-page screenshot, etc.).
    [UxmlElement]
    public partial class Html2UxmlEmbed : Html2UxmlPanel
    {
        readonly Label _placeholder;
        string _src = string.Empty;
        string _embedType = string.Empty;
        string _data = string.Empty;
        string _kind = "embed";

        [UxmlAttribute("src")]
        public string Src
        {
            get => _src;
            set { _src = value ?? string.Empty; UpdatePlaceholderText(); }
        }

        [UxmlAttribute("embed-type")]
        public string EmbedType
        {
            get => _embedType;
            set { _embedType = value ?? string.Empty; UpdatePlaceholderText(); }
        }

        [UxmlAttribute("data")]
        public string Data
        {
            get => _data;
            set { _data = value ?? string.Empty; UpdatePlaceholderText(); }
        }

        [UxmlAttribute("kind")]
        public string Kind
        {
            get => _kind;
            set { _kind = string.IsNullOrEmpty(value) ? "embed" : value.ToLowerInvariant(); UpdatePlaceholderText(); }
        }

        public Html2UxmlEmbed()
        {
            AddToClassList("html2uxml-embed");
            style.minWidth = 80;
            style.minHeight = 40;
            style.justifyContent = Justify.Center;
            style.alignItems = Align.Center;
            style.backgroundColor = new Color(0.95f, 0.95f, 0.95f, 1f);
            style.borderTopWidth = 1;
            style.borderRightWidth = 1;
            style.borderBottomWidth = 1;
            style.borderLeftWidth = 1;
            style.borderTopColor = new Color(0.7f, 0.7f, 0.7f, 1f);
            style.borderRightColor = new Color(0.7f, 0.7f, 0.7f, 1f);
            style.borderBottomColor = new Color(0.7f, 0.7f, 0.7f, 1f);
            style.borderLeftColor = new Color(0.7f, 0.7f, 0.7f, 1f);

            _placeholder = new Label { name = "html2uxml-embed-label", pickingMode = PickingMode.Ignore };
            _placeholder.style.color = new Color(0.4f, 0.4f, 0.4f, 1f);
            _placeholder.style.unityTextAlign = TextAnchor.MiddleCenter;
            Add(_placeholder);
            UpdatePlaceholderText();
        }

        void UpdatePlaceholderText()
        {
            string url = !string.IsNullOrEmpty(_src) ? _src
                : !string.IsNullOrEmpty(_data) ? _data
                : string.Empty;
            string head = string.IsNullOrEmpty(_embedType) ? _kind : $"{_kind} ({_embedType})";
            _placeholder.text = string.IsNullOrEmpty(url) ? $"[{head}]" : $"[{head}: {url}]";
        }
    }
}
