using System.Collections.Generic;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // Per-tag mapping result. Used by ElementMap.MapElement / InputMap.MapInput.
    public enum TextHandling
    {
        Drop,           // no text on element
        AssignToText,   // text="" attribute on the UXML node
        WrapInLabel,    // emit child ui:Label with text content
    }

    public sealed class ElementMapping
    {
        public string UxmlType;
        public Dictionary<string, string> ExtraAttrs;
        public TextHandling TextHandling;

        public ElementMapping(string uxmlType, Dictionary<string, string> extraAttrs, TextHandling textHandling)
        {
            UxmlType = uxmlType;
            ExtraAttrs = extraAttrs ?? new Dictionary<string, string>();
            TextHandling = textHandling;
        }

        public ElementMapping Clone()
            => new ElementMapping(UxmlType, new Dictionary<string, string>(ExtraAttrs), TextHandling);
    }
}
