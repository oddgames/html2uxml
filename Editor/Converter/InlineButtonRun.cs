using System;
using System.Collections.Generic;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // Wrap consecutive inline-flow buttons into a flex-row container.
    // Mirrors converter.py:
    //   _is_inline_button_control, _inline_button_run,
    //   _inline_control_run_wrapper_decls, _is_ignorable_text_node
    //
    // Browsers lay out adjacent <button>/<a>/<input type=button> elements
    // inline by default; UI Toolkit puts each on its own row in a parent that
    // is column-flex. Bundle each consecutive run of inline buttons into a
    // shared flex-row wrapper so they sit side-by-side.
    public static class InlineButtonRun
    {
        // Find the longest run of inline button controls starting at `start`.
        // Returns the run + the index after the run. Empty when run < 2.
        // `resolvedDisplayLookup`, when non-null, is consulted for the
        // node's cascade-resolved `display` so author CSS classes (not
        // just inline style) can opt a button out of inline grouping.
        public static (List<HtmlLoader.HtmlNode> run, int next) Detect(
            List<HtmlLoader.HtmlNode> children,
            int start,
            Func<HtmlLoader.HtmlNode, string> resolvedDisplayLookup = null)
        {
            var run = new List<HtmlLoader.HtmlNode>();
            int index = start;
            while (index < children.Count)
            {
                var child = children[index];
                if (IsIgnorableTextNode(child)) { index++; continue; }
                if (!IsInlineButtonControl(child, resolvedDisplayLookup)) break;
                run.Add(child);
                index++;
            }
            return run.Count < 2 ? (new List<HtmlLoader.HtmlNode>(), start) : (run, index);
        }

        public static bool IsInlineButtonControl(
            HtmlLoader.HtmlNode node,
            Func<HtmlLoader.HtmlNode, string> resolvedDisplayLookup = null)
        {
            if (node == null || node.IsText) return false;
            string tag = (node.Tag ?? "").ToLowerInvariant();
            bool isButton = tag == "button"
                          || (tag == "input"
                                && (AttrUtil.Get(node.Attrs, "type") ?? "text").ToLowerInvariant() is string t
                                && (t == "button" || t == "submit" || t == "reset"));
            if (!isButton) return false;
            // Skip when inline style or author CSS class explicitly
            // makes the button non-inline. Author classes are surfaced
            // via the optional resolved-display lookup; without it we
            // fall back to inline-style only (legacy behavior). Cam
            // buttons set `display:flex` via class — wrapping them
            // forces row-flow when the column container actually wants
            // them stacked vertically each at full container width.
            string display = (InlineStyleProp(node, "display") ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(display) && resolvedDisplayLookup != null)
                display = (resolvedDisplayLookup(node) ?? "").Trim().ToLowerInvariant();
            if (display == "none" || display == "block" || display == "flex"
                || display == "inline-flex"
                || display == "grid" || display == "table" || display == "list-item")
                return false;
            return true;
        }

        public static bool IsIgnorableTextNode(HtmlLoader.HtmlNode node)
            => node != null && node.IsText
               && (node.IsComment || string.IsNullOrWhiteSpace(node.Text));

        public static List<KeyValuePair<string, string>> WrapperDecls()
            => new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("display", "flex"),
                new KeyValuePair<string, string>("flex-direction", "row"),
                // flex-wrap: the cam-column case (5 buttons in a 78px
                // parent) relies on wrap to stack them vertically; the
                // horizontal toolbar case (DOWNLOAD/EXPORT/EXIT) can
                // also wrap if the parent gets compressed. Keep wrap on
                // since cam-column breakage is worse than a single-row
                // toolbar wrapping. Sized parents that author CSS
                // specifies will still keep all children inline.
                new KeyValuePair<string, string>("flex-wrap", "wrap"),
                new KeyValuePair<string, string>("align-items", "center"),
            };

        static string InlineStyleProp(HtmlLoader.HtmlNode node, string prop)
        {
            string style = AttrUtil.Get(node?.Attrs, "style");
            if (string.IsNullOrEmpty(style)) return null;
            foreach (var raw in style.Split(';'))
            {
                int colon = raw.IndexOf(':');
                if (colon <= 0) continue;
                string p = raw.Substring(0, colon).Trim();
                if (string.Equals(p, prop, System.StringComparison.OrdinalIgnoreCase))
                    return raw.Substring(colon + 1).Trim();
            }
            return null;
        }
    }
}
