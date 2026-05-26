// source: html2uxml/converter.py:_text_raw_from_style,
//                                  _merge_inherited_text_raw,
//                                  _inherited_text_label_decls,
//                                  _text_label_decls_for_props
//
// Per-node inherited typography snapshot. Python emits an extra USS rule
// onto every Label that materialises the resolved text cascade (font /
// color / line-box props the node inherited but never set itself). This
// is what gives slug-named rules like `.h2u-volume`, `.h2u-premium-plan`
// and `.h2u-sync-progress` their `-unity-font-definition` + `color` snapshot
// even when the author CSS only set per-rule font-size.
//
// The C# port previously skipped this entirely. EmitNode now consults
// `InheritedText.Builder.EffectiveTextRaw(node)` and asks
// `InheritedTextLabelDecls()` for the props that need to be re-emitted.

using System.Collections.Generic;
using ODDGames.Html2Uxml.Editor.Converter.Css;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    public static class InheritedText
    {
        // Mirror of converter._TEXT_INHERIT_RAW_PROPS — every property that
        // a child Label inherits across the cascade. Used to filter both
        // the per-node "own" snapshot and the parent-derived "effective"
        // snapshot.
        public static readonly HashSet<string> InheritRawProps = new HashSet<string>
        {
            "font-family", "font-weight", "font-style", "font-size",
            "letter-spacing", "word-spacing", "color",
            "text-shadow", "text-align", "text-transform", "text-decoration",
            "white-space", "text-overflow",
            "-webkit-text-stroke", "-webkit-text-stroke-width", "-webkit-text-stroke-color",
            "text-stroke",
            "-unity-font-definition", "-unity-font-style", "-unity-text-align",
            "-unity-text-outline-width", "-unity-text-outline-color",
            "--odd-font-family", "--odd-font-weight", "--odd-font-atlas-mode",
        };

        // Mirror of converter._TEXT_LABEL_MAPPED_RAW_PROPS — the subset that
        // actually maps cleanly to a USS decl. (text-transform / text-decoration
        // are handled by mutating the text payload at emit time, not as USS.)
        public static readonly string[] LabelMappedRawProps = new string[]
        {
            "font-family", "font-weight", "font-style", "font-size",
            "letter-spacing", "word-spacing", "color",
            "text-shadow", "text-align",
            "white-space", "text-overflow",
            "-webkit-text-stroke", "-webkit-text-stroke-width", "-webkit-text-stroke-color",
            "text-stroke",
            "-unity-font-definition", "-unity-font-style", "-unity-text-align",
            "-unity-text-outline-width", "-unity-text-outline-color",
            "--odd-font-family", "--odd-font-weight",
        };

        // Mirror of converter._FONT_TEXT_RAW_PROPS — the font-related subset.
        // When ANY of these is missing from `own`, the snapshot must include
        // ALL font props that exist in `effective`, so the variant picker
        // (FontInjector) sees a coherent family/weight/size triple per rule.
        public static readonly string[] FontTextRawProps = new string[]
        {
            "font-family", "font-weight", "font-style", "font-size",
            "--odd-font-family", "--odd-font-weight",
            "-unity-font-definition", "-unity-font-style",
        };

        // Mirror of converter._text_raw_from_style. Reads only the inherit-able
        // text props out of a per-node resolved decl list (which contains the
        // node's own matched-rule decls plus inline style).
        public static Dictionary<string, string> TextRawFromDecls(
            List<KeyValuePair<string, string>> decls)
        {
            var outDict = new Dictionary<string, string>(System.StringComparer.Ordinal);
            if (decls == null) return outDict;
            foreach (var kv in decls)
            {
                string p = kv.Key;
                if (!InheritRawProps.Contains(p)) continue;
                outDict[p] = kv.Value; // last write wins, matches resolver insertion order
            }
            return outDict;
        }

        // Mirror of converter._merge_inherited_text_raw. inherit/unset/etc.
        // skip the propagation; `initial` deletes the prop from the effective
        // map so the cascade falls back to the UA default.
        public static Dictionary<string, string> MergeInheritedTextRaw(
            Dictionary<string, string> inherited,
            Dictionary<string, string> own)
        {
            var effective = inherited == null
                ? new Dictionary<string, string>(System.StringComparer.Ordinal)
                : new Dictionary<string, string>(inherited, System.StringComparer.Ordinal);
            if (own == null) return effective;
            foreach (var kv in own)
            {
                string low = (kv.Value ?? "").Trim().ToLowerInvariant();
                if (low == "inherit" || low == "unset" || low == "revert" || low == "revert-layer")
                    continue;
                if (low == "initial")
                {
                    effective.Remove(kv.Key);
                    continue;
                }
                effective[kv.Key] = kv.Value;
            }
            return effective;
        }

        // Mirror of converter._inherited_text_label_decls. Returns the USS-ready
        // (post-StyleMapper) declarations that materialise the node's inherited
        // typography snapshot. Returns empty when the cascade has nothing
        // missing-vs-own to backfill.
        public static List<KeyValuePair<string, string>> InheritedTextLabelDecls(
            Dictionary<string, string> effectiveTextRaw,
            Dictionary<string, string> ownTextRaw,
            List<string> warningsOut = null)
        {
            var empty = new List<KeyValuePair<string, string>>();
            if (effectiveTextRaw == null || effectiveTextRaw.Count == 0) return empty;

            var ownExplicit = new HashSet<string>(System.StringComparer.Ordinal);
            if (ownTextRaw != null)
            {
                foreach (var kv in ownTextRaw)
                {
                    string low = (kv.Value ?? "").Trim().ToLowerInvariant();
                    if (low == "inherit" || low == "unset" || low == "revert" || low == "revert-layer")
                        continue;
                    ownExplicit.Add(kv.Key);
                }
            }

            // `missing` = keys in LabelMappedRawProps that are present in
            // `effective` but absent from `own_explicit` (i.e. the value was
            // inherited rather than set explicitly).
            var missing = new List<string>();
            var fontProps = new HashSet<string>(FontTextRawProps);
            bool anyFontMissing = false;
            foreach (var prop in LabelMappedRawProps)
            {
                if (!effectiveTextRaw.ContainsKey(prop)) continue;
                if (ownExplicit.Contains(prop)) continue;
                missing.Add(prop);
                if (fontProps.Contains(prop)) anyFontMissing = true;
            }
            if (missing.Count == 0) return empty;

            // Order: every font prop that exists in effective FIRST (so the
            // family / weight / size triple stays adjacent for FontInjector),
            // then the remaining missing props in declaration order.
            var props = new List<string>();
            if (anyFontMissing)
            {
                foreach (var prop in FontTextRawProps)
                    if (effectiveTextRaw.ContainsKey(prop))
                        props.Add(prop);
            }
            foreach (var prop in missing)
                if (!fontProps.Contains(prop))
                    props.Add(prop);

            return TextLabelDeclsForProps(effectiveTextRaw, props, warningsOut);
        }

        // Mirror of converter._text_label_decls_for_props — pull values from
        // the snapshot in `props` order, run through StyleMapper, and drop the
        // synthetic `__attr__` markers (those are UXML attribute hints, not
        // USS decls).
        public static List<KeyValuePair<string, string>> TextLabelDeclsForProps(
            Dictionary<string, string> textRaw,
            IList<string> props,
            List<string> warningsOut = null)
        {
            var pairs = new List<KeyValuePair<string, string>>();
            foreach (var prop in props)
            {
                if (textRaw.TryGetValue(prop, out var value))
                    pairs.Add(new KeyValuePair<string, string>(prop, value));
            }
            if (pairs.Count == 0) return new List<KeyValuePair<string, string>>();

            var mapped = StyleMapper.MapDeclarations(pairs);
            if (warningsOut != null && mapped.Warnings != null && mapped.Warnings.Count > 0)
                warningsOut.AddRange(mapped.Warnings);

            // Mirror of _split_synthetic_decls — drop any `__attr__` markers
            // (e.g. `__bold__`, `__italic__`, `__picking-mode__`) that
            // StyleMapper inserts for UXML attribute hoisting; they're not
            // valid USS properties.
            var realDecls = new List<KeyValuePair<string, string>>(mapped.Decls.Count);
            foreach (var kv in mapped.Decls)
            {
                if (kv.Key.StartsWith("__") && kv.Key.EndsWith("__")) continue;
                realDecls.Add(kv);
            }
            return realDecls;
        }

        // ------------------------------------------------------------------
        // Per-tree pre-walk: build a node -> effective-text-raw map.
        //
        // Mirrors how _emit_node threads `inherited_text_raw` down the recursion
        // (each child's `effective` is merged from parent's `effective` + own).
        // We do it as a one-shot pre-walk so EmitNode can look up the answer
        // without coordinating with the parent's recursion frame.
        // ------------------------------------------------------------------
        public sealed class Builder
        {
            readonly Dictionary<HtmlLoader.HtmlNode, Dictionary<string, string>> _effective
                = new Dictionary<HtmlLoader.HtmlNode, Dictionary<string, string>>(
                    ReferenceEqualityComparer<HtmlLoader.HtmlNode>.Instance);
            readonly Dictionary<HtmlLoader.HtmlNode, Dictionary<string, string>> _own
                = new Dictionary<HtmlLoader.HtmlNode, Dictionary<string, string>>(
                    ReferenceEqualityComparer<HtmlLoader.HtmlNode>.Instance);

            public Dictionary<string, string> EffectiveTextRaw(HtmlLoader.HtmlNode node)
                => node != null && _effective.TryGetValue(node, out var v) ? v : null;
            public Dictionary<string, string> OwnTextRaw(HtmlLoader.HtmlNode node)
                => node != null && _own.TryGetValue(node, out var v) ? v : null;

            public static Builder Build(
                HtmlLoader.HtmlNode root,
                Dictionary<HtmlLoader.HtmlNode, List<KeyValuePair<string, string>>> resolved)
            {
                var b = new Builder();
                if (root == null) return b;
                b.Walk(root, null);
                _ = resolved; // resolved is consumed via b.Walk -> own-from-decls
                return b;
            }

            // Walk children, accumulating effective_text_raw down the tree.
            // Each call computes own-from-resolved then merges with parent's
            // effective and recurses.
            public void WalkTree(HtmlLoader.HtmlNode root,
                                 Dictionary<HtmlLoader.HtmlNode, List<KeyValuePair<string, string>>> resolved)
            {
                _resolved = resolved;
                Walk(root, null);
            }

            Dictionary<HtmlLoader.HtmlNode, List<KeyValuePair<string, string>>> _resolved;

            void Walk(HtmlLoader.HtmlNode node, Dictionary<string, string> parentEffective)
            {
                if (node == null) return;
                Dictionary<string, string> own = null;
                Dictionary<string, string> effective = parentEffective;
                if (!node.IsText && node.Tag != "__root__")
                {
                    List<KeyValuePair<string, string>> decls = null;
                    if (_resolved != null) _resolved.TryGetValue(node, out decls);
                    own = TextRawFromDecls(decls);
                    effective = MergeInheritedTextRaw(parentEffective, own);
                    _own[node] = own;
                    _effective[node] = effective;
                }
                if (node.Children == null) return;
                foreach (var c in node.Children)
                {
                    if (c.IsText) continue;
                    Walk(c, effective);
                }
            }
        }
    }
}
