using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // ::before / ::after materialisation. Mirrors converter.py:
    //   _emit_synthetic_pseudo, _split_synthetic_decls,
    //   _pseudo_has_visible_output (subset)
    //
    // Walks every CSS rule whose selector ends with `::before` or `::after`,
    // matches the host selector against each node, and queues a synthetic
    // Label child for the host. Author rules dropped by
    // CssSelector.HasUnsupportedFeatures (because of the `::`) are recovered
    // here.
    public static class PseudoElements
    {
        public enum Kind { Before, After }

        public sealed class PseudoEntry
        {
            public Kind Kind;
            public string Content;
            public List<KeyValuePair<string, string>> Decls;
        }

        // Build a per-node map of pseudo-element entries.
        internal static Dictionary<HtmlLoader.HtmlNode, List<PseudoEntry>> Collect(
            HtmlLoader.HtmlNode root,
            List<CssLoader.CssRule> rules)
        {
            var byNode = new Dictionary<HtmlLoader.HtmlNode, List<PseudoEntry>>(
                ReferenceEqualityComparer<HtmlLoader.HtmlNode>.Instance);
            if (root == null || rules == null) return byNode;

            // Build a flat node list with sibling indices once.
            var allNodes = new List<(HtmlLoader.HtmlNode node, List<HtmlLoader.HtmlNode> ancestors, int siblingIndex, List<HtmlLoader.HtmlNode> siblings)>();
            CollectNodesRecursive(root, new List<HtmlLoader.HtmlNode>(), allNodes);

            foreach (var rule in rules)
            {
                var hosted = StripPseudoElement(rule.ParsedSelector, out Kind? kind);
                if (kind == null || hosted == null) continue;
                string content = ExtractContentString(rule.Decls);
                if (content == null) continue; // skip rules with no `content:`

                foreach (var (node, ancestors, siblingIndex, siblings) in allNodes)
                {
                    if (!ResolverMatching.SelectorMatches(node, hosted, ancestors, siblingIndex, siblings))
                        continue;
                    if (!byNode.TryGetValue(node, out var list))
                    {
                        list = new List<PseudoEntry>();
                        byNode[node] = list;
                    }
                    list.Add(new PseudoEntry
                    {
                        Kind = kind.Value,
                        Content = content,
                        Decls = rule.Decls,
                    });
                }
            }
            return byNode;
        }

        // Detect a trailing `::before`/`::after` pseudo in the selector's last
        // compound. Returns a clone of the selector with the pseudo removed
        // and `kind` set to the pseudo type. Returns null if the selector has
        // no pseudo-element or has unsupported features after stripping.
        static CssSelector StripPseudoElement(CssSelector sel, out Kind? kind)
        {
            kind = null;
            if (sel == null || sel.Chain == null || sel.Chain.Count == 0) return null;
            var (combinator, lastCompound) = sel.Chain[sel.Chain.Count - 1];
            if (lastCompound?.Pseudo == null) return null;

            string matched = null;
            foreach (var ps in lastCompound.Pseudo)
            {
                string lo = ps.ToLowerInvariant();
                if (lo == "::before" || lo == ":before") { matched = ps; kind = Kind.Before; break; }
                if (lo == "::after"  || lo == ":after")  { matched = ps; kind = Kind.After;  break; }
            }
            if (matched == null) return null;

            var newPseudo = new List<string>(lastCompound.Pseudo);
            newPseudo.Remove(matched);
            // Reject if remaining pseudos include something USS doesn't support.
            foreach (var ps in newPseudo)
            {
                string kw = ps.TrimStart(':');
                int p = kw.IndexOf('(');
                if (p >= 0) kw = kw.Substring(0, p);
                if (!CssParser.UssPseudoKeywords.Contains(kw)) return null;
            }

            var newCompound = new CssCompoundSelector
            {
                Tag = lastCompound.Tag,
                Id = lastCompound.Id,
                Classes = new List<string>(lastCompound.Classes),
                Pseudo = newPseudo,
                Attrs = new List<(string, string, string)>(lastCompound.Attrs),
            };
            var newChain = new List<(string, CssCompoundSelector)>(sel.Chain.Count);
            for (int i = 0; i < sel.Chain.Count - 1; i++) newChain.Add(sel.Chain[i]);
            newChain.Add((combinator, newCompound));
            var clone = new CssSelector
            {
                Raw = sel.Raw,
                Chain = newChain,
            };
            // Reject combinator/attribute features the matcher can't handle.
            if (clone.HasUnsupportedFeatures()) return null;
            return clone;
        }

        // Pull `content: "…"` from the rule. Returns null when missing.
        static string ExtractContentString(List<KeyValuePair<string, string>> decls)
        {
            string raw = null;
            for (int i = decls.Count - 1; i >= 0; i--)
                if (decls[i].Key == "content") { raw = decls[i].Value; break; }
            if (raw == null) return null;
            string trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed.ToLowerInvariant() == "none" || trimmed.ToLowerInvariant() == "normal")
                return null;
            // Strip surrounding "..." or '...' pair.
            if (trimmed.Length >= 2)
            {
                char first = trimmed[0], last = trimmed[trimmed.Length - 1];
                if ((first == '"' || first == '\'') && first == last)
                {
                    return UnescapeCssString(trimmed.Substring(1, trimmed.Length - 2));
                }
            }
            // attr() / counter() / open-quote etc — leave verbatim, browsers handle these.
            return trimmed;
        }

        // CSS string-escape sequences: \\ \" \xx (hex). Most fixtures use plain
        // text so a minimal unescape is enough.
        static string UnescapeCssString(string s)
        {
            return Regex.Replace(s, @"\\([0-9a-fA-F]{1,6})\s?", m =>
            {
                int code = System.Convert.ToInt32(m.Groups[1].Value, 16);
                return char.ConvertFromUtf32(code);
            }).Replace("\\\\", "\\").Replace("\\\"", "\"").Replace("\\'", "'");
        }

        static void CollectNodesRecursive(
            HtmlLoader.HtmlNode parent,
            List<HtmlLoader.HtmlNode> ancestors,
            List<(HtmlLoader.HtmlNode, List<HtmlLoader.HtmlNode>, int, List<HtmlLoader.HtmlNode>)> outList)
        {
            if (parent?.Children == null) return;
            var siblings = ResolverMatching.NonTextChildren(parent);
            for (int i = 0; i < siblings.Count; i++)
            {
                var node = siblings[i];
                outList.Add((node, new List<HtmlLoader.HtmlNode>(ancestors), i, siblings));
                ancestors.Add(parent);
                CollectNodesRecursive(node, ancestors, outList);
                ancestors.RemoveAt(ancestors.Count - 1);
            }
        }
    }
}
