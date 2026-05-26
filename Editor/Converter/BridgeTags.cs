using System.Collections.Generic;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // Bridge-aware UXML tag upgrade. Mirrors converter.py:
    //   _ODD_UXML_TAGS, _to_odd_uxml_tag,
    //   _BRIDGE_REQUIRED_PROPS, _requires_bridge_prop
    //
    // When a node's USS rule contains a custom prop the runtime
    // Html2UxmlPanel reads (gradients, shadows, masks, clip-path,
    // animation), the UXML element must be the corresponding
    // odd:Html2Uxml* type so the runtime adds its painter manipulator.
    public static class BridgeTags
    {
        public static readonly HashSet<string> BridgeRequiredProps = new HashSet<string>
        {
            "--odd-shadow-offset-x",
            "--odd-shadow-offset-y",
            "--odd-shadow-blur",
            "--odd-shadow-color",
            "--odd-box-shadows",
            "--odd-inner-shadow-offset-x",
            "--odd-inner-shadow-offset-y",
            "--odd-inner-shadow-blur",
            "--odd-inner-shadow-spread",
            "--odd-inner-shadow-color",
            "--odd-gradient",
            "--odd-radial-gradient",
            "--odd-radial-gradient-2",
            "--odd-repeating-linear-gradient",
            "--odd-tiled-radial-gradient",
            "--odd-background-pattern-size",
            "--odd-background-pattern-position",
            "--odd-background-color",
            "--odd-mask-image",
            "--odd-mask-fade-color",
            "--odd-clip-polygon",
            "--odd-vector-icon",
            "--odd-animation-name",
            "--odd-animation-keyframes",
        };

        static readonly Dictionary<string, string> OddUxmlTags = new Dictionary<string, string>
        {
            { "ui:VisualElement",  "odd:Html2UxmlElement" },
            { "ui:Label",          "odd:Html2UxmlLabel" },
            { "ui:Button",         "odd:Html2UxmlButton" },
            { "ui:ScrollView",     "odd:Html2UxmlScrollView" },
            { "ui:TextField",      "odd:Html2UxmlTextField" },
            { "ui:FloatField",     "odd:Html2UxmlFloatField" },
            { "ui:Slider",         "odd:Html2UxmlSlider" },
            { "ui:Toggle",         "odd:Html2UxmlToggle" },
            { "ui:RadioButton",    "odd:Html2UxmlRadioButton" },
            { "ui:DropdownField",  "odd:Html2UxmlDropdownField" },
            { "ui:ProgressBar",    "odd:Html2UxmlProgressBar" },
            { "ui:Foldout",        "odd:Html2UxmlFoldout" },
            { "ui:GroupBox",       "odd:Html2UxmlGroupBox" },
        };

        public static string ToOddUxmlTag(string tag)
            => OddUxmlTags.TryGetValue(tag, out var v) ? v : tag;

        public static bool RequiresBridge(IEnumerable<KeyValuePair<string, string>> decls)
        {
            if (decls == null) return false;
            foreach (var kv in decls)
                if (BridgeRequiredProps.Contains(kv.Key)) return true;
            return false;
        }
    }
}
