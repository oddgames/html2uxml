// source: html2uxml/resolver.py (selector matching half)
//
// Selector → DOM matching primitives. Faithful port of resolver._matches_*.
// Kept separate from the cascade loop so the cascade file stays readable.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    internal static class ResolverMatching
    {
        // Same set as _DYNAMIC_PSEUDOS in resolver.py.
        public static readonly HashSet<string> DynamicPseudos = new HashSet<string>
        {
            "hover", "active", "focus", "disabled", "enabled", "checked", "root",
            "selected", "inactive",
        };

        public static bool SelectorMatches(
            HtmlLoader.HtmlNode node,
            CssSelector selector,
            List<HtmlLoader.HtmlNode> ancestors,
            int siblingIndex,
            List<HtmlLoader.HtmlNode> siblings)
            => MatchesChain(node, selector.Chain, ancestors, siblingIndex, siblings);

        // Mirrors _matches_chain. `chain` is consumed right-to-left.
        public static bool MatchesChain(
            HtmlLoader.HtmlNode node,
            List<(string Combinator, CssCompoundSelector Compound)> chain,
            List<HtmlLoader.HtmlNode> parentChain,
            int siblingIndex,
            List<HtmlLoader.HtmlNode> siblings)
        {
            if (chain == null || chain.Count == 0) return false;
            var (combinator, compound) = chain[chain.Count - 1];
            if (!MatchesCompound(node, compound, parentChain, siblingIndex, siblings))
                return false;
            if (chain.Count == 1) return true;

            var rest = chain.GetRange(0, chain.Count - 1);

            if (combinator == ">")
            {
                if (parentChain.Count == 0) return false;
                var parent = parentChain[parentChain.Count - 1];
                var gs = NonTextChildren(parent);
                int pidx = gs.IndexOf(parent); // mirrors Python: try { gs.index(parent) } except ValueError -> -1
                return MatchesChain(parent, rest, Slice(parentChain, 0, parentChain.Count - 1), pidx, gs);
            }

            if (combinator == "+")
            {
                if (siblingIndex <= 0) return false;
                var prev = siblings[siblingIndex - 1];
                return MatchesChain(prev, rest, parentChain, siblingIndex - 1, siblings);
            }

            if (combinator == "~")
            {
                for (int i = siblingIndex - 1; i >= 0; i--)
                {
                    if (MatchesChain(siblings[i], rest, parentChain, i, siblings))
                        return true;
                }
                return false;
            }

            // Default: descendant. Try every ancestor.
            for (int i = parentChain.Count - 1; i >= 0; i--)
            {
                var anc = parentChain[i];
                List<HtmlLoader.HtmlNode> sibs;
                int idx;
                if (i > 0)
                {
                    var grand = parentChain[i - 1];
                    sibs = NonTextChildren(grand);
                    idx = sibs.IndexOf(anc);
                }
                else
                {
                    sibs = new List<HtmlLoader.HtmlNode> { anc };
                    idx = 0;
                }
                if (MatchesChain(anc, rest, Slice(parentChain, 0, i), idx, sibs))
                    return true;
            }
            return false;
        }

        public static bool MatchesCompound(
            HtmlLoader.HtmlNode node,
            CssCompoundSelector comp,
            List<HtmlLoader.HtmlNode> ancestors,
            int siblingIndex,
            List<HtmlLoader.HtmlNode> siblings)
        {
            if (comp.Tag != "*" && node.Tag != comp.Tag) return false;
            if (comp.Id != null)
            {
                var actualId = AttrUtil.Get(node.Attrs, "id");
                if (actualId != comp.Id) return false;
            }
            if (comp.Classes.Count > 0)
            {
                var nodeClasses = new HashSet<string>(node.Classes());
                foreach (var c in comp.Classes)
                    if (!nodeClasses.Contains(c)) return false;
            }
            foreach (var (op, name, value) in comp.Attrs)
            {
                var actual = AttrUtil.Get(node.Attrs, name);
                if (op == "")
                {
                    if (actual == null) return false;
                }
                else if (op == "=")
                {
                    if (actual != value) return false;
                }
                else if (op == "~=")
                {
                    if (actual == null) return false;
                    var tokens = actual.Split(new[] { ' ', '\t', '\n', '\r', '\f' },
                                              StringSplitOptions.RemoveEmptyEntries);
                    bool found = false;
                    foreach (var t in tokens) if (t == value) { found = true; break; }
                    if (!found) return false;
                }
                else if (op == "|=")
                {
                    if (actual == null) return false;
                    if (actual != value && !actual.StartsWith(value + "-", StringComparison.Ordinal))
                        return false;
                }
                else if (op == "^=")
                {
                    if (actual == null || !actual.StartsWith(value, StringComparison.Ordinal))
                        return false;
                }
                else if (op == "$=")
                {
                    if (actual == null || !actual.EndsWith(value, StringComparison.Ordinal))
                        return false;
                }
                else if (op == "*=")
                {
                    if (actual == null || actual.IndexOf(value, StringComparison.Ordinal) < 0)
                        return false;
                }
            }
            foreach (var pseudo in comp.Pseudo)
            {
                if (!MatchesPseudo(node, pseudo, ancestors, siblingIndex, siblings))
                    return false;
            }
            return true;
        }

        private static bool MatchesPseudo(
            HtmlLoader.HtmlNode node,
            string pseudo,
            List<HtmlLoader.HtmlNode> ancestors,
            int siblingIndex,
            List<HtmlLoader.HtmlNode> siblings)
        {
            string low = pseudo.ToLowerInvariant();
            if (low == "::before" || low == "::after") return true;
            var (name, arg) = PseudoNameAndArg(low);
            if (DynamicPseudos.Contains(name))
            {
                // Runtime-state pseudos pass through (the rule fires for the
                // base element; USS keeps the pseudo verbatim).
                return true;
            }
            int index = siblingIndex + 1;
            switch (name)
            {
                case "first-child":
                    return siblingIndex == 0;
                case "last-child":
                    return siblings.Count > 0 && siblingIndex == siblings.Count - 1;
                case "only-child":
                    return siblings.Count == 1;
                case "nth-child":
                    return arg != null && MatchesNthChild(index, arg);
                case "nth-last-child":
                    return arg != null && MatchesNthChild(siblings.Count - siblingIndex, arg);
                case "not":
                    if (arg == null) return false;
                    foreach (var raw in CssParser.SplitSelectorList(arg))
                    {
                        var sel = CssParser.ParseSelector(raw);
                        if (sel != null && SelectorMatches(node, sel, ancestors, siblingIndex, siblings))
                            return false;
                    }
                    return true;
            }
            return false;
        }

        private static (string Name, string Arg) PseudoNameAndArg(string pseudo)
        {
            string stripped = pseudo.TrimStart(':');
            int paren = stripped.IndexOf('(');
            if (paren < 0) return (stripped, null);
            string name = stripped.Substring(0, paren);
            string rest = stripped.Substring(paren + 1);
            string arg = rest.EndsWith(")", StringComparison.Ordinal)
                ? rest.Substring(0, rest.Length - 1).Trim()
                : rest.Trim();
            return (name, arg);
        }

        private static readonly Regex NthRegex =
            new Regex(@"^([+-]?\d*)n([+-]\d+)?$", RegexOptions.Compiled);

        private static bool MatchesNthChild(int index, string formula)
        {
            string f = formula.Replace(" ", string.Empty).ToLowerInvariant();
            if (f == "odd") return index % 2 == 1;
            if (f == "even") return index % 2 == 0;
            if (int.TryParse(f, out int literal)) return index == literal;
            var m = NthRegex.Match(f);
            if (!m.Success) return false;
            string aRaw = m.Groups[1].Value;
            string bRaw = m.Groups[2].Value;
            int a;
            if (aRaw == "" || aRaw == "+") a = 1;
            else if (aRaw == "-") a = -1;
            else if (!int.TryParse(aRaw, out a)) return false;
            int b;
            if (string.IsNullOrEmpty(bRaw)) b = 0;
            else if (!int.TryParse(bRaw, out b)) return false;
            if (a == 0) return index == b;
            int delta = index - b;
            // Python: delta % a == 0 and delta // a >= 0  (floor div).
            // C# `/` and `%` on ints round toward zero, so we recompute floor
            // semantics manually.
            int rem = delta % a;
            // Python modulo result has same sign as divisor; emulate:
            if ((rem != 0) && ((rem < 0) != (a < 0))) rem += a;
            if (rem != 0) return false;
            int floorDiv = (delta - rem) / a;
            return floorDiv >= 0;
        }

        // Mirrors Python `[c for c in parent.children if not c.is_text]`.
        public static List<HtmlLoader.HtmlNode> NonTextChildren(HtmlLoader.HtmlNode parent)
        {
            var result = new List<HtmlLoader.HtmlNode>(parent.Children.Count);
            foreach (var c in parent.Children)
                if (!c.IsText) result.Add(c);
            return result;
        }

        private static List<T> Slice<T>(List<T> source, int start, int count)
            => source.GetRange(start, count);
    }
}
