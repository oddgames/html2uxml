using System.Collections.Generic;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // Tiny helpers shared across the mapping layer. Mirrors the per-call
    // dict.get / "in" idioms in the Python source.
    internal static class AttrUtil
    {
        public static string Get(IDictionary<string, string> d, string key)
            => d != null && d.TryGetValue(key, out var v) ? v : null;

        public static bool Has(IDictionary<string, string> d, string key)
            => d != null && d.ContainsKey(key);

        public static bool NonEmpty(IDictionary<string, string> d, string key)
            => d != null && d.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v);

        // Mirrors mappings._add_text_validation: copies validation-related
        // HTML form attributes onto the UXML attribute dict.
        public static void AddTextValidation(IDictionary<string, string> attrs, IDictionary<string, string> target)
        {
            if (Has(attrs, "required")) target["required"] = "true";
            if (Has(attrs, "readonly")) target["readonly"] = "true";
            if (Has(attrs, "disabled")) target["disabled"] = "true";
            if (NonEmpty(attrs, "minlength")) target["minlength"] = attrs["minlength"];
            if (NonEmpty(attrs, "maxlength")) target["maxlength"] = attrs["maxlength"];
            if (NonEmpty(attrs, "pattern"))   target["pattern"]   = attrs["pattern"];
            if (NonEmpty(attrs, "name"))      target["html-name"] = attrs["name"];
        }
    }
}
