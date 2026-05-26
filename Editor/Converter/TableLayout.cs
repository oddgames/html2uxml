using System.Collections.Generic;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // Table tag → flex-row/column layout. Mirrors converter.py:
    //   _table_layout_decls, _has_previous_table_sibling,
    //   _TABLE_SECTION_TAGS, _TABLE_CELL_TAGS, _TABLE_COLLAPSE_OVERLAP
    //
    // USS has no native table model. The converter approximates table/tr/td
    // with flex containers, and `border-collapse: collapse` with negative
    // margins on subsequent rows/cells. This skeleton honours inline `style="…"`
    // for layout-overrides; cascade-aware reads (display:none on a row, etc.)
    // are deferred to the iterative resolver pass.
    public static class TableLayout
    {
        public static readonly HashSet<string> TableSectionTags = new HashSet<string> { "thead", "tbody", "tfoot" };
        public static readonly HashSet<string> TableCellTags    = new HashSet<string> { "th", "td" };
        public const string CollapseOverlap = "0.5px";

        public static List<KeyValuePair<string, string>> LayoutDecls(
            HtmlLoader.HtmlNode node,
            HtmlLoader.HtmlNode parent,
            bool tableBorderCollapse)
        {
            string tag = (node?.Tag ?? "").ToLowerInvariant();
            bool isTable = tag == "table";
            bool isSection = TableSectionTags.Contains(tag);
            bool isRow = tag == "tr";
            bool isCell = TableCellTags.Contains(tag);
            if (!isTable && !isSection && !isRow && !isCell)
                return new List<KeyValuePair<string, string>>();

            string display = (InlineStyleProp(node, "display") ?? "").Trim().ToLowerInvariant();
            if (display == "none") return new List<KeyValuePair<string, string>>();

            var decls = new List<KeyValuePair<string, string>>();
            if (isTable)
            {
                if (display != "flex" && display != "inline-flex") decls.Add(KV("display", "flex"));
                if (InlineStyleProp(node, "flex-direction") == null) decls.Add(KV("flex-direction", "column"));
                if (InlineStyleProp(node, "align-items") == null)    decls.Add(KV("align-items", "stretch"));
            }
            else if (isSection)
            {
                if (display != "flex" && display != "inline-flex") decls.Add(KV("display", "flex"));
                if (InlineStyleProp(node, "flex-direction") == null) decls.Add(KV("flex-direction", "column"));
                if (InlineStyleProp(node, "align-items") == null)    decls.Add(KV("align-items", "stretch"));
                if (InlineStyleProp(node, "width") == null)          decls.Add(KV("width", "100%"));
                if (tableBorderCollapse && HasPreviousSibling(node, parent, TableSectionTags))
                    decls.Add(KV("margin-top", "-" + CollapseOverlap));
            }
            else if (isRow)
            {
                if (display != "flex" && display != "inline-flex") decls.Add(KV("display", "flex"));
                if (InlineStyleProp(node, "flex-direction") == null) decls.Add(KV("flex-direction", "row"));
                if (InlineStyleProp(node, "align-items") == null)    decls.Add(KV("align-items", "stretch"));
                if (InlineStyleProp(node, "width") == null)          decls.Add(KV("width", "100%"));
                if (tableBorderCollapse && HasPreviousSibling(node, parent, new HashSet<string> { "tr" }))
                    decls.Add(KV("margin-top", "-" + CollapseOverlap));
            }
            else // cell
            {
                if (InlineStyleProp(node, "flex-grow") == null)   decls.Add(KV("flex-grow", "1"));
                if (InlineStyleProp(node, "flex-shrink") == null) decls.Add(KV("flex-shrink", "1"));
                if (InlineStyleProp(node, "flex-basis") == null)  decls.Add(KV("flex-basis", "0"));
                if (InlineStyleProp(node, "min-width") == null)   decls.Add(KV("min-width", "0"));
                if (HasOnlyInlineText(node))
                {
                    if (display != "flex" && display != "inline-flex") decls.Add(KV("display", "flex"));
                    if (InlineStyleProp(node, "flex-direction") == null) decls.Add(KV("flex-direction", "row"));
                    if (InlineStyleProp(node, "align-items") == null)    decls.Add(KV("align-items", "center"));
                }
                if (tableBorderCollapse && HasPreviousSibling(node, parent, TableCellTags))
                    decls.Add(KV("margin-left", "-" + CollapseOverlap));
            }
            return decls;
        }

        // Read `border-collapse` off the <table> via inline style.
        public static bool TableBorderCollapse(HtmlLoader.HtmlNode table)
            => string.Equals(InlineStyleProp(table, "border-collapse"), "collapse",
                System.StringComparison.OrdinalIgnoreCase);

        static bool HasPreviousSibling(HtmlLoader.HtmlNode node, HtmlLoader.HtmlNode parent, HashSet<string> tags)
        {
            if (parent?.Children == null) return false;
            foreach (var child in parent.Children)
            {
                if (ReferenceEquals(child, node)) return false;
                if (!child.IsText && tags.Contains((child.Tag ?? "").ToLowerInvariant())) return true;
            }
            return false;
        }

        static bool HasOnlyInlineText(HtmlLoader.HtmlNode node)
        {
            if (node?.Children == null) return false;
            bool sawText = false;
            foreach (var c in node.Children)
            {
                if (c.IsText)
                {
                    if (!string.IsNullOrWhiteSpace(c.Text)) sawText = true;
                    continue;
                }
                string t = (c.Tag ?? "").ToLowerInvariant();
                if (t != "span" && t != "b" && t != "i" && t != "em" && t != "strong"
                    && t != "small" && t != "code" && t != "u" && t != "br") return false;
            }
            return sawText;
        }

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

        static KeyValuePair<string, string> KV(string k, string v) => new KeyValuePair<string, string>(k, v);
    }
}
