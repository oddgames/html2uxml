using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    // Button variant for CSS features that need runtime layout support while
    // keeping button semantics and controller click wiring intact.
    [UxmlElement]
    public partial class Html2UxmlButton : Button
    {
        static readonly CustomStyleProperty<float> RowGap = new CustomStyleProperty<float>("--odd-row-gap");
        static readonly CustomStyleProperty<float> ColumnGap = new CustomStyleProperty<float>("--odd-column-gap");

        float _rowGap;
        float _columnGap;
        bool _hasGap;

        public Html2UxmlButton()
        {
            RegisterCallback<CustomStyleResolvedEvent>(OnStylesResolved);
            RegisterCallback<GeometryChangedEvent>(_ => ApplyGap());
        }

        void OnStylesResolved(CustomStyleResolvedEvent evt)
        {
            var style = evt.customStyle;
            float rg = 0f;
            float cg = 0f;
            bool gapAny = false;
            if (style.TryGetValue(RowGap, out var rgv)) { rg = rgv; gapAny = true; }
            if (style.TryGetValue(ColumnGap, out var cgv)) { cg = cgv; gapAny = true; }

            _rowGap = rg;
            _columnGap = cg;
            _hasGap = gapAny;
            ApplyGap();
        }

        void ApplyGap()
        {
            if (!_hasGap || childCount < 2) return;
            schedule.Execute(ApplyGapImmediate);
        }

        void ApplyGapImmediate()
        {
            if (!_hasGap || childCount < 2) return;
            bool isColumn = resolvedStyle.flexDirection == FlexDirection.Column
                         || resolvedStyle.flexDirection == FlexDirection.ColumnReverse;
            bool isReverse = resolvedStyle.flexDirection == FlexDirection.RowReverse
                          || resolvedStyle.flexDirection == FlexDirection.ColumnReverse;
            float spacing = isColumn ? _rowGap : _columnGap;
            int visualIndex = 0;
            for (int i = 0; i < childCount; i++)
            {
                var c = ElementAt(i);
                c.style.marginTop = 0f;
                c.style.marginBottom = 0f;
                c.style.marginLeft = 0f;
                c.style.marginRight = 0f;
                if (visualIndex == 0)
                {
                    visualIndex++;
                    continue;
                }

                if (isColumn)
                {
                    if (isReverse) c.style.marginBottom = spacing;
                    else c.style.marginTop = spacing;
                }
                else
                {
                    if (isReverse) c.style.marginRight = spacing;
                    else c.style.marginLeft = spacing;
                }
                visualIndex++;
            }
        }
    }
}
