using System.Collections.Generic;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // Recognises the author-side opt-in attributes that let HTML/CSS source
    // declare runtime-populated UI:
    //
    //   data-h2u-list="<key>"        — container is a runtime list. Only the
    //                                  first matching child template survives
    //                                  conversion; siblings are dropped.
    //   data-h2u-item                — explicit template child. Optional: when
    //                                  absent the first element child wins.
    //   data-h2u-field="<name>"      — descendant inside the template; its
    //                                  UXML `name` becomes `h2u-field-<name>`
    //                                  so Html2UxmlList.Field can look it up.
    //   data-h2u-dynamic             — on <select>: skip baking <option> text
    //                                  into the `choices` attr; runtime calls
    //                                  SetChoices later.
    //   data-h2u-radio-group="<key>" — alias for data-h2u-list intended for
    //                                  containers of <input type=radio>.
    //
    // The dyn-list class on the container + dyn-template class on the
    // surviving child give the runtime helper stable hooks that don't
    // depend on the author's class names.
    public static class DynamicMarkers
    {
        public const string ListClass = "h2u-dyn-list";
        public const string TemplateClass = "h2u-dyn-template";
        public const string FieldNamePrefix = "h2u-field-";

        public static string GetListKey(HtmlLoader.HtmlNode node)
        {
            if (node?.Attrs == null) return null;
            return AttrUtil.Get(node.Attrs, "data-h2u-list")
                ?? AttrUtil.Get(node.Attrs, "data-h2u-radio-group");
        }

        public static bool IsListContainer(HtmlLoader.HtmlNode node)
            => !string.IsNullOrEmpty(GetListKey(node));

        public static bool IsItem(HtmlLoader.HtmlNode node)
            => node?.Attrs != null && AttrUtil.Has(node.Attrs, "data-h2u-item");

        public static string GetFieldName(HtmlLoader.HtmlNode node)
            => node?.Attrs == null ? null : AttrUtil.Get(node.Attrs, "data-h2u-field");

        public static bool HasDynamicFlag(HtmlLoader.HtmlNode node)
            => node?.Attrs != null && AttrUtil.Has(node.Attrs, "data-h2u-dynamic");

        // Pick the template child of a list container. Returns the first
        // child with data-h2u-item, otherwise the first non-text element.
        public static HtmlLoader.HtmlNode PickTemplateChild(IList<HtmlLoader.HtmlNode> children)
        {
            if (children == null) return null;
            HtmlLoader.HtmlNode firstElement = null;
            foreach (var c in children)
            {
                if (c == null || c.IsText) continue;
                if (firstElement == null) firstElement = c;
                if (IsItem(c)) return c;
            }
            return firstElement;
        }
    }
}
