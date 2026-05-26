// source: html2uxml/css_parser.py
//
// Tolerant CSS tokenizer + selector parser. Faithful port of the Python
// css_parser module — kept hand-written rather than delegated to ExCSS so the
// resolver's cascade behaviour (specificity, order, !important) matches the
// Python tests exactly. ExCSS is still referenced from the editor asmdef but
// silently drops some rule shapes (e.g. unusual pseudos) and reorders
// declarations in ways that would diverge from the Python output.
//
// Public types live in CssLoader.cs. This file holds the internal grammar.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // Single declaration parsed out of a CSS rule body.
    internal sealed class CssDeclaration
    {
        public string Prop;
        public string Value;
        public bool Important;

        public CssDeclaration(string prop, string value, bool important)
        {
            Prop = prop;
            Value = value;
            Important = important;
        }
    }

    // Pre-split rule (before selector parsing).
    internal sealed class RawCssRule
    {
        public List<string> Selectors;
        public List<CssDeclaration> Declarations;
        public int Order;
        // `/* ... */` comments that appeared in source between the previous
        // rule (or the start of the sheet) and this rule's selector. Carried
        // through so the USS emitter can mirror them above the generated rule.
        public List<string> LeadingComments;
    }

    internal sealed class CssCompoundSelector
    {
        public string Tag = "*";          // "*" => any tag
        public string Id;                  // null => no id
        public List<string> Classes = new List<string>();
        public List<string> Pseudo = new List<string>();           // pseudo-classes/elements verbatim
        public List<(string Op, string Name, string Value)> Attrs  // op in {"", "=", "~=", "^=", "$=", "*=", "|="}
            = new List<(string, string, string)>();
    }

    internal sealed class CssSelector
    {
        // Each entry: (combinator-from-left, compound). The first entry's
        // combinator is " " by convention (no real left-hand side).
        public List<(string Combinator, CssCompoundSelector Compound)> Chain
            = new List<(string, CssCompoundSelector)>();
        public string Raw;

        public (int Ids, int Classes, int Tags) Specificity()
        {
            int ids = 0, classes = 0, tags = 0;
            foreach (var (_, c) in Chain)
            {
                if (c.Id != null) ids++;
                classes += c.Classes.Count + c.Pseudo.Count + c.Attrs.Count;
                if (c.Tag != "*") tags++;
            }
            return (ids, classes, tags);
        }

        public bool HasUnsupportedFeatures()
        {
            foreach (var (combinator, c) in Chain)
            {
                if (combinator == "+" || combinator == "~") return true;
                if (c.Attrs.Count > 0) return true;
                foreach (var ps in c.Pseudo)
                {
                    if (ps.StartsWith("::", StringComparison.Ordinal)) return true;
                    var kw = ps.TrimStart(':');
                    int paren = kw.IndexOf('(');
                    if (paren >= 0) kw = kw.Substring(0, paren);
                    if (!CssParser.UssPseudoKeywords.Contains(kw)) return true;
                }
            }
            return false;
        }

        public bool HasRuntimePseudo()
        {
            foreach (var (_, c) in Chain)
            {
                foreach (var ps in c.Pseudo)
                {
                    var kw = ps.TrimStart(':');
                    int paren = kw.IndexOf('(');
                    if (paren >= 0) kw = kw.Substring(0, paren);
                    if (CssParser.UssPseudoKeywords.Contains(kw)) return true;
                }
            }
            return false;
        }
    }

    internal static class CssParser
    {
        // USS-supported runtime pseudos. Mirrors _USS_PSEUDO_KEYWORDS.
        public static readonly HashSet<string> UssPseudoKeywords = new HashSet<string>
        {
            "hover", "active", "focus", "disabled", "enabled", "checked", "root",
            "selected", "inactive",
        };

        private static readonly Regex CommentRegex =
            new Regex(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);

        // Strip /* ... */ comments. Mirrors css_parser._COMMENT_RE.sub.
        public static string StripComments(string source)
            => source == null ? string.Empty : CommentRegex.Replace(source, string.Empty);

        // Strip /* ... */ comments while recording each comment's body and the
        // offset (in the *stripped* string) at which it appeared. Used by
        // ParseCss to attach top-of-rule comments to RawCssRule.LeadingComments.
        internal static string StripCommentsAndCollect(string source, out List<(int Offset, string Body)> comments)
        {
            comments = new List<(int, string)>();
            if (string.IsNullOrEmpty(source)) return source ?? string.Empty;
            var sb = new StringBuilder(source.Length);
            int i = 0;
            int n = source.Length;
            while (i < n)
            {
                if (i + 1 < n && source[i] == '/' && source[i + 1] == '*')
                {
                    int end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end == -1) { i = n; break; }
                    int contentStart = i + 2;
                    string body = source.Substring(contentStart, end - contentStart);
                    comments.Add((sb.Length, body));
                    i = end + 2;
                    continue;
                }
                sb.Append(source[i++]);
            }
            return sb.ToString();
        }

        // Mirrors css_parser.parse_css. `startOrder` lets multi-sheet callers
        // (CssLoader) keep monotonically increasing source order across files.
        public static List<RawCssRule> ParseCss(string source, ref int orderCounter)
        {
            var rules = new List<RawCssRule>();
            if (string.IsNullOrEmpty(source)) return rules;

            source = StripCommentsAndCollect(source, out var comments);
            int commentCursor = 0;
            int i = 0;
            int n = source.Length;
            while (i < n)
            {
                while (i < n && char.IsWhiteSpace(source[i])) i++;
                if (i >= n) break;

                if (source[i] == '@')
                {
                    // @media bodies hold the desktop layout for most modern
                    // sites (Google's `@media (min-width:569px)` centers the
                    // logo + sizes the search pill). UI Toolkit can't evaluate
                    // the condition, but parsing the inner rules as if they
                    // were top-level matches the intent for a desktop-sized
                    // panel — the alternative is dropping ~half the cascade.
                    if (IsAtRuleWithUnwrappableBody(source, i, out int bodyStart, out int bodyEnd))
                    {
                        string atBody = source.Substring(bodyStart + 1, bodyEnd - bodyStart - 1);
                        var nested = ParseCss(atBody, ref orderCounter);
                        rules.AddRange(nested);
                        i = bodyEnd + 1;
                        continue;
                    }
                    i = SkipAtRule(source, i);
                    continue;
                }

                int brace = source.IndexOf('{', i);
                if (brace == -1) break;

                string selectorsText = source.Substring(i, brace - i);
                int end = FindBlockEnd(source, brace);
                if (end == -1) break;

                string body = source.Substring(brace + 1, end - brace - 1);
                var decls = ParseDeclarations(body);
                var sels = SplitSelectorList(selectorsText);

                // Pull every comment whose stripped-source offset falls before
                // this rule's selector — those are the comments that lived
                // immediately above the rule in the original sheet.
                List<string> leading = null;
                while (commentCursor < comments.Count && comments[commentCursor].Offset <= i)
                {
                    if (leading == null) leading = new List<string>();
                    leading.Add(comments[commentCursor].Body);
                    commentCursor++;
                }

                if (sels.Count > 0 && decls.Count > 0)
                {
                    rules.Add(new RawCssRule
                    {
                        Selectors = sels,
                        Declarations = decls,
                        LeadingComments = leading,
                        Order = orderCounter,
                    });
                    orderCounter++;
                }
                i = end + 1;
            }
            return rules;
        }

        // Identify `@media`, `@supports`, `@layer` at-rules whose body should
        // be unwrapped (the inner rules promoted to the top-level cascade).
        // Returns the `{` and matching `}` offsets when applicable.
        // `@keyframes`, `@font-face`, `@font-feature-values`, `@counter-style`
        // and at-rules without a body are left to SkipAtRule.
        private static bool IsAtRuleWithUnwrappableBody(string source, int i, out int braceStart, out int braceEnd)
        {
            braceStart = -1;
            braceEnd = -1;
            int n = source.Length;
            int j = i + 1;
            int wordStart = j;
            while (j < n && (char.IsLetterOrDigit(source[j]) || source[j] == '-')) j++;
            if (j == wordStart) return false;
            string word = source.Substring(wordStart, j - wordStart);
            if (!word.Equals("media", StringComparison.OrdinalIgnoreCase)
                && !word.Equals("supports", StringComparison.OrdinalIgnoreCase)
                && !word.Equals("layer", StringComparison.OrdinalIgnoreCase))
                return false;

            int semi = source.IndexOf(';', j);
            int brace = source.IndexOf('{', j);
            if (brace == -1 || (semi != -1 && semi < brace)) return false; // bodyless variant
            int end = FindBlockEnd(source, brace);
            if (end == -1) return false;
            braceStart = brace;
            braceEnd = end;
            return true;
        }

        // Skip `@import url(...);` (no body) or `@media ... { ... }` (with body).
        private static int SkipAtRule(string source, int i)
        {
            int n = source.Length;
            int semi = source.IndexOf(';', i);
            int brace = source.IndexOf('{', i);
            if (brace == -1 || (semi != -1 && semi < brace))
                return semi != -1 ? semi + 1 : n;
            int end = FindBlockEnd(source, brace);
            return end != -1 ? end + 1 : n;
        }

        // Given index of `{`, find matching `}` accounting for nesting.
        public static int FindBlockEnd(string source, int start)
        {
            int depth = 0;
            int n = source.Length;
            for (int i = start; i < n; i++)
            {
                char c = source[i];
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) return i;
                }
            }
            return -1;
        }

        private static readonly Regex ImportantTrailRegex =
            new Regex(@"!\s*important\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Mirrors css_parser._parse_declarations. Public because resolver also
        // calls it for inline `style="..."` attributes.
        public static List<CssDeclaration> ParseDeclarations(string body)
        {
            var output = new List<CssDeclaration>();
            foreach (var raw in SplitDeclarations(body))
            {
                int colon = raw.IndexOf(':');
                if (colon < 0) continue;
                string prop = raw.Substring(0, colon).Trim().ToLowerInvariant();
                string value = raw.Substring(colon + 1).Trim();
                if (prop.Length == 0 || value.Length == 0) continue;

                bool important = false;
                var m = ImportantTrailRegex.Match(value);
                if (m.Success)
                {
                    important = true;
                    value = value.Substring(0, m.Index).TrimEnd();
                }
                output.Add(new CssDeclaration(prop, value, important));
            }
            return output;
        }

        // Split on `;` respecting parens/quotes.
        private static List<string> SplitDeclarations(string body)
        {
            var parts = new List<string>();
            var buf = new StringBuilder();
            int depth = 0;
            char? quote = null;
            bool escaped = false;
            foreach (char c in body)
            {
                if (quote.HasValue)
                {
                    buf.Append(c);
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == quote.Value) quote = null;
                    continue;
                }
                if (c == '\'' || c == '"')
                {
                    quote = c;
                    buf.Append(c);
                }
                else if (c == '(') { depth++; buf.Append(c); }
                else if (c == ')') { if (depth > 0) depth--; buf.Append(c); }
                else if (c == ';' && depth == 0)
                {
                    var s = buf.ToString().Trim();
                    if (s.Length > 0) parts.Add(s);
                    buf.Clear();
                }
                else buf.Append(c);
            }
            var tail = buf.ToString().Trim();
            if (tail.Length > 0) parts.Add(tail);
            return parts;
        }

        // Split `a, b, :not(c, d)` -> ["a", "b", ":not(c, d)"]. Top-level
        // commas only — functional pseudos can legally contain commas.
        public static List<string> SplitSelectorList(string text)
        {
            var selectors = new List<string>();
            var buf = new StringBuilder();
            int paren = 0, bracket = 0;
            char? quote = null;
            bool escaped = false;
            foreach (char c in text)
            {
                if (quote.HasValue)
                {
                    buf.Append(c);
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == quote.Value) quote = null;
                    continue;
                }
                if (c == '\'' || c == '"') { quote = c; buf.Append(c); }
                else if (c == '(') { paren++; buf.Append(c); }
                else if (c == ')') { if (paren > 0) paren--; buf.Append(c); }
                else if (c == '[') { bracket++; buf.Append(c); }
                else if (c == ']') { if (bracket > 0) bracket--; buf.Append(c); }
                else if (c == ',' && paren == 0 && bracket == 0)
                {
                    var s = buf.ToString().Trim();
                    if (s.Length > 0) selectors.Add(s);
                    buf.Clear();
                }
                else buf.Append(c);
            }
            var tail = buf.ToString().Trim();
            if (tail.Length > 0) selectors.Add(tail);
            return selectors;
        }

        // ------------------------------------------------------------------
        // Selector parsing
        // ------------------------------------------------------------------

        public static CssSelector ParseSelector(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            raw = raw.Trim();
            if (raw.Length == 0) return null;

            var sel = new CssSelector { Raw = raw };
            int pos = 0;
            int n = raw.Length;
            string pendingCombinator = " ";
            bool first = true;

            while (pos < n)
            {
                string combinator = " ";
                bool hadWs = false;
                while (pos < n && char.IsWhiteSpace(raw[pos])) { hadWs = true; pos++; }

                if (pos < n && (raw[pos] == '+' || raw[pos] == '>' || raw[pos] == '~'))
                {
                    combinator = raw[pos].ToString();
                    pos++;
                    while (pos < n && char.IsWhiteSpace(raw[pos])) pos++;
                }
                else if (hadWs)
                {
                    combinator = " ";
                }

                if (pos >= n) break;

                int start = pos;
                while (pos < n && !char.IsWhiteSpace(raw[pos]) &&
                       raw[pos] != '+' && raw[pos] != '>' && raw[pos] != '~')
                {
                    if (raw[pos] == '(')
                    {
                        int depth = 1;
                        pos++;
                        while (pos < n && depth > 0)
                        {
                            if (raw[pos] == '(') depth++;
                            else if (raw[pos] == ')') depth--;
                            pos++;
                        }
                        continue;
                    }
                    pos++;
                }
                string token = raw.Substring(start, pos - start);
                if (token.Length == 0) break;
                var compound = ParseCompound(token);
                if (compound == null) return null;
                sel.Chain.Add((first ? pendingCombinator : combinator, compound));
                first = false;
                pendingCombinator = " ";
            }

            return sel.Chain.Count > 0 ? sel : null;
        }

        // Mirrors _COMPOUND_RE / _ATTR_RE.
        private static readonly Regex CompoundRegex = new Regex(
            @"^(?<tag>[a-zA-Z*][\w-]*)?(?<rest>(?:[#.][\w-]+|\[[^\]]*\]|::?[\w-]+(?:\([^)]*\))?)*)$",
            RegexOptions.Compiled);

        private static readonly Regex AttrRegex = new Regex(
            @"\[\s*(?<name>[\w-]+)\s*(?:(?<op>[~|^$*]?=)\s*(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)'|(?<bare>[^\]\s]+)))?\s*\]",
            RegexOptions.Compiled);

        private static CssCompoundSelector ParseCompound(string token)
        {
            var m = CompoundRegex.Match(token);
            if (!m.Success) return null;

            var compound = new CssCompoundSelector();
            string tag = m.Groups["tag"].Success ? m.Groups["tag"].Value : "";
            compound.Tag = tag.Length == 0 ? "*" : tag.ToLowerInvariant();
            string rest = m.Groups["rest"].Success ? m.Groups["rest"].Value : string.Empty;

            int i = 0;
            while (i < rest.Length)
            {
                char c = rest[i];
                if (c == '.')
                {
                    int j = i + 1;
                    while (j < rest.Length && (char.IsLetterOrDigit(rest[j]) || rest[j] == '_' || rest[j] == '-')) j++;
                    compound.Classes.Add(rest.Substring(i + 1, j - i - 1));
                    i = j;
                }
                else if (c == '#')
                {
                    int j = i + 1;
                    while (j < rest.Length && (char.IsLetterOrDigit(rest[j]) || rest[j] == '_' || rest[j] == '-')) j++;
                    compound.Id = rest.Substring(i + 1, j - i - 1);
                    i = j;
                }
                else if (c == '[')
                {
                    int j = rest.IndexOf(']', i);
                    if (j == -1) { i = rest.Length; continue; }
                    string seg = rest.Substring(i, j - i + 1);
                    var am = AttrRegex.Match(seg);
                    if (am.Success)
                    {
                        string name = am.Groups["name"].Value;
                        string op = am.Groups["op"].Success ? am.Groups["op"].Value : string.Empty;
                        string value;
                        if (am.Groups["dq"].Success) value = am.Groups["dq"].Value;
                        else if (am.Groups["sq"].Success) value = am.Groups["sq"].Value;
                        else if (am.Groups["bare"].Success) value = am.Groups["bare"].Value;
                        else value = string.Empty;
                        compound.Attrs.Add((op, name, value));
                    }
                    i = j + 1;
                }
                else if (c == ':')
                {
                    int j = i + 1;
                    if (j < rest.Length && rest[j] == ':') j++;
                    while (j < rest.Length && (char.IsLetterOrDigit(rest[j]) || rest[j] == '_' || rest[j] == '-')) j++;
                    if (j < rest.Length && rest[j] == '(')
                    {
                        int depth = 1;
                        j++;
                        while (j < rest.Length && depth > 0)
                        {
                            if (rest[j] == '(') depth++;
                            else if (rest[j] == ')') depth--;
                            j++;
                        }
                    }
                    compound.Pseudo.Add(rest.Substring(i, j - i));
                    i = j;
                }
                else
                {
                    i++;
                }
            }
            return compound;
        }
    }
}
