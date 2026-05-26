// source: html2uxml/css_parser.py + html2uxml/converter.py:_collect_css
//
// Public CSS loading surface for the converter. Collects CSS text from a
// parsed HTML document — inline <style> blocks plus <link rel="stylesheet">
// hrefs resolved against the source HTML's directory — and produces an
// ordered list of CssRule objects whose source order is preserved across
// sheets (so the resolver's "later wins on tie" cascade rule still works
// when multiple stylesheets are involved).
//
// Selector parsing/specificity lives in CssParser.cs. Keeping the loader
// thin and the parser separate matches the css_parser.py / resolver.py
// split on the Python side.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    public static class CssLoader
    {
        // Public rule shape — lightweight wrapper around the internal
        // RawCssRule + CssSelector. SelectorText is one selector (the rule
        // is exploded across the comma-separated selector list before being
        // returned). Specificity is the canonical (id, class, tag) tuple
        // packed into a single int with the same ordering as Python's tuple
        // comparison: ids * 1_000_000 + classes * 1_000 + tags.
        public sealed class CssRule
        {
            public string SelectorText;
            public List<KeyValuePair<string, string>> Decls;
            public int Specificity;
            public int OriginIndex;        // monotonically increasing source order
            // Source-CSS `/* ... */` blocks that sat immediately above the
            // rule. Shared across every CssRule produced from one
            // comma-exploded source rule.
            public List<string> LeadingComments;

            // Internal fields the resolver needs but external callers don't.
            internal CssSelector ParsedSelector;
            internal List<CssDeclaration> RawDecls;
        }

        // Walk the original source HTML to gather <link>/<style> CSS text,
        // then parse each chunk into rules sharing a single source-order
        // counter. The HtmlNode parameter is accepted to match the spec's
        // public surface but is unused — HtmlLoader has already stripped
        // <link>/<style> from the tree, so we re-parse the source file to
        // recover the side-channel data (matches Python which keeps a
        // ParsedHTML object around for exactly this reason).
        public static List<CssRule> LoadFromHtml(HtmlLoader.HtmlNode root, string sourceHtmlPath)
        {
            if (string.IsNullOrEmpty(sourceHtmlPath))
                return new List<CssRule>();

            HtmlLoader.ParsedHtml parsed;
            try
            {
                parsed = HtmlLoader.ParseFromFile(sourceHtmlPath);
            }
            catch (FileNotFoundException) { return new List<CssRule>(); }
            catch (DirectoryNotFoundException) { return new List<CssRule>(); }

            var sb = new StringBuilder();
            foreach (var inline in parsed.InlineStyles)
            {
                if (string.IsNullOrEmpty(inline)) continue;
                sb.Append(inline);
                sb.Append('\n');
            }

            string baseDir = Path.GetDirectoryName(Path.GetFullPath(sourceHtmlPath));
            foreach (var href in parsed.LinkedStylesheets)
            {
                if (string.IsNullOrEmpty(href)) continue;
                if (LooksLikeRemoteUrl(href)) continue; // mirrors converter.py: skipped silently
                string path;
                try
                {
                    path = Path.IsPathRooted(href) ? href : Path.Combine(baseDir ?? string.Empty, href);
                }
                catch (ArgumentException) { continue; }

                if (!File.Exists(path)) continue;
                try
                {
                    sb.Append(File.ReadAllText(path, Encoding.UTF8));
                    sb.Append('\n');
                }
                catch (IOException) { /* missing/locked sheet — skip silently */ }
            }

            int order = 0;
            return ParseAndExplode(sb.ToString(), ref order);
        }

        // Convenience for callers that already have a CSS string in hand
        // (tests, the runtime live-reload path, etc).
        public static List<CssRule> ParseString(string cssText, int originIndex = 0)
        {
            int order = originIndex;
            return ParseAndExplode(cssText, ref order);
        }

        // Concatenated CSS text for the source HTML — inline <style> blocks
        // followed by every linked stylesheet (in document order). Used by
        // the animation bridge which needs raw @keyframes blocks the parser
        // doesn't surface as rules.
        public static string LoadRawCssText(string sourceHtmlPath)
        {
            if (string.IsNullOrEmpty(sourceHtmlPath)) return string.Empty;
            HtmlLoader.ParsedHtml parsed;
            try { parsed = HtmlLoader.ParseFromFile(sourceHtmlPath); }
            catch (FileNotFoundException) { return string.Empty; }
            catch (DirectoryNotFoundException) { return string.Empty; }

            var sb = new StringBuilder();
            foreach (var inline in parsed.InlineStyles)
            {
                if (string.IsNullOrEmpty(inline)) continue;
                sb.Append(inline); sb.Append('\n');
            }
            string baseDir = Path.GetDirectoryName(Path.GetFullPath(sourceHtmlPath));
            foreach (var href in parsed.LinkedStylesheets)
            {
                if (string.IsNullOrEmpty(href)) continue;
                if (LooksLikeRemoteUrl(href)) continue;
                string path;
                try { path = Path.IsPathRooted(href) ? href : Path.Combine(baseDir ?? string.Empty, href); }
                catch (ArgumentException) { continue; }
                if (!File.Exists(path)) continue;
                try { sb.Append(File.ReadAllText(path, Encoding.UTF8)); sb.Append('\n'); }
                catch (IOException) { }
            }
            return sb.ToString();
        }

        // Internal: parse the raw CSS, run each selector through the
        // selector parser, and explode comma-separated selector lists into
        // one CssRule per selector (so the cascade can sort by specificity
        // per-selector, matching resolver.parse_rules + selector_matches).
        internal static List<CssRule> ParseAndExplode(string cssText, ref int orderCounter)
        {
            var output = new List<CssRule>();
            var rawRules = CssParser.ParseCss(cssText, ref orderCounter);
            foreach (var raw in rawRules)
            {
                var pairs = ToPairs(raw.Declarations);
                foreach (var sel in raw.Selectors)
                {
                    var parsed = CssParser.ParseSelector(sel);
                    if (parsed == null) continue;
                    output.Add(new CssRule
                    {
                        SelectorText = parsed.Raw,
                        Decls = pairs,
                        Specificity = PackSpecificity(parsed.Specificity()),
                        OriginIndex = raw.Order,
                        LeadingComments = raw.LeadingComments,
                        ParsedSelector = parsed,
                        RawDecls = raw.Declarations,
                    });
                }
            }
            return output;
        }

        internal static List<KeyValuePair<string, string>> ToPairs(List<CssDeclaration> decls)
        {
            var pairs = new List<KeyValuePair<string, string>>(decls.Count);
            foreach (var d in decls)
                pairs.Add(new KeyValuePair<string, string>(d.Prop, d.Value));
            return pairs;
        }

        // (ids, classes, tags) ordered exactly like Python tuple compare.
        // Each component capped well below 1000 in practice.
        internal static int PackSpecificity((int Ids, int Classes, int Tags) s)
            => s.Ids * 1_000_000 + s.Classes * 1_000 + s.Tags;

        private static bool LooksLikeRemoteUrl(string href)
        {
            if (string.IsNullOrEmpty(href)) return false;
            if (href.StartsWith("//", StringComparison.Ordinal)) return true;
            int colon = href.IndexOf(':');
            if (colon <= 0) return false;
            // crude scheme check — any prefix like http:, https:, data:, file:
            for (int i = 0; i < colon; i++)
            {
                char c = href[i];
                if (!(char.IsLetterOrDigit(c) || c == '+' || c == '-' || c == '.'))
                    return false;
            }
            return true;
        }
    }
}
