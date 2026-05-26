// source: html2uxml/resolver.py (cascade + tree walk half)
//
// CSS cascade: walks the HTML tree, matches every CssLoader.CssRule against
// every element, sorts the matches by specificity + source order, applies
// declarations with !important precedence, and finally lets inline
// `style="..."` overrides in (which beat normal stylesheet rules but lose
// to !important rules — see resolver._resolve_for in Python).
//
// Selector matching primitives live in ResolverMatching.cs.

using System;
using System.Collections.Generic;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    public static class Resolver
    {
        // Public entry point.
        //
        // Returns a per-node ordered (prop, value) list that has already been
        // collapsed by the cascade — duplicates removed, !important honored,
        // inline style="" merged in. Callers are expected to feed this
        // straight into the USS emitter.
        //
        // The map is keyed by HtmlNode reference (object identity), matching
        // Python's `id(node)` keying.
        public static Dictionary<HtmlLoader.HtmlNode, List<KeyValuePair<string, string>>> Resolve(
            HtmlLoader.HtmlNode root,
            List<CssLoader.CssRule> rules)
        {
            var output = new Dictionary<HtmlLoader.HtmlNode, List<KeyValuePair<string, string>>>(
                ReferenceEqualityComparer<HtmlLoader.HtmlNode>.Instance);
            if (root == null) return output;
            Walk(root, new List<HtmlLoader.HtmlNode>(), rules, output);
            return output;
        }

        private static void Walk(
            HtmlLoader.HtmlNode node,
            List<HtmlLoader.HtmlNode> ancestors,
            List<CssLoader.CssRule> rules,
            Dictionary<HtmlLoader.HtmlNode, List<KeyValuePair<string, string>>> output)
        {
            if (!node.IsText && node.Tag != "__root__")
            {
                List<HtmlLoader.HtmlNode> sibs;
                int sibIdx;
                if (ancestors.Count > 0)
                {
                    var parent = ancestors[ancestors.Count - 1];
                    sibs = ResolverMatching.NonTextChildren(parent);
                    sibIdx = sibs.IndexOf(node);
                }
                else
                {
                    sibs = new List<HtmlLoader.HtmlNode> { node };
                    sibIdx = 0;
                }
                output[node] = ResolveFor(node, ancestors, rules, sibIdx, sibs);
            }

            // Append-then-recurse-then-pop avoids per-step list allocations
            // while preserving the same `ancestors + [node]` semantics.
            ancestors.Add(node);
            foreach (var child in node.Children)
            {
                if (child.IsText) continue;
                Walk(child, ancestors, rules, output);
            }
            ancestors.RemoveAt(ancestors.Count - 1);
        }

        // Per-element cascade.
        private static List<KeyValuePair<string, string>> ResolveFor(
            HtmlLoader.HtmlNode node,
            List<HtmlLoader.HtmlNode> ancestors,
            List<CssLoader.CssRule> rules,
            int sibIdx,
            List<HtmlLoader.HtmlNode> siblings)
        {
            // (specificity, order, decls) — sorted ascending so later wins.
            var matches = new List<(int Specificity, int Order, List<CssDeclaration> Decls)>();

            foreach (var rule in rules)
            {
                var sel = rule.ParsedSelector;
                if (sel == null) continue;
                if (!ResolverMatching.SelectorMatches(node, sel, ancestors, sibIdx, siblings))
                    continue;

                // ::before / ::after rules are intentionally skipped here:
                // the Python resolver materialises them as synthetic sibling
                // nodes with their own decl lists, but the spec's public C#
                // surface only exposes the per-element collapsed decl list.
                // Pseudo-element handling can layer back in later without
                // changing the cascade for the host element itself.
                if (PseudoElementKind(sel) != null) continue;

                // USS-unsupported features (attribute selectors, ::pseudo
                // elements, :nth-* on non-runtime pseudos, etc) are dropped
                // unless the rule also carries a runtime pseudo. This mirrors
                // _resolve_for's `unsupported_decls` branch — those decls
                // never make it into the base cascade.
                if (sel.HasUnsupportedFeatures())
                {
                    if (sel.HasRuntimePseudo()) continue; // runtime pseudo -> kept for USS emit, not for base cascade
                    continue;
                }

                // Rules whose chain includes a runtime pseudo (`:hover`,
                // `:focus`, ...) likewise don't contribute to the resting
                // base style — they only fire at runtime. The resolver in
                // Python keeps these on a `pseudo_rules` list; for the
                // collapsed-decl-list public surface we just exclude them.
                if (HasPseudo(sel)) continue;

                matches.Add((rule.Specificity, rule.OriginIndex, rule.RawDecls));
            }

            // Tuple compare: (specificity, order). Python's list.sort is
            // stable; OrderBy in C# is also stable.
            matches.Sort((a, b) =>
            {
                int c = a.Specificity.CompareTo(b.Specificity);
                return c != 0 ? c : a.Order.CompareTo(b.Order);
            });

            // bag: prop -> (value, important). Iteration order = insertion
            // order, which is what we emit at the end (Python uses dict[]
            // which preserves insertion order in 3.7+).
            var bag = new Dictionary<string, (string Value, bool Important)>(StringComparer.Ordinal);
            var insertionOrder = new List<string>();

            foreach (var (_, _, decls) in matches)
            {
                foreach (var d in decls)
                {
                    Apply(bag, insertionOrder, d.Prop, d.Value, d.Important);
                }
            }

            // Inline style="..." beats stylesheet rules (loses to !important).
            string inline = AttrUtil.Get(node.Attrs, "style");
            if (!string.IsNullOrEmpty(inline))
            {
                foreach (var d in CssParser.ParseDeclarations(inline))
                {
                    if (!bag.TryGetValue(d.Prop, out var cur))
                    {
                        Apply(bag, insertionOrder, d.Prop, d.Value, d.Important);
                    }
                    else if (!cur.Important || d.Important)
                    {
                        // Either the existing decl is non-important (inline
                        // wins) or the new inline decl is itself important
                        // (also wins).
                        bag[d.Prop] = (d.Value, d.Important);
                    }
                }
            }

            var output = new List<KeyValuePair<string, string>>(insertionOrder.Count);
            foreach (var prop in insertionOrder)
                output.Add(new KeyValuePair<string, string>(prop, bag[prop].Value));
            return output;
        }

        private static void Apply(
            Dictionary<string, (string Value, bool Important)> bag,
            List<string> insertionOrder,
            string prop, string value, bool important)
        {
            if (!bag.TryGetValue(prop, out var cur))
            {
                bag[prop] = (value, important);
                insertionOrder.Add(prop);
                return;
            }
            // Mirrors resolver._resolve_for's bag update predicate exactly:
            //   if cur is None or (d.important and not cur.important)
            //                   or (d.important == cur.important):
            //       bag[d.prop] = (d.value, d.important)
            // The third clause means "same importance => later wins".
            if ((important && !cur.Important) || important == cur.Important)
            {
                bag[prop] = (value, important);
            }
        }

        private static bool HasPseudo(CssSelector sel)
        {
            foreach (var (_, c) in sel.Chain)
                if (c.Pseudo.Count > 0) return true;
            return false;
        }

        private static string PseudoElementKind(CssSelector sel)
        {
            if (sel.Chain.Count == 0) return null;
            var (_, comp) = sel.Chain[sel.Chain.Count - 1];
            foreach (var ps in comp.Pseudo)
            {
                string low = ps.ToLowerInvariant();
                if (low == "::before") return "before";
                if (low == "::after") return "after";
            }
            return null;
        }
    }

    // Reference-equality comparer so the per-node dict keys mirror Python's
    // id(node) keying. Prevents two distinct nodes with structurally equal
    // contents from colliding (which would otherwise cascade-collapse the
    // wrong styles together).
    internal sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
    {
        public static readonly ReferenceEqualityComparer<T> Instance = new ReferenceEqualityComparer<T>();
        public bool Equals(T x, T y) => ReferenceEquals(x, y);
        public int GetHashCode(T obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
