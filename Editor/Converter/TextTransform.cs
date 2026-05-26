using System.Globalization;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // text-transform / text-decoration application at emit time. Mirrors
    // converter._apply_text_transform plus the TMP rich-text wrapping
    // converter does inline when emitting <ui:Label text="…">. text-decoration
    // and text-transform are dropped by mappings.DROP_PROPS so they do not
    // appear in USS — they're consumed here instead.
    public static class TextTransform
    {
        public static string ApplyTransform(string text, string transform)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(transform)) return text;
            string t = transform.Trim().ToLowerInvariant();
            switch (t)
            {
                case "uppercase":  return text.ToUpperInvariant();
                case "lowercase":  return text.ToLowerInvariant();
                case "capitalize": return ToTitleCase(text);
                default: return text;
            }
        }

        public static string ApplyDecoration(string text, string decoration)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(decoration)) return text;
            string d = decoration.ToLowerInvariant();
            // Author can write `text-decoration: underline overline …`. TMP
            // supports <u>, <s>. Order tags inside-out so they nest cleanly.
            string outText = text;
            if (d.Contains("line-through")) outText = "<s>" + outText + "</s>";
            if (d.Contains("underline"))    outText = "<u>" + outText + "</u>";
            return outText;
        }

        // Python's str.title() lowercases every char then capitalises the
        // first letter of each word. C# TextInfo.ToTitleCase preserves
        // existing capitals; mimic Python behaviour.
        static string ToTitleCase(string text)
        {
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant());
        }
    }
}
