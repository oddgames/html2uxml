using System.Collections.Generic;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // UA / Unity-default reset rules. Mirrors converter.py:
    //   _BUTTON_RESET_DECLS, _emit_button_reset,
    //   _INPUT_TAG_DEFAULTS, _emit_input_defaults
    //
    // Unity's built-in Button + BaseField bring chrome (padding, borders,
    // 3px-side margin) that browsers don't. Reset BEFORE author CSS so the
    // author's rules win on equal specificity.
    public static class UnityDefaults
    {
        static readonly List<KeyValuePair<string, string>> ButtonResetDecls = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string, string>("margin", "0"),
            new KeyValuePair<string, string>("padding", "0"),
            new KeyValuePair<string, string>("min-width", "0"),
            new KeyValuePair<string, string>("min-height", "0"),
            new KeyValuePair<string, string>("background-color", "rgba(0, 0, 0, 0)"),
            new KeyValuePair<string, string>("border-top-width", "0"),
            new KeyValuePair<string, string>("border-right-width", "0"),
            new KeyValuePair<string, string>("border-bottom-width", "0"),
            new KeyValuePair<string, string>("border-left-width", "0"),
            new KeyValuePair<string, string>("border-top-left-radius", "0"),
            new KeyValuePair<string, string>("border-top-right-radius", "0"),
            new KeyValuePair<string, string>("border-bottom-right-radius", "0"),
            new KeyValuePair<string, string>("border-bottom-left-radius", "0"),
            new KeyValuePair<string, string>("-unity-text-align", "middle-center"),
            // Unity's built-in Button USS class hard-codes
            // align-self:flex-start, which blocks the parent's
            // align-items:stretch from reaching button children. CSS
            // buttons stretch to fill their flex column container's
            // cross-axis by default, so emit stretch explicitly.
            new KeyValuePair<string, string>("align-self", "stretch"),
        };

        static readonly Dictionary<string, List<KeyValuePair<string, string>>> InputTagDefaults
            = new Dictionary<string, List<KeyValuePair<string, string>>>
            {
                { "input",    ZeroMargin() },
                { "textarea", ZeroMargin() },
                { "select",   ZeroMargin() },
            };

        public static List<KeyValuePair<string, string>> ButtonReset() => new List<KeyValuePair<string, string>>(ButtonResetDecls);

        public static IReadOnlyDictionary<string, List<KeyValuePair<string, string>>> InputDefaults => InputTagDefaults;

        public static bool TreeContainsButton(HtmlLoader.HtmlNode node)
        {
            if (node == null || node.IsText) return false;
            string t = (node.Tag ?? "").ToLowerInvariant();
            if (t == "button" || t == "a") return true;
            if (t == "input")
            {
                string ty = (AttrUtil.Get(node.Attrs, "type") ?? "text").ToLowerInvariant();
                if (ty == "button" || ty == "submit" || ty == "reset") return true;
            }
            if (node.Children != null)
                foreach (var c in node.Children)
                    if (TreeContainsButton(c)) return true;
            return false;
        }

        public static bool TreeContainsTag(HtmlLoader.HtmlNode node, string tag)
        {
            if (node == null || node.IsText) return false;
            if ((node.Tag ?? "").ToLowerInvariant() == tag) return true;
            if (node.Children != null)
                foreach (var c in node.Children)
                    if (TreeContainsTag(c, tag)) return true;
            return false;
        }

        static List<KeyValuePair<string, string>> ZeroMargin()
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("margin-top", "0"),
                new KeyValuePair<string, string>("margin-right", "0"),
                new KeyValuePair<string, string>("margin-bottom", "0"),
                new KeyValuePair<string, string>("margin-left", "0"),
            };
    }
}
