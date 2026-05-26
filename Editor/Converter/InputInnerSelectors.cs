using System.Collections.Generic;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // Mirror padding/font decls onto Unity's nested editable-text + placeholder
    // children so the visible text inset matches the browser. Mirrors:
    //   _INPUT_INNER_TAGS, _INPUT_INNER_DECLS,
    //   _input_inner_text_selectors, _select_inner_text_selectors,
    //   _input_placeholder_selectors,
    //   _input_inner_rules_for_decls, _input_inner_padding_rules
    public static class InputInnerSelectors
    {
        public static readonly HashSet<string> InputInnerTags = new HashSet<string>
        {
            "input", "textarea", "select",
        };

        public static readonly HashSet<string> InputInnerDecls = new HashSet<string>
        {
            "padding","padding-top","padding-right","padding-bottom","padding-left",
            "font-size","letter-spacing",
            "-unity-text-align","-unity-font-style","-unity-font-definition",
            "color",
        };

        public static List<string> InputInnerTextSelectors(string ussSelector)
            => DedupePreservingOrder(new List<string>
            {
                ussSelector + " > .unity-base-text-field__input",
                ussSelector + " > .unity-base-text-field__input > .unity-text-element--inner-input-field-component",
                ussSelector + " > #unity-text-input",
                ussSelector + " #unity-text-input",
                ussSelector + " > .unity-text-input",
                ussSelector + " .unity-text-input",
            });

        public static List<string> SelectInnerTextSelectors(string ussSelector)
            => DedupePreservingOrder(new List<string>
            {
                ussSelector + " > .html2uxml-dropdown-label",
                ussSelector + " .html2uxml-dropdown-label",
                ussSelector + " > .html2uxml-dropdown-menu > .html2uxml-dropdown-option",
                ussSelector + " .html2uxml-dropdown-option",
                ussSelector + " > .unity-base-popup-field__input > .unity-base-popup-field__text",
                ussSelector + " .unity-base-popup-field__text",
                ussSelector + " > .unity-base-popup-field__input > .html2uxml-dropdown-arrow",
                ussSelector + " .html2uxml-dropdown-arrow",
            });

        public static List<string> InputPlaceholderSelectors(string ussSelector)
            => DedupePreservingOrder(new List<string>
            {
                ussSelector + " > .html2uxml-placeholder",
                ussSelector + " .html2uxml-placeholder",
                ussSelector + " > #html2uxml-placeholder",
                ussSelector + " #html2uxml-placeholder",
            });

        public static List<(string selector, List<KeyValuePair<string, string>> decls)>
            InputInnerRulesForDecls(string ussSelector,
                                    IList<KeyValuePair<string, string>> decls,
                                    string tag)
        {
            var inner = new List<KeyValuePair<string, string>>();
            foreach (var kv in decls) if (InputInnerDecls.Contains(kv.Key)) inner.Add(kv);
            var rules = new List<(string, List<KeyValuePair<string, string>>)>();
            if (inner.Count == 0) return rules;

            if (tag == "select")
            {
                var selectText = new List<KeyValuePair<string, string>>();
                foreach (var kv in inner) if (!kv.Key.StartsWith("padding")) selectText.Add(kv);
                if (selectText.Count == 0) return rules;
                foreach (var sel in SelectInnerTextSelectors(ussSelector))
                    rules.Add((sel, new List<KeyValuePair<string, string>>(selectText)));
                return rules;
            }

            var placeholder = new List<KeyValuePair<string, string>>();
            foreach (var kv in inner) if (kv.Key != "color") placeholder.Add(kv);
            foreach (var sel in InputInnerTextSelectors(ussSelector))
                rules.Add((sel, new List<KeyValuePair<string, string>>(inner)));
            if (placeholder.Count > 0)
                foreach (var sel in InputPlaceholderSelectors(ussSelector))
                    rules.Add((sel, new List<KeyValuePair<string, string>>(placeholder)));
            return rules;
        }

        // Inspect the selector's last compound and return inner-text rules
        // if it targets an input/textarea/select tag. Empty list otherwise.
        internal static List<(string selector, List<KeyValuePair<string, string>> decls)>
            InputInnerPaddingRules(CssSelector sel,
                                   string ussSelector,
                                   IList<KeyValuePair<string, string>> decls)
        {
            if (sel == null || sel.Chain == null || sel.Chain.Count == 0)
                return new List<(string, List<KeyValuePair<string, string>>)>();
            var last = sel.Chain[sel.Chain.Count - 1].Compound;
            string tag = (last?.Tag ?? "").ToLowerInvariant();
            if (!InputInnerTags.Contains(tag))
                return new List<(string, List<KeyValuePair<string, string>>)>();
            return InputInnerRulesForDecls(ussSelector, decls, tag);
        }

        static List<string> DedupePreservingOrder(List<string> values)
        {
            var seen = new HashSet<string>();
            var outList = new List<string>();
            foreach (var v in values)
            {
                if (seen.Contains(v)) continue;
                seen.Add(v);
                outList.Add(v);
            }
            return outList;
        }
    }
}
