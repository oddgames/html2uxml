using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ODDGames.Html2Uxml.Editor.Converter.Css
{
    // CSS `flex` shorthand parsing. Mirrors mappings.py _map_flex_shorthand /
    // _is_flex_number / _is_flex_basis_token.
    public static class CssFlex
    {
        static readonly Regex FlexNumberRe = new Regex(@"^\d*\.?\d+$");
        static readonly HashSet<string> ContentKeywords = new HashSet<string>
        {
            "auto","content","min-content","max-content","fit-content",
        };
        static readonly string[] FuncPrefixes = { "calc(", "var(", "min(", "max(", "clamp(" };

        public static List<KeyValuePair<string, string>> MapFlexShorthand(string value, List<string> warnings)
        {
            string raw = (value ?? "").Trim();
            string low = raw.ToLowerInvariant();
            if (low == "none")
                return Pairs(("flex-grow", "0"), ("flex-shrink", "0"), ("flex-basis", "auto"));
            if (low == "auto")
                return Pairs(("flex-grow", "1"), ("flex-shrink", "1"), ("flex-basis", "auto"));
            if (low == "initial")
                return Pairs(("flex-grow", "0"), ("flex-shrink", "1"), ("flex-basis", "auto"));

            var parts = CssText.SplitWhitespaceTopLevel(raw);
            if (parts.Count == 0) return null;

            string grow = null, shrink = null, basis = null;

            if (parts.Count == 1)
            {
                string tok = parts[0];
                if (IsFlexNumber(tok)) { grow = tok; shrink = "1"; basis = "0"; }
                else if (IsFlexBasisToken(tok)) { grow = "1"; shrink = "1"; basis = tok; }
            }
            else if (parts.Count == 2 && IsFlexNumber(parts[0]))
            {
                grow = parts[0];
                if (IsFlexNumber(parts[1])) { shrink = parts[1]; basis = "0"; }
                else if (IsFlexBasisToken(parts[1])) { shrink = "1"; basis = parts[1]; }
            }
            else if (parts.Count == 3 && IsFlexNumber(parts[0]) && IsFlexNumber(parts[1]))
            {
                grow = parts[0];
                shrink = parts[1];
                if (IsFlexBasisToken(parts[2])) basis = parts[2];
            }

            if (grow == null || shrink == null || basis == null)
            {
                warnings.Add($"flex shorthand not supported, dropped: {value}");
                return null;
            }
            return Pairs(("flex-grow", grow), ("flex-shrink", shrink), ("flex-basis", basis));
        }

        public static bool IsFlexNumber(string token)
            => FlexNumberRe.IsMatch((token ?? "").Trim());

        public static bool IsFlexBasisToken(string token)
        {
            string t = (token ?? "").Trim().ToLowerInvariant();
            if (t.Length == 0) return false;
            if (ContentKeywords.Contains(t)) return true;
            foreach (var prefix in FuncPrefixes) if (t.StartsWith(prefix)) return true;
            return CssLength.LenRe.IsMatch(t);
        }

        static List<KeyValuePair<string, string>> Pairs(params (string, string)[] kv)
        {
            var l = new List<KeyValuePair<string, string>>(kv.Length);
            foreach (var (k, v) in kv) l.Add(new KeyValuePair<string, string>(k, v));
            return l;
        }
    }
}
