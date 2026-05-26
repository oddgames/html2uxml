using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ODDGames.Html2Uxml.Editor.Converter.Assets;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // Post-process USS: replace `--odd-font-family: "X"` (+ `--odd-font-weight`
    // + `-unity-font-style`) with `-unity-font-definition: url("…")` pointing
    // at the best matching downloaded font variant. Mirrors the rewrite path
    // in cli.py around `_font_definition_ref` and `_FF_RULE_RE`.
    public static class FontInjector
    {
        static readonly Regex FontFamilyDeclRe = new Regex(
            @"--odd-font-family\s*:\s*[""']?([^""';\}]+)[""']?\s*;", RegexOptions.IgnoreCase);
        static readonly Regex FontWeightDeclRe = new Regex(
            @"--odd-font-weight\s*:\s*([^;\}]+);", RegexOptions.IgnoreCase);
        static readonly Regex UnityFontStyleRe = new Regex(
            @"-unity-font-style\s*:\s*([^;\}]+);", RegexOptions.IgnoreCase);
        static readonly Regex RuleRe = new Regex(
            @"(?<head>[^{}]+)\{(?<body>[^{}]*)\}", RegexOptions.Singleline);
        // UXML element scanning — used to count text-bearing elements that
        // contribute "implicit weight 400" usages even when no per-element
        // USS rule spells out `--odd-font-weight`. Mirrors Python's font_usages
        // collection, which records (family, weight) tuples per text node.
        static readonly Regex UxmlTextElementRe = new Regex(
            @"<[\w:]+\b(?<attrs>[^>]*?)\btext\s*=\s*""(?<text>[^""]*)""(?<rest>[^>]*)/?>",
            RegexOptions.Singleline);
        static readonly Regex UxmlClassAttrRe = new Regex(
            @"\bclass\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);

        public static IEnumerable<string> CollectFamilies(string uss)
        {
            var seen = new HashSet<string>();
            if (string.IsNullOrEmpty(uss)) return seen;
            foreach (Match m in FontFamilyDeclRe.Matches(uss))
                seen.Add(m.Groups[1].Value.Trim());
            return seen;
        }

        // Rewrite each rule body that mentions --odd-font-family. Returns the
        // new USS. `mapping` maps font-family -> available variants (downloaded
        // .ttf paths under Fonts/<file>); empty mapping = synthesize the path
        // deterministically (so the USS still parses cleanly even if no
        // download happened).
        public static string Inject(string uss,
                                    Dictionary<string, List<FontVariant>> mapping,
                                    bool useTextcoreFontAssets,
                                    string uxml = null)
        {
            if (string.IsNullOrEmpty(uss)) return uss;
            string primaryFamily = FindPrimaryFamily(uss);
            // Pre-scan: highest authored weight in the document. Python's
            // font picker selects the heaviest variant for the body when
            // descendants use it, which keeps the inherited cascade weight
            // consistent across labels.
            int maxWeight = ScanMaxWeight(uss);
            // Per-family "available downloaded weights" approximation. Python's
            // `_select_font_variant` picks the closest weight from the variants
            // that were actually downloaded (driven by `font_usages` — the set
            // of (family, weight, italic) tuples gathered from text-bearing
            // elements). When a body rule has no explicit weight (desired=400)
            // but only weight 700 was downloaded, Python returns 700, not 400.
            // Mirror that: build the wanted-weights set from the USS's explicit
            // `--odd-font-weight` declarations plus the implicit-400 usages
            // contributed by text-bearing UXML elements that lack any per-class
            // explicit weight. Then pick the closest available weight when the
            // current rule itself has no explicit weight.
            var availableWeights = ScanAvailableWeights(uss, uxml);
            return RuleRe.Replace(uss, match =>
            {
                string head = match.Groups["head"].Value;
                string body = match.Groups["body"].Value;
                bool hasFamily = FontFamilyDeclRe.IsMatch(body);
                bool hasWeight = FontWeightDeclRe.IsMatch(body);
                bool hasBold   = false;
                var fsm = UnityFontStyleRe.Match(body);
                if (fsm.Success && fsm.Groups[1].Value.ToLowerInvariant().Contains("bold"))
                    hasBold = true;
                if (!hasFamily && !hasWeight && !hasBold) return match.Value;
                string family = hasFamily
                    ? FontFamilyDeclRe.Match(body).Groups[1].Value.Trim()
                    : primaryFamily;
                if (string.IsNullOrEmpty(family)) return match.Value;
                int desiredWeight = ReadWeight(body);
                bool desiredItalic = ReadItalic(body);
                // No explicit weight on this rule? Snap desired_weight to the
                // closest weight that's actually present in the document. This
                // matches Python: a body with no explicit weight inherits the
                // closest downloaded variant, which can be 700 if 400 was
                // never authored anywhere.
                if (!hasWeight && !hasBold && availableWeights.TryGetValue(family, out var weights) && weights.Count > 0)
                {
                    desiredWeight = ClosestWeight(weights, 400);
                }
                string ttfPath = SelectVariant(family, desiredWeight, desiredItalic, mapping);
                if (string.IsNullOrEmpty(ttfPath))
                    ttfPath = SynthesizeFontPath(family, desiredWeight, desiredItalic);
                string finalPath = useTextcoreFontAssets ? ToSdfAsset(ttfPath) : ttfPath;
                string newBody = RewriteBody(body, finalPath, hasFamily);
                return head + "{" + newBody + "}";
            });
        }

        static int ScanMaxWeight(string uss)
        {
            int max = 400;
            foreach (Match m in FontWeightDeclRe.Matches(uss))
            {
                string raw = m.Groups[1].Value.Trim().ToLowerInvariant();
                if (raw == "bold" || raw == "bolder") { if (700 > max) max = 700; continue; }
                if (raw == "normal" || raw == "lighter") continue;
                if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > max)
                    max = n;
            }
            return max;
        }

        // Build per-family "available weights" — the set of weights that
        // Python's downloader would have fetched, given the same document.
        // Sources:
        //   1. Every explicit `--odd-font-weight` in any USS rule that also
        //      mentions `--odd-font-family` (or inherits the primary family).
        //   2. Implicit weight 400 from rules that mention `--odd-font-family`
        //      but NO explicit weight, AND aren't the body/inheritance carrier
        //      (i.e. there's at least one OTHER such rule, OR a text-bearing
        //      UXML element exists whose USS class has no explicit weight).
        static Dictionary<string, HashSet<int>> ScanAvailableWeights(string uss, string uxml)
        {
            var explicitWeights = new Dictionary<string, HashSet<int>>(System.StringComparer.OrdinalIgnoreCase);
            // implicitRuleCount[family] = number of rules with --odd-font-family
            // but no explicit --odd-font-weight. The body rule alone won't
            // count toward the wanted set — only rules >= 2 will pull 400 in.
            var implicitRuleCount = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
            string primaryFamily = FindPrimaryFamily(uss);
            // Class names on per-element USS rules that explicitly set a weight.
            // Used to decide whether a UXML text element's class is "explicit".
            var classesWithExplicitWeight = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            foreach (Match m in RuleRe.Matches(uss))
            {
                string head = m.Groups["head"].Value;
                string body = m.Groups["body"].Value;
                bool hasFamily = FontFamilyDeclRe.IsMatch(body);
                bool hasWeight = FontWeightDeclRe.IsMatch(body);
                bool hasBold = false;
                var fsm = UnityFontStyleRe.Match(body);
                if (fsm.Success && fsm.Groups[1].Value.ToLowerInvariant().Contains("bold"))
                    hasBold = true;
                if (!hasFamily && !hasWeight && !hasBold) continue;
                string family = hasFamily
                    ? FontFamilyDeclRe.Match(body).Groups[1].Value.Trim()
                    : primaryFamily;
                if (string.IsNullOrEmpty(family)) continue;
                if (hasWeight || hasBold)
                {
                    int w = ReadWeight(body);
                    if (hasBold && !hasWeight) w = 700;
                    if (!explicitWeights.TryGetValue(family, out var set))
                        explicitWeights[family] = set = new HashSet<int>();
                    set.Add(w);
                    foreach (string cls in ExtractSelectorClasses(head))
                        classesWithExplicitWeight.Add(cls);
                }
                else
                {
                    if (!implicitRuleCount.TryGetValue(family, out int c)) c = 0;
                    implicitRuleCount[family] = c + 1;
                }
            }

            // Scan UXML for text-bearing elements that don't already carry a
            // class with explicit weight. Each such element contributes an
            // implicit-400 usage to the inherited family (assumed to be the
            // primary). Mirrors Python's `_font_usage_from_text` records.
            // Skip elements without any `class=...` attribute — those are
            // typically widget-synthesized labels (e.g. `<ui:Label text="72%"/>`
            // inside a `<meter>`) which don't appear in Python's font_usages.
            int implicitUxmlContributors = 0;
            if (!string.IsNullOrEmpty(uxml))
            {
                foreach (Match m in UxmlTextElementRe.Matches(uxml))
                {
                    string text = m.Groups["text"].Value;
                    if (string.IsNullOrEmpty(text)) continue;
                    string attrs = m.Groups["attrs"].Value + m.Groups["rest"].Value;
                    var classMatch = UxmlClassAttrRe.Match(attrs);
                    if (!classMatch.Success) continue;
                    bool hasExplicit = false;
                    foreach (string cls in classMatch.Groups[1].Value.Split(' '))
                    {
                        string trimmed = cls.Trim();
                        if (trimmed.Length == 0) continue;
                        if (classesWithExplicitWeight.Contains(trimmed)) { hasExplicit = true; break; }
                    }
                    if (!hasExplicit) implicitUxmlContributors++;
                }
            }

            // Merge: implicit weight 400 lands in the available set when more
            // than one rule contributes implicitly, OR when at least one
            // text-bearing UXML element with no explicit weight class exists.
            var merged = new Dictionary<string, HashSet<int>>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var kv in explicitWeights)
                merged[kv.Key] = new HashSet<int>(kv.Value);
            foreach (var kv in implicitRuleCount)
            {
                bool include400 = kv.Value >= 2 || implicitUxmlContributors >= 1;
                // Body rule + no other content => fall back to 400 anyway, so
                // single-rule implicit also gets 400 when explicit set is empty.
                if (!merged.TryGetValue(kv.Key, out var set))
                    merged[kv.Key] = set = new HashSet<int>();
                if (include400 || set.Count == 0) set.Add(400);
            }
            return merged;
        }

        static IEnumerable<string> ExtractSelectorClasses(string selectorHead)
        {
            // Pull `.foo` tokens out of a selector. Compound selectors and
            // descendant combinators are fine — every `.token` is yielded.
            foreach (Match m in Regex.Matches(selectorHead, @"\.([A-Za-z0-9_-]+)"))
                yield return m.Groups[1].Value;
        }

        static int ClosestWeight(HashSet<int> weights, int desired)
        {
            int best = desired;
            int bestScore = int.MaxValue;
            foreach (int w in weights)
            {
                int score = System.Math.Abs(w - desired) * 10 + (w <= desired ? 0 : 1);
                if (score < bestScore) { bestScore = score; best = w; }
            }
            return best;
        }

        static string FindPrimaryFamily(string uss)
        {
            var m = FontFamilyDeclRe.Match(uss);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        static int ReadWeight(string body)
        {
            var m = FontWeightDeclRe.Match(body);
            if (!m.Success) return 400;
            string raw = m.Groups[1].Value.Trim().ToLowerInvariant();
            if (raw == "bold" || raw == "bolder") return 700;
            if (raw == "normal" || raw == "lighter") return 400;
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 400;
        }

        static bool ReadItalic(string body)
        {
            var m = UnityFontStyleRe.Match(body);
            if (!m.Success) return false;
            string v = m.Groups[1].Value.Trim().ToLowerInvariant();
            return v.Contains("italic");
        }

        static string SelectVariant(string family, int weight, bool italic,
                                    Dictionary<string, List<FontVariant>> mapping)
        {
            if (mapping == null || !mapping.TryGetValue(family, out var variants) || variants.Count == 0)
                return null;
            FontVariant best = null;
            int bestScore = int.MaxValue;
            foreach (var v in variants)
            {
                int score = System.Math.Abs(v.Weight - weight) * 10 + (v.Italic == italic ? 0 : 5);
                if (score < bestScore) { bestScore = score; best = v; }
            }
            return best?.Path;
        }

        // `Fonts/Arial-700.ttf` style — matches the Python downloader's naming.
        static string SynthesizeFontPath(string family, int weight, bool italic)
        {
            string slug = SafeFamily(family);
            string suffix = italic ? "-italic" : "";
            return $"Fonts/{slug}-{weight}{suffix}.ttf";
        }

        static string SafeFamily(string family)
        {
            var sb = new StringBuilder();
            foreach (char c in family)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (c == ' ' || c == '-') sb.Append(c);
            }
            return sb.ToString().Trim().Replace(' ', '-');
        }

        static string ToSdfAsset(string ttfPath)
        {
            // `Fonts/Arial-700.ttf` -> `Fonts/Arial-700 SDF.asset`
            int slash = ttfPath.LastIndexOf('/');
            string dir = slash >= 0 ? ttfPath.Substring(0, slash + 1) : "";
            string file = slash >= 0 ? ttfPath.Substring(slash + 1) : ttfPath;
            int dot = file.LastIndexOf('.');
            string stem = dot >= 0 ? file.Substring(0, dot) : file;
            return dir + stem + " SDF.asset";
        }

        // Read the weight encoded in the font filename. Picks up patterns
        // like `Foo-700.ttf`, `Foo-900-Italic.ttf`, and the SDF variants
        // `Foo-900-Italic SDF.asset`. Used so RewriteBody can decide
        // whether `-unity-font-style: bold-and-italic` would double-bold
        // a font file that already encodes the heavy weight.
        static int FontRefWeight(string fontRef)
        {
            if (string.IsNullOrEmpty(fontRef)) return 400;
            var m = Regex.Match(fontRef,
                @"-(\d{3})(?:-italic)?(?:\.ttf| SDF\.asset)",
                RegexOptions.IgnoreCase);
            if (m.Success && int.TryParse(m.Groups[1].Value, out var n)) return n;
            return 400;
        }

        static string RewriteBody(string body, string fontRef, bool hadFamilyDecl)
        {
            string out_ = body;
            if (hadFamilyDecl)
            {
                out_ = FontFamilyDeclRe.Replace(out_, m =>
                {
                    string indent = LeadingWhitespace(m.Value);
                    return $"{indent}-unity-font-definition: url(\"{fontRef}\");";
                });
            }
            else
            {
                // No --odd-font-family in this rule — inject a font-def line
                // ahead of the first font-style/weight decl so Unity picks up
                // the inherited family with the chosen variant.
                var anchor = UnityFontStyleRe.Match(out_);
                if (!anchor.Success) anchor = FontWeightDeclRe.Match(out_);
                if (anchor.Success)
                {
                    string indent = LeadingWhitespace(anchor.Value);
                    string injected = $"{indent}-unity-font-definition: url(\"{fontRef}\");\n";
                    out_ = out_.Substring(0, anchor.Index) + injected + out_.Substring(anchor.Index);
                }
            }
            out_ = FontWeightDeclRe.Replace(out_, "");
            int variantWeight = FontRefWeight(fontRef);
            bool variantIsHeavy = variantWeight >= 700;
            out_ = UnityFontStyleRe.Replace(out_, m =>
            {
                string raw = m.Groups[1].Value.Trim().ToLowerInvariant();
                bool isItalic = raw.Contains("italic");
                bool isBold = raw.Contains("bold");
                // Variant file already encodes the heavy weight — keeping
                // `-unity-font-style: bold` here makes Unity faux-bold a
                // font that's already 700+ and produces an unreadable smear.
                if (isBold && variantIsHeavy)
                {
                    return isItalic ? m.Value.Replace("bold-and-italic", "italic") : "";
                }
                if (isBold && !isItalic) return ""; // variant absorbed bold
                return m.Value;
            });
            out_ = Regex.Replace(out_, @"^\s*\r?\n", "", RegexOptions.Multiline);
            return out_;
        }

        static string LeadingWhitespace(string s)
        {
            int i = 0;
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t')) i++;
            return s.Substring(0, i);
        }
    }
}
