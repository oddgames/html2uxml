// source: html2uxml/html_parser.py
//
// HTML -> normalized intermediate tree used by the rest of the converter.
//
// Faithful port of html2uxml/html_parser.py. Uses AngleSharp for parsing but
// re-implements the pre/post processing the Python version did with stdlib
// html.parser:
//   * NBSP (U+00A0) is normalized to a regular space across the whole input
//     (some sources from docx/copy-paste have NBSP inside `<...>` which breaks
//     tokenization, and UI Toolkit text doesn't care about the distinction).
//   * <svg>...</svg> blocks are extracted before parsing so AngleSharp's HTML
//     fragment parser does not lowercase case-sensitive SVG attributes
//     (viewBox, preserveAspectRatio). Each block is replaced with
//     `<svg data-svg-id="N"></svg>` and stashed on ParsedHtml.SvgBlocks.
//   * <style> contents are collected into ParsedHtml.InlineStyles and the
//     element itself is dropped from the node tree.
//   * <link rel="stylesheet"> hrefs are collected into
//     ParsedHtml.LinkedStylesheets (also matches `rel="preload" as="style"`).
//   * <script>/<noscript>/<template>/<title>/<head> subtrees are dropped.
//   * Whitespace is collapsed except inside <pre>/<code>/<textarea>; pure
//     whitespace text nodes outside those are skipped entirely.
//   * The returned HtmlNode is the <body> element if present, otherwise the
//     synthetic root containing whatever top-level elements were parsed.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    public static class HtmlLoader
    {
        // Matches html_parser.Node. Tag is lowercased; for text nodes Tag is ""
        // (or null) and Text holds the content.
        public sealed class HtmlNode
        {
            public string Tag;
            public string Text;
            public bool IsComment;
            public Dictionary<string, string> Attrs = new Dictionary<string, string>();
            public List<HtmlNode> Children = new List<HtmlNode>();

            // Comments share the empty-Tag shape with text nodes so existing
            // selector/layout walkers (which use `IsText` to skip text leaves)
            // also skip comments. Emitters that consume `.Text` must branch
            // on `IsComment` explicitly to avoid leaking comment bodies into
            // labels / slugs / form-association text.
            public bool IsText => string.IsNullOrEmpty(Tag);

            public List<string> Classes()
            {
                var result = new List<string>();
                if (Attrs == null) return result;
                if (!Attrs.TryGetValue("class", out var cls) || string.IsNullOrEmpty(cls))
                    return result;
                foreach (var token in cls.Split(new[] { ' ', '\t', '\n', '\r', '\f' },
                                                StringSplitOptions.RemoveEmptyEntries))
                    result.Add(token);
                return result;
            }
        }

        // Mirrors html_parser.ParsedHTML — body root + document-scoped
        // side-channel data the rest of the converter needs.
        public sealed class ParsedHtml
        {
            public HtmlNode Root;
            public List<string> InlineStyles = new List<string>();
            public List<string> LinkedStylesheets = new List<string>();
            public List<string> SvgBlocks = new List<string>();
        }

        // Public spec surface — single-root return.
        public static HtmlNode LoadFromFile(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            var html = File.ReadAllText(path, Encoding.UTF8);
            return LoadFromString(html);
        }

        public static HtmlNode LoadFromString(string html, string baseUrl = null)
        {
            return ParseFromString(html, baseUrl).Root;
        }

        // Variant that also exposes inline styles, linked stylesheets and
        // raw SVG blocks. The rest of the C# converter port will call these.
        public static ParsedHtml ParseFromFile(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException(nameof(path));
            var html = File.ReadAllText(path, Encoding.UTF8);
            return ParseFromString(html);
        }

        public static ParsedHtml ParseFromString(string html, string baseUrl = null)
        {
            if (html == null) html = string.Empty;

            // Normalize NBSP to space (matches html_parser.parse_html).
            html = html.Replace(' ', ' ');

            // Pull raw SVG blocks out before HTML parsing.
            var svgBlocks = new List<string>();
            html = ExtractSvgBlocks(html, svgBlocks);

            var parser = new HtmlParser(new HtmlParserOptions
            {
                IsScripting = false,
                IsStrictMode = false,
                IsKeepingSourceReferences = false,
            });
            var doc = parser.ParseDocument(html);

            var builder = new TreeBuilder();
            builder.Walk(doc);

            var bodyOrRoot = FindBody(builder.Root) ?? builder.Root;

            return new ParsedHtml
            {
                Root = bodyOrRoot,
                InlineStyles = builder.StyleBlocks,
                LinkedStylesheets = builder.LinkedStylesheets,
                SvgBlocks = svgBlocks,
            };
        }

        // ---------- internals ----------

        private static readonly HashSet<string> HeadOnlyTags = new HashSet<string>(StringComparer.Ordinal)
        {
            "script", "noscript", "template", "meta", "link", "title", "head",
        };

        private static readonly HashSet<string> WhitespacePreserveTags = new HashSet<string>(StringComparer.Ordinal)
        {
            "pre", "code", "textarea",
        };

        // Regex equivalent of _SVG_BLOCK_RE in html_parser.py.
        // re.IGNORECASE | re.DOTALL  ->  RegexOptions.IgnoreCase | RegexOptions.Singleline.
        private static readonly Regex SvgBlockRegex = new Regex(
            @"<svg\b[^>]*>.*?</svg\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        // CSS `url("data:image/svg+xml;utf8,<svg ...></svg>")` legitimately
        // contains an inline <svg>...</svg>; the SVG extractor must leave it
        // alone or it injects bare `"` chars into the CSS string and the
        // generated USS fails to parse.
        private static readonly Regex StyleBlockRegex = new Regex(
            @"<style\b[^>]*>.*?</style\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        private static string ExtractSvgBlocks(string source, List<string> blocks)
        {
            var styleRegions = new List<(int Start, int End)>();
            foreach (Match m in StyleBlockRegex.Matches(source))
                styleRegions.Add((m.Index, m.Index + m.Length));

            return SvgBlockRegex.Replace(source, match =>
            {
                foreach (var r in styleRegions)
                {
                    if (match.Index >= r.Start && match.Index < r.End)
                        return match.Value;
                }
                int idx = blocks.Count;
                blocks.Add(match.Value);
                return $"<svg data-svg-id=\"{idx}\"></svg>";
            });
        }

        private static HtmlNode FindBody(HtmlNode root)
        {
            foreach (var child in root.Children)
            {
                if (child.Tag == "body") return child;
                if (child.Tag == "html")
                {
                    foreach (var inner in child.Children)
                    {
                        if (inner.Tag == "body") return inner;
                    }
                }
            }
            return null;
        }

        // Mirrors html_parser._collapse: runs of whitespace -> single space,
        // but does not strip leading/trailing whitespace (the caller decides
        // whether the resulting node is whitespace-only).
        private static string CollapseWhitespace(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new StringBuilder(s.Length);
            bool prevSpace = false;
            foreach (char ch in s)
            {
                if (ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r')
                {
                    if (!prevSpace) sb.Append(' ');
                    prevSpace = true;
                }
                else
                {
                    sb.Append(ch);
                    prevSpace = false;
                }
            }
            return sb.ToString();
        }

        private static bool IsWhitespace(string s)
        {
            if (string.IsNullOrEmpty(s)) return true;
            foreach (var ch in s)
            {
                if (!char.IsWhiteSpace(ch)) return false;
            }
            return true;
        }

        private static bool IsStylesheetLink(IElement el)
        {
            var rel = (el.GetAttribute("rel") ?? string.Empty).ToLowerInvariant();
            var relTokens = new HashSet<string>(StringComparer.Ordinal);
            foreach (var tok in rel.Split(new[] { ' ', '\t', '\n', '\r', '\f' },
                                          StringSplitOptions.RemoveEmptyEntries))
                relTokens.Add(tok);
            if (relTokens.Contains("stylesheet")) return true;
            if (relTokens.Contains("preload"))
            {
                var asAttr = (el.GetAttribute("as") ?? string.Empty).ToLowerInvariant();
                if (asAttr == "style") return true;
            }
            return false;
        }

        // Walk the AngleSharp DOM and produce the same intermediate tree the
        // Python _TreeBuilder produced. We deliberately do NOT mirror
        // AngleSharp's auto-inserted <html>/<head>/<body> wrappers when the
        // input was a fragment without them — Python only emits whatever the
        // tokenizer saw. To approximate that, when AngleSharp's body is empty
        // and head is empty we still expose <html>/<body> in the tree (the
        // FindBody helper will return body). When the input had a body, that
        // body is returned. This matches Python behaviour for the inputs the
        // converter sees (which always go through parse_html and pick body).
        private sealed class TreeBuilder
        {
            public HtmlNode Root = new HtmlNode { Tag = "__root__" };
            public List<string> StyleBlocks = new List<string>();
            public List<string> LinkedStylesheets = new List<string>();

            public void Walk(IHtmlDocument doc)
            {
                // Process <head> contents first to collect <link>/<style>
                // (AngleSharp synthesises a head even for fragments).
                if (doc.Head != null)
                {
                    CollectHeadSidechannel(doc.Head);
                }
                // Mirror Python: emit <html> wrapper holding <head>/<body> only
                // when the source actually contained one. AngleSharp always
                // synthesises them, so we infer "real" presence by checking if
                // the document element has source-meaningful attributes OR a
                // non-empty <head> outside of the side-channel tags. Simpler
                // and faithful to converter usage: always look up <body> via
                // FindBody, and seed the synthetic root with the body subtree
                // (and any stray top-level frame siblings). For converter
                // callers this collapses to "return the body element".
                if (doc.Body != null)
                {
                    var html = new HtmlNode { Tag = "html" };
                    Root.Children.Add(html);
                    var body = new HtmlNode { Tag = "body" };
                    CopyAttrs(doc.Body, body.Attrs);
                    html.Children.Add(body);
                    AppendChildren(doc.Body, body, insidePre: false);
                }
            }

            private void CollectHeadSidechannel(IElement head)
            {
                foreach (var child in head.ChildNodes)
                {
                    if (child is IElement el)
                    {
                        var name = el.LocalName?.ToLowerInvariant();
                        if (name == "style")
                        {
                            // Concatenate all text descendants — matches what
                            // html.parser would have fed into handle_data.
                            var sb = new StringBuilder();
                            CollectRawText(el, sb);
                            StyleBlocks.Add(sb.ToString());
                        }
                        else if (name == "link" && IsStylesheetLink(el))
                        {
                            var href = el.GetAttribute("href");
                            if (!string.IsNullOrEmpty(href))
                                LinkedStylesheets.Add(href);
                        }
                    }
                }
            }

            private static void CollectRawText(INode node, StringBuilder sb)
            {
                foreach (var child in node.ChildNodes)
                {
                    if (child.NodeType == NodeType.Text)
                        sb.Append(child.TextContent);
                    else if (child.NodeType == NodeType.Element)
                        CollectRawText(child, sb);
                }
            }

            private void AppendChildren(IElement source, HtmlNode dest, bool insidePre)
            {
                foreach (var child in source.ChildNodes)
                {
                    AppendNode(child, dest, insidePre);
                }
            }

            private void AppendNode(INode node, HtmlNode dest, bool insidePre)
            {
                switch (node.NodeType)
                {
                    case NodeType.Text:
                        AppendText(node.TextContent, dest, insidePre);
                        break;
                    case NodeType.Element:
                        if (node is IElement el) AppendElement(el, dest, insidePre);
                        break;
                    case NodeType.Comment:
                        // Preserve `<!-- ... -->` so the emitter can mirror it
                        // into the generated UXML. Dropping comments would lose
                        // any author annotations the source HTML carried.
                        AppendComment(node.TextContent, dest);
                        break;
                    // Document types and processing instructions are dropped.
                }
            }

            private void AppendComment(string data, HtmlNode parent)
            {
                if (data == null) data = string.Empty;
                parent.Children.Add(new HtmlNode { Tag = "", IsComment = true, Text = data });
            }

            private void AppendText(string data, HtmlNode parent, bool insidePre)
            {
                if (string.IsNullOrEmpty(data)) return;
                bool preserve = insidePre || (parent.Tag != null && WhitespacePreserveTags.Contains(parent.Tag));
                string text = preserve ? data : CollapseWhitespace(data);
                if (text.Length == 0) return;
                if (!preserve && IsWhitespace(text)) return;
                parent.Children.Add(new HtmlNode { Tag = "", Text = text });
            }

            private void AppendElement(IElement el, HtmlNode parent, bool insidePre)
            {
                var tag = el.LocalName?.ToLowerInvariant() ?? string.Empty;

                // Side-channel collection — these elements are dropped from
                // the tree. <link rel=stylesheet> + <style> were already
                // pulled by CollectHeadSidechannel for <head>; handle the
                // body-scoped variants here too.
                if (tag == "style")
                {
                    var sb = new StringBuilder();
                    CollectRawText(el, sb);
                    StyleBlocks.Add(sb.ToString());
                    return;
                }
                if (tag == "link" && IsStylesheetLink(el))
                {
                    var href = el.GetAttribute("href");
                    if (!string.IsNullOrEmpty(href))
                        LinkedStylesheets.Add(href);
                    return;
                }
                if (HeadOnlyTags.Contains(tag))
                {
                    // script / noscript / template / meta / title / head — drop entirely.
                    return;
                }

                var node = new HtmlNode { Tag = tag };
                CopyAttrs(el, node.Attrs);
                parent.Children.Add(node);

                bool childPre = insidePre || WhitespacePreserveTags.Contains(tag);
                AppendChildren(el, node, childPre);
            }

            private static void CopyAttrs(IElement el, Dictionary<string, string> dest)
            {
                foreach (var attr in el.Attributes)
                {
                    var key = attr.Name?.ToLowerInvariant();
                    if (string.IsNullOrEmpty(key)) continue;
                    dest[key] = attr.Value ?? string.Empty;
                }
            }
        }
    }
}
