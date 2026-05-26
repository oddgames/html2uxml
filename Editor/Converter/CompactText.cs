using System.Collections.Generic;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // Simplified port of converter._compact_text_container_decls /
    // _is_compact_text_stack. Adds `min-height:0; overflow:hidden` to
    // column-flex containers whose only children are inline-text elements
    // (span/p/h1-h6/etc.). Browsers shrink-to-fit such stacks; UI Toolkit
    // grows them by default unless we cap them.
    //
    // Skeleton omits the deep _compact_nowrap_label_decls predicate and the
    // 32px-height row variant from converter.py. Adds enough behaviour for
    // typical card / button text stacks not to overflow their parent.
    public static class CompactText
    {
        static readonly HashSet<string> InlineTextTags = new HashSet<string>
        {
            "span","p","label","small","strong","em","b","i","u","code",
            "h1","h2","h3","h4","h5","h6","figcaption","caption","summary",
            "legend","dt","dd","time","output",
        };

        public static List<KeyValuePair<string, string>> ContainerDecls(
            HtmlLoader.HtmlNode node,
            EmitStateView state)
        {
            if (node?.Children == null) return new List<KeyValuePair<string, string>>();
            string display = state.ResolvedProp(node, "display")?.Trim().ToLowerInvariant();
            if (display != "flex" && display != "inline-flex")
                return new List<KeyValuePair<string, string>>();
            string flexDir = state.ResolvedProp(node, "flex-direction")?.Trim().ToLowerInvariant();
            if (flexDir != "column") return new List<KeyValuePair<string, string>>();
            // All non-text children must be inline-text tags.
            int textChildren = 0;
            foreach (var child in node.Children)
            {
                if (child.IsComment) continue;
                if (child.IsText)
                {
                    if (!string.IsNullOrWhiteSpace(child.Text)) return new List<KeyValuePair<string, string>>();
                    continue;
                }
                string t = (child.Tag ?? "").ToLowerInvariant();
                if (!InlineTextTags.Contains(t)) return new List<KeyValuePair<string, string>>();
                textChildren++;
            }
            if (textChildren == 0) return new List<KeyValuePair<string, string>>();
            var decls = new List<KeyValuePair<string, string>>();
            if (state.ResolvedProp(node, "min-height") == null)
                decls.Add(new KeyValuePair<string, string>("min-height", "0"));
            if (state.ResolvedProp(node, "overflow") == null)
                decls.Add(new KeyValuePair<string, string>("overflow", "hidden"));
            return decls;
        }

        // Minimal interface so the helper doesn't depend on EmitState directly.
        public interface EmitStateView
        {
            string ResolvedProp(HtmlLoader.HtmlNode node, string prop);
        }
    }
}
