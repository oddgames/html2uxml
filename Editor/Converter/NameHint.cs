using System.Text;
using System.Text.RegularExpressions;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // Pick a human slug for a node's generated USS class. Mirrors:
    //   _resolve_node_name_hint, _slug_from_node_text, _slug_from_om_id
    //
    // Order: forced → data-h2u-name → id → name → first non-h2u/non-__om
    // class → direct-text slug → data-om-id slug.
    public static class NameHint
    {
        static readonly Regex OmIdRe = new Regex(@"([A-Za-z0-9_-]+)\.[A-Za-z0-9]+(:\d+(?::\d+)*)?$");

        public static string Resolve(HtmlLoader.HtmlNode node, string forced = null)
        {
            if (!string.IsNullOrEmpty(forced)) return forced;
            if (node?.Attrs == null) return null;
            string name = AttrUtil.Get(node.Attrs, "data-h2u-list")
                       ?? AttrUtil.Get(node.Attrs, "data-h2u-radio-group")
                       ?? AttrUtil.Get(node.Attrs, "data-h2u-name")
                       ?? AttrUtil.Get(node.Attrs, "id")
                       ?? AttrUtil.Get(node.Attrs, "name");
            if (string.IsNullOrEmpty(name))
            {
                string clsAttr = AttrUtil.Get(node.Attrs, "class") ?? "";
                foreach (var cls in clsAttr.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries))
                {
                    if (string.IsNullOrEmpty(cls)) continue;
                    if (cls.StartsWith("h2u-") || cls.StartsWith("__om-")) continue;
                    name = cls; break;
                }
            }
            if (string.IsNullOrEmpty(name)) name = SlugFromText(node);
            if (string.IsNullOrEmpty(name))
            {
                string om = AttrUtil.Get(node.Attrs, "data-om-id");
                if (!string.IsNullOrEmpty(om)) name = SlugFromOmId(om);
            }
            return string.IsNullOrEmpty(name) ? null : name;
        }

        public static string SlugFromText(HtmlLoader.HtmlNode node)
        {
            if (node == null || node.IsText || node.Children == null) return "";
            var sb = new StringBuilder();
            foreach (var child in node.Children)
            {
                if (!child.IsText || child.IsComment) continue;
                string text = (child.Text ?? "").Trim();
                if (text.Length > 0) { if (sb.Length > 0) sb.Append(' '); sb.Append(text); }
            }
            string raw = sb.ToString().Trim();
            if (raw.Length == 0) return "";
            if (raw.Length > 32) raw = raw.Substring(0, 32);
            string slug = Regex.Replace(raw, @"[^A-Za-z0-9]+", "-").Trim('-').ToLowerInvariant();
            return slug;
        }

        public static string SlugFromOmId(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string last = Regex.Replace(value.Trim(), @".*[/\\]", "");
            var match = OmIdRe.Match(last);
            string slug;
            if (match.Success)
            {
                string head = match.Groups[1].Value;
                string tail = (match.Groups[2].Success ? match.Groups[2].Value : "").Replace(":", "-");
                slug = head + tail;
            }
            else
            {
                slug = Regex.Replace(last, @"\.[A-Za-z0-9]+", "");
            }
            slug = Regex.Replace(slug, @"[^A-Za-z0-9_-]+", "-").Trim('-');
            return slug;
        }
    }
}
