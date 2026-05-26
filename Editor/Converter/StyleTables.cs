using System.Collections.Generic;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // Static lookup tables used by the CSS-declaration mapper. Pure data;
    // mirrors the constant tables at the top of html2uxml/mappings.py.
    public static class StyleTables
    {
        // CSS keywords that mean "don't apply" — filter before mapping.
        public static readonly HashSet<string> SKIP_VALUES = new HashSet<string>
        {
            "unset","initial","inherit","revert","revert-layer",
        };

        // CSS properties USS does not support. Dropped with a warning.
        public static readonly HashSet<string> DROP_PROPS = new HashSet<string>
        {
            "mask","mask-type","appearance",
            "float","clear","box-sizing","user-select",
            "perspective","perspective-origin","transform-style",
            "backface-visibility","mix-blend-mode","background-blend-mode",
            "isolation","contain","will-change",
            "outline-offset","list-style","table-layout",
            "border-spacing","caption-side",
            "scroll-behavior","scroll-snap-type","scroll-snap-align",
            "touch-action","writing-mode","direction",
            "text-decoration",   // consumed by Label rich-text
            "text-transform",    // consumed by Label text pre-processing
            "text-indent","vertical-align",
            "word-break","overflow-wrap","hyphens",
        };

        // CSS cursor keyword -> USS cursor keyword.
        public static readonly Dictionary<string, string> CURSOR_MAP =
            new Dictionary<string, string>
            {
                {"default",   "arrow"},
                {"auto",      "arrow"},
                {"pointer",   "link"},
                {"text",      "text"},
                {"move",      "pan"},
                {"grab",      "pan"},
                {"grabbing",  "pan"},
                {"ns-resize", "resize-vertical"},
                {"ew-resize", "resize-horizontal"},
                {"n-resize",  "resize-vertical"},
                {"s-resize",  "resize-vertical"},
                {"e-resize",  "resize-horizontal"},
                {"w-resize",  "resize-horizontal"},
                {"zoom-in",   "zoom"},
                {"zoom-out",  "zoom"},
            };

        // object-fit -> -unity-background-scale-mode (only meaningful when an
        // element has a background-image rather than child Image content).
        public static readonly Dictionary<string, string> OBJECT_FIT_MAP =
            new Dictionary<string, string>
            {
                {"fill",       "stretch-to-fill"},
                {"cover",      "scale-and-crop"},
                {"contain",    "scale-to-fit"},
                {"scale-down", "scale-to-fit"},
                {"none",       "stretch-to-fill"},
            };

        // text-align -> -unity-text-align.
        public static readonly Dictionary<string, string> TEXT_ALIGN_MAP =
            new Dictionary<string, string>
            {
                {"left",    "middle-left"},
                {"center",  "middle-center"},
                {"right",   "middle-right"},
                {"start",   "middle-left"},
                {"end",     "middle-right"},
                {"justify", "middle-left"},
            };

        public static readonly HashSet<string> NAMED_COLORS = new HashSet<string>
        {
            "transparent","black","white","red","green","blue","yellow",
            "orange","purple","gray","grey","silver","maroon","navy",
            "teal","aqua","fuchsia","lime","olive","pink","brown","cyan",
            "magenta","gold","indigo","violet","tan","khaki","salmon",
            "crimson","coral","azure","beige","ivory","lavender","plum",
            "turquoise","wheat","snow",
        };
    }
}
