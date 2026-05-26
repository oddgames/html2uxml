using System.Collections.Generic;

namespace ODDGames.Html2Uxml.Editor.Converter.Css
{
    // Merge filter declarations generated from multiple CSS properties so
    // Unity sees one combined `filter:` chain. Mirrors mappings._combine_filter_declarations.
    public static class CssFilterCombine
    {
        public static List<KeyValuePair<string, string>> CombineFilterDeclarations(
            List<KeyValuePair<string, string>> decls)
        {
            var combined = new List<KeyValuePair<string, string>>();
            var filterParts = new List<string>();
            int filterIndex = -1;
            foreach (var kv in decls)
            {
                if (kv.Key != "filter") { combined.Add(kv); continue; }
                if (filterIndex < 0)
                {
                    filterIndex = combined.Count;
                    combined.Add(new KeyValuePair<string, string>("filter", ""));
                }
                string v = (kv.Value ?? "").Trim();
                if (v.Length > 0) filterParts.Add(v);
            }
            if (filterIndex < 0) return combined;
            if (filterParts.Count == 0)
            {
                combined.RemoveAt(filterIndex);
                return combined;
            }
            combined[filterIndex] = new KeyValuePair<string, string>("filter", string.Join(" ", filterParts));
            return combined;
        }
    }
}
