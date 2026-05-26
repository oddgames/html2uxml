using System.Collections.Generic;
using static ODDGames.Html2Uxml.Editor.Converter.AttrUtil;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // Port of html2uxml.mappings.map_input — dispatches <input type="..."> to
    // the right UXML control with the right attribute set.
    public static class InputMap
    {
        public static ElementMapping MapInput(IDictionary<string, string> attrs)
        {
            string t = (Get(attrs, "type") ?? "text").ToLowerInvariant();
            string value = Get(attrs, "value") ?? "";
            string placeholder = Get(attrs, "placeholder") ?? "";

            if (IsTextInputType(t))
            {
                var a = new Dictionary<string, string>();
                if (value.Length > 0) a["value"] = value;
                if (placeholder.Length > 0) a["placeholder"] = placeholder;
                a["input-type"] = t;
                AddTextValidation(attrs, a);
                return new ElementMapping("odd:Html2UxmlTextField", a, TextHandling.Drop);
            }
            if (t == "password")
            {
                var a = new Dictionary<string, string>();
                if (value.Length > 0) a["value"] = value;
                if (placeholder.Length > 0) a["placeholder"] = placeholder;
                a["password"] = "true";
                a["input-type"] = "password";
                AddTextValidation(attrs, a);
                return new ElementMapping("odd:Html2UxmlTextField", a, TextHandling.Drop);
            }
            if (t == "number")
            {
                var a = new Dictionary<string, string>();
                if (value.Length > 0) a["value"] = value;
                if (Has(attrs, "min"))  a["min"]  = attrs["min"];
                if (Has(attrs, "max"))  a["max"]  = attrs["max"];
                if (Has(attrs, "step")) a["step"] = attrs["step"];
                if (Has(attrs, "required")) a["required"] = "true";
                if (Has(attrs, "readonly")) a["readonly"] = "true";
                if (Has(attrs, "disabled")) a["disabled"] = "true";
                return new ElementMapping("odd:Html2UxmlFloatField", a, TextHandling.Drop);
            }
            if (t == "range")
            {
                var a = new Dictionary<string, string>();
                if (Has(attrs, "min"))  a["low-value"]  = attrs["min"];
                if (Has(attrs, "max"))  a["high-value"] = attrs["max"];
                if (Has(attrs, "step")) a["step"]       = attrs["step"];
                if (value.Length > 0) a["value"] = value;
                if (Has(attrs, "disabled")) a["disabled"] = "true";
                return new ElementMapping("ui:Slider", a, TextHandling.Drop);
            }
            if (t == "checkbox")
            {
                var a = new Dictionary<string, string>();
                if (Has(attrs, "checked"))  a["value"]    = "true";
                if (Has(attrs, "disabled")) a["disabled"] = "true";
                if (Has(attrs, "required")) a["required"] = "true";
                if (NonEmpty(attrs, "name")) a["html-name"] = attrs["name"];
                return new ElementMapping("ui:Toggle", a, TextHandling.Drop);
            }
            if (t == "radio")
            {
                var a = new Dictionary<string, string>();
                if (Has(attrs, "checked"))  a["value"]    = "true";
                if (Has(attrs, "disabled")) a["disabled"] = "true";
                if (Has(attrs, "required")) a["required"] = "true";
                if (NonEmpty(attrs, "name")) a["html-name"] = attrs["name"];
                return new ElementMapping("ui:RadioButton", a, TextHandling.Drop);
            }
            if (t == "button" || t == "submit" || t == "reset")
            {
                var a = new Dictionary<string, string> { { "button-type", t } };
                if (Has(attrs, "disabled")) a["disabled"] = "true";
                if (NonEmpty(attrs, "name")) a["form-name"] = attrs["name"];
                if (value.Length > 0)
                {
                    a["form-value"] = value;
                    a["text"] = value;
                }
                return new ElementMapping("odd:Html2UxmlButton", a, TextHandling.Drop);
            }
            if (t == "file")
            {
                var a = new Dictionary<string, string>
                {
                    { "button-type", "file" },
                    { "text", Get(attrs, "data-h2u-file-label") ?? "Choose File" },
                };
                if (Has(attrs, "disabled")) a["disabled"] = "true";
                if (NonEmpty(attrs, "name"))   a["form-name"] = attrs["name"];
                if (NonEmpty(attrs, "accept")) a["accept"]    = attrs["accept"];
                if (Has(attrs, "multiple"))    a["multiple"]  = "true";
                return new ElementMapping("odd:Html2UxmlButton", a, TextHandling.Drop);
            }

            // Fallback for unknown / un-handled input types: bare TextField.
            return new ElementMapping("ui:TextField", new Dictionary<string, string>(), TextHandling.Drop);
        }

        static bool IsTextInputType(string t)
        {
            switch (t)
            {
                case "text": case "email": case "search": case "url": case "tel":
                case "date": case "datetime-local": case "month": case "time":
                case "week": case "color":
                    return true;
                default: return false;
            }
        }
    }
}
