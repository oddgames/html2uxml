using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ODDGames.Html2Uxml.Editor
{
    // Pulls a remote page + its linked stylesheets and <img> assets into a
    // local directory so the existing C# converter (which only reads from
    // disk) can run on it. Inline <style> blocks and downloaded CSS files
    // get their relative url() / @import refs rewritten to ABSOLUTE URLs;
    // the converter's AssetWriter then handles fetching them when called
    // with DownloadRemoteAssets=true.
    //
    // Synchronous on purpose — callers should run it on a Task.Run so the
    // editor UI thread stays responsive.
    public static class Html2UxmlUrlFetcher
    {
        public sealed class FetchResult
        {
            public string LocalHtmlPath;
            public string Slug;
            public string PageTitle;
            public List<string> Warnings = new List<string>();
        }

        // ---------- regexes ------------------------------------------------

        // <link rel="stylesheet" ... href="...">  (also matches preload/style)
        static readonly Regex LinkRx = new Regex(
            @"<link\b(?<attrs>[^>]*?)\bhref\s*=\s*(?<q>[""'])(?<href>[^""']+)\k<q>(?<rest>[^>]*)>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // <img ... src="...">
        static readonly Regex ImgRx = new Regex(
            @"<img\b(?<attrs>[^>]*?)\bsrc\s*=\s*(?<q>[""'])(?<src>[^""']+)\k<q>(?<rest>[^>]*?)>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // <style>...</style>
        static readonly Regex StyleBlockRx = new Regex(
            @"(?<open><style\b[^>]*>)(?<body>.*?)(?<close></style\s*>)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

        // url(...) inside CSS — captures the path between optional quotes.
        static readonly Regex CssUrlRx = new Regex(
            @"url\(\s*(?<q>[""']?)(?<path>[^""'()\s]+)\k<q>\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // @import "..."; or @import url(...)
        static readonly Regex CssImportRx = new Regex(
            @"@import\s+(?:url\(\s*(?<q1>[""']?)(?<u1>[^""'()\s]+)\k<q1>\s*\)|(?<q2>[""'])(?<u2>[^""']+)\k<q2>)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // <title>...</title>
        static readonly Regex TitleRx = new Regex(
            @"<title[^>]*>(?<t>[\s\S]*?)</title\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ---------- entry --------------------------------------------------

        public static FetchResult Fetch(string pageUrl, string destDir, CancellationToken ct, bool flattenCascade = false)
        {
            if (string.IsNullOrWhiteSpace(pageUrl)) throw new ArgumentException("URL required");
            if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri))
                throw new ArgumentException("Invalid URL: " + pageUrl);
            if (pageUri.Scheme != "http" && pageUri.Scheme != "https")
                throw new ArgumentException("Only http/https supported (got " + pageUri.Scheme + ")");

            Directory.CreateDirectory(destDir);
            var result = new FetchResult { Slug = SlugFromUri(pageUri) };

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/120.0 Safari/537.36 html2uxml-compare/0.1");
            http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,*/*");

            // 1. HTML
            string html;
            using (var req = new HttpRequestMessage(HttpMethod.Get, pageUri))
            using (var resp = http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).GetAwaiter().GetResult())
            {
                resp.EnsureSuccessStatusCode();
                using var s = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                using var rdr = new StreamReader(s, DetectEncoding(resp), true);
                html = rdr.ReadToEnd();
            }

            ct.ThrowIfCancellationRequested();
            result.PageTitle = ExtractTitle(html) ?? result.Slug;

            // 2. Inline <style>: rewrite url() to absolute (page URL base).
            html = StyleBlockRx.Replace(html, m =>
            {
                string body = m.Groups["body"].Value;
                body = RewriteCssUrls(body, pageUri);
                return m.Groups["open"].Value + body + m.Groups["close"].Value;
            });

            // 3. <link rel="stylesheet">: download, rewrite url(), save local,
            //    then patch the href in the HTML to point at the local file.
            int linkIdx = 0;
            html = LinkRx.Replace(html, m =>
            {
                string attrs = m.Groups["attrs"].Value + m.Groups["rest"].Value;
                if (!LooksLikeStylesheet(attrs)) return m.Value;

                string href = m.Groups["href"].Value;
                if (!Uri.TryCreate(pageUri, href, out var cssUri)) return m.Value;
                if (cssUri.Scheme != "http" && cssUri.Scheme != "https") return m.Value;

                string localName;
                try { localName = DownloadCss(http, cssUri, destDir, linkIdx++, ct, result.Warnings); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    result.Warnings.Add($"link href fetch failed: {cssUri} — {e.Message}");
                    return m.Value;
                }
                if (localName == null) return m.Value;

                return ReplaceAttrValue(m.Value, "href", localName);
            });

            // 4. <img src=...>: download, rewrite to local relative.
            int imgIdx = 0;
            html = ImgRx.Replace(html, m =>
            {
                string src = m.Groups["src"].Value;
                if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    return m.Value;
                if (!Uri.TryCreate(pageUri, src, out var imgUri)) return m.Value;
                if (imgUri.Scheme != "http" && imgUri.Scheme != "https") return m.Value;

                string localName;
                try { localName = DownloadBinary(http, imgUri, destDir, "img_" + imgIdx++, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    result.Warnings.Add($"img src fetch failed: {imgUri} — {e.Message}");
                    return m.Value;
                }
                if (localName == null) return m.Value;

                return ReplaceAttrValue(m.Value, "src", localName);
            });

            // 5. Optional cascade flatten via AngleSharp.Css. When enabled,
            //    every element gets its resolved declarations baked into an
            //    inline `style="..."`, @media is evaluated at a 1280x720
            //    viewport, and downstream stages only see flat per-element
            //    styles. Off by default — heavy on large pages, but useful
            //    for sites whose layout depends on @media or deep cascade
            //    matching that the converter's own Resolver doesn't honour.
            if (flattenCascade)
            {
                try
                {
                    var inlineRes = Html2UxmlCssInliner.Inline(html, destDir);
                    if (!string.IsNullOrEmpty(inlineRes.Html))
                    {
                        html = inlineRes.Html;
                        foreach (var w in inlineRes.Warnings)
                            result.Warnings.Add("[inliner] " + w);
                    }
                }
                catch (Exception e)
                {
                    result.Warnings.Add("CSS inliner failed (cascade left as-is): " + e.Message);
                }
            }

            // 6. Write HTML.
            string htmlPath = Path.Combine(destDir, result.Slug + ".html");
            File.WriteAllText(htmlPath, html, Utf8NoBom);
            result.LocalHtmlPath = htmlPath;
            return result;
        }

        // ---------- helpers ------------------------------------------------

        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        static Encoding DetectEncoding(HttpResponseMessage resp)
        {
            try
            {
                var charset = resp.Content?.Headers?.ContentType?.CharSet;
                if (!string.IsNullOrEmpty(charset))
                    return Encoding.GetEncoding(charset.Trim('"'));
            }
            catch { /* fall through */ }
            return Encoding.UTF8;
        }

        static string DownloadCss(
            HttpClient http, Uri cssUri, string destDir, int idx,
            CancellationToken ct, List<string> warnings)
        {
            string css;
            using (var req = new HttpRequestMessage(HttpMethod.Get, cssUri))
            using (var resp = http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).GetAwaiter().GetResult())
            {
                resp.EnsureSuccessStatusCode();
                using var s = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                using var rdr = new StreamReader(s, DetectEncoding(resp), true);
                css = rdr.ReadToEnd();
            }

            // Inline @import — for each, fetch and inline. The C# converter's
            // CssLoader doesn't follow @import, so we have to expand here or
            // the cascade will be missing rules.
            css = InlineCssImports(http, css, cssUri, destDir, ct, warnings, depth: 0);

            // Rewrite url(...) refs to absolute (resolved against this CSS's URL).
            css = RewriteCssUrls(css, cssUri);

            string baseName = SafeName(Path.GetFileNameWithoutExtension(cssUri.AbsolutePath));
            if (string.IsNullOrEmpty(baseName)) baseName = "sheet";
            string localName = $"sheet_{idx:000}_{baseName}.css";
            File.WriteAllText(Path.Combine(destDir, localName), css, Utf8NoBom);
            return localName;
        }

        static string InlineCssImports(
            HttpClient http, string css, Uri cssBase, string destDir,
            CancellationToken ct, List<string> warnings, int depth)
        {
            if (depth > 4) return css; // bail on deep import chains
            return CssImportRx.Replace(css, m =>
            {
                string raw = !string.IsNullOrEmpty(m.Groups["u1"].Value)
                    ? m.Groups["u1"].Value : m.Groups["u2"].Value;
                if (string.IsNullOrEmpty(raw)) return m.Value;
                if (!Uri.TryCreate(cssBase, raw, out var importUri)) return m.Value;
                if (importUri.Scheme != "http" && importUri.Scheme != "https") return m.Value;
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, importUri);
                    using var resp = http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).GetAwaiter().GetResult();
                    resp.EnsureSuccessStatusCode();
                    using var s = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                    using var rdr = new StreamReader(s, DetectEncoding(resp), true);
                    string body = rdr.ReadToEnd();
                    body = InlineCssImports(http, body, importUri, destDir, ct, warnings, depth + 1);
                    body = RewriteCssUrls(body, importUri);
                    return "/* @import inlined: " + importUri + " */\n" + body + "\n";
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e)
                {
                    warnings.Add($"@import fetch failed: {importUri} — {e.Message}");
                    return m.Value;
                }
            });
        }

        static string RewriteCssUrls(string css, Uri cssBase)
        {
            if (string.IsNullOrEmpty(css)) return css ?? string.Empty;
            return CssUrlRx.Replace(css, m =>
            {
                string raw = m.Groups["path"].Value;
                if (string.IsNullOrEmpty(raw)) return m.Value;
                if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return m.Value;
                if (raw.StartsWith("#")) return m.Value;
                if (Uri.TryCreate(raw, UriKind.Absolute, out _)) return m.Value;
                if (!Uri.TryCreate(cssBase, raw, out var abs)) return m.Value;
                return "url(\"" + abs + "\")";
            });
        }

        static string DownloadBinary(
            HttpClient http, Uri url, string destDir, string basePrefix, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
            using var s = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            byte[] bytes = ms.ToArray();

            string ext = Path.GetExtension(url.AbsolutePath);
            if (string.IsNullOrEmpty(ext) || ext.Length > 6) ext = ".bin";
            string baseName = SafeName(Path.GetFileNameWithoutExtension(url.AbsolutePath));
            if (string.IsNullOrEmpty(baseName)) baseName = "asset";
            string localName = $"{basePrefix}_{baseName}{ext}";

            // Unity's SVGImporter blows up on `fill="var(--x)"` and similar
            // runtime-only CSS references that landing pages litter their
            // sprite sheets with. Strip them now so the asset re-imports
            // cleanly under Assets/.
            if (ext.Equals(".svg", StringComparison.OrdinalIgnoreCase))
            {
                string svg = Encoding.UTF8.GetString(bytes);
                svg = SanitizeSvgText(svg);
                File.WriteAllText(Path.Combine(destDir, localName), svg, Utf8NoBom);
            }
            else
            {
                File.WriteAllBytes(Path.Combine(destDir, localName), bytes);
            }
            return localName;
        }

        // SVG sanitizer — replaces unresolved CSS `var(--*)` tokens with
        // their fallback (if present) or `currentColor` so Unity's SVG
        // importer doesn't throw. Conservative: only touches var() syntax,
        // leaves the rest of the document alone.
        static readonly Regex SvgVarRx = new Regex(
            @"var\(\s*--[\w-]+\s*(?:,\s*(?<fb>[^)]+))?\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Matches `currentColor` as a standalone paint value. Used by the
        // sanitizer below so Unity's SVG importer doesn't fail on
        // `fill="currentColor"` or `stroke="currentColor"`.
        static readonly Regex SvgCurrentColorRx = new Regex(
            @"\bcurrentColor\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string SanitizeSvgText(string svg)
        {
            if (string.IsNullOrEmpty(svg)) return svg ?? string.Empty;
            // Unity's VectorGraphics SVGImporter doesn't accept `currentColor`
            // or CSS variable refs — fall back to opaque black for paint
            // values. The fallback colour from var(--x, #fff) is still used
            // when present.
            svg = SvgVarRx.Replace(svg, m =>
            {
                string fb = m.Groups["fb"].Value;
                if (string.IsNullOrWhiteSpace(fb)) return "#000000";
                string trimmed = fb.Trim();
                if (trimmed.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
                    return "#ffffff";
                return trimmed;
            });
            svg = SvgCurrentColorRx.Replace(svg, "#ffffff");
            svg = OversampleSvgRasterTarget(svg);
            return svg;
        }

        // Bump the SVG's top-level width/height by a fixed factor so Unity's
        // VectorGraphics importer rasterises into a larger texture. The
        // displayed size is still controlled by USS width/height on the
        // background-image host element — we just give the importer more
        // pixels to work with, which removes the chunky aliased look on
        // 10x10 icons rendered into a 24x24 button when both source and
        // raster target are tiny. viewBox is left alone so the geometry
        // stays the same; only the produced texture's resolution changes.
        // Disabled — the VectorGraphics importer renders to a tessellated
        // mesh, not a texture, so attr scaling doesn't behave like extra
        // texture pixels. Bumping width/height also confuses the asset's
        // intrinsic-size pickup that USS background-size relies on, and
        // the icons end up reading as missing/blank when displayed at
        // their original USS-host size. Keep oversample off until we wire
        // import-time scaling via the .svg.meta path instead.
        const int SvgRasterOversample = 1;

        static readonly Regex SvgRootWidthRx = new Regex(
            @"(?<=<svg\b[^>]{0,400}?\swidth\s*=\s*[""'])(?<v>\d+(?:\.\d+)?)(?<unit>[a-z%]*)(?=[""'])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        static readonly Regex SvgRootHeightRx = new Regex(
            @"(?<=<svg\b[^>]{0,400}?\sheight\s*=\s*[""'])(?<v>\d+(?:\.\d+)?)(?<unit>[a-z%]*)(?=[""'])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static string OversampleSvgRasterTarget(string svg)
        {
            string Bump(Match m)
            {
                string unit = m.Groups["unit"].Value;
                // % / em / ex aren't pixel-equivalents and would change
                // the resolved layout if we scaled them — skip those.
                if (!string.IsNullOrEmpty(unit) && unit != "px") return m.Value;
                if (!double.TryParse(m.Groups["v"].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double v)) return m.Value;
                double scaled = v * SvgRasterOversample;
                return scaled.ToString("0.###",
                    System.Globalization.CultureInfo.InvariantCulture) + unit;
            }
            svg = SvgRootWidthRx.Replace(svg, Bump);
            svg = SvgRootHeightRx.Replace(svg, Bump);
            return svg;
        }

        static bool LooksLikeStylesheet(string attrs)
        {
            var rel = Regex.Match(attrs, @"\brel\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (!rel.Success) return false;
            var tokens = rel.Groups[1].Value
                .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.ToLowerInvariant()).ToList();
            if (tokens.Contains("stylesheet")) return true;
            if (tokens.Contains("preload"))
            {
                var asAttr = Regex.Match(attrs, @"\bas\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
                if (asAttr.Success && asAttr.Groups[1].Value.Trim().ToLowerInvariant() == "style")
                    return true;
            }
            return false;
        }

        // Replace one specific attribute's value inside a tag string. Used so
        // the rest of the tag (other attributes, whitespace, casing) survives
        // intact — important for downstream parsers that may key off attr order.
        static string ReplaceAttrValue(string tag, string attrName, string newValue)
        {
            var rx = new Regex(
                @"\b" + Regex.Escape(attrName) + @"\s*=\s*([""'])[^""']*\1",
                RegexOptions.IgnoreCase);
            return rx.Replace(tag, attrName + "=\"" + newValue + "\"", 1);
        }

        static string ExtractTitle(string html)
        {
            var m = TitleRx.Match(html ?? string.Empty);
            if (!m.Success) return null;
            string raw = m.Groups["t"].Value.Trim();
            if (string.IsNullOrEmpty(raw)) return null;
            // crude entity decode — Chrome's title bar shows the decoded form.
            return System.Net.WebUtility.HtmlDecode(raw);
        }

        public static string SlugFromUri(Uri uri)
        {
            string host = uri.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
            string path = uri.AbsolutePath.Trim('/');
            string combined = string.IsNullOrEmpty(path) ? host : host + "_" + path;
            string slug = SafeName(combined);
            if (slug.Length > 60) slug = slug.Substring(0, 60);
            if (string.IsNullOrEmpty(slug)) slug = "page";
            return slug;
        }

        static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
                else if (c == '-' || c == '_') sb.Append(c);
                else if (sb.Length > 0 && sb[sb.Length - 1] != '_') sb.Append('_');
            }
            return sb.ToString().Trim('_');
        }
    }
}
