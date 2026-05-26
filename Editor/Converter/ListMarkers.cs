using System.Collections.Generic;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // <li> bullet / numbered marker emission. Mirrors converter.py:
    //   _list_marker, _list_item_container_decls,
    //   _list_marker_label_decls, _list_container_marker_decls,
    //   _list_has_marked_items
    //
    // Skeleton port: cascade-aware list-style-type lookup is deferred
    // (converter.py reads ResolvedStyle). For now we honour an inline
    // `style="list-style-type: …"` on the <li> or its parent, falling back
    // to the tag default (• for ul, 1. for ol).
    public static class ListMarkers
    {
        public static string Marker(HtmlLoader.HtmlNode li,
                                    HtmlLoader.HtmlNode parent,
                                    int ordinal)
        {
            if (li == null || (li.Tag ?? "").ToLowerInvariant() != "li") return null;
            if (parent == null) return null;
            string parentTag = (parent.Tag ?? "").ToLowerInvariant();
            if (parentTag != "ul" && parentTag != "ol") return null;

            string style = (InlineStyleProp(li, "list-style-type")
                          ?? InlineStyleProp(parent, "list-style-type")
                          ?? InlineStyleProp(li, "list-style")
                          ?? InlineStyleProp(parent, "list-style")
                          ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(style))
                style = parentTag == "ol" ? "decimal" : "disc";
            if (style.Contains("none")) return null;
            return parentTag == "ol" ? ordinal + "." : "•";
        }

        public static List<KeyValuePair<string, string>> ItemContainerDecls(bool hasMarker)
        {
            var decls = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("display", "flex"),
                new KeyValuePair<string, string>("flex-direction", "row"),
            };
            if (hasMarker)
                decls.Add(new KeyValuePair<string, string>("align-items", "flex-start"));
            return decls;
        }

        public static List<KeyValuePair<string, string>> MarkerLabelDecls(HtmlLoader.HtmlNode parent)
        {
            bool ordered = parent != null && (parent.Tag ?? "").ToLowerInvariant() == "ol";
            string width = ordered ? "20px" : "14px";
            string marginRight = ordered ? "4px" : "10px";
            return new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("width", width),
                new KeyValuePair<string, string>("min-width", width),
                new KeyValuePair<string, string>("margin-right", marginRight),
                new KeyValuePair<string, string>("margin-top", "0"),
                new KeyValuePair<string, string>("margin-bottom", "0"),
                new KeyValuePair<string, string>("padding", "0"),
                new KeyValuePair<string, string>("flex-shrink", "0"),
                new KeyValuePair<string, string>("-unity-text-align", ordered ? "upper-right" : "upper-left"),
            };
        }

        public static bool HasMarkedItems(HtmlLoader.HtmlNode list)
        {
            if (list == null || list.Children == null) return false;
            string parentTag = (list.Tag ?? "").ToLowerInvariant();
            if (parentTag != "ul" && parentTag != "ol") return false;
            int ordinal = 1;
            foreach (var child in list.Children)
            {
                if (child.IsText) continue;
                if ((child.Tag ?? "").ToLowerInvariant() != "li") continue;
                if (Marker(child, list, ordinal) != null) return true;
                ordinal++;
            }
            return false;
        }

        // Pull a single property out of a node's `style="…"` attribute. Naive
        // split; matches what ConvertEngine.ParseInlineStyle does.
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
