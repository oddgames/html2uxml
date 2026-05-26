using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    // Runtime helper for the `data-h2u-list` author convention. The converter
    // freezes one DOM child of a marked container as a template (carrying
    // the .h2u-dyn-template class, with `display: none` from the emitted
    // helper rule). Callers feed `Bind` a sequence and a per-row filler;
    // the helper clones the template once per item, strips the template
    // class, and walks fields by name (data-h2u-field on the source HTML
    // becomes `name="h2u-field-<name>"` in the generated UXML).
    public static class Html2UxmlList
    {
        public const string TemplateClass = "h2u-dyn-template";
        public const string CloneClass = "h2u-dyn-clone";
        public const string FieldNamePrefix = "h2u-field-";

        public static void Bind<T>(VisualElement container,
                                   IEnumerable<T> items,
                                   Action<VisualElement, T> fill)
        {
            if (container == null) throw new ArgumentNullException(nameof(container));
            if (items == null) return;

            var template = FindTemplate(container);
            if (template == null)
            {
                Debug.LogWarning(
                    $"[html2uxml] Html2UxmlList.Bind: no .{TemplateClass} child inside container '{container.name}'.");
                return;
            }

            ClearClones(container);

            foreach (var item in items)
            {
                var clone = Clone(template);
                clone.RemoveFromClassList(TemplateClass);
                clone.AddToClassList(CloneClass);
                container.Add(clone);
                fill?.Invoke(clone, item);
            }
        }

        public static void Clear(VisualElement container)
        {
            if (container == null) return;
            ClearClones(container);
        }

        // Look up a named field inside a cloned row. The author marked the
        // descendant with `data-h2u-field="<name>"`; the converter assigned
        // it the corresponding UXML `name` so this lookup is O(tree).
        public static VisualElement Field(VisualElement row, string fieldName)
        {
            if (row == null || string.IsNullOrEmpty(fieldName)) return null;
            return row.Q<VisualElement>(name: FieldNamePrefix + fieldName);
        }

        static VisualElement FindTemplate(VisualElement container)
        {
            for (int i = 0; i < container.hierarchy.childCount; i++)
            {
                var c = container.hierarchy[i];
                if (c.ClassListContains(TemplateClass)) return c;
            }
            return null;
        }

        static void ClearClones(VisualElement container)
        {
            for (int i = container.hierarchy.childCount - 1; i >= 0; i--)
            {
                var c = container.hierarchy[i];
                if (c.ClassListContains(CloneClass)) c.RemoveFromHierarchy();
            }
        }

        // VisualElement has no built-in Clone — hand-roll one that copies
        // the runtime type, class list, name, and (for TextElement / Button)
        // text. Recurses through the hierarchy. Inline `style.*` runtime
        // mutations are NOT copied; USS classes handle styling.
        static VisualElement Clone(VisualElement source)
        {
            VisualElement copy;
            try
            {
                copy = (VisualElement)Activator.CreateInstance(source.GetType());
            }
            catch (Exception)
            {
                // Custom elements without a public parameterless ctor fall
                // back to a plain VisualElement; classes still carry across,
                // so USS styling survives.
                copy = new VisualElement();
            }

            copy.name = source.name;
            foreach (var cls in source.GetClasses())
                copy.AddToClassList(cls);

            if (source is Button srcBtn && copy is Button copyBtn)
                copyBtn.text = srcBtn.text;
            else if (source is TextElement srcText && copy is TextElement copyText)
                copyText.text = srcText.text;

            for (int i = 0; i < source.hierarchy.childCount; i++)
                copy.hierarchy.Add(Clone(source.hierarchy[i]));

            return copy;
        }
    }
}
