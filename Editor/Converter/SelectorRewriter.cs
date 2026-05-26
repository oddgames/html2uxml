using System.Collections.Generic;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // Rewrite parsed CSS selectors into the subset USS accepts. Mirrors:
    //   _UNITY_USS_TYPES_LOWER, _is_unity_uss_type, _h2u_tag_class,
    //   _render_compound_for_uss, _rewrite_selector_for_uss
    //
    // USS only honours its built-in element-type names (Button, Toggle, …) and
    // the runtime classes shipped under odd:Html2Uxml*. Every other tag in the
    // author's CSS gets rewritten to a synthetic class selector
    // (`.h2u-tag-<tag>`); the converter then attaches that class to each UXML
    // node whose source tag matches.
    public static class SelectorRewriter
    {
        public const string CheckedStateClass = "__h2u-state-checked";

        public static readonly HashSet<string> UnityUssTypesLower = new HashSet<string>
        {
            "button", "toggle", "scrollview", "textfield", "floatfield",
            "integerfield", "slider", "sliderint", "image", "visualelement",
            "radiobutton", "radiobuttongroup", "dropdownfield", "progressbar",
            "foldout", "groupbox", "repeatbutton", "box", "listview", "treeview",
            "minmaxslider", "vector2field", "vector3field", "vector4field",
            "boundsfield", "rectfield", "colorfield", "objectfield", "enumfield",
            "maskfield", "longfield", "doublefield", "helpbox", "tabview", "tab",
            "html2uxmlpanel", "html2uxmlelement", "html2uxmlbutton", "html2uxmllabel",
            "html2uxmlscrollview", "html2uxmltextfield", "html2uxmlfloatfield",
            "html2uxmlslider", "html2uxmltoggle", "html2uxmlradiobutton",
            "html2uxmldropdownfield", "html2uxmlprogressbar", "html2uxmlfoldout",
            "html2uxmlgroupbox", "html2uxmlscaleroot",
            "html2uxmlvideo", "html2uxmlaudio", "html2uxmlimage", "html2uxmlcanvas",
            "html2uxmlembed",
        };

        public static bool IsUnityUssType(string tag)
            => !string.IsNullOrEmpty(tag) && tag != "*"
               && UnityUssTypesLower.Contains(tag.ToLowerInvariant());

        public static string H2UTagClass(string tag) => "h2u-tag-" + tag.ToLowerInvariant();

        // Render a single compound selector. `taggedTags` is mutated with any
        // tag rewritten to its `h2u-tag-X` synthetic class so the emitter can
        // attach the matching class to every UXML node of that tag.
        internal static string RenderCompoundForUss(CssCompoundSelector comp, HashSet<string> taggedTags)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(comp.Tag) && comp.Tag != "*")
            {
                if (IsUnityUssType(comp.Tag)) parts.Add(comp.Tag);
                else
                {
                    parts.Add("." + H2UTagClass(comp.Tag));
                    taggedTags?.Add(comp.Tag.ToLowerInvariant());
                }
            }
            if (!string.IsNullOrEmpty(comp.Id)) parts.Add("#" + comp.Id);
            foreach (var c in comp.Classes) parts.Add("." + c);
            foreach (var ps in comp.Pseudo)
            {
                string pseudo = ps.StartsWith(":") ? ps : ":" + ps;
                parts.Add(pseudo == ":checked" ? "." + CheckedStateClass : pseudo);
            }
            return parts.Count == 0 ? "*" : string.Concat(parts);
        }

        internal static string RewriteSelectorForUss(CssSelector sel, HashSet<string> taggedTags)
        {
            if (sel == null || sel.Chain == null || sel.Chain.Count == 0) return sel?.Raw ?? "*";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < sel.Chain.Count; i++)
            {
                var (combinator, comp) = sel.Chain[i];
                if (i > 0)
                    sb.Append(combinator == " " ? " " : " " + combinator + " ");
                sb.Append(RenderCompoundForUss(comp, taggedTags));
            }
            return sb.ToString();
        }
    }
}
