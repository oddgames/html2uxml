using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Css.Dom;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace ODDGames.Html2Uxml.Editor
{
    // Flattens an HTML document's CSS cascade into per-element inline
    // `style=""` attributes via AngleSharp.Css. After this pass the HTML
    // carries no <style> or <link rel=stylesheet> elements and no class
    // selectors apply — every element has its computed declarations baked
    // into its style attribute. The downstream converter (which already
    // honours inline `style` via ParseInlineStyle) sees a flat declaration
    // set per element, sidestepping the cascade-matching code path and the
    // class of layout bugs that came with it on real-world sites.
    //
    // Behaviour:
    //   * <style> bodies and <link rel=stylesheet href="..."> targets are
    //     concatenated as input to AngleSharp's stylesheet parser. Linked
    //     hrefs resolve relative to `htmlSourceDir`.
    //   * @media rules are evaluated at the configured viewport before
    //     declarations are pinned to elements — desktop layouts that hid
    //     behind `@media (min-width:569px)` etc come through.
    //   * Inheritance, !important, and specificity are handled by the
    //     CSSOM engine the same way a browser would.
    //   * `var()`/`calc()` are NOT evaluated by AngleSharp.Css — those still
    //     fall through to the converter's existing simplify/collapse passes.
    //   * Computed values that match the CSS initial value are skipped to
    //     keep the inlined output legible and the converter's downstream
    //     mappers from emitting redundant declarations.
    public static class Html2UxmlCssInliner
    {
        public sealed class InlineOptions
        {
            // Viewport used when evaluating `@media (min-width: …)` rules.
            // Matches a typical desktop window so the desktop layout wins
            // over mobile fallbacks that come earlier in the cascade.
            public int ViewportWidth = 1280;
            public int ViewportHeight = 720;
        }

        public sealed class InlineResult
        {
            public string Html;
            public int ElementsStyled;
            public int RulesParsed;
            public List<string> Warnings = new List<string>();
        }

        // Per-class layout dump — for every element with at least one
        // author class, average the CSS-computed width/height/padding from
        // the resolved cascade. Used by the comparison tooling to diff
        // per-class sizes between the browser cascade and the UXML render.
        // Note: values come from CSS, not real layout, so width:auto stays
        // unresolved and is reported as NaN; elements with explicit pixel
        // sizes (the common case for absolute-positioned widgets) match
        // what Chrome would compute.
        public sealed class ClassLayout
        {
            public int Count;
            public double W, H, PL, PT, PR, PB;
        }

        public static Dictionary<string, ClassLayout> DumpLayoutByClass(
            string htmlText, string htmlSourceDir,
            int viewportWidth = 1280, int viewportHeight = 720)
        {
            var byClass = new Dictionary<string, ClassLayout>();
            if (string.IsNullOrEmpty(htmlText)) return byClass;

            string mergedCss = CollectCss(htmlText, htmlSourceDir, new List<string>());
            string rewritten = ReplaceCssElements(htmlText, mergedCss);

            var config = Configuration.Default
                .WithCss()
                .WithRenderDevice(new DefaultRenderDevice
                {
                    ViewPortWidth = viewportWidth,
                    ViewPortHeight = viewportHeight,
                });
            var ctx = BrowsingContext.New(config);
            IDocument doc;
            try { doc = ctx.OpenAsync(req => req.Content(rewritten)).GetAwaiter().GetResult(); }
            catch { return byClass; }

            var win = doc.DefaultView;
            var totals = new Dictionary<string, (int n, double w, double h, double pl, double pt, double pr, double pb)>();

            double Px(string v)
            {
                if (string.IsNullOrEmpty(v)) return double.NaN;
                v = v.Trim();
                if (!v.EndsWith("px")) return double.NaN;
                return double.TryParse(v.Substring(0, v.Length - 2),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var d)
                    ? d : double.NaN;
            }
            void Add(double a, ref double sum, ref int n)
            { if (!double.IsNaN(a)) { sum += a; n++; } }

            foreach (var el in doc.All)
            {
                if (el == null) continue;
                string clsAttr = el.GetAttribute("class");
                if (string.IsNullOrWhiteSpace(clsAttr)) continue;
                ICssStyleDeclaration cs;
                try { cs = win?.GetComputedStyle(el, null); }
                catch { continue; }
                if (cs == null) continue;

                double w = Px(cs.GetPropertyValue("width"));
                double h = Px(cs.GetPropertyValue("height"));
                double pl = Px(cs.GetPropertyValue("padding-left"));
                double pt = Px(cs.GetPropertyValue("padding-top"));
                double pr = Px(cs.GetPropertyValue("padding-right"));
                double pb = Px(cs.GetPropertyValue("padding-bottom"));
                if (double.IsNaN(w) && double.IsNaN(h)) continue;

                foreach (var c in clsAttr.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (c.StartsWith("h2u-") || c.StartsWith("unity-")) continue;
                    if (!totals.TryGetValue(c, out var t)) t = (0, 0, 0, 0, 0, 0, 0);
                    int wn = t.n, hn = t.n, pln = t.n, ptn = t.n, prn = t.n, pbn = t.n;
                    // Use a single counter — averages will divide by t.n.
                    t = (t.n + 1, t.w + (double.IsNaN(w) ? 0 : w),
                                  t.h + (double.IsNaN(h) ? 0 : h),
                                  t.pl + (double.IsNaN(pl) ? 0 : pl),
                                  t.pt + (double.IsNaN(pt) ? 0 : pt),
                                  t.pr + (double.IsNaN(pr) ? 0 : pr),
                                  t.pb + (double.IsNaN(pb) ? 0 : pb));
                    totals[c] = t;
                }
            }

            foreach (var kv in totals)
            {
                int n = kv.Value.n;
                byClass[kv.Key] = new ClassLayout
                {
                    Count = n,
                    W = kv.Value.w / n,
                    H = kv.Value.h / n,
                    PL = kv.Value.pl / n,
                    PT = kv.Value.pt / n,
                    PR = kv.Value.pr / n,
                    PB = kv.Value.pb / n,
                };
            }
            return byClass;
        }

        // Top-level entry. `htmlSourceDir` is used to resolve relative
        // <link href> values; pass the directory the HTML lives in.
        public static InlineResult Inline(string htmlText, string htmlSourceDir, InlineOptions options = null)
        {
            options ??= new InlineOptions();
            var result = new InlineResult();
            if (string.IsNullOrEmpty(htmlText)) { result.Html = htmlText ?? string.Empty; return result; }

            // 1. Concatenate every <style> body + every <link rel=stylesheet> href
            //    target into a single CSS blob — AngleSharp will parse this
            //    once and run the cascade against it.
            string mergedCss = CollectCss(htmlText, htmlSourceDir, result.Warnings);

            // 2. Rewrite the HTML so AngleSharp only sees ONE <style> with
            //    the merged CSS (and no <link rel=stylesheet>). Otherwise
            //    AngleSharp tries to fetch linked stylesheets through its
            //    own loader, which isn't wired up to our disk paths.
            string rewritten = ReplaceCssElements(htmlText, mergedCss);

            // 3. Open in AngleSharp with CSS engine + a viewport that
            //    matches a desktop browser.
            var config = Configuration.Default
                .WithCss()
                .WithRenderDevice(new DefaultRenderDevice
                {
                    ViewPortWidth = options.ViewportWidth,
                    ViewPortHeight = options.ViewportHeight,
                });
            var context = BrowsingContext.New(config);
            IDocument doc;
            try
            {
                doc = context.OpenAsync(req => req.Content(rewritten)).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                result.Warnings.Add("AngleSharp open failed: " + e.Message);
                result.Html = htmlText;
                return result;
            }

            // 4. Walk every element, pull its cascaded style, write back as
            //    inline `style=""`. Skip <html>/<head>/<style>/<link>/<script>
            //    where inline style is meaningless or already stripped.
            int styled = 0;
            foreach (var element in doc.All)
            {
                if (element is IHtmlHtmlElement || element is IHtmlHeadElement) continue;
                string tag = element.LocalName?.ToLowerInvariant();
                if (tag == "style" || tag == "link" || tag == "script" || tag == "meta" || tag == "title")
                    continue;

                ICssStyleDeclaration style = null;
                try
                {
                    style = doc.DefaultView?.GetComputedStyle(element, null);
                }
                catch (Exception e)
                {
                    result.Warnings.Add($"computed style failed for <{tag}>: {e.Message}");
                    continue;
                }
                if (style == null || style.Length == 0) continue;

                string serialized = SerializeNonInitialDecls(style);
                if (string.IsNullOrEmpty(serialized)) continue;

                // Merge with any existing inline style — user-authored
                // wins by going LAST (CSS cascade: inline + last = wins).
                string existing = element.GetAttribute("style");
                string final = string.IsNullOrWhiteSpace(existing)
                    ? serialized
                    : serialized + "; " + existing;
                element.SetAttribute("style", final);
                styled++;
            }
            result.ElementsStyled = styled;

            // 5. Strip the synthesized <style> so the downstream converter
            //    doesn't re-apply the cascade on top of the inlined output.
            foreach (var s in new List<IElement>(doc.QuerySelectorAll("style")))
                s.Remove();
            foreach (var l in new List<IElement>(doc.QuerySelectorAll("link[rel=stylesheet], link[rel=preload][as=style]")))
                l.Remove();

            result.Html = SerializeDocument(doc);
            return result;
        }

        // Serialize one ICssStyleDeclaration as a "prop:val; prop:val"
        // string, skipping properties whose value is the CSS initial
        // (AngleSharp.Css explicitly emits `initial` for longhands not set
        // by the cascade — we don't want to flood the inline output with
        // them).
        static string SerializeNonInitialDecls(ICssStyleDeclaration style)
        {
            var sb = new StringBuilder();
            bool first = true;
            for (int i = 0; i < style.Length; i++)
            {
                string prop = style[i];
                if (string.IsNullOrEmpty(prop)) continue;
                string val = style.GetPropertyValue(prop);
                if (string.IsNullOrEmpty(val)) continue;
                string trimmed = val.Trim();
                if (trimmed.Equals("initial", StringComparison.OrdinalIgnoreCase)) continue;
                if (trimmed.Equals("inherit", StringComparison.OrdinalIgnoreCase)) continue;
                if (trimmed.Equals("unset", StringComparison.OrdinalIgnoreCase)) continue;
                if (!first) sb.Append("; ");
                sb.Append(prop).Append(": ").Append(trimmed);
                first = false;
            }
            return sb.ToString();
        }

        // Pull all <style> contents + all linked stylesheet contents into
        // one CSS string. Linked hrefs resolve relative to `baseDir`. We
        // walk the raw HTML with regex rather than parsing twice — the only
        // thing we need is the inline blob ordering.
        static readonly Regex StyleBlockRx = new Regex(
            @"<style\b[^>]*>(?<body>[\s\S]*?)</style\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex LinkStyleRx = new Regex(
            @"<link\b(?<attrs>[^>]*?)\bhref\s*=\s*(?<q>[""'])(?<href>[^""']+)\k<q>[^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex RelStyleRx = new Regex(
            @"\brel\s*=\s*[""'][^""']*\bstylesheet\b[^""']*[""']",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static string CollectCss(string html, string baseDir, List<string> warnings)
        {
            var sb = new StringBuilder();

            // Document-order walk so cascade order matches what the browser
            // would see. We iterate over a combined match list sorted by
            // index.
            var styleMatches = new List<(int Index, string Body)>();
            foreach (Match m in StyleBlockRx.Matches(html))
                styleMatches.Add((m.Index, m.Groups["body"].Value));
            var linkMatches = new List<(int Index, string Href)>();
            foreach (Match m in LinkStyleRx.Matches(html))
            {
                if (!RelStyleRx.IsMatch(m.Groups["attrs"].Value)) continue;
                linkMatches.Add((m.Index, m.Groups["href"].Value));
            }

            // Merge and sort.
            var merged = new List<(int Index, bool IsLink, string Payload)>();
            foreach (var s in styleMatches) merged.Add((s.Index, false, s.Body));
            foreach (var l in linkMatches)  merged.Add((l.Index, true,  l.Href));
            merged.Sort((a, b) => a.Index.CompareTo(b.Index));

            foreach (var item in merged)
            {
                if (!item.IsLink)
                {
                    sb.Append(item.Payload).Append('\n');
                    continue;
                }
                string href = item.Payload;
                if (string.IsNullOrEmpty(href)) continue;
                if (Uri.TryCreate(href, UriKind.Absolute, out _))
                {
                    warnings.Add("Skipped remote stylesheet (UrlFetcher should have localised it): " + href);
                    continue;
                }
                string path;
                try { path = Path.IsPathRooted(href) ? href : Path.Combine(baseDir ?? string.Empty, href); }
                catch (ArgumentException) { continue; }
                if (!File.Exists(path)) { warnings.Add("Stylesheet not found: " + path); continue; }
                try { sb.Append(File.ReadAllText(path)).Append('\n'); }
                catch (IOException e) { warnings.Add("Failed to read stylesheet " + path + ": " + e.Message); }
            }

            return sb.ToString();
        }

        // Replace all <style> and stylesheet <link> elements in the source
        // HTML with a single <style> carrying the merged CSS. Position:
        // we drop the synthesized block where the first removed CSS source
        // sat so document order is roughly preserved.
        static string ReplaceCssElements(string html, string mergedCss)
        {
            int insertAt = -1;
            var removals = new List<(int Start, int Length)>();

            foreach (Match m in StyleBlockRx.Matches(html))
            {
                if (insertAt < 0) insertAt = m.Index;
                removals.Add((m.Index, m.Length));
            }
            foreach (Match m in LinkStyleRx.Matches(html))
            {
                if (!RelStyleRx.IsMatch(m.Groups["attrs"].Value)) continue;
                if (insertAt < 0) insertAt = m.Index;
                removals.Add((m.Index, m.Length));
            }

            string synthBlock = "<style>" + mergedCss + "</style>";
            if (removals.Count == 0)
            {
                int headEnd = IndexOfHeadEnd(html);
                if (headEnd >= 0) return html.Insert(headEnd, synthBlock);
                return synthBlock + html;
            }

            removals.Sort((a, b) => a.Start.CompareTo(b.Start));
            var sb = new StringBuilder(html.Length + mergedCss.Length);
            int cursor = 0;
            bool inserted = false;
            foreach (var r in removals)
            {
                if (r.Start > cursor) sb.Append(html, cursor, r.Start - cursor);
                if (!inserted && r.Start == insertAt)
                {
                    sb.Append(synthBlock);
                    inserted = true;
                }
                cursor = r.Start + r.Length;
            }
            if (cursor < html.Length) sb.Append(html, cursor, html.Length - cursor);
            if (!inserted) sb.Append(synthBlock);
            return sb.ToString();
        }

        static int IndexOfHeadEnd(string html)
        {
            int i = html.IndexOf("</head", StringComparison.OrdinalIgnoreCase);
            return i < 0 ? -1 : i;
        }

        // Serialize the AngleSharp document back to an HTML string. We use
        // an explicit formatter so the output is parseable (the default
        // AngleSharp formatter does HTML5-friendly output).
        static string SerializeDocument(IDocument doc)
        {
            using var writer = new StringWriter();
            doc.ToHtml(writer, AngleSharp.Html.HtmlMarkupFormatter.Instance);
            return writer.ToString();
        }
    }
}
