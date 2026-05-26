using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ODDGames.Html2Uxml.Editor.Converter.Css;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // Skeleton port of html2uxml/converter.py.
    //
    // Coverage (this file):
    //   - End-to-end path: HtmlLoader → CssLoader → SelectorRewriter →
    //     StyleMapper → tag dispatch (ElementMap / InputMap) → UXML emit.
    //   - Author CSS rules emitted with rewritten selectors (see
    //     SelectorRewriter); tags rewritten to `.h2u-tag-X` get the matching
    //     class attached during DOM walk.
    //   - Inline `style="…"` becomes a per-node `.h2u-N` rule.
    //   - InputInnerSelectors mirrors padding/font onto Unity's nested
    //     TextField/DropdownField text + placeholder children.
    //   - Basic text handling (TextHandling.AssignToText / WrapInLabel / Drop).
    //
    // NOT YET PORTED (faithful Python features deferred):
    //   - z-index overlay clones / paint-order rewriting
    //   - text-gradient rich-text JSON injection (<gradient="…"> wrapping)
    //   - @keyframes → --odd-animation-* bridge
    //   - SVG inline emission, data-URI rewrites, asset slugifying
    //   - List markers, table layout grid, fieldset disabled cascade
    //   - Pseudo-element :before / :after materialisation
    //   - Compact text container heuristics (4500+ LOC of label decisions)
    //   - Inline run wrapping (anchor-in-paragraph etc.)
    public static class ConvertEngine
    {
        public sealed class ConvertOptions
        {
            public string SourceHtmlPath;
            public bool SkipBodyDimensions = false;
        }

        public sealed class ConvertResult
        {
            public string Uxml;
            public string Uss;
            public List<string> Warnings = new List<string>();
            public List<string> ReferencedAssetUrls = new List<string>();
            // Extra files the converter generates inline (currently extracted
            // <svg> blocks). Paths are bundle-relative under UI/, e.g.
            // "Images/icon.svg".
            public Dictionary<string, string> ExtraAssetFiles = new Dictionary<string, string>();
        }

        public static ConvertResult Convert(ConvertOptions opt)
        {
            if (opt == null) throw new System.ArgumentNullException(nameof(opt));
            if (string.IsNullOrEmpty(opt.SourceHtmlPath))
                throw new System.ArgumentException("SourceHtmlPath required");

            var result = new ConvertResult();
            var parsed = HtmlLoader.ParseFromFile(opt.SourceHtmlPath);
            var rules = CssLoader.LoadFromHtml(parsed.Root, opt.SourceHtmlPath);
            string rawCss = CssLoader.LoadRawCssText(opt.SourceHtmlPath);

            var state = new EmitState();
            state.AnimationKeyframes = AnimationBridge.ExtractKeyframes(rawCss);
            state.SvgBlocks = parsed.SvgBlocks ?? new List<string>();
            state.RawRules = rules;

            // background-clip:text + linear-gradient → registered text gradient.
            // Mutates rule decls (drops bg paint props) so the wrap consumes
            // them; runs BEFORE author CSS emission and resolver.
            TextGradient.Preprocess(rules, state.TextGradient, state.Warnings);

            // UA / Unity-default reset rules emitted BEFORE author CSS so
            // user rules with equal specificity win on cascade order.
            EmitDefaultResets(parsed.Root, state);
            // Prepend browser UA stylesheet through the same author-CSS path,
            // filtered to tags present in the tree (Python only emits the
            // rule when the tag is used — keeps the USS lean).
            var allTags = CollectAllTags(parsed.Root);
            var uaRules = CssLoader.ParseString(ChromeDefaults.UserAgentCss, originIndex: -1);
            var uaUsed = new List<CssLoader.CssRule>();
            foreach (var r in uaRules)
                if (UaSelectorTouchesAnyTag(r.ParsedSelector, allTags)) uaUsed.Add(r);
            EmitAuthorCssRules(uaUsed, state);
            EmitAuthorCssRules(rules, state);
            // Scan rules for bridge-required props and pre-compute the set of
            // DOM nodes whose UXML tag must upgrade to odd:Html2Uxml*.
            ComputeBridgeNodes(parsed.Root, rules, state);
            // Cascade-resolve per-node decls so we can read text-transform /
            // text-decoration etc. at emit time. The resolved view is NOT the
            // styling source — author rules already emit verbatim. This is
            // only consumed for converter-side decisions on text content.
            state.Resolved = Resolver.Resolve(parsed.Root, rules);
            // Pre-walk: per-node effective-text-raw map (what Python threads
            // down via `inherited_text_raw`). Lets EmitNode emit a per-node
            // typography snapshot for every Label that would otherwise inherit
            // its font/color from an ancestor.
            state.InheritedText = new InheritedText.Builder();
            state.InheritedText.WalkTree(parsed.Root, state.Resolved);
            // ::before / ::after rule materialisation — selectors that end in
            // a pseudo-element are dropped from the cascade emission and
            // surfaced as synthetic Label children at emit time.
            state.PseudoEntries = PseudoElements.Collect(parsed.Root, rules);
            // <datalist> options + checkable-input <label for=…> merges.
            state.FormAssoc = FormControlAssociation.Build(parsed.Root);

            var bodyChildren = parsed.Root?.Children ?? new List<HtmlLoader.HtmlNode>();
            var bodyBuf = new StringBuilder();
            EmitChildren(parsed.Root, bodyChildren, state, bodyBuf, depth: 2);

            // Late-emit helpers that depend on whether any node opted in.
            EmitDynTemplateRule(state);

            // Wrap body in `<ui:UXML>` with `<Style src="…" />` import +
            // optional Html2UxmlPanel root so the runtime panel can paint
            // bridge props on the body's own background. Mirrors _wrap_uxml.
            string ussFilename = (string.IsNullOrEmpty(opt.SourceHtmlPath)
                ? "out"
                : Path.GetFileNameWithoutExtension(opt.SourceHtmlPath)) + ".uss";
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
            sb.Append("<ui:UXML xmlns:ui=\"UnityEngine.UIElements\" xmlns:uie=\"UnityEditor.UIElements\"");
            if (state.UsesBridge) sb.Append(" xmlns:odd=\"ODDGames.Html2Uxml\"");
            sb.Append(">\n");
            sb.Append("    <Style src=\"").Append(XmlEscape(ussFilename)).Append("\" />\n");
            // Body wrapper: Html2UxmlPanel when bridge props in scope, else
            // a plain VisualElement. Author body rule applies via .h2u-tag-body.
            string bodyTag = state.UsesBridge ? "odd:Html2UxmlPanel" : "ui:VisualElement";
            sb.Append("    <").Append(bodyTag).Append(" class=\"h2u-tag-body\">\n");
            sb.Append(bodyBuf);
            sb.Append("    </").Append(bodyTag).Append(">\n");
            sb.Append("</ui:UXML>\n");

            // Body now has the .h2u-tag-body class; ensure the tag-class rule
            // exists even if author CSS only targets `body` selectors.
            state.TaggedTags.Add("body");

            result.Uxml = sb.ToString();
            result.Uss = EmitUss(state);
            result.Warnings.AddRange(state.Warnings);
            result.ReferencedAssetUrls = state.ReferencedAssetUrls;
            // Bundle SVG + text-gradient JSON files together. SVG paths are
            // already prefixed with the Images/ subdir; gradients land in the
            // bundle root so the runtime locator can find `<name>.h2utg.json`.
            result.ExtraAssetFiles = new Dictionary<string, string>(state.SvgFiles);
            foreach (var kv in state.TextGradient.Files)
                result.ExtraAssetFiles[kv.Key] = kv.Value;
            return result;
        }

        // ------------------------------------------------------------------
        // EmitState — accumulator for per-node USS rules + warnings.
        // ------------------------------------------------------------------
        sealed class EmitState
        {
            public Dictionary<string, List<KeyValuePair<string, string>>> RulesBySelector
                = new Dictionary<string, List<KeyValuePair<string, string>>>();
            // Source CSS comment bodies queued for the next selector to be
            // emitted. EmitUss flushes them above the corresponding rule block.
            public Dictionary<string, List<string>> CommentsBySelector
                = new Dictionary<string, List<string>>();
            public List<string> SelectorOrder = new List<string>();
            public List<string> Warnings = new List<string>();
            public List<string> ReferencedAssetUrls = new List<string>();
            public HashSet<string> TaggedTags = new HashSet<string>();
            public bool NeedsInlineFlowRule;
            // A data-h2u-item or data-h2u-list was seen; emit the
            // .h2u-dyn-template { display: none; } helper once.
            public bool NeedsDynTemplateRule;
            public Dictionary<string, AnimationBridge.KeyframeBlock> AnimationKeyframes
                = new Dictionary<string, AnimationBridge.KeyframeBlock>();
            public Dictionary<HtmlLoader.HtmlNode, List<KeyValuePair<string, string>>> Resolved;
            // Per-node text-raw inheritance snapshot (mirrors Python's
            // `inherited_text_raw` thread). Built once after Resolver runs.
            public InheritedText.Builder InheritedText;
            public Dictionary<HtmlLoader.HtmlNode, List<PseudoElements.PseudoEntry>> PseudoEntries
                = new Dictionary<HtmlLoader.HtmlNode, List<PseudoElements.PseudoEntry>>(
                    ReferenceEqualityComparer<HtmlLoader.HtmlNode>.Instance);
            public List<string> SvgBlocks = new List<string>();
            public Dictionary<string, string> SvgFiles = new Dictionary<string, string>();   // bundle-relative path → contents
            public Dictionary<string, int> SvgNameCounts = new Dictionary<string, int>();
            public bool SvgWarningEmitted;
            public string SvgAssetsSubdir = "Images";
            // Stack of `border-collapse:collapse` flags for each enclosing
            // <table>. Top of stack = closest ancestor table.
            public Stack<bool> TableCollapseStack = new Stack<bool>();
            // CSS-inherited text props: each EmitNode may push a value the
            // resolver didn't know to inherit. Top of stack = nearest ancestor.
            public Stack<string> InheritedTextTransform = new Stack<string>();
            public Stack<string> InheritedTextDecoration = new Stack<string>();
            public FormControlAssociation.Associations FormAssoc
                = new FormControlAssociation.Associations();
            public TextGradient.State TextGradient = new TextGradient.State();
            public Dictionary<string, CssSelector> GradientSelectorParsed;
            // Raw CSS rule list — kept around so per-node passes (input
            // attribute-selector matcher, etc.) can re-walk author rules
            // without rebuilding them.
            public List<CssLoader.CssRule> RawRules;
            // Per-child decls injected by parent (gap baking). Consumed during
            // the child's EmitNode and cleared.
            public Dictionary<HtmlLoader.HtmlNode, List<KeyValuePair<string, string>>> PendingChildDecls
                = new Dictionary<HtmlLoader.HtmlNode, List<KeyValuePair<string, string>>>(
                    ReferenceEqualityComparer<HtmlLoader.HtmlNode>.Instance);
            // Nodes whose UXML tag must upgrade to odd:Html2Uxml* because a
            // matching CSS rule (or inline style) emits a bridge custom prop.
            public HashSet<HtmlLoader.HtmlNode> BridgeNodes
                = new HashSet<HtmlLoader.HtmlNode>(
                    ReferenceEqualityComparer<HtmlLoader.HtmlNode>.Instance);
            public bool UsesBridge;
            public int NextAnonClassId = 1;

            public string AllocateClass(string nameHint)
            {
                if (!string.IsNullOrEmpty(nameHint))
                {
                    string slug = "h2u-" + Slugify(nameHint);
                    if (!RulesBySelector.ContainsKey("." + slug)) return slug;
                    int n = 2;
                    while (RulesBySelector.ContainsKey("." + slug + "-" + n)) n++;
                    return slug + "-" + n;
                }
                return "h2u-" + (NextAnonClassId++);
            }

            public void AddRule(string selector, List<KeyValuePair<string, string>> decls)
            {
                if (decls == null || decls.Count == 0) return;
                if (!RulesBySelector.TryGetValue(selector, out var existing))
                {
                    existing = new List<KeyValuePair<string, string>>();
                    RulesBySelector[selector] = existing;
                    SelectorOrder.Add(selector);
                }
                existing.AddRange(decls);
            }

            public void AddRuleComments(string selector, List<string> comments)
            {
                if (comments == null || comments.Count == 0) return;
                if (!CommentsBySelector.TryGetValue(selector, out var bucket))
                {
                    bucket = new List<string>();
                    CommentsBySelector[selector] = bucket;
                }
                bucket.AddRange(comments);
            }

            static string Slugify(string s)
            {
                var b = new StringBuilder();
                foreach (char c in s.ToLowerInvariant())
                {
                    if (char.IsLetterOrDigit(c)) b.Append(c);
                    else if (b.Length > 0 && b[b.Length - 1] != '-') b.Append('-');
                }
                while (b.Length > 0 && b[b.Length - 1] == '-') b.Length--;
                return b.Length > 0 ? b.ToString() : "el";
            }
        }

        // ------------------------------------------------------------------
        // Pre-pass: find every node whose matched CSS rules (or inline style)
        // emit a bridge custom prop. EmitNode swaps those nodes to the
        // odd:Html2Uxml* equivalent so the runtime painter manipulator runs.
        // ------------------------------------------------------------------
        static void ComputeBridgeNodes(HtmlLoader.HtmlNode root,
                                       List<CssLoader.CssRule> rules,
                                       EmitState state)
        {
            if (root == null || rules == null) return;
            // Walk every node; collect matching rules; map each rule's decls;
            // if any emit a bridge prop, mark the node.
            var allNodes = new List<(HtmlLoader.HtmlNode node, List<HtmlLoader.HtmlNode> ancestors, int siblingIdx, List<HtmlLoader.HtmlNode> siblings)>();
            CollectNodesForBridge(root, new List<HtmlLoader.HtmlNode>(), allNodes);

            // Cache mapped rule decls so we don't re-map per node.
            var mappedRules = new List<(CssSelector sel, List<KeyValuePair<string, string>> decls)>();
            foreach (var r in rules)
            {
                if (r.ParsedSelector == null || r.ParsedSelector.HasUnsupportedFeatures()) continue;
                var mapped = StyleMapper.MapDeclarations(r.Decls);
                if (BridgeTags.RequiresBridge(mapped.Decls))
                    mappedRules.Add((r.ParsedSelector, mapped.Decls));
            }
            foreach (var (node, ancestors, idx, siblings) in allNodes)
            {
                bool needs = false;
                foreach (var (sel, _) in mappedRules)
                {
                    if (ResolverMatching.SelectorMatches(node, sel, ancestors, idx, siblings))
                    { needs = true; break; }
                }
                if (!needs)
                {
                    string inline = AttrUtil.Get(node.Attrs, "style");
                    if (!string.IsNullOrWhiteSpace(inline))
                    {
                        var mapped = StyleMapper.MapDeclarations(ParseInlineStyle(inline));
                        if (BridgeTags.RequiresBridge(mapped.Decls)) needs = true;
                    }
                }
                if (needs) { state.BridgeNodes.Add(node); state.UsesBridge = true; }
            }
        }

        static void CollectNodesForBridge(
            HtmlLoader.HtmlNode parent,
            List<HtmlLoader.HtmlNode> ancestors,
            List<(HtmlLoader.HtmlNode, List<HtmlLoader.HtmlNode>, int, List<HtmlLoader.HtmlNode>)> outList)
        {
            if (parent?.Children == null) return;
            var siblings = ResolverMatching.NonTextChildren(parent);
            for (int i = 0; i < siblings.Count; i++)
            {
                var node = siblings[i];
                outList.Add((node, new List<HtmlLoader.HtmlNode>(ancestors), i, siblings));
                ancestors.Add(parent);
                CollectNodesForBridge(node, ancestors, outList);
                ancestors.RemoveAt(ancestors.Count - 1);
            }
        }

        // ------------------------------------------------------------------
        // Default reset rules (Unity Button / BaseField margin neutralisation).
        // ------------------------------------------------------------------
        static HashSet<string> CollectAllTags(HtmlLoader.HtmlNode root)
        {
            var seen = new HashSet<string>();
            CollectAllTagsRecursive(root, seen);
            return seen;
        }

        static void CollectAllTagsRecursive(HtmlLoader.HtmlNode node, HashSet<string> seen)
        {
            if (node == null || node.IsText) return;
            string t = (node.Tag ?? "").ToLowerInvariant();
            if (t.Length > 0) seen.Add(t);
            if (node.Children != null)
                foreach (var c in node.Children) CollectAllTagsRecursive(c, seen);
        }

        // True when at least one compound in the chain targets a tag the tree
        // contains. Skips UA rules whose selectors only mention absent tags.
        static bool UaSelectorTouchesAnyTag(CssSelector sel, HashSet<string> usedTags)
        {
            if (sel?.Chain == null || sel.Chain.Count == 0) return false;
            // Conservative: every compound's tag (when present) must exist
            // in the tree. Any unsuitable compound disqualifies the rule.
            foreach (var (_, comp) in sel.Chain)
            {
                if (string.IsNullOrEmpty(comp.Tag) || comp.Tag == "*") continue;
                if (!usedTags.Contains(comp.Tag.ToLowerInvariant())) return false;
            }
            return true;
        }

        static bool SelectorTargetsOnlyTag(CssSelector sel, string tag)
        {
            if (sel?.Chain == null || sel.Chain.Count != 1) return false;
            var (_, comp) = sel.Chain[0];
            return comp != null
                && (comp.Tag ?? "").ToLowerInvariant() == tag
                && string.IsNullOrEmpty(comp.Id)
                && (comp.Classes == null || comp.Classes.Count == 0)
                && (comp.Pseudo == null || comp.Pseudo.Count == 0)
                && (comp.Attrs == null || comp.Attrs.Count == 0);
        }

        static void EmitDefaultResets(HtmlLoader.HtmlNode root, EmitState state)
        {
            if (UnityDefaults.TreeContainsButton(root))
            {
                var reset = UnityDefaults.ButtonReset();
                state.AddRule("Button",        reset);
                state.AddRule(".unity-button", reset);
                // Unity's runtime theme ships `.unity-button { align-self:
                // flex-start }`. Bump specificity on JUST align-self via a
                // compound selector so we beat the theme — but DON'T put
                // the whole reset block there, otherwise the (0,2,0)
                // specificity beats author class rules like
                // `.mt-replay__exit { padding: 0 7px }` and silently wipes
                // their padding too.
                state.AddRule(".unity-button.unity-button",
                    new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("align-self", "auto"),
                    });
            }
            if (UnityDefaults.TreeContainsTag(root, "br"))
            {
                state.AddRule(".br", new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("width", "100%"),
                    new KeyValuePair<string, string>("height", "0"),
                    new KeyValuePair<string, string>("min-height", "0"),
                    new KeyValuePair<string, string>("flex-basis", "100%"),
                    new KeyValuePair<string, string>("flex-shrink", "0"),
                });
            }
            foreach (var kv in UnityDefaults.InputDefaults)
            {
                if (!UnityDefaults.TreeContainsTag(root, kv.Key)) continue;
                state.TaggedTags.Add(kv.Key);
                state.AddRule("." + SelectorRewriter.H2UTagClass(kv.Key), kv.Value);
            }

            // Always emit the inline-flow helper — EmitNode tags individual
            // elements with .h2u-inline-flow when their children are all
            // inline (span/a/em/strong/label/text), and the rule restores
            // row layout. Cheap to emit unconditionally; the class only
            // attaches where needed.
            state.AddRule(".h2u-inline-flow", new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("flex-direction", "row"),
                // No flex-wrap: CSS inline flow doesn't wrap blocks of
                // spans onto separate lines unless the content overflows
                // explicit width. UI Toolkit's wrap behavior breaks
                // button labels onto a second line when the button is
                // sized tightly (e.g. ONBOARD label drops below its
                // icon inside a fixed-min-width cam button).
                new KeyValuePair<string, string>("align-items", "center"),
            });
        }

        // Emit the dynamic-template helper once if the document used any
        // data-h2u-list / data-h2u-item markers. Runtime Html2UxmlList.Bind
        // strips this class from clones so they become visible after fill.
        static void EmitDynTemplateRule(EmitState state)
        {
            if (!state.NeedsDynTemplateRule) return;
            state.AddRule("." + DynamicMarkers.TemplateClass,
                new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("display", "none"),
                });
        }

        // Returns true when `node` has at least two children that are all
        // inline-flow (span, a, em, strong, label, small, br, sub, sup, b,
        // i, u, code, text). Used to flip the synthesized block-flex parent
        // from column back to row, matching CSS inline-flow defaults.
        static readonly HashSet<string> InlineFlowTags = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            "span", "a", "em", "strong", "label", "small", "br",
            "sub", "sup", "b", "i", "u", "code", "abbr", "cite",
            "mark", "q", "s", "del", "ins", "var", "kbd", "samp",
            "time", "tt", "wbr",
        };

        static bool HasOnlyInlineChildren(HtmlLoader.HtmlNode node)
        {
            if (node?.Children == null) return false;
            int meaningful = 0;
            foreach (var c in node.Children)
            {
                if (c == null) continue;
                if (c.IsComment) continue;
                if (c.IsText)
                {
                    if (!string.IsNullOrWhiteSpace(c.Text)) meaningful++;
                    continue;
                }
                string tag = (c.Tag ?? "").ToLowerInvariant();
                if (!InlineFlowTags.Contains(tag)) return false;
                meaningful++;
            }
            return meaningful >= 2;
        }

        // ------------------------------------------------------------------
        // Author CSS rule emission.
        // ------------------------------------------------------------------
        static void EmitAuthorCssRules(List<CssLoader.CssRule> rules, EmitState state)
        {
            // Deduplicate by ParsedSelector reference: CssLoader explodes
            // comma-separated selectors into separate rules with shared decls.
            // Walk in declared order; SelectorOrder reflects that.
            foreach (var rule in rules)
            {
                var sel = rule.ParsedSelector;
                if (sel == null) continue;
                if (sel.HasUnsupportedFeatures()) continue;

                var mapped = StyleMapper.MapDeclarations(rule.Decls);
                state.Warnings.AddRange(mapped.Warnings);

                // @keyframes-driven animation bridge — emits --odd-animation-*
                // alongside the rule's own decls.
                var animationDecls = AnimationBridge.AnimationCustomDecls(
                    rule.Decls, state.AnimationKeyframes, state.Warnings);
                if (animationDecls.Count > 0)
                    mapped.Decls.AddRange(animationDecls);

                if (mapped.Decls.Count == 0) continue;

                // Skip rules targeting only the synthetic <html> root — the
                // emitter never produces an html element, so .h2u-tag-html
                // rules are unreachable. body rules are kept (body is the
                // wrapper element).
                if (SelectorTargetsOnlyTag(sel, "html")) continue;

                string ussSelector = SelectorRewriter.RewriteSelectorForUss(sel, state.TaggedTags);
                state.AddRuleComments(ussSelector, rule.LeadingComments);
                state.AddRule(ussSelector, mapped.Decls);

                // Mirror padding/font onto Unity's nested input children.
                foreach (var (innerSel, innerDecls) in
                    InputInnerSelectors.InputInnerPaddingRules(sel, ussSelector, mapped.Decls))
                {
                    state.AddRule(innerSel, innerDecls);
                }
            }
        }

        // ------------------------------------------------------------------
        // Per-node emission.
        // ------------------------------------------------------------------
        static void EmitChildren(HtmlLoader.HtmlNode parent,
                                 List<HtmlLoader.HtmlNode> children,
                                 EmitState state, StringBuilder sb, int depth)
        {
            if (children == null) return;
            // Reorder by z-index: equal z preserves DOM order; higher z paints
            // later (Unity paints later siblings on top).
            var ordered = ReorderByZIndex(children, state);

            // data-h2u-list container: drop every sibling except the one
            // template child. The author keeps preview rows in the HTML for
            // visual-compare against Chrome; the converter freezes a single
            // template so runtime cloning has a clean starting point.
            if (parent != null && DynamicMarkers.IsListContainer(parent))
            {
                var template = DynamicMarkers.PickTemplateChild(ordered);
                ordered = template != null
                    ? new List<HtmlLoader.HtmlNode> { template }
                    : new List<HtmlLoader.HtmlNode>();
                state.NeedsDynTemplateRule = true;
            }
            string parentTag = (parent?.Tag ?? "").ToLowerInvariant();
            bool isList = parentTag == "ul" || parentTag == "ol";
            int ordinal = 1;
            int i = 0;
            int visualIndex = 0;
            // If the parent already lays out children as a flex row,
            // the run-wrapper is redundant — and harmful, because its
            // `flex-wrap: wrap` makes the last sibling drop onto a
            // second line whenever the row is even slightly tight. The
            // wrapper exists to coerce row layout under UI Toolkit's
            // column-default; skip it when the parent's own CSS already
            // calls for row flow.
            // state.Resolved holds the RAW CSS-spec props (display,
            // flex-direction). CSS defaults `display:flex` to row-flow
            // when no flex-direction is set, so treat blank as row too.
            string parentDisplay = (ResolvedView.GetProp(state, parent, "display") ?? "").Trim().ToLowerInvariant();
            string parentFlexDir = (ResolvedView.GetProp(state, parent, "flex-direction") ?? "").Trim().ToLowerInvariant();
            bool parentIsRowFlex = parent != null
                && (parentDisplay == "flex" || parentDisplay == "inline-flex")
                && (string.IsNullOrEmpty(parentFlexDir)
                    || parentFlexDir == "row"
                    || parentFlexDir == "row-reverse");

            while (i < ordered.Count)
            {
                // Bundle consecutive inline buttons into a flex-row wrapper.
                // Pass a display-lookup so author CSS classes that set
                // `display:flex`/`display:block` opt the button out
                // (cam-buttons: display:flex via class, parent is plain
                // block — Detect would otherwise wrap them and break
                // vertical stacking).
                var (run, next) = parentIsRowFlex
                    ? (new List<HtmlLoader.HtmlNode>(), i)
                    : InlineButtonRun.Detect(ordered, i,
                        n => ResolvedView.GetProp(state, n, "display"));
                if (run.Count > 0)
                {
                    string runClass = state.AllocateClass("button-row");
                    state.AddRule("." + runClass, InlineButtonRun.WrapperDecls());
                    Indent(sb, depth);
                    sb.Append("<ui:VisualElement class=\"").Append(runClass).Append("\">\n");
                    foreach (var btn in run)
                    {
                        EmitNodeWithGap(btn, parent, 0, state, sb, depth + 1, visualIndex);
                        visualIndex++;
                    }
                    Indent(sb, depth);
                    sb.Append("</ui:VisualElement>\n");
                    i = next;
                    continue;
                }

                var child = ordered[i];
                if (isList && !child.IsText && (child.Tag ?? "").ToLowerInvariant() == "li")
                {
                    EmitNodeWithGap(child, parent, ordinal, state, sb, depth, visualIndex);
                    ordinal++;
                }
                else
                {
                    EmitNodeWithGap(child, parent, 0, state, sb, depth, visualIndex);
                }
                if (!child.IsText) visualIndex++;
                i++;
            }
        }

        // Emit a child plus inject the gap-margin onto the child's own rule.
        static void EmitNodeWithGap(HtmlLoader.HtmlNode child, HtmlLoader.HtmlNode parent,
                                    int liOrdinal, EmitState state, StringBuilder sb,
                                    int depth, int visualIndex)
        {
            // Insert into pending-decl bucket so EmitNode picks them up while
            // building the per-node rule.
            if (!child.IsText && parent != null)
            {
                var gap = LayoutHelpers.StaticGapDecls(
                    p => ResolvedView.GetProp(state, parent, p),
                    p => ResolvedView.GetProp(state, child, p),
                    visualIndex);
                if (gap.Count > 0) state.PendingChildDecls[child] = gap;
            }
            EmitNode(child, parent, liOrdinal, state, sb, depth);
        }

        static List<HtmlLoader.HtmlNode> ReorderByZIndex(List<HtmlLoader.HtmlNode> children, EmitState state)
        {
            if (children.Count < 2) return children;
            var enriched = new List<(int z, int idx, HtmlLoader.HtmlNode node)>(children.Count);
            bool anyZ = false;
            for (int i = 0; i < children.Count; i++)
            {
                int z = 0;
                var c = children[i];
                if (!c.IsText)
                {
                    string raw = ResolvedView.GetProp(state, c, "z-index")
                              ?? InlineStyleProp(c, "z-index");
                    int? parsed = LayoutHelpers.ParseZIndex(raw);
                    if (parsed.HasValue) { z = parsed.Value; anyZ = true; }
                }
                enriched.Add((z, i, c));
            }
            if (!anyZ) return children;
            enriched.Sort((a, b) =>
            {
                int cz = a.z.CompareTo(b.z);
                return cz != 0 ? cz : a.idx.CompareTo(b.idx);
            });
            var result = new List<HtmlLoader.HtmlNode>(children.Count);
            foreach (var e in enriched) result.Add(e.node);
            return result;
        }

        static string InlineStyleProp(HtmlLoader.HtmlNode node, string prop)
        {
            string style = AttrUtil.Get(node?.Attrs, "style");
            if (string.IsNullOrEmpty(style)) return null;
            foreach (var raw in style.Split(';'))
            {
                int colon = raw.IndexOf(':');
                if (colon <= 0) continue;
                string p = raw.Substring(0, colon).Trim();
                if (string.Equals(p, prop, System.StringComparison.OrdinalIgnoreCase))
                    return raw.Substring(colon + 1).Trim();
            }
            return null;
        }

        static void EmitNode(HtmlLoader.HtmlNode node,
                             HtmlLoader.HtmlNode parent,
                             int liOrdinal,
                             EmitState state, StringBuilder sb, int depth)
        {
            if (node == null) return;
            if (node.IsComment)
            {
                EmitHtmlComment(node.Text, sb, depth);
                return;
            }
            if (node.IsText)
            {
                string trimmed = (node.Text ?? "").Trim();
                if (trimmed.Length == 0) return;
                Indent(sb, depth);
                sb.Append("<ui:Label text=\"").Append(XmlEscape(trimmed)).Append("\" />\n");
                return;
            }

            string tag = (node.Tag ?? "").ToLowerInvariant();
            if (string.IsNullOrEmpty(tag)) return;

            // <svg> placeholders carry data-svg-id pointing into state.SvgBlocks;
            // resolve to a sibling .svg file and emit Html2UxmlElement.
            if (tag == "svg") { EmitSvgNode(node, parent, state, sb, depth); return; }

            if (ElementMap.SKIP_TAGS.Contains(tag)) return;
            // <datalist> children are walked at pre-pass; skip them at emit.
            if (tag == "datalist") return;
            // <label> already merged into a sibling Toggle/RadioButton.
            if (state.FormAssoc.SkipLabels.Contains(node)) return;

            var mapping = ElementMap.MapElement(tag, node.Attrs);

            // Always upgrade ui:* to odd:Html2Uxml*. ui:VisualElement → Element
            // by default but promotes to odd:Html2UxmlPanel when the node has
            // bridge custom-prop output (gradient/shadow/mask/clip/etc.) so
            // the runtime panel painter runs.
            string upgraded = BridgeTags.ToOddUxmlTag(mapping.UxmlType);
            if (mapping.UxmlType == "ui:VisualElement" && state.BridgeNodes.Contains(node))
                upgraded = "odd:Html2UxmlPanel";
            if (upgraded != mapping.UxmlType)
            {
                mapping.UxmlType = upgraded;
                state.UsesBridge = true;
            }

            // Inject the merged label text onto checkable inputs, plus
            // datalist choices for input/select with a list= reference.
            if (state.FormAssoc.InputLabelText.TryGetValue(node, out var labelText))
                mapping.ExtraAttrs["text"] = labelText;
            string listRef = AttrUtil.Get(node.Attrs, "list");
            if (!string.IsNullOrEmpty(listRef)
                && state.FormAssoc.Datalists.TryGetValue(listRef, out var choices))
            {
                mapping.ExtraAttrs["choices"] = string.Join(",", choices);
            }
            if (tag == "select")
                ApplySelectOptions(node, mapping);

            // Inline style="..." → per-node .h2u-N rule (author CSS already
            // emits as its original selector, so we intentionally only handle
            // inline here to avoid duplication).
            string generatedClass = null;
            string inlineStyle = AttrUtil.Get(node.Attrs, "style");
            if (!string.IsNullOrWhiteSpace(inlineStyle))
            {
                var inlineDecls = ParseInlineStyle(inlineStyle);
                if (inlineDecls.Count > 0)
                {
                    var mapped = StyleMapper.MapDeclarations(inlineDecls);
                    state.Warnings.AddRange(mapped.Warnings);
                    if (mapped.Decls.Count > 0)
                    {
                        string nameHint = NameHint.Resolve(node);
                        generatedClass = state.AllocateClass(nameHint);
                        state.AddRule("." + generatedClass, mapped.Decls);
                    }
                }
            }

            // Normal-flow shrink: children of block-level parents get
            // `flex-shrink: 0` so a tall body's column flex doesn't collapse
            // them. Skipped for flex/grid parents (let layout do its job).
            var flowDecls = LayoutHelpers.NormalFlowChildDecls(
                node, parent,
                p => ResolvedView.GetProp(state, node, p),
                parent != null ? (System.Func<string, string>)(p => ResolvedView.GetProp(state, parent, p))
                               : (System.Func<string, string>)(p => null));
            if (flowDecls.Count > 0)
            {
                if (generatedClass == null)
                {
                    string nameHint = NameHint.Resolve(node) ?? tag;
                    generatedClass = state.AllocateClass(nameHint);
                }
                state.AddRule("." + generatedClass, flowDecls);
            }

            // Apply any parent-injected gap-margin decls.
            if (state.PendingChildDecls.TryGetValue(node, out var gapDecls))
            {
                if (generatedClass == null)
                {
                    string nameHint = NameHint.Resolve(node) ?? tag;
                    generatedClass = state.AllocateClass(nameHint);
                }
                state.AddRule("." + generatedClass, gapDecls);
                state.PendingChildDecls.Remove(node);
            }

            // Inline-form-controls wrapper: a div whose direct children are
            // checkable inputs (+ their associated labels) gets flex-row
            // wrapper decls onto its own per-node rule. Mirrors converter.py
            // line ~2761 (inline_form_controls + _TEXT_CONTAINER_UXML_TAGS).
            if (NodeContainsCheckableInput(node)
                && (mapping.UxmlType == "ui:VisualElement"
                    || mapping.UxmlType == "odd:Html2UxmlElement"
                    || mapping.UxmlType == "odd:Html2UxmlPanel"))
            {
                if (generatedClass == null)
                {
                    string nameHint = NameHint.Resolve(node) ?? tag;
                    generatedClass = state.AllocateClass(nameHint);
                }
                state.AddRule("." + generatedClass, InlineButtonRun.WrapperDecls());
            }

            // <input> attribute-selector rule playback + range default.
            // Each emits as its OWN allocated class so the cascade ordering
            // matches Python (multiple per-node rules, not one merged rule).
            // Tracked for class-list assembly below.
            string inputAttrClass = null;
            string rangeDefaultClass = null;
            string rangePseudoHostClass = null;
            if (tag == "input")
            {
                var inputAttrDecls = ResolveInputAttributeRule(node, state);
                if (inputAttrDecls != null && inputAttrDecls.Count > 0)
                {
                    string nameHint = NameHint.Resolve(node) ?? "input";
                    inputAttrClass = state.AllocateClass(nameHint);
                    state.AddRule("." + inputAttrClass, inputAttrDecls);
                }
                if ((AttrUtil.Get(node.Attrs, "type") ?? "").ToLowerInvariant() == "range"
                    && ResolvedView.GetProp(state, node, "width") == null
                    && !DeclsContain(inputAttrDecls, "width")
                    && InlineStyleProp(node, "width") == null)
                {
                    string nameHint = NameHint.Resolve(node) ?? "range";
                    rangeDefaultClass = state.AllocateClass(nameHint);
                    state.AddRule("." + rangeDefaultClass, new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("width", "180px"),
                        new KeyValuePair<string, string>("flex-grow", "0"),
                        new KeyValuePair<string, string>("flex-shrink", "0"),
                    });
                }
                var rangePseudoRules = ResolveRangePseudoRules(node, state);
                if (rangePseudoRules != null && rangePseudoRules.Any)
                {
                    string hostClass = inputAttrClass ?? generatedClass;
                    if (hostClass == null)
                    {
                        hostClass = state.AllocateClass(NameHint.Resolve(node) ?? "range");
                        rangePseudoHostClass = hostClass;
                    }

                    string hostSelector = "." + hostClass;
                    state.AddRule(hostSelector + " > .html2uxml-slider-track", rangePseudoRules.Track);
                    state.AddRule(hostSelector + " .html2uxml-slider-thumb", rangePseudoRules.Thumb);
                    state.AddRule(hostSelector + " .html2uxml-slider-fill", rangePseudoRules.Fill);
                    if (rangePseudoRules.HasTrack && !rangePseudoRules.HasFill)
                    {
                        state.AddRule(hostSelector + " .html2uxml-slider-fill",
                            new List<KeyValuePair<string, string>>
                            {
                                new KeyValuePair<string, string>("display", "none"),
                            });
                    }
                }
            }

            // List item container layout: <li> needs flex-row + leading
            // marker label. Compute marker text now; emit container decls
            // onto the same per-node rule.
            string liMarker = ListMarkers.Marker(node, parent, liOrdinal);
            if (liMarker != null)
            {
                if (generatedClass == null)
                {
                    string nameHint = NameHint.Resolve(node) ?? "li";
                    generatedClass = state.AllocateClass(nameHint);
                }
                state.AddRule("." + generatedClass, ListMarkers.ItemContainerDecls(hasMarker: true));
            }

            // Compact text container shrink — column-flex parents with
            // only inline-text children grow in UI Toolkit unless we cap
            // overflow + min-height. Browsers shrink-to-fit by default.
            var compactDecls = CompactText.ContainerDecls(node, new ResolvedView(state));
            if (compactDecls.Count > 0)
            {
                if (generatedClass == null)
                {
                    string nameHint = NameHint.Resolve(node) ?? tag;
                    generatedClass = state.AllocateClass(nameHint);
                }
                state.AddRule("." + generatedClass, compactDecls);
            }

            // Compact-nowrap / display-label heuristics: pin Label box to the
            // source line height for tight HUD text.
            if (mapping.UxmlType == "ui:Label" || mapping.UxmlType == "odd:Html2UxmlLabel")
            {
                var sv = new LabelHeuristics.StyleView { Get = p => ResolvedView.GetProp(state, node, p) };
                var labelDecls = LabelHeuristics.CompactNowrapLabelDecls(sv);
                if (labelDecls.Count == 0) labelDecls = LabelHeuristics.CompactExplicitLineHeightLabelDecls(sv);
                if (labelDecls.Count == 0) labelDecls = LabelHeuristics.CompactSmallDisplayLabelDecls(sv);
                if (labelDecls.Count > 0)
                {
                    if (generatedClass == null)
                    {
                        string nameHint = NameHint.Resolve(node) ?? tag;
                        generatedClass = state.AllocateClass(nameHint);
                    }
                    state.AddRule("." + generatedClass, labelDecls);
                }

                // Inherited typography snapshot (mirrors Python
                // `_inherited_text_label_decls`). Only fires for Labels that
                // actually carry text — generated Element/Panel children with
                // an empty text attr don't need a font cascade snapshot.
                if (mapping.TextHandling == TextHandling.AssignToText
                    && state.InheritedText != null)
                {
                    var effective = state.InheritedText.EffectiveTextRaw(node);
                    var own = state.InheritedText.OwnTextRaw(node);
                    var inheritedDecls = InheritedText.InheritedTextLabelDecls(
                        effective, own, state.Warnings);
                    if (inheritedDecls.Count > 0)
                    {
                        if (generatedClass == null)
                        {
                            string nameHint = NameHint.Resolve(node) ?? tag;
                            generatedClass = state.AllocateClass(nameHint);
                        }
                        state.AddRule("." + generatedClass, inheritedDecls);
                    }
                }
            }

            // Table layout: USS has no native table model — convert table/tr/td
            // chains to flex containers + cells. border-collapse uses negative
            // margins on subsequent rows/cells.
            bool collapseInScope = state.TableCollapseStack.Count > 0
                                && state.TableCollapseStack.Peek();
            if (tag == "table")
            {
                bool tableCollapse = TableLayout.TableBorderCollapse(node)
                    || ResolvedBorderCollapse(node, state);
                state.TableCollapseStack.Push(tableCollapse);
                collapseInScope = tableCollapse;
            }
            var tableDecls = TableLayout.LayoutDecls(node, parent, collapseInScope);
            if (tableDecls.Count > 0)
            {
                if (generatedClass == null)
                {
                    string nameHint = NameHint.Resolve(node) ?? tag;
                    generatedClass = state.AllocateClass(nameHint);
                }
                state.AddRule("." + generatedClass, tableDecls);
            }

            // Build the final class list: optional generated + extra per-input
            // classes + tag synthetic + author classes.
            var classes = new List<string>();
            if (generatedClass != null) classes.Add(generatedClass);
            if (inputAttrClass != null && inputAttrClass != generatedClass) classes.Add(inputAttrClass);

            // CSS block elements containing only inline children (span, a, em,
            // strong, label) lay out their kids on one line in browsers; UI
            // Toolkit defaults a block to flex-column, which stacks them
            // vertically. Tag such nodes with .h2u-inline-flow so the
            // sheet-level rule (emitted in EmitDefaultResets) restores
            // row-flow. Author class rules override later in the cascade.
            if (HasOnlyInlineChildren(node))
            {
                classes.Add("h2u-inline-flow");
                state.NeedsInlineFlowRule = true;
            }
            if (rangeDefaultClass != null && rangeDefaultClass != generatedClass
                && rangeDefaultClass != inputAttrClass) classes.Add(rangeDefaultClass);
            if (rangePseudoHostClass != null && rangePseudoHostClass != generatedClass
                && rangePseudoHostClass != inputAttrClass
                && rangePseudoHostClass != rangeDefaultClass) classes.Add(rangePseudoHostClass);
            if (state.TaggedTags.Contains(tag)) classes.Add(SelectorRewriter.H2UTagClass(tag));
            string authorClasses = AttrUtil.Get(node.Attrs, "class");
            if (!string.IsNullOrEmpty(authorClasses))
                classes.Add(authorClasses);
            if (mapping.ExtraAttrs.TryGetValue("class", out var extraClass))
                classes.Add(extraClass);

            // Dynamic-list markers — see DynamicMarkers.cs. Container gets
            // `h2u-dyn-list`; template child (data-h2u-item, or the lone
            // surviving child of a list container) gets `h2u-dyn-template`.
            string dynListKey = DynamicMarkers.GetListKey(node);
            if (!string.IsNullOrEmpty(dynListKey))
            {
                classes.Add(DynamicMarkers.ListClass);
                state.NeedsDynTemplateRule = true;
            }
            bool isDynTemplate = parent != null
                && DynamicMarkers.IsListContainer(parent);
            if (isDynTemplate)
            {
                classes.Add(DynamicMarkers.TemplateClass);
                state.NeedsDynTemplateRule = true;
            }

            var attrSb = new StringBuilder();
            if (classes.Count > 0)
                attrSb.Append(" class=\"").Append(XmlEscape(string.Join(" ", classes))).Append("\"");

            // Resolve a UXML `name` attribute. Priority:
            //   1. data-h2u-list / data-h2u-radio-group on the container,
            //   2. data-h2u-field on a template descendant (prefixed so the
            //      runtime helper can find it via Q(name: "h2u-field-X")),
            //   3. an existing mapping-supplied name (kept for completeness).
            string uxmlName = null;
            if (!string.IsNullOrEmpty(dynListKey))
                uxmlName = dynListKey;
            string fieldName = DynamicMarkers.GetFieldName(node);
            if (uxmlName == null && !string.IsNullOrEmpty(fieldName))
                uxmlName = DynamicMarkers.FieldNamePrefix + fieldName;
            if (uxmlName == null && mapping.ExtraAttrs.TryGetValue("name", out var existingName))
                uxmlName = existingName;
            if (!string.IsNullOrEmpty(uxmlName))
                attrSb.Append(" name=\"").Append(XmlEscape(uxmlName)).Append("\"");

            foreach (var kv in mapping.ExtraAttrs)
            {
                if (kv.Key == "class") continue;
                if (kv.Key == "name") continue; // already emitted above
                attrSb.Append(' ').Append(kv.Key).Append("=\"").Append(XmlEscape(kv.Value)).Append("\"");
            }

            string textPayload = null;
            switch (mapping.TextHandling)
            {
                case TextHandling.AssignToText:
                    textPayload = GatherInlineText(node);
                    textPayload = ApplyTextEffects(textPayload, node, state);
                    if (!string.IsNullOrEmpty(textPayload))
                        attrSb.Append(" text=\"").Append(XmlEscape(textPayload)).Append("\"");
                    break;
            }

            // Track local-relative asset references so AssetWriter can copy them.
            string src = AttrUtil.Get(node.Attrs, "src");
            if (!string.IsNullOrEmpty(src) && !src.Contains("://"))
                state.ReferencedAssetUrls.Add(src);

            // Buttons (and other AssignToText controls) normally collapse
            // their inline content into the `text=` attr. That works for
            // <button>Save</button> but loses per-child styling for the
            // common `<button><span class="icon">★</span><span class="lbl">
            // Hero</span></button>` pattern — the merged text doesn't know
            // about the label's letter-spacing or the icon's color. When
            // any element child exists, render the button as a container
            // and let the children emit as their own styled Labels / icons.
            bool hasElementChild =
                node.Children != null
                && node.Children.Any(c => c != null && !c.IsText);
            bool useContainerMode =
                mapping.TextHandling == TextHandling.AssignToText
                && hasElementChild;
            if (useContainerMode)
            {
                // Strip the merged text=… attr we already appended so the
                // button doesn't emit both a `text` attribute and child
                // elements (UI Toolkit picks one).
                int textIdx = attrSb.ToString().IndexOf(" text=\"");
                if (textIdx >= 0)
                {
                    int closeIdx = attrSb.ToString().IndexOf('"', textIdx + 8);
                    if (closeIdx > textIdx)
                        attrSb.Remove(textIdx, closeIdx - textIdx + 1);
                }
            }

            bool hasChildren = !SuppressNativeReplacementChildren(tag)
                && (node.Children != null && node.Children.Count > 0)
                && (mapping.TextHandling != TextHandling.AssignToText || useContainerMode);

            Indent(sb, depth);
            sb.Append('<').Append(mapping.UxmlType).Append(attrSb.ToString());
            if (!hasChildren && mapping.TextHandling != TextHandling.WrapInLabel)
            {
                sb.Append(" />\n");
                return;
            }
            sb.Append(">\n");

            // Emit list-item bullet/number as a leading Label child.
            if (liMarker != null)
            {
                string markerClass = state.AllocateClass("marker");
                state.AddRule("." + markerClass, ListMarkers.MarkerLabelDecls(parent));
                Indent(sb, depth + 1);
                sb.Append("<ui:Label class=\"").Append(markerClass)
                  .Append("\" text=\"").Append(XmlEscape(liMarker)).Append("\" />\n");
            }

            // ::before pseudo-element materialisation.
            EmitPseudo(node, state, sb, depth + 1, PseudoElements.Kind.Before);

            if (mapping.TextHandling == TextHandling.WrapInLabel)
            {
                string ownText = GatherDirectText(node);
                if (!string.IsNullOrWhiteSpace(ownText))
                {
                    string finalText = ApplyTextEffects(ownText.Trim(), node, state);
                    // CSS text-align inherits down through block boxes;
                    // UI Toolkit's -unity-text-align is per-TextElement.
                    // The author rule lives on the parent div, not on
                    // the synthetic Label we emit below — copy the
                    // resolved alignment + width onto the Label so
                    // right/center alignment actually fires (e.g. the
                    // "CAM" header that should hug its column's right
                    // edge stays left-aligned without this).
                    string textAlign = ResolvedTextProp(node, state, "-unity-text-align");
                    string labelInline = null;
                    if (!string.IsNullOrEmpty(textAlign))
                        labelInline = "-unity-text-align: " + textAlign + "; width: 100%;";
                    Indent(sb, depth + 1);
                    sb.Append("<ui:Label");
                    if (!string.IsNullOrEmpty(labelInline))
                        sb.Append(" style=\"").Append(XmlEscape(labelInline)).Append("\"");
                    sb.Append(" text=\"").Append(XmlEscape(finalText)).Append("\" />\n");
                }
            }

            if (hasChildren)
            {
                // Push CSS-inherited text props for descendant ApplyTextEffects.
                string ownTransform = ResolvedTextProp(node, state, "text-transform");
                string ownDecoration = ResolvedTextProp(node, state, "text-decoration");
                bool pushedT = false, pushedD = false;
                if (!string.IsNullOrEmpty(ownTransform)) { state.InheritedTextTransform.Push(ownTransform); pushedT = true; }
                if (!string.IsNullOrEmpty(ownDecoration)) { state.InheritedTextDecoration.Push(ownDecoration); pushedD = true; }

                // For WrapInLabel: text was already emitted as a single
                // synthetic Label above; skip text-only children to avoid
                // emitting them again.
                bool skipTextChildren = mapping.TextHandling == TextHandling.WrapInLabel;
                if (skipTextChildren)
                {
                    var nonText = new List<HtmlLoader.HtmlNode>(node.Children.Count);
                    foreach (var c in node.Children) if (!c.IsText) nonText.Add(c);
                    EmitChildren(node, nonText, state, sb, depth + 1);
                }
                else
                {
                    EmitChildren(node, node.Children, state, sb, depth + 1);
                }

                if (pushedT) state.InheritedTextTransform.Pop();
                if (pushedD) state.InheritedTextDecoration.Pop();
            }

            // ::after pseudo-element materialisation.
            EmitPseudo(node, state, sb, depth + 1, PseudoElements.Kind.After);

            Indent(sb, depth);
            sb.Append("</").Append(mapping.UxmlType).Append(">\n");

            // Pop the table-collapse stack for the table we pushed above.
            if (tag == "table" && state.TableCollapseStack.Count > 0)
                state.TableCollapseStack.Pop();
        }

        static bool DeclsContain(List<KeyValuePair<string, string>> decls, string property)
        {
            if (decls == null || string.IsNullOrEmpty(property))
                return false;
            foreach (var kv in decls)
                if (string.Equals(kv.Key, property, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        static bool SuppressNativeReplacementChildren(string tag)
        {
            switch (tag)
            {
                case "select":
                case "progress":
                case "meter":
                case "img":
                case "canvas":
                case "video":
                case "audio":
                case "br":
                case "hr":
                    return true;
                default:
                    return false;
            }
        }

        static void ApplySelectOptions(HtmlLoader.HtmlNode select, ElementMapping mapping)
        {
            // data-h2u-dynamic opts out of baking <option> text into the
            // `choices` attr; the runtime calls SetChoices later.
            if (DynamicMarkers.HasDynamicFlag(select))
                return;

            var choices = new List<string>();
            string selected = null;
            CollectSelectOptions(select, choices, ref selected);
            if (choices.Count == 0)
                return;

            mapping.ExtraAttrs["choices"] = string.Join(",", choices);
            mapping.ExtraAttrs["value"] = selected ?? choices[0];
        }

        static void CollectSelectOptions(HtmlLoader.HtmlNode node, List<string> choices, ref string selected)
        {
            if (node?.Children == null)
                return;

            foreach (var child in node.Children)
            {
                if (child == null || child.IsText)
                    continue;
                string tag = (child.Tag ?? "").ToLowerInvariant();
                if (tag == "option")
                {
                    string text = GatherInlineText(child).Trim();
                    if (string.IsNullOrEmpty(text))
                        text = AttrUtil.Get(child.Attrs, "value") ?? string.Empty;
                    if (string.IsNullOrEmpty(text))
                        continue;
                    choices.Add(text);
                    if (selected == null && AttrUtil.Has(child.Attrs, "selected"))
                        selected = text;
                    continue;
                }

                if (tag == "optgroup")
                    CollectSelectOptions(child, choices, ref selected);
            }
        }

        static void EmitPseudo(HtmlLoader.HtmlNode node, EmitState state,
                               StringBuilder sb, int depth, PseudoElements.Kind kind)
        {
            if (state.PseudoEntries == null
                || !state.PseudoEntries.TryGetValue(node, out var entries)) return;
            foreach (var e in entries)
            {
                if (e.Kind != kind) continue;
                var mapped = StyleMapper.MapDeclarations(e.Decls);
                state.Warnings.AddRange(mapped.Warnings);
                // Drop the `content` decl from USS — it's already consumed as
                // the Label text. (StyleMapper already drops it as unknown.)
                string ownClass = state.AllocateClass(kind == PseudoElements.Kind.Before ? "before" : "after");
                if (mapped.Decls.Count > 0) state.AddRule("." + ownClass, mapped.Decls);
                string text = ApplyTextEffectsRaw(e.Content, mapped.Decls);
                Indent(sb, depth);
                sb.Append("<odd:Html2UxmlLabel class=\"").Append(ownClass)
                  .Append("\" text=\"").Append(XmlEscape(text)).Append("\" />\n");
            }
        }

        // Apply text-transform / text-decoration from a pseudo-element's own
        // decls (no node lookup, no inherited cascade).
        static string ApplyTextEffectsRaw(string text, List<KeyValuePair<string, string>> decls)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string transform = null, decoration = null;
            foreach (var kv in decls)
            {
                if (kv.Key == "text-transform")  transform = kv.Value;
                else if (kv.Key == "text-decoration") decoration = kv.Value;
            }
            if (transform != null)  text = TextTransform.ApplyTransform(text, transform);
            if (decoration != null) text = TextTransform.ApplyDecoration(text, decoration);
            return text;
        }

        static bool NodeContainsCheckableInput(HtmlLoader.HtmlNode node)
        {
            if (node?.Children == null) return false;
            foreach (var c in node.Children)
            {
                if (c.IsText) continue;
                if (FormControlAssociation.IsCheckableInput(c)) return true;
            }
            return false;
        }

        // Walk RawRules looking for `input[type="X"]` selectors whose
        // attribute predicate matches this input. Returns the union of
        // mapped decls (or null when nothing matches). Compensates for the
        // resolver dropping rules with attribute selectors. Rejects rules
        // whose selector includes a pseudo-element (`::-webkit-slider-thumb`,
        // `::placeholder`, etc.) — those style Unity-internal subwidgets we
        // don't expose, so attaching their decls to the host input is wrong.
        static List<KeyValuePair<string, string>> ResolveInputAttributeRule(
            HtmlLoader.HtmlNode node, EmitState state)
        {
            if (state.RawRules == null) return null;
            string nodeType = (AttrUtil.Get(node.Attrs, "type") ?? "text").ToLowerInvariant();
            var collected = new List<KeyValuePair<string, string>>();
            foreach (var rule in state.RawRules)
            {
                var sel = rule.ParsedSelector;
                if (sel?.Chain == null || sel.Chain.Count == 0) continue;
                var (_, comp) = sel.Chain[sel.Chain.Count - 1];
                if (comp == null) continue;
                if ((comp.Tag ?? "").ToLowerInvariant() != "input") continue;
                if (comp.Attrs == null || comp.Attrs.Count == 0) continue;
                if (HasPseudoElement(comp)) continue;
                bool typeMatches = false;
                foreach (var (op, name, value) in comp.Attrs)
                {
                    if ((name ?? "").ToLowerInvariant() != "type") continue;
                    if (op == "=" && (value ?? "").ToLowerInvariant() == nodeType)
                    { typeMatches = true; break; }
                }
                if (!typeMatches) continue;
                var mapped = StyleMapper.MapDeclarations(rule.Decls);
                state.Warnings.AddRange(mapped.Warnings);
                collected.AddRange(mapped.Decls);
            }
            return collected.Count > 0 ? collected : null;
        }

        sealed class RangePseudoRules
        {
            public readonly List<KeyValuePair<string, string>> Track = new List<KeyValuePair<string, string>>();
            public readonly List<KeyValuePair<string, string>> Thumb = new List<KeyValuePair<string, string>>();
            public readonly List<KeyValuePair<string, string>> Fill = new List<KeyValuePair<string, string>>();
            public bool HasTrack;
            public bool HasFill;
            public bool Any => Track.Count > 0 || Thumb.Count > 0 || Fill.Count > 0 || HasTrack || HasFill;
        }

        static RangePseudoRules ResolveRangePseudoRules(
            HtmlLoader.HtmlNode node, EmitState state)
        {
            if (state.RawRules == null) return null;
            if ((AttrUtil.Get(node.Attrs, "type") ?? "").ToLowerInvariant() != "range")
                return null;

            var result = new RangePseudoRules();
            bool hostAppearanceNone = RangeHostAppearanceNone(node, state);
            foreach (var rule in state.RawRules)
            {
                var sel = rule.ParsedSelector;
                if (sel?.Chain == null || sel.Chain.Count == 0) continue;
                var (_, comp) = sel.Chain[sel.Chain.Count - 1];
                if (comp == null) continue;
                if ((comp.Tag ?? "").ToLowerInvariant() != "input") continue;
                string target = RangePseudoTarget(comp);
                if (target == null) continue;
                if (!InputTypeSelectorMatches(comp, "range")) continue;

                var mapped = StyleMapper.MapDeclarations(rule.Decls);
                state.Warnings.AddRange(mapped.Warnings);
                if (mapped.Decls.Count == 0)
                    continue;

                if (target == "track")
                {
                    result.HasTrack = true;
                    result.Track.AddRange(mapped.Decls);
                }
                else if (target == "thumb")
                {
                    if (!hostAppearanceNone)
                        continue;
                    result.Thumb.AddRange(NormalizeSliderThumbDecls(mapped.Decls));
                }
                else if (target == "fill")
                {
                    result.HasFill = true;
                    result.Fill.AddRange(mapped.Decls);
                }
            }
            return result.Any ? result : null;
        }

        static bool RangeHostAppearanceNone(HtmlLoader.HtmlNode node, EmitState state)
        {
            string inlineAppearance = InlineStyleProp(node, "appearance")
                ?? InlineStyleProp(node, "-webkit-appearance");
            if (CssValueIsNone(inlineAppearance))
                return true;

            if (state.RawRules == null)
                return false;
            foreach (var rule in state.RawRules)
            {
                var sel = rule.ParsedSelector;
                if (sel?.Chain == null || sel.Chain.Count == 0) continue;
                var (_, comp) = sel.Chain[sel.Chain.Count - 1];
                if (comp == null) continue;
                if ((comp.Tag ?? "").ToLowerInvariant() != "input") continue;
                if (!InputTypeSelectorMatches(comp, "range")) continue;
                if (HasPseudoElement(comp)) continue;
                foreach (var decl in rule.RawDecls)
                {
                    string prop = (decl.Prop ?? "").ToLowerInvariant();
                    if ((prop == "appearance" || prop == "-webkit-appearance")
                        && CssValueIsNone(decl.Value))
                        return true;
                }
            }
            return false;
        }

        static bool CssValueIsNone(string value)
            => !string.IsNullOrWhiteSpace(value)
               && value.Trim().ToLowerInvariant() == "none";

        static string RangePseudoTarget(CssCompoundSelector comp)
        {
            if (comp?.Pseudo == null) return null;
            foreach (var ps in comp.Pseudo)
            {
                string low = (ps ?? "").ToLowerInvariant();
                switch (low)
                {
                    case "::-webkit-slider-runnable-track":
                    case "::-moz-range-track":
                    case "::-ms-track":
                        return "track";
                    case "::-webkit-slider-thumb":
                    case "::-moz-range-thumb":
                    case "::-ms-thumb":
                        return "thumb";
                    case "::-moz-range-progress":
                    case "::-ms-fill-lower":
                        return "fill";
                }
            }
            return null;
        }

        static bool InputTypeSelectorMatches(CssCompoundSelector comp, string expectedType)
        {
            bool hasTypePredicate = false;
            foreach (var (op, name, value) in comp.Attrs)
            {
                if ((name ?? "").ToLowerInvariant() != "type") continue;
                hasTypePredicate = true;
                if (op == "=" && (value ?? "").ToLowerInvariant() == expectedType)
                    return true;
            }
            return !hasTypePredicate && expectedType == "text";
        }

        static List<KeyValuePair<string, string>> NormalizeSliderThumbDecls(
            List<KeyValuePair<string, string>> decls)
        {
            var result = new List<KeyValuePair<string, string>>(decls.Count + 2);
            string width = null;
            string marginTop = null;
            bool hasMarginLeft = false;
            bool hasTop = false;

            foreach (var kv in decls)
            {
                if (kv.Key == "margin-top")
                {
                    marginTop = kv.Value;
                    continue;
                }
                if (kv.Key == "margin-left")
                    hasMarginLeft = true;
                if (kv.Key == "top")
                    hasTop = true;
                if (kv.Key == "width")
                    width = kv.Value;
                result.Add(kv);
            }

            // Browsers position range thumbs by their center; this slider does
            // the same with `left: <percent>` plus a negative half-width margin.
            if (!hasMarginLeft && TryParsePx(width, out var w))
                result.Add(new KeyValuePair<string, string>("margin-left", FormatPx(-w * 0.5f)));

            // WebKit uses margin-top on the thumb to move it relative to the
            // track. Our thumb is absolutely positioned inside the track, so
            // convert the same authored value to `top`.
            if (!hasTop && !string.IsNullOrEmpty(marginTop))
                result.Add(new KeyValuePair<string, string>("top", marginTop));

            return result;
        }

        static bool TryParsePx(string value, out float px)
        {
            px = 0f;
            if (string.IsNullOrWhiteSpace(value))
                return false;
            string trimmed = value.Trim().ToLowerInvariant();
            if (!trimmed.EndsWith("px"))
                return false;
            trimmed = trimmed.Substring(0, trimmed.Length - 2).Trim();
            return float.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out px);
        }

        static string FormatPx(float value)
            => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "px";

        static bool HasPseudoElement(CssCompoundSelector comp)
        {
            if (comp?.Pseudo == null) return false;
            foreach (var ps in comp.Pseudo)
                if (!string.IsNullOrEmpty(ps) && ps.StartsWith("::")) return true;
            return false;
        }

        // Wraps EmitState's resolved cascade so CompactText (and any other
        // helper) doesn't depend on the private EmitState shape.
        sealed class ResolvedView : CompactText.EmitStateView
        {
            readonly EmitState _state;
            public ResolvedView(EmitState state) { _state = state; }
            public string ResolvedProp(HtmlLoader.HtmlNode node, string prop)
                => GetProp(_state, node, prop);

            public static string GetProp(EmitState state, HtmlLoader.HtmlNode node, string prop)
            {
                if (state.Resolved == null) return null;
                if (!state.Resolved.TryGetValue(node, out var decls) || decls == null) return null;
                for (int i = decls.Count - 1; i >= 0; i--)
                    if (decls[i].Key == prop) return decls[i].Value;
                return null;
            }
        }

        static bool ResolvedBorderCollapse(HtmlLoader.HtmlNode node, EmitState state)
        {
            if (state.Resolved == null) return false;
            if (!state.Resolved.TryGetValue(node, out var decls) || decls == null) return false;
            for (int i = decls.Count - 1; i >= 0; i--)
                if (decls[i].Key == "border-collapse")
                    return string.Equals(decls[i].Value.Trim(), "collapse",
                        System.StringComparison.OrdinalIgnoreCase);
            return false;
        }

        // ------------------------------------------------------------------
        // SVG emission.
        // ------------------------------------------------------------------
        static void EmitSvgNode(HtmlLoader.HtmlNode node, HtmlLoader.HtmlNode parent,
                                EmitState state, StringBuilder sb, int depth)
        {
            string raw = "";
            string sidRaw = AttrUtil.Get(node.Attrs, "data-svg-id");
            if (!string.IsNullOrEmpty(sidRaw)
                && int.TryParse(sidRaw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var sid)
                && sid >= 0 && sid < state.SvgBlocks.Count)
            {
                raw = state.SvgBlocks[sid] ?? "";
            }

            if (string.IsNullOrEmpty(raw))
            {
                Indent(sb, depth);
                sb.Append("<odd:Html2UxmlElement />\n");
                return;
            }

            string filename = SvgFilenameFor(node, parent, state);
            string bundlePath = state.SvgAssetsSubdir + "/" + filename;
            if (!state.SvgFiles.ContainsKey(bundlePath))
                state.SvgFiles[bundlePath] = raw;

            if (!state.SvgWarningEmitted)
            {
                state.Warnings.Add(
                    "SVG assets emitted; Unity requires SVG import support "
                  + "(com.unity.vectorgraphics) or these icons may render as warning triangles");
                state.SvgWarningEmitted = true;
            }

            string ownClass = state.AllocateClass(SvgEmitter.SvgContextSlug(node, parent));
            var decls = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("background-image", $"url(\"{bundlePath}\")"),
                new KeyValuePair<string, string>("-unity-background-scale-mode", "scale-to-fit"),
            };
            var dims = SvgEmitter.SvgDimensions(raw);
            if (dims.HasValue)
            {
                decls.Add(new KeyValuePair<string, string>("width",  dims.Value.w.ToString("g", System.Globalization.CultureInfo.InvariantCulture) + "px"));
                decls.Add(new KeyValuePair<string, string>("height", dims.Value.h.ToString("g", System.Globalization.CultureInfo.InvariantCulture) + "px"));
            }
            else
            {
                // No intrinsic dimensions on the SVG — modern sites size
                // icons via the parent (e.g. Google's voice/lens icons sit
                // inside a 40px button container). Fill the parent so the
                // icon shows up instead of collapsing to 0x0.
                decls.Add(new KeyValuePair<string, string>("width", "100%"));
                decls.Add(new KeyValuePair<string, string>("height", "100%"));
            }
            state.AddRule("." + ownClass, decls);

            var classes = new List<string> { ownClass };
            string authorClasses = AttrUtil.Get(node.Attrs, "class");
            if (!string.IsNullOrEmpty(authorClasses)) classes.Add(authorClasses);
            Indent(sb, depth);
            sb.Append("<odd:Html2UxmlElement class=\"")
              .Append(XmlEscape(string.Join(" ", classes)))
              .Append("\" />\n");
        }

        static string SvgFilenameFor(HtmlLoader.HtmlNode node, HtmlLoader.HtmlNode parent, EmitState state)
        {
            string slug = SvgEmitter.SvgContextSlug(node, parent);
            int count = (state.SvgNameCounts.TryGetValue(slug, out var n) ? n : 0) + 1;
            state.SvgNameCounts[slug] = count;
            return count == 1 ? slug + ".svg" : $"{slug}-{count}.svg";
        }


        // ------------------------------------------------------------------
        // USS emission.
        //
        // Per-property dedupe (last value wins) mirrors Python's
        // _emit_uss: when the same selector is added twice (UA-default
        // rule + author override on the same tag), the later decls
        // shadow the earlier. Without this the C# port emits every
        // shorthand-expanded border/background/sizing decl twice on
        // tags like `.h2u-tag-meter`.
        // ------------------------------------------------------------------
        static string EmitUss(EmitState state)
        {
            var sb = new StringBuilder();
            foreach (var selector in state.SelectorOrder)
            {
                var decls = state.RulesBySelector[selector];
                var dedup = new Dictionary<string, string>();
                var order = new List<string>();
                foreach (var kv in decls)
                {
                    if (!dedup.ContainsKey(kv.Key)) order.Add(kv.Key);
                    dedup[kv.Key] = kv.Value;
                }
                if (order.Count == 0) continue;
                if (state.CommentsBySelector.TryGetValue(selector, out var pending))
                {
                    foreach (var c in pending)
                        sb.Append("/*").Append(SanitizeCssComment(c)).Append("*/\n");
                }
                sb.Append(selector).Append(" {\n");
                foreach (var k in order)
                    sb.Append("    ").Append(k).Append(": ").Append(dedup[k]).Append(";\n");
                sb.Append("}\n\n");
            }
            return sb.ToString();
        }

        // CSS comments can't contain `*/`; collapse any inner occurrences to
        // `* /` so the emitted block stays well-formed.
        static string SanitizeCssComment(string body)
        {
            if (string.IsNullOrEmpty(body)) return body ?? string.Empty;
            return body.Replace("*/", "* /");
        }

        // ------------------------------------------------------------------
        // Helpers.
        // ------------------------------------------------------------------
        static void Indent(StringBuilder sb, int depth)
        {
            for (int i = 0; i < depth; i++) sb.Append("    ");
        }

        static string GatherDirectText(HtmlLoader.HtmlNode node)
        {
            if (node?.Children == null) return "";
            var sb = new StringBuilder();
            foreach (var c in node.Children)
                if (c.IsText && !c.IsComment && !string.IsNullOrEmpty(c.Text)) sb.Append(c.Text);
            return sb.ToString();
        }

        static string GatherInlineText(HtmlLoader.HtmlNode node)
        {
            if (node == null || node.IsComment) return "";
            if (node.IsText) return node.Text ?? "";
            if (node.Children == null) return "";
            var sb = new StringBuilder();
            foreach (var c in node.Children) sb.Append(GatherInlineText(c));
            return sb.ToString();
        }

        // Emit an HTML comment as a UXML comment. Sanitizes `--` runs and a
        // trailing `-`, which XML disallows inside `<!-- ... -->`. Multi-line
        // bodies are split so each line keeps the surrounding indent.
        static void EmitHtmlComment(string text, StringBuilder sb, int depth)
        {
            string body = text ?? string.Empty;
            // XML forbids `--` inside a comment and a trailing `-` adjacent to `-->`.
            while (body.Contains("--")) body = body.Replace("--", "- -");
            if (body.EndsWith("-")) body += " ";
            bool multiLine = body.IndexOf('\n') >= 0;
            Indent(sb, depth);
            if (!multiLine)
            {
                sb.Append("<!-- ").Append(body.Trim()).Append(" -->\n");
                return;
            }
            sb.Append("<!--\n");
            foreach (var rawLine in body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                Indent(sb, depth);
                sb.Append("    ").Append(rawLine.TrimEnd()).Append('\n');
            }
            Indent(sb, depth);
            sb.Append("-->\n");
        }

        // text-transform + text-decoration are dropped by StyleMapper; reapply
        // by mutating the text payload at emit time. Looks at resolver output
        // first (author rule cascade), then the inheritance stack (since
        // Resolver doesn't propagate inherited props), then inline style.
        static string ApplyTextEffects(string text, HtmlLoader.HtmlNode node, EmitState state)
        {
            if (string.IsNullOrEmpty(text)) return text;
            string transform = ResolvedTextProp(node, state, "text-transform")
                            ?? PeekStack(state.InheritedTextTransform);
            string decoration = ResolvedTextProp(node, state, "text-decoration")
                             ?? PeekStack(state.InheritedTextDecoration);
            if (!string.IsNullOrEmpty(transform)) text = TextTransform.ApplyTransform(text, transform);
            if (!string.IsNullOrEmpty(decoration)) text = TextTransform.ApplyDecoration(text, decoration);
            string gradient = NodeTextGradient(node, state);
            if (gradient != null) text = TextGradient.MaybeWrapTextGradient(text, gradient);
            return text;
        }

        static string PeekStack(Stack<string> stack)
            => stack != null && stack.Count > 0 ? stack.Peek() : null;

        static string NodeTextGradient(HtmlLoader.HtmlNode node, EmitState state)
        {
            if (state.TextGradient == null
                || state.TextGradient.ByAuthorSelector.Count == 0
                || state.Resolved == null) return null;
            // The resolver's flattened decls don't carry selector references,
            // so re-scan: find any rule whose author selector matches the node
            // AND was registered as a gradient. Keep it cheap: only iterate the
            // gradient map (small) and re-match each.
            foreach (var entry in state.TextGradient.ByAuthorSelector)
            {
                if (state.GradientSelectorParsed == null) state.GradientSelectorParsed = new Dictionary<string, CssSelector>();
                if (!state.GradientSelectorParsed.TryGetValue(entry.Key, out var sel))
                {
                    sel = CssParser.ParseSelector(entry.Key);
                    state.GradientSelectorParsed[entry.Key] = sel;
                }
                if (sel == null) continue;
                if (NodeMatchesSelector(node, sel)) return entry.Value;
            }
            return null;
        }

        static bool NodeMatchesSelector(HtmlLoader.HtmlNode node, CssSelector sel)
        {
            // Walk via stored ancestor chain on the node? We don't keep one,
            // so reconstruct via Resolved which has node→decls but no parent
            // info. For the gradient case the selector is usually a simple
            // class on the node itself; just match that.
            if (sel.Chain == null || sel.Chain.Count == 0) return false;
            var (_, last) = sel.Chain[sel.Chain.Count - 1];
            return ResolverMatching.MatchesCompound(node, last,
                new List<HtmlLoader.HtmlNode>(), 0, new List<HtmlLoader.HtmlNode>());
        }

        static string ResolvedTextProp(HtmlLoader.HtmlNode node, EmitState state, string prop)
        {
            if (state.Resolved != null && state.Resolved.TryGetValue(node, out var decls) && decls != null)
            {
                // Last occurrence wins — matches CSS cascade.
                for (int i = decls.Count - 1; i >= 0; i--)
                    if (decls[i].Key == prop) return decls[i].Value;
            }
            // Fallback: inline `style="..."` on the node itself.
            string inline = AttrUtil.Get(node?.Attrs, "style");
            if (string.IsNullOrEmpty(inline)) return null;
            foreach (var raw in inline.Split(';'))
            {
                int colon = raw.IndexOf(':');
                if (colon <= 0) continue;
                string p = raw.Substring(0, colon).Trim();
                if (string.Equals(p, prop, System.StringComparison.OrdinalIgnoreCase))
                    return raw.Substring(colon + 1).Trim();
            }
            return null;
        }

        // Parse a `style="prop: value; prop: value;"` attribute. Naive split is
        // fine here — production CSS values may contain `;` only inside
        // strings/url(), which authors rarely put in inline style.
        static List<KeyValuePair<string, string>> ParseInlineStyle(string style)
        {
            var outDecls = new List<KeyValuePair<string, string>>();
            if (string.IsNullOrEmpty(style)) return outDecls;
            foreach (var raw in style.Split(';'))
            {
                int colon = raw.IndexOf(':');
                if (colon <= 0) continue;
                string prop = raw.Substring(0, colon).Trim();
                string value = raw.Substring(colon + 1).Trim();
                if (prop.Length == 0 || value.Length == 0) continue;
                outDecls.Add(new KeyValuePair<string, string>(prop, value));
            }
            return outDecls;
        }

        static string XmlEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            return s
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
        }
    }
}
