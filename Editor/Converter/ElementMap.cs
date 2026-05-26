using System.Collections.Generic;
using static ODDGames.Html2Uxml.Editor.Converter.AttrUtil;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // Port of the html2uxml.mappings ELEMENT_MAP table + map_element() dispatch.
    // Special-case branches (input/video/img/canvas/etc.) are handled here
    // before falling through to ELEMENT_MAP.
    public static class ElementMap
    {
        public static readonly HashSet<string> SKIP_TAGS = new HashSet<string>
        {
            "svg", "iframe", "embed", "object", "math",
            "noscript", "script", "template",
        };

        public static readonly Dictionary<string, ElementMapping> ELEMENT_MAP =
            new Dictionary<string, ElementMapping>
            {
                { "div",        E("ui:VisualElement", TextHandling.WrapInLabel) },
                { "section",    E("ui:VisualElement", TextHandling.WrapInLabel) },
                { "article",    E("ui:VisualElement", TextHandling.WrapInLabel) },
                { "header",     E("ui:VisualElement", TextHandling.WrapInLabel) },
                { "footer",     E("ui:VisualElement", TextHandling.WrapInLabel) },
                { "main",       E("ui:VisualElement", TextHandling.WrapInLabel) },
                { "nav",        E("ui:VisualElement", TextHandling.WrapInLabel) },
                { "aside",      E("ui:VisualElement", TextHandling.WrapInLabel) },
                { "form",       E("ui:VisualElement", TextHandling.WrapInLabel) },
                { "details",    E("ui:Foldout",       TextHandling.WrapInLabel) },
                { "summary",    E("ui:Label",         TextHandling.AssignToText) },
                { "progress",   E("odd:Html2UxmlProgressBar", TextHandling.Drop) },
                { "meter",      E("odd:Html2UxmlMeter",       TextHandling.Drop) },
                { "output",     E("ui:Label",         TextHandling.AssignToText) },
                { "dialog",     E("ui:VisualElement", TextHandling.WrapInLabel) },
                { "menu",       E("ui:VisualElement", TextHandling.WrapInLabel) },
                { "figure",     E("ui:VisualElement", TextHandling.WrapInLabel) },
                { "figcaption", E("ui:Label",         TextHandling.AssignToText) },
                { "ul",         E("ui:VisualElement", TextHandling.WrapInLabel) },
                { "ol",         E("ui:VisualElement", TextHandling.WrapInLabel) },
                { "li",         E("ui:VisualElement", TextHandling.WrapInLabel) },
                { "dl",         E("ui:VisualElement", TextHandling.WrapInLabel) },
                { "dt",         E("ui:Label",         TextHandling.AssignToText) },
                { "dd",         E("ui:Label",         TextHandling.AssignToText) },
                { "table",      E("odd:Html2UxmlTable",        TextHandling.WrapInLabel) },
                { "caption",    E("ui:Label",         TextHandling.AssignToText) },
                { "tr",         E("odd:Html2UxmlTableRow",     TextHandling.WrapInLabel) },
                { "thead",      E("odd:Html2UxmlTableSection", TextHandling.WrapInLabel) },
                { "tbody",      E("odd:Html2UxmlTableSection", TextHandling.WrapInLabel) },
                { "tfoot",      E("odd:Html2UxmlTableSection", TextHandling.WrapInLabel) },

                { "span",       E("ui:Label", TextHandling.AssignToText) },
                { "p",          E("ui:Label", TextHandling.AssignToText) },
                { "label",      E("ui:Label", TextHandling.AssignToText) },
                { "small",      E("ui:Label", TextHandling.AssignToText) },
                { "strong",     E("ui:Label", TextHandling.AssignToText) },
                { "em",         E("ui:Label", TextHandling.AssignToText) },
                { "b",          E("ui:Label", TextHandling.AssignToText) },
                { "i",          E("ui:Label", TextHandling.AssignToText) },
                { "u",          E("ui:Label", TextHandling.AssignToText) },
                { "code",       E("ui:Label", TextHandling.AssignToText) },
                { "pre",        E("ui:Label", TextHandling.AssignToText) },
                { "h1",         E("ui:Label", TextHandling.AssignToText) },
                { "h2",         E("ui:Label", TextHandling.AssignToText) },
                { "h3",         E("ui:Label", TextHandling.AssignToText) },
                { "h4",         E("ui:Label", TextHandling.AssignToText) },
                { "h5",         E("ui:Label", TextHandling.AssignToText) },
                { "h6",         E("ui:Label", TextHandling.AssignToText) },
                { "legend",     E("ui:Label", TextHandling.AssignToText) },
                { "title",      E("ui:Label", TextHandling.AssignToText) },
                { "time",       E("ui:Label", TextHandling.AssignToText) },

                { "button",     E("ui:Button", TextHandling.AssignToText) },
                { "a",          E("ui:Button", TextHandling.AssignToText) },

                { "img",        E("ui:VisualElement", TextHandling.Drop) },
                { "br",         E("ui:VisualElement", new Dictionary<string,string>{{"class","br"}}, TextHandling.Drop) },
                { "hr",         E("ui:VisualElement", new Dictionary<string,string>{{"class","hr"}}, TextHandling.Drop) },

                { "fieldset",   E("ui:GroupBox",     TextHandling.WrapInLabel) },
                { "textarea",   E("ui:TextField",    new Dictionary<string,string>{{"multiline","true"}}, TextHandling.AssignToText) },
                { "select",     E("ui:DropdownField", TextHandling.Drop) },
            };

        public static ElementMapping MapElement(string tag, IDictionary<string, string> attrs)
        {
            tag = tag.ToLowerInvariant();
            if (tag == "input") return InputMap.MapInput(attrs);

            switch (tag)
            {
                case "datalist": case "colgroup": case "col":
                case "source":   case "track":    case "param":
                    return new ElementMapping("ui:VisualElement",
                        new Dictionary<string, string>(), TextHandling.Drop);
            }

            if (tag == "progress")
            {
                var a = new Dictionary<string, string>
                {
                    { "low-value", "0" },
                    { "high-value", NonEmpty(attrs, "max") ? attrs["max"] : "1" },
                };
                if (NonEmpty(attrs, "value"))
                    a["value"] = attrs["value"];
                else
                    a["indeterminate"] = "true";
                return new ElementMapping("odd:Html2UxmlProgressBar", a, TextHandling.Drop);
            }

            if (tag == "meter")
            {
                var a = new Dictionary<string, string>
                {
                    { "low-value", NonEmpty(attrs, "min") ? attrs["min"] : "0" },
                    { "high-value", NonEmpty(attrs, "max") ? attrs["max"] : "1" },
                };
                if (NonEmpty(attrs, "value"))   a["value"]   = attrs["value"];
                if (NonEmpty(attrs, "low"))     a["low"]     = attrs["low"];
                if (NonEmpty(attrs, "high"))    a["high"]    = attrs["high"];
                if (NonEmpty(attrs, "optimum")) a["optimum"] = attrs["optimum"];
                return new ElementMapping("odd:Html2UxmlMeter", a, TextHandling.Drop);
            }

            if (tag == "video" || tag == "audio")
            {
                var a = new Dictionary<string, string>();
                string src = Get(attrs, "src") ?? "";
                if (src.Length > 0) a["src"] = src;
                if (Has(attrs, "controls")) a["controls"] = "true";
                if (Has(attrs, "autoplay")) a["autoplay"] = "true";
                if (Has(attrs, "loop"))     a["loop"]     = "true";
                if (Has(attrs, "muted"))    a["muted"]    = "true";
                if (tag == "video" && NonEmpty(attrs, "poster"))
                    a["poster"] = attrs["poster"];
                string cls = tag == "video" ? "odd:Html2UxmlVideo" : "odd:Html2UxmlAudio";
                return new ElementMapping(cls, a, TextHandling.Drop);
            }

            if (tag == "img")
            {
                var a = new Dictionary<string, string>();
                if (NonEmpty(attrs, "src"))     a["src"]     = attrs["src"];
                if (NonEmpty(attrs, "alt"))     a["alt"]     = attrs["alt"];
                if (NonEmpty(attrs, "loading")) a["loading"] = attrs["loading"];
                return new ElementMapping("odd:Html2UxmlImage", a, TextHandling.Drop);
            }

            if (tag == "canvas")
            {
                var a = new Dictionary<string, string>();
                if (NonEmpty(attrs, "width"))  a["canvas-width"]  = attrs["width"];
                if (NonEmpty(attrs, "height")) a["canvas-height"] = attrs["height"];
                return new ElementMapping("odd:Html2UxmlCanvas", a, TextHandling.Drop);
            }

            if (tag == "embed" || tag == "object" || tag == "math")
            {
                var a = new Dictionary<string, string> { { "kind", tag } };
                if (NonEmpty(attrs, "src"))  a["src"]        = attrs["src"];
                if (NonEmpty(attrs, "data")) a["data"]       = attrs["data"];
                if (NonEmpty(attrs, "type")) a["embed-type"] = attrs["type"];
                return new ElementMapping("odd:Html2UxmlEmbed", a, TextHandling.Drop);
            }

            if (tag == "thead" || tag == "tbody" || tag == "tfoot")
                return new ElementMapping("odd:Html2UxmlTableSection",
                    new Dictionary<string, string> { { "section", tag } },
                    TextHandling.WrapInLabel);

            if (tag == "td" || tag == "th")
            {
                var a = new Dictionary<string, string>();
                if (NonEmpty(attrs, "colspan")) a["colspan"] = attrs["colspan"];
                if (NonEmpty(attrs, "rowspan")) a["rowspan"] = attrs["rowspan"];
                if (tag == "th") a["header"] = "true";
                return new ElementMapping("odd:Html2UxmlTableCell", a, TextHandling.WrapInLabel);
            }

            if (tag == "a")
            {
                var a = new Dictionary<string, string>();
                string href = Get(attrs, "href") ?? "";
                if (href.Length > 0) a["href"] = href;
                return new ElementMapping("ui:Button", a, TextHandling.AssignToText);
            }

            if (tag == "output")
            {
                var a = new Dictionary<string, string>();
                string forAttr = Get(attrs, "for") ?? "";
                if (forAttr.Length > 0)
                {
                    int sp = forAttr.IndexOf(' ');
                    a["for-control"] = sp < 0 ? forAttr : forAttr.Substring(0, sp);
                }
                if (NonEmpty(attrs, "name")) a["html-name"] = attrs["name"];
                return new ElementMapping("ui:Label", a, TextHandling.AssignToText);
            }

            if (tag == "time")
            {
                var a = new Dictionary<string, string>();
                if (NonEmpty(attrs, "datetime")) a["datetime"] = attrs["datetime"];
                return new ElementMapping("ui:Label", a, TextHandling.AssignToText);
            }

            if (tag == "textarea")
            {
                var a = new Dictionary<string, string> { { "multiline", "true" } };
                string placeholder = Get(attrs, "placeholder") ?? "";
                if (placeholder.Length > 0) a["placeholder"] = placeholder;
                if (Has(attrs, "required")) a["required"] = "true";
                if (Has(attrs, "readonly")) a["readonly"] = "true";
                if (Has(attrs, "disabled")) a["disabled"] = "true";
                if (NonEmpty(attrs, "maxlength")) a["maxlength"] = attrs["maxlength"];
                if (NonEmpty(attrs, "minlength")) a["minlength"] = attrs["minlength"];
                return new ElementMapping("odd:Html2UxmlTextField", a, TextHandling.AssignToText);
            }

            if (tag == "select")
            {
                var a = new Dictionary<string, string>();
                if (Has(attrs, "disabled")) a["disabled"] = "true";
                if (Has(attrs, "required")) a["required"] = "true";
                if (NonEmpty(attrs, "name")) a["html-name"] = attrs["name"];
                return new ElementMapping("ui:DropdownField", a, TextHandling.Drop);
            }

            if (tag == "fieldset")
            {
                var a = new Dictionary<string, string>();
                // ui:GroupBox has no `disabled` UXML attr but the standard
                // `enabled` flag on every VisualElement cascades SetEnabled to
                // children, matching <fieldset disabled> semantics.
                if (Has(attrs, "disabled")) a["enabled"] = "false";
                return new ElementMapping("ui:GroupBox", a, TextHandling.Drop);
            }

            if (tag == "button")
            {
                var a = new Dictionary<string, string>();
                if (NonEmpty(attrs, "type")) a["button-type"] = attrs["type"].ToLowerInvariant();
                if (Has(attrs, "disabled"))  a["disabled"]    = "true";
                if (NonEmpty(attrs, "name"))  a["form-name"]  = attrs["name"];
                if (NonEmpty(attrs, "value")) a["form-value"] = attrs["value"];
                return new ElementMapping("odd:Html2UxmlButton", a, TextHandling.AssignToText);
            }

            if (SKIP_TAGS.Contains(tag))
                return new ElementMapping("ui:VisualElement",
                    new Dictionary<string, string> { { "class", "placeholder-" + tag } },
                    TextHandling.Drop);

            if (ELEMENT_MAP.TryGetValue(tag, out var em))
                return em.Clone();

            return new ElementMapping("ui:VisualElement",
                new Dictionary<string, string>(), TextHandling.WrapInLabel);
        }

        static ElementMapping E(string uxmlType, TextHandling th)
            => new ElementMapping(uxmlType, new Dictionary<string, string>(), th);

        static ElementMapping E(string uxmlType, Dictionary<string, string> attrs, TextHandling th)
            => new ElementMapping(uxmlType, attrs, th);
    }
}
