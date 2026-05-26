using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // Inline <svg>...</svg> blocks → sibling .svg asset + Html2UxmlElement
    // pointing at it via background-image. Mirrors converter.py:
    //   _emit_svg, _svg_dimensions, _svg_context_slug,
    //   _slugify_asset_label, EmitState.svg_filename
    //
    // HtmlLoader has already extracted raw <svg> markup into ParsedHtml.SvgBlocks
    // and replaced it with `<svg data-svg-id="N"></svg>` in the tree.
    public static class SvgEmitter
    {
        static readonly Regex SvgDimRe = new Regex(
            @"(?<![-A-Za-z0-9_])width\s*=\s*[""']([^""']+)[""']|"
          + @"(?<![-A-Za-z0-9_])height\s*=\s*[""']([^""']+)[""']|"
          + @"(?<![-A-Za-z0-9_])viewBox\s*=\s*[""']([^""']+)[""']");

        public static (double w, double h)? SvgDimensions(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            int headEnd = raw.IndexOf('>');
            string head = headEnd > 0 ? raw.Substring(0, headEnd) : raw;
            double? width = null, height = null, viewW = null, viewH = null;
            foreach (Match m in SvgDimRe.Matches(head))
            {
                if (m.Groups[1].Success) width  = ToFloat(m.Groups[1].Value);
                else if (m.Groups[2].Success) height = ToFloat(m.Groups[2].Value);
                else if (m.Groups[3].Success)
                {
                    var parts = m.Groups[3].Value.Replace(",", " ")
                        .Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 4)
                    {
                        viewW = ToFloat(parts[2]);
                        viewH = ToFloat(parts[3]);
                    }
                }
            }
            double? w = width  ?? viewW;
            double? h = height ?? viewH;
            if (!w.HasValue || !h.HasValue) return null;
            return (w.Value, h.Value);
        }

        static double? ToFloat(string s)
        {
            string t = (s ?? "").Trim();
            if (t.EndsWith("px")) t = t.Substring(0, t.Length - 2).Trim();
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : (double?)null;
        }

        public static string SvgContextSlug(HtmlLoader.HtmlNode node, HtmlLoader.HtmlNode parent)
        {
            HtmlLoader.HtmlNode[] sources = { parent, node };
            foreach (var src in sources)
            {
                if (src == null) continue;
                foreach (var key in new[] { "id", "name", "title", "aria-label" })
                {
                    string v = AttrUtil.Get(src.Attrs, key);
                    if (!string.IsNullOrEmpty(v)) return SlugifyAssetLabel(v);
                }
                string method = AttrUtil.Get(src.Attrs, "data-h2u-method");
                if (!string.IsNullOrEmpty(method))
                    return SlugifyAssetLabel(Regex.Replace(method, @"^On", ""));
                string clsAttr = AttrUtil.Get(src.Attrs, "class") ?? "";
                foreach (var cls in clsAttr.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries))
                    if (!string.IsNullOrEmpty(cls) && !cls.StartsWith("h2u-"))
                        return SlugifyAssetLabel(cls);
            }
            return "svg";
        }

        public static string SlugifyAssetLabel(string value)
        {
            if (string.IsNullOrEmpty(value)) return "svg";
            // camelCase → camel-Case
            string spaced = Regex.Replace(value, @"([a-z0-9])([A-Z])", "$1-$2");
            string slug = Regex.Replace(spaced, @"[^A-Za-z0-9]+", "-").Trim('-').ToLowerInvariant();
            if (slug.Length > 64) slug = slug.Substring(0, 64).TrimEnd('-');
            return slug.Length == 0 ? "svg" : slug;
        }
    }
}
