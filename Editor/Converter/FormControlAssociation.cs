using System.Collections.Generic;

namespace ODDGames.Html2Uxml.Editor.Converter
{
    // Collect <datalist> options + associate adjacent <label> with checkable
    // inputs. Mirrors converter.py:
    //   _collect_datalist_options, _associated_label_index,
    //   _prepare_form_control_labels, _is_checkable_input
    public static class FormControlAssociation
    {
        public sealed class Associations
        {
            // datalist id -> ordered option values.
            public Dictionary<string, List<string>> Datalists
                = new Dictionary<string, List<string>>();
            // input node -> text pulled from the associated <label for=…>.
            public Dictionary<HtmlLoader.HtmlNode, string> InputLabelText
                = new Dictionary<HtmlLoader.HtmlNode, string>(
                    ReferenceEqualityComparer<HtmlLoader.HtmlNode>.Instance);
            // <label> nodes that have been merged into a control and should
            // not be emitted as standalone Labels.
            public HashSet<HtmlLoader.HtmlNode> SkipLabels
                = new HashSet<HtmlLoader.HtmlNode>(
                    ReferenceEqualityComparer<HtmlLoader.HtmlNode>.Instance);
        }

        public static Associations Build(HtmlLoader.HtmlNode root)
        {
            var assoc = new Associations();
            if (root == null) return assoc;
            CollectDatalists(root, assoc);
            PrepareLabels(root, assoc);
            return assoc;
        }

        static void CollectDatalists(HtmlLoader.HtmlNode node, Associations assoc)
        {
            if (node == null || node.IsText || node.Children == null) return;
            string tag = (node.Tag ?? "").ToLowerInvariant();
            if (tag == "datalist")
            {
                string id = AttrUtil.Get(node.Attrs, "id");
                if (!string.IsNullOrEmpty(id))
                {
                    var values = new List<string>();
                    foreach (var child in node.Children)
                    {
                        if (child.IsText) continue;
                        if ((child.Tag ?? "").ToLowerInvariant() != "option") continue;
                        string v = AttrUtil.Get(child.Attrs, "value")
                                ?? AttrUtil.Get(child.Attrs, "label")
                                ?? GatherInlineText(child);
                        if (!string.IsNullOrEmpty(v)) values.Add(v);
                    }
                    if (values.Count > 0) assoc.Datalists[id] = values;
                }
                return;
            }
            foreach (var child in node.Children) CollectDatalists(child, assoc);
        }

        static void PrepareLabels(HtmlLoader.HtmlNode parent, Associations assoc)
        {
            if (parent == null || parent.Children == null) return;
            var children = parent.Children;
            for (int idx = 0; idx < children.Count; idx++)
            {
                var node = children[idx];
                if (!IsCheckableInput(node)) { PrepareLabels(node, assoc); continue; }
                int labelIdx = AssociatedLabelIndex(children, idx);
                if (labelIdx < 0) continue;
                var label = children[labelIdx];
                string text = GatherInlineText(label);
                if (string.IsNullOrEmpty(text)) continue;
                assoc.InputLabelText[node] = text;
                assoc.SkipLabels.Add(label);
            }
            foreach (var child in children)
                if (!child.IsText) PrepareLabels(child, assoc);
        }

        static int AssociatedLabelIndex(List<HtmlLoader.HtmlNode> children, int inputIndex)
        {
            var node = children[inputIndex];
            string inputId = AttrUtil.Get(node.Attrs, "id") ?? "";
            for (int idx = inputIndex + 1; idx < children.Count; idx++)
            {
                var child = children[idx];
                if (child.IsComment) continue;
                if (child.IsText)
                {
                    if (!string.IsNullOrWhiteSpace(child.Text)) return -1;
                    continue;
                }
                if ((child.Tag ?? "").ToLowerInvariant() != "label") return -1;
                string labelFor = AttrUtil.Get(child.Attrs, "for") ?? "";
                if (inputId.Length > 0 && labelFor.Length > 0 && labelFor != inputId) return -1;
                return idx;
            }
            return -1;
        }

        public static bool IsCheckableInput(HtmlLoader.HtmlNode node)
        {
            if (node == null || node.IsText) return false;
            if ((node.Tag ?? "").ToLowerInvariant() != "input") return false;
            string t = (AttrUtil.Get(node.Attrs, "type") ?? "text").ToLowerInvariant();
            return t == "checkbox" || t == "radio";
        }

        static string GatherInlineText(HtmlLoader.HtmlNode node)
        {
            if (node == null || node.IsComment) return "";
            if (node.IsText) return node.Text ?? "";
            if (node.Children == null) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var c in node.Children) sb.Append(GatherInlineText(c));
            return sb.ToString();
        }
    }
}
