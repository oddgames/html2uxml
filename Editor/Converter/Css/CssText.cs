using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace ODDGames.Html2Uxml.Editor.Converter.Css
{
    // Low-level text utilities used by every CSS-value mapper. Mirrors the
    // small helpers at the top of html2uxml/mappings.py:
    //   _split_top_level_commas, _split_whitespace_top_level,
    //   _quote_for_uss, _extract_call_body, _find_gradient,
    //   _extract_gradient_body, _strip_unit, _ensure_unit
    public static class CssText
    {
        // CSS function regex used by _find_gradient.
        static readonly Regex GradFnRe = new Regex(
            @"\b(linear|radial|conic|repeating-linear|repeating-radial)-gradient\s*\(",
            RegexOptions.IgnoreCase);

        // Split a CSS value at top-level commas (respects parens + quoted strings).
        public static List<string> SplitTopLevelCommas(string value)
        {
            var parts = new List<string>();
            if (string.IsNullOrEmpty(value)) return parts;
            int depth = 0;
            int start = 0;
            char? quote = null;
            bool escaped = false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (quote.HasValue)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == quote.Value) quote = null;
                    continue;
                }
                if (c == '\'' || c == '"') quote = c;
                if (c == '(') depth++;
                else if (c == ')') depth = Math.Max(0, depth - 1);
                else if (c == ',' && depth == 0)
                {
                    string part = value.Substring(start, i - start).Trim();
                    if (part.Length > 0) parts.Add(part);
                    start = i + 1;
                }
            }
            string tail = value.Substring(start).Trim();
            if (tail.Length > 0) parts.Add(tail);
            return parts;
        }

        // Split a CSS value at top-level whitespace (respects parens + quoted strings).
        public static List<string> SplitWhitespaceTopLevel(string value)
        {
            var parts = new List<string>();
            if (string.IsNullOrEmpty(value)) return parts;
            int depth = 0;
            char? quote = null;
            var buf = new StringBuilder();
            foreach (char c in value)
            {
                if (quote.HasValue)
                {
                    buf.Append(c);
                    if (c == quote.Value) quote = null;
                    continue;
                }
                if (c == '\'' || c == '"') { quote = c; buf.Append(c); continue; }
                if (c == '(') { depth++; buf.Append(c); continue; }
                if (c == ')') { depth = Math.Max(0, depth - 1); buf.Append(c); continue; }
                if (char.IsWhiteSpace(c) && depth == 0)
                {
                    if (buf.Length > 0) { parts.Add(buf.ToString()); buf.Clear(); }
                    continue;
                }
                buf.Append(c);
            }
            if (buf.Length > 0) parts.Add(buf.ToString());
            return parts;
        }

        // Wrap a raw CSS value so the USS lexer accepts it as a string literal.
        public static string QuoteForUss(string value)
        {
            string inner = (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
            return "\"" + inner + "\"";
        }

        // Find `<fn_name>(...)` with balanced parens, return inner body or null.
        public static string ExtractCallBody(string value, string fnName)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(fnName)) return null;
            var pat = new Regex(Regex.Escape(fnName) + @"\s*\(", RegexOptions.IgnoreCase);
            var m = pat.Match(value);
            if (!m.Success) return null;
            int i = m.Index + m.Length;
            int depth = 1;
            int start = i;
            while (i < value.Length && depth > 0)
            {
                char c = value[i];
                if (c == '(') depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0) return value.Substring(start, i - start);
                }
                i++;
            }
            return null;
        }

        // Locate a `*gradient(...)` call. Returns (start, endExcl, text) or null.
        public static (int start, int endExcl, string text)? FindGradient(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            var m = GradFnRe.Match(value);
            if (!m.Success) return null;
            int start = m.Index;
            int parenIdx = value.IndexOf('(', m.Index + m.Length - 1);
            if (parenIdx < 0) return null;
            int i = parenIdx + 1;
            int depth = 1;
            while (i < value.Length && depth > 0)
            {
                char c = value[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                i++;
            }
            if (depth != 0) return null;
            return (start, i, value.Substring(start, i - start));
        }

        // Extract the body of a top-level function call (between first '(' and last ')').
        public static string ExtractGradientBody(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            int open = value.IndexOf('(');
            int close = value.LastIndexOf(')');
            if (open < 0 || close <= open) return null;
            return value.Substring(open + 1, close - open - 1);
        }

        // Drop a CSS length unit so the value parses as a plain number — used
        // for runtime CustomStyleProperty<float> bridges.
        public static string StripUnit(string n)
        {
            if (string.IsNullOrEmpty(n)) return n;
            string[] units = { "px", "rem", "em", "%" };
            foreach (var u in units)
            {
                if (n.EndsWith(u))
                {
                    string s = n.Substring(0, n.Length - u.Length);
                    return s.Length == 0 ? "0" : s;
                }
            }
            return n;
        }

        public static string EnsureUnit(string n)
        {
            if (string.IsNullOrEmpty(n)) return n;
            if (n == "0") return n;
            if (n.EndsWith("px") || n.EndsWith("em") || n.EndsWith("rem") || n.EndsWith("%")) return n;
            return n + "px";
        }
    }
}
