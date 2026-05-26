using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ODDGames.Html2Uxml.Editor.Converter.Css
{
    public sealed class MapResult
    {
        public List<KeyValuePair<string, string>> Decls;
        public List<string> Warnings;

        public MapResult()
        {
            Decls = new List<KeyValuePair<string, string>>();
            Warnings = new List<string>();
        }
    }

    // Entry point — port of mappings.map_declarations + the giant _map_one
    // dispatch. Translates a list of (prop, value) CSS pairs into USS pairs,
    // routing per-property paths to the topic helpers in this folder.
    public static class StyleMapper
    {
        const string TextRasterWeightCompensation = "0.1px";

        // When true, every coloured rule gets a 0.1px same-coloured outline
        // to match Chrome's heavier DirectWrite rasterization. When the
        // converter targets raw TTF refs (useTextcoreFontAssets=false),
        // Unity's font rasterizer is already weighted similarly, so the
        // outline just paints glyphs too bold and ruins readability.
        // Set by the Pipeline before MapDeclarations runs.
        public static bool ApplyChromeRasterCompensation = false;

        static readonly HashSet<string> PassThroughProps = new HashSet<string>
        {
            "all",
            "color","opacity","background-color",
            "background-position","background-position-x","background-position-y",
            "background-repeat","background-size",
            "width","height","min-width","min-height","max-width","max-height",
            "left","right","top","bottom",
            "padding","padding-top","padding-right","padding-bottom","padding-left",
            "margin","margin-top","margin-right","margin-bottom","margin-left",
            "border-width","border-top-width","border-right-width","border-bottom-width","border-left-width",
            "border-color","border-top-color","border-right-color","border-bottom-color","border-left-color",
            "border-top-left-radius","border-top-right-radius","border-bottom-left-radius","border-bottom-right-radius",
            "flex-grow","flex-shrink","flex-basis","flex-direction","flex-wrap",
            "align-items","align-self","align-content",
            "justify-content","justify-self",
            "visibility",
            "letter-spacing","font-size","word-spacing",
            "aspect-ratio",
            "text-shadow",
            "transition","transition-property",
            "transition-duration","transition-delay","transition-timing-function",
            "translate","rotate","scale",
            "transform-origin",
        };

        static readonly HashSet<string> VendorKeep = new HashSet<string>
        {
            "-webkit-backdrop-filter","-webkit-mask-image",
            "-webkit-text-stroke","-webkit-text-stroke-width","-webkit-text-stroke-color",
        };

        static readonly Regex AutoNormalNoneSafeProps = new Regex(@"^.*$");
        static readonly HashSet<string> AutoNormalSkipExceptions = new HashSet<string>
        {
            "display","overflow","white-space","visibility",
            "margin","margin-top","margin-right","margin-bottom","margin-left",
            "width","height","min-width","max-width","min-height","max-height",
            "left","right","top","bottom",
            "pointer-events",
        };

        static readonly Regex FontPxOrRel = new Regex(@"(\d+(?:\.\d+)?(px|em|%))");
        static readonly Regex BorderImageNumRe = new Regex(@"-?\d*\.?\d+");
        static readonly Regex UrlRe = new Regex(@"url\([^)]+\)");
        static readonly Regex IntRe = new Regex(@"^-?\d+$");

        // Public entry — ported from `map_declarations`.
        public static MapResult MapDeclarations(IList<KeyValuePair<string, string>> decls)
        {
            var result = new MapResult();
            var warnings = result.Warnings;

            // Normalise: trim, em/rem→px, modern color slash → comma form.
            var normalized = new List<KeyValuePair<string, string>>(decls.Count);
            foreach (var kv in decls)
            {
                string v = CssColor.CoerceModernColor(CssLength.CoerceUnits((kv.Value ?? "").Trim()));
                // UI Toolkit's USS reader stores calc()/min()/max()/clamp()
                // as Enum-typed values and then trips ApplyGlobalKeyword with
                // an IndexOutOfRange when the property expects a Dimension.
                // Collapse the function to its first concrete dimension token
                // so layout still gets a number to work with. CSS variables
                // (var(--x)) are left alone — Unity resolves them at apply.
                v = SimplifyMathFuncs(v, warnings, kv.Key);
                normalized.Add(new KeyValuePair<string, string>(kv.Key, v));
            }

            var (patternDecls, patternSkip, patternRewrites) =
                CssBackground.ExtractBackgroundPatternDecls(normalized, warnings);

            bool isBold = false, isItalic = false, hasFontStyleDecl = false;
            bool fromDisplayBlock = false;
            string textAlignFlexJustify = null;

            var inputProps = new HashSet<string>();
            foreach (var kv in normalized) inputProps.Add(kv.Key.ToLowerInvariant());

            // CSS default for display:flex is row; USS default is column. Backfill.
            bool needsRowDefault = false;
            if (inputProps.Contains("display") && !inputProps.Contains("flex-direction"))
            {
                foreach (var kv in decls)
                {
                    if (kv.Key.ToLowerInvariant() != "display") continue;
                    string lv = (kv.Value ?? "").Trim().ToLowerInvariant();
                    if (lv == "flex" || lv == "inline-flex") { needsRowDefault = true; break; }
                }
            }

            var outDecls = new List<KeyValuePair<string, string>>();

            for (int idx = 0; idx < normalized.Count; idx++)
            {
                if (patternSkip.Contains(idx)) continue;
                string prop = normalized[idx].Key;
                string v = patternRewrites.TryGetValue(idx, out var rew) ? rew : normalized[idx].Value;
                if (string.IsNullOrEmpty(v) || StyleTables.SKIP_VALUES.Contains(v.ToLowerInvariant())) continue;
                string vlow = v.ToLowerInvariant();
                if ((vlow == "auto" || vlow == "normal" || vlow == "none") && !AutoNormalSkipExceptions.Contains(prop))
                    continue;

                var oneResult = MapOne(prop, v, warnings);
                if (oneResult == null) continue;
                foreach (var kv in oneResult)
                {
                    if (kv.Key == "__bold__")        { isBold = true; hasFontStyleDecl = true; }
                    else if (kv.Key == "__italic__") { isItalic = true; hasFontStyleDecl = true; }
                    else if (kv.Key == "__font-normal__") { hasFontStyleDecl = true; }
                    else if (kv.Key == "__text-align-flex-justify__") { textAlignFlexJustify = kv.Value; }
                    else if (kv.Key == "__block-flex__") { fromDisplayBlock = true; }
                    else outDecls.Add(kv);
                }
            }

            outDecls.AddRange(patternDecls);

            if (hasFontStyleDecl)
            {
                if (isBold && isItalic) outDecls.Add(new KeyValuePair<string, string>("-unity-font-style", "bold-and-italic"));
                else if (isBold)        outDecls.Add(new KeyValuePair<string, string>("-unity-font-style", "bold"));
                else if (isItalic)      outDecls.Add(new KeyValuePair<string, string>("-unity-font-style", "italic"));
                else                    outDecls.Add(new KeyValuePair<string, string>("-unity-font-style", "normal"));
            }

            if (needsRowDefault)
            {
                bool already = false;
                foreach (var kv in outDecls) if (kv.Key == "flex-direction") { already = true; break; }
                if (!already) outDecls.Add(new KeyValuePair<string, string>("flex-direction", "row"));
            }

            outDecls = CssFilterCombine.CombineFilterDeclarations(outDecls);

            // Deduplicate keeping last occurrence.
            var seen = new Dictionary<string, string>();
            var seenOrder = new List<string>();
            foreach (var kv in outDecls)
            {
                if (!seen.ContainsKey(kv.Key)) seenOrder.Add(kv.Key);
                seen[kv.Key] = kv.Value;
            }
            if (seen.ContainsKey("--odd-clip-polygon") && seen.ContainsKey("background-color"))
            {
                seen["--odd-background-color"] = seen["background-color"];
                seen.Remove("background-color");
                seenOrder.Remove("background-color");
                if (!seenOrder.Contains("--odd-background-color")) seenOrder.Add("--odd-background-color");
            }

            // text-align justification fallback for flex containers.
            if (!seen.ContainsKey("justify-content"))
            {
                bool isFlex = (seen.TryGetValue("display", out var d) && d == "flex") || seen.ContainsKey("flex-direction");
                if (isFlex)
                {
                    string justify = null;
                    if (seen.TryGetValue("-unity-text-align", out var ta))
                    {
                        switch (ta)
                        {
                            case "middle-center": justify = "center"; break;
                            case "middle-right":  justify = "flex-end"; break;
                            case "middle-left":   justify = "flex-start"; break;
                        }
                    }
                    if (textAlignFlexJustify != null) justify = textAlignFlexJustify;
                    if (justify != null)
                    {
                        seen["justify-content"] = justify;
                        if (!seenOrder.Contains("justify-content")) seenOrder.Add("justify-content");
                    }
                }
            }

            CssBoxSizing.DropInertUnitySlices(seen, normalized);
            CssBoxSizing.ApplyContentBoxSizing(seen, normalized);
            CssBorder.ClampOversizedBorderRadii(seen, warnings);
            ApplyTextRasterWeightCompensation(seen, seenOrder);
            ApplyBlockStretch(seen, seenOrder, fromDisplayBlock);
            ApplyMaxWidthStretch(seen, seenOrder);

            foreach (var k in seenOrder)
                if (seen.ContainsKey(k))
                    result.Decls.Add(new KeyValuePair<string, string>(k, seen[k]));
            return result;
        }

        // CSS rules like `.foo { max-width: 688px; margin: 0 auto; }` rely
        // on the block-default `width: auto` filling the parent's inline axis
        // up to the max-width cap, with `margin: auto` centering the leftover
        // space. UI Toolkit treats `width: auto` as content-sized, so the
        // element collapses to its children's preferred width and the cap
        // never engages. Backfill width:100% so max-width bounds + auto
        // margins work as authored.
        static void ApplyMaxWidthStretch(Dictionary<string, string> seen, List<string> seenOrder)
        {
            if (!seen.ContainsKey("max-width")) return;
            if (seen.ContainsKey("width") || seen.ContainsKey("min-width")) return;
            if (seen.ContainsKey("flex") || seen.ContainsKey("flex-grow") || seen.ContainsKey("flex-basis")) return;
            if (seen.ContainsKey("position"))
            {
                string p = (seen["position"] ?? "").Trim().ToLowerInvariant();
                if (p == "absolute" || p == "fixed") return;
            }
            seen["width"] = "100%";
            if (!seenOrder.Contains("width")) seenOrder.Add("width");
        }

        // CSS `display: block` makes a child fill its parent's inline axis by
        // default. UI Toolkit's flex children shrink to content unless the
        // cross-axis size is set. When a rule maps `display:block` to flex
        // column without an explicit width/min-width/max-width or grow rule,
        // synthesize width:100% so block-level children stop collapsing.
        static void ApplyBlockStretch(
            Dictionary<string, string> seen, List<string> seenOrder, bool fromDisplayBlock)
        {
            if (!fromDisplayBlock) return;
            if (seen.ContainsKey("width") || seen.ContainsKey("min-width")
                || seen.ContainsKey("flex-grow") || seen.ContainsKey("flex")
                || seen.ContainsKey("align-self") || seen.ContainsKey("position"))
                return;
            seen["width"] = "100%";
            if (!seenOrder.Contains("width")) seenOrder.Add("width");
        }

        static void ApplyTextRasterWeightCompensation(Dictionary<string, string> seen, List<string> seenOrder)
        {
            if (!ApplyChromeRasterCompensation) return;
            if (!seen.TryGetValue("color", out var color) || string.IsNullOrWhiteSpace(color))
                return;
            if (seen.ContainsKey("-unity-text-outline-width") || seen.ContainsKey("-unity-text-outline-color"))
                return;

            // Chrome's DirectWrite rasterizer lands a touch heavier than UI Toolkit's
            // TextCore path for the same TTF. A tiny same-color outline matches that
            // render weight without changing glyph metrics or layout.
            seen["-unity-text-outline-width"] = TextRasterWeightCompensation;
            seen["-unity-text-outline-color"] = color;
            if (!seenOrder.Contains("-unity-text-outline-width")) seenOrder.Add("-unity-text-outline-width");
            if (!seenOrder.Contains("-unity-text-outline-color")) seenOrder.Add("-unity-text-outline-color");
        }

        // Per-property dispatch — port of `_map_one`.
        public static List<KeyValuePair<string, string>> MapOne(string prop, string value, List<string> warnings)
        {
            if (!VendorKeep.Contains(prop)
                && (prop.StartsWith("-webkit-") || prop.StartsWith("-moz-") || prop.StartsWith("-ms-")))
                return null;
            if (prop == "text-decoration" || prop == "text-transform") return null;
            if (prop == "animation" || prop.StartsWith("animation-"))
                return new List<KeyValuePair<string, string>>();
            if (prop == "z-index") return null;
            if (prop == "pointer-events")
            {
                string v = value.Trim().ToLowerInvariant();
                if (v == "none") return Pair("__picking-mode__", "Ignore");
                return null;
            }
            if (prop == "backdrop-filter" || prop == "-webkit-backdrop-filter")
                return CssFilter.MapBackdropFilter(value, warnings);
            if (prop == "mask-image" || prop == "-webkit-mask-image")
                return CssFilter.MapMaskImage(prop, value, warnings);
            if (prop == "border-collapse")
                return new List<KeyValuePair<string, string>>();
            if (StyleTables.DROP_PROPS.Contains(prop))
            {
                warnings.Add($"unsupported in USS, dropped: {prop}: {value}");
                return null;
            }

            if (prop == "display")
            {
                string vd = value.ToLowerInvariant();
                if (vd == "none") return Pair("display", "none");
                if (vd == "flex") return Pair("display", "flex");
                if (vd == "inline-flex") return Pairs(("display", "flex"), ("flex-direction", "row"));
                if (vd == "block" || vd == "list-item")
                    // Marker prop `__block-flex__` is filtered out before emit;
                    // BlockStretchSynthesis uses it to know which flex columns
                    // originated from `display:block` so it can synthesize
                    // width:100% (CSS block fills its inline axis by default,
                    // UI Toolkit flex children don't unless told to).
                    return Pairs(("display", "flex"), ("flex-direction", "column"), ("__block-flex__", "1"));
                if (vd == "inline" || vd == "inline-block") return Pairs(("display", "flex"), ("flex-direction", "row"));
                if (vd.StartsWith("grid") || vd.StartsWith("table") || vd == "contents" || vd == "ruby")
                {
                    warnings.Add($"display: {value} approximated as flex");
                    return Pair("display", "flex");
                }
                return Pair("display", "flex");
            }

            if (prop == "text-align")
            {
                string vt = value.ToLowerInvariant();
                StyleTables.TEXT_ALIGN_MAP.TryGetValue(vt, out var aligned);
                var outDecls = Pair("-unity-text-align", aligned ?? "middle-left");
                if (vt == "justify") outDecls.Add(new KeyValuePair<string, string>("__text-align-flex-justify__", "space-between"));
                return outDecls;
            }

            if (prop == "font-weight")
            {
                string vw = value.ToLowerInvariant();
                int? n = null;
                if (int.TryParse(vw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) n = parsed;
                var outDecls = new List<KeyValuePair<string, string>>();
                if (n.HasValue) outDecls.Add(new KeyValuePair<string, string>("--odd-font-weight", n.Value.ToString(CultureInfo.InvariantCulture)));
                else if (vw == "bold" || vw == "bolder") outDecls.Add(new KeyValuePair<string, string>("--odd-font-weight", "700"));
                else if (vw == "normal" || vw == "lighter") outDecls.Add(new KeyValuePair<string, string>("--odd-font-weight", "400"));
                if (vw == "bold" || vw == "bolder" || (n.HasValue && n.Value >= 600))
                    outDecls.Add(new KeyValuePair<string, string>("__bold__", "1"));
                else
                    outDecls.Add(new KeyValuePair<string, string>("__font-normal__", "0"));
                return outDecls;
            }
            if (prop == "font-style")
            {
                string vfs = value.ToLowerInvariant();
                if (vfs == "italic" || vfs == "oblique") return Pair("__italic__", "1");
                return Pair("__font-normal__", "0");
            }

            if (prop == "font-family")
            {
                string first = value.Split(new[] { ',' }, 2)[0].Trim().Trim('"').Trim('\'');
                if (string.IsNullOrEmpty(first)) return null;
                string lf = first.ToLowerInvariant();
                if (lf == "serif" || lf == "sans-serif" || lf == "monospace" || lf == "cursive" || lf == "fantasy"
                    || lf == "system-ui" || lf == "ui-serif" || lf == "ui-sans-serif" || lf == "ui-monospace"
                    || lf == "ui-rounded" || lf == "math" || lf == "emoji" || lf == "fangsong")
                    return null;
                return Pair("--odd-font-family", "\"" + first + "\"");
            }

            if (prop == "font")
            {
                var m = FontPxOrRel.Match(value);
                var outDecls = new List<KeyValuePair<string, string>>();
                if (m.Success) outDecls.Add(new KeyValuePair<string, string>("font-size", m.Groups[1].Value));
                warnings.Add("font shorthand split partially; specify font-size/-unity-font-definition explicitly");
                return outDecls.Count > 0 ? outDecls : null;
            }

            if (prop == "background")        return CssBackground.MapBackgroundLayers(value, warnings);
            if (prop == "background-image")  return CssBackground.MapBackgroundLayers(value, warnings);
            if (prop == "background-color")  return Pair("background-color", value);

            if (prop == "border") return CssBorder.SplitBorder(value, new[] { "top", "right", "bottom", "left" });
            if (prop == "border-top" || prop == "border-right" || prop == "border-bottom" || prop == "border-left")
                return CssBorder.SplitBorder(value, new[] { prop.Substring("border-".Length) });
            if (prop == "border-width" || prop == "border-top-width" || prop == "border-right-width"
             || prop == "border-bottom-width" || prop == "border-left-width")
                return CssBorder.MapBorderWidthProperty(prop, value);
            if (prop == "border-radius") return CssBorder.MapBorderRadius(value, warnings);
            if (prop == "border-top-left-radius" || prop == "border-top-right-radius"
             || prop == "border-bottom-right-radius" || prop == "border-bottom-left-radius")
                return Pair(prop, CssBorder.FirstRadiusComponent(value, warnings));

            if (prop == "cursor")
            {
                if (value.Contains("url(") || value.Contains("resource(")) return Pair("cursor", value);
                var toks = value.Split((char[])null, System.StringSplitOptions.RemoveEmptyEntries);
                string first = toks.Length > 0 ? toks[0].ToLowerInvariant() : "";
                if (StyleTables.CURSOR_MAP.TryGetValue(first, out var mapped)) return Pair("cursor", mapped);
                warnings.Add($"cursor: {value} -- no USS keyword equivalent, dropped");
                return null;
            }

            if (prop == "position")
            {
                string vp = value.ToLowerInvariant();
                if (vp == "relative" || vp == "absolute" || vp == "initial" || vp == "static")
                    return Pair("position", vp == "static" ? "relative" : vp);
                if (vp == "fixed" || vp == "sticky")
                {
                    warnings.Add($"position: {value} approximated as absolute");
                    return Pair("position", "absolute");
                }
                return null;
            }

            if (prop == "overflow")
            {
                string vo = value.ToLowerInvariant();
                if (vo == "hidden" || vo == "visible") return Pair("overflow", vo);
                if (vo == "auto" || vo == "scroll" || vo == "clip")
                {
                    warnings.Add($"overflow: {value} approximated as hidden (use a ScrollView for scroll)");
                    return Pair("overflow", "hidden");
                }
                return null;
            }
            if (prop == "overflow-x" || prop == "overflow-y")
            {
                warnings.Add($"{prop} not supported in USS; use overflow");
                return null;
            }

            if (prop == "object-fit")
            {
                string vo = value.ToLowerInvariant();
                if (StyleTables.OBJECT_FIT_MAP.TryGetValue(vo, out var mapped))
                    return Pair("-unity-background-scale-mode", mapped);
                return null;
            }

            if (prop == "box-shadow") return CssShadow.MapBoxShadow(value, warnings);

            if (prop == "transform") return CssTransform.SplitTransform(value, warnings);

            // gap / row-gap / column-gap on flex parents are NOT emitted
            // as USS — UI Toolkit's reported support is patchy and the
            // converter already bakes the gap into per-child margins via
            // LayoutHelpers.StaticGapDecls. Emitting USS gap on top of
            // that would double the spacing on engines that do honour
            // the property.
            if (prop == "gap" || prop == "row-gap" || prop == "column-gap")
                return new List<KeyValuePair<string, string>>();

            if (prop == "inset")
            {
                var sides = CssLength.ExpandBox(value);
                if (sides == null) return null;
                var (top, right, bottom, left) = sides.Value;
                return Pairs(("top", top), ("right", right), ("bottom", bottom), ("left", left));
            }

            if (prop == "white-space")
            {
                string vw = value.ToLowerInvariant();
                if (vw == "normal" || vw == "nowrap" || vw == "pre" || vw == "pre-wrap") return Pair("white-space", vw);
                if (vw == "pre-line" || vw == "break-spaces") return Pair("white-space", "pre-wrap");
                warnings.Add($"white-space: {value} approximated as normal");
                return Pair("white-space", "normal");
            }

            if (prop == "outline")
            {
                warnings.Add("outline approximated as border (occupies layout space)");
                return CssBorder.SplitBorder(value, new[] { "top", "right", "bottom", "left" });
            }

            if (prop == "clip-path")
            {
                string trimmed = value.Trim();
                if (trimmed.ToLowerInvariant().StartsWith("polygon"))
                    return Pair("--odd-clip-polygon", CssText.QuoteForUss(trimmed));
                warnings.Add($"clip-path: {value} -- only polygon() is bridged");
                return null;
            }

            if (prop == "filter") return CssFilter.MapFilter(value, warnings);

            if (prop == "text-overflow")
            {
                string vo = value.ToLowerInvariant();
                if (vo == "clip" || vo == "ellipsis") return Pair("text-overflow", vo);
                return null;
            }

            if (prop == "border-image")
            {
                var um = UrlRe.Match(value);
                if (!um.Success) { warnings.Add($"border-image: {value} dropped (no url() source)"); return null; }
                string url = um.Value;
                string rest = value.Substring(0, um.Index) + value.Substring(um.Index + um.Length);
                string slicePart = rest.Split(new[] { '/' }, 2)[0];
                var nums = new List<string>();
                foreach (Match nm in BorderImageNumRe.Matches(slicePart)) nums.Add(nm.Value);
                if (nums.Count == 0) return Pair("background-image", url);
                string t, r, b, l;
                if (nums.Count == 1) { t = r = b = l = nums[0]; }
                else if (nums.Count == 2) { t = nums[0]; r = nums[1]; b = t; l = r; }
                else if (nums.Count == 3) { t = nums[0]; r = nums[1]; b = nums[2]; l = r; }
                else { t = nums[0]; r = nums[1]; b = nums[2]; l = nums[3]; }
                return new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("background-image", url),
                    new KeyValuePair<string, string>("-unity-slice-top",    SliceInt(t)),
                    new KeyValuePair<string, string>("-unity-slice-right",  SliceInt(r)),
                    new KeyValuePair<string, string>("-unity-slice-bottom", SliceInt(b)),
                    new KeyValuePair<string, string>("-unity-slice-left",   SliceInt(l)),
                };
            }
            if (prop == "border-image-source") return Pair("background-image", value);
            if (prop == "border-image-slice")
            {
                var nums = new List<string>();
                foreach (Match nm in BorderImageNumRe.Matches(value)) nums.Add(nm.Value);
                if (nums.Count == 0) return null;
                string t, r, b, l;
                if (nums.Count == 1) { t = r = b = l = nums[0]; }
                else if (nums.Count == 2) { t = nums[0]; r = nums[1]; b = t; l = r; }
                else if (nums.Count == 3) { t = nums[0]; r = nums[1]; b = nums[2]; l = r; }
                else { t = nums[0]; r = nums[1]; b = nums[2]; l = nums[3]; }
                return new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("-unity-slice-top",    SliceInt(t)),
                    new KeyValuePair<string, string>("-unity-slice-right",  SliceInt(r)),
                    new KeyValuePair<string, string>("-unity-slice-bottom", SliceInt(b)),
                    new KeyValuePair<string, string>("-unity-slice-left",   SliceInt(l)),
                };
            }

            if (prop == "line-height")
            {
                string vlh = value.Trim().ToLowerInvariant();
                if (vlh == "normal" || vlh == "inherit" || vlh == "initial") return null;
                if (vlh.EndsWith("px")) return Pair("-unity-paragraph-spacing", vlh);
                warnings.Add($"line-height: {value} approximated as 0 paragraph spacing");
                return Pair("-unity-paragraph-spacing", "0");
            }

            if (prop == "-webkit-text-stroke" || prop == "text-stroke")
            {
                var parts = (value ?? "").Split(new[] { ' ' }, 2, System.StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return null;
                var outDecls = Pair("-unity-text-outline-width", parts[0]);
                if (parts.Length > 1) outDecls.Add(new KeyValuePair<string, string>("-unity-text-outline-color", parts[1]));
                return outDecls;
            }
            if (prop == "-webkit-text-stroke-width") return Pair("-unity-text-outline-width", value);
            if (prop == "-webkit-text-stroke-color") return Pair("-unity-text-outline-color", value);

            if (prop == "text-shadow") return Pair("text-shadow", value);

            if (prop == "flex") return CssFlex.MapFlexShorthand(value, warnings);

            if (prop.StartsWith("-unity-")) return Pair(prop, value);

            // UI Toolkit's aspect-ratio takes a single number, not the CSS
            // `<width> / <height>` form. Collapse it so values like
            // "1/1" or "16/9" don't fail USS parsing as `/ (Delim)`.
            if (prop == "aspect-ratio")
            {
                var slashParts = value.Split('/');
                if (slashParts.Length == 2
                    && double.TryParse(slashParts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var aw)
                    && double.TryParse(slashParts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ah)
                    && ah != 0)
                {
                    value = (aw / ah).ToString("g", CultureInfo.InvariantCulture);
                }
            }

            if (PassThroughProps.Contains(prop))
            {
                if ((prop == "background-repeat" || prop == "background-position" || prop == "background-size")
                    && value.Contains(","))
                {
                    string first = value.Split(new[] { ',' }, 2)[0].Trim();
                    if (first.Length == 0)
                        first = (prop == "background-repeat") ? "no-repeat" : "0% 0%";
                    value = first;
                }
                return Pair(prop, value);
            }
            if (prop.StartsWith("--")) return Pair(prop, value);

            warnings.Add($"unmapped CSS property dropped: {prop}: {value}");
            return null;
        }

        static readonly Regex MathFuncRx = new Regex(
            @"\b(?<fn>calc|min|max|clamp)\(",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Match the first dimension/percentage/integer token. Used to pick
        // a fallback when collapsing calc()/min()/max()/clamp() — UI Toolkit
        // can't evaluate those, so we drop to a single concrete value rather
        // than emit a property type-mismatch with the cascade.
        static readonly Regex FirstDimensionRx = new Regex(
            @"-?\d+(?:\.\d+)?(?:px|%|em|rem|vh|vw|vmin|vmax|s|ms|deg|rad|turn|fr)?",
            RegexOptions.Compiled);

        static string SimplifyMathFuncs(string value, List<string> warnings, string propName)
        {
            if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
            if (value.IndexOf('(') < 0) return value;

            var sb = new System.Text.StringBuilder(value.Length);
            int i = 0;
            while (i < value.Length)
            {
                var m = MathFuncRx.Match(value, i);
                if (!m.Success) { sb.Append(value, i, value.Length - i); break; }
                sb.Append(value, i, m.Index - i);

                // Walk to the matching close paren (handles nested parens).
                int start = m.Index + m.Length;
                int depth = 1;
                int j = start;
                while (j < value.Length && depth > 0)
                {
                    char c = value[j++];
                    if (c == '(') depth++;
                    else if (c == ')') depth--;
                }
                if (depth != 0)
                {
                    sb.Append(value, m.Index, value.Length - m.Index);
                    break;
                }

                string body = value.Substring(start, j - 1 - start);
                string firstDim = FirstDimensionRx.Match(body).Value;
                if (string.IsNullOrEmpty(firstDim)) firstDim = "0";
                warnings?.Add($"collapsed {m.Groups["fn"].Value}() in '{propName}' to '{firstDim}' (UI Toolkit can't evaluate math funcs)");
                sb.Append(firstDim);
                i = j;
            }
            return sb.ToString();
        }

        static string SliceInt(string raw)
        {
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return "0";
            return ((int)n).ToString(CultureInfo.InvariantCulture);
        }

        static List<KeyValuePair<string, string>> Pair(string k, string v)
            => new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>(k, v) };

        static List<KeyValuePair<string, string>> Pairs(params (string, string)[] kv)
        {
            var l = new List<KeyValuePair<string, string>>(kv.Length);
            foreach (var (k, v) in kv) l.Add(new KeyValuePair<string, string>(k, v));
            return l;
        }
    }
}
