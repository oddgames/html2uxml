using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace ODDGames.Html2Uxml
{
    [UxmlElement]
    public partial class Html2UxmlTable : Html2UxmlPanel
    {
        bool _layoutScheduled;

        public Html2UxmlTable()
        {
            RegisterCallback<AttachToPanelEvent>(_ => ScheduleTableLayout());
            RegisterCallback<GeometryChangedEvent>(_ => ScheduleTableLayout());
        }

        internal void ScheduleTableLayout()
        {
            if (panel == null || _layoutScheduled)
                return;

            _layoutScheduled = true;
            schedule.Execute(() =>
            {
                _layoutScheduled = false;
                ApplyTableLayout();
            }).StartingIn(0);
        }

        void ApplyTableLayout()
        {
            float tableWidth = contentRect.width;
            if (float.IsNaN(tableWidth) || tableWidth <= 0.5f)
                return;

            var rows = new List<Html2UxmlTableRow>();
            CollectRows(this, rows);
            if (rows.Count == 0)
                return;

            int columnCount = 0;
            foreach (var row in rows)
                columnCount = Mathf.Max(columnCount, row.ColumnSpanCount);
            if (columnCount == 0)
                return;

            var minWidths = new float[columnCount];
            foreach (var row in rows)
            {
                int column = 0;
                foreach (var cell in row.Cells())
                {
                    int span = Mathf.Clamp(cell.ColSpan, 1, columnCount - column);
                    float perColumn = Mathf.Max(1f, EstimateMinWidth(cell) / span);
                    for (int i = 0; i < span && column + i < columnCount; i++)
                        minWidths[column + i] = Mathf.Max(minWidths[column + i], perColumn);
                    column += span;
                    if (column >= columnCount)
                        break;
                }
            }

            float totalMin = 0f;
            for (int i = 0; i < minWidths.Length; i++)
                totalMin += minWidths[i];

            float extra = Mathf.Max(0f, tableWidth - totalMin);
            float perExtra = columnCount > 0 ? extra / columnCount : 0f;
            var widths = new float[columnCount];
            for (int i = 0; i < widths.Length; i++)
                widths[i] = minWidths[i] + perExtra;

            foreach (var row in rows)
            {
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Stretch;
                row.style.width = tableWidth;

                int column = 0;
                foreach (var cell in row.Cells())
                {
                    int span = Mathf.Clamp(cell.ColSpan, 1, columnCount - column);
                    float spanWidth = 0f;
                    for (int i = 0; i < span && column + i < widths.Length; i++)
                        spanWidth += widths[column + i];

                    ApplyCellWidth(cell, spanWidth);
                    column += span;
                    if (column >= columnCount)
                        break;
                }
            }
        }

        static void ApplyCellWidth(Html2UxmlTableCell cell, float width)
        {
            if (width <= 0f)
                return;

            cell.style.flexGrow = 0f;
            cell.style.flexShrink = 0f;
            cell.style.flexBasis = width;
            cell.style.width = width;
        }

        static void CollectRows(VisualElement root, List<Html2UxmlTableRow> rows)
        {
            foreach (var child in root.Children())
            {
                if (child is Html2UxmlTable)
                    continue;
                if (child is Html2UxmlTableRow row)
                {
                    rows.Add(row);
                    continue;
                }
                CollectRows(child, rows);
            }
        }

        static float EstimateMinWidth(VisualElement element)
        {
            float contentWidth = 0f;
            if (element is TextElement textElement)
                contentWidth = Mathf.Max(contentWidth, EstimateTextWidth(textElement));

            foreach (var child in element.Children())
            {
                if (child is Html2UxmlTable)
                    continue;
                contentWidth = Mathf.Max(contentWidth, EstimateMinWidth(child));
            }

            var style = element.resolvedStyle;
            return contentWidth
                + style.paddingLeft
                + style.paddingRight
                + style.borderLeftWidth
                + style.borderRightWidth;
        }

        static float EstimateTextWidth(TextElement element)
        {
            string text = element.text ?? string.Empty;
            if (text.Length == 0)
                return 0f;

            int longest = 0;
            int current = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n' || c == '\r')
                {
                    longest = Mathf.Max(longest, current);
                    current = 0;
                }
                else
                {
                    current++;
                }
            }
            longest = Mathf.Max(longest, current);

            float fontSize = element.resolvedStyle.fontSize;
            if (fontSize <= 0f || float.IsNaN(fontSize))
                fontSize = 14f;

            return longest * fontSize * 0.56f;
        }
    }

    [UxmlElement]
    public partial class Html2UxmlTableSection : Html2UxmlPanel
    {
        public Html2UxmlTableSection()
        {
            RegisterCallback<AttachToPanelEvent>(_ => NotifyTable());
            RegisterCallback<GeometryChangedEvent>(_ => NotifyTable());
        }

        void NotifyTable()
        {
            FindTable(this)?.ScheduleTableLayout();
        }

        internal static Html2UxmlTable FindTable(VisualElement element)
        {
            for (var current = element.parent; current != null; current = current.parent)
            {
                if (current is Html2UxmlTable table)
                    return table;
            }
            return null;
        }
    }

    [UxmlElement]
    public partial class Html2UxmlTableRow : Html2UxmlPanel
    {
        public Html2UxmlTableRow()
        {
            RegisterCallback<AttachToPanelEvent>(_ => NotifyTable());
            RegisterCallback<GeometryChangedEvent>(_ => NotifyTable());
        }

        internal int ColumnSpanCount
        {
            get
            {
                int count = 0;
                foreach (var cell in Cells())
                    count += Mathf.Max(1, cell.ColSpan);
                return count;
            }
        }

        internal IEnumerable<Html2UxmlTableCell> Cells()
        {
            foreach (var child in Children())
            {
                if (child is Html2UxmlTableCell cell)
                    yield return cell;
            }
        }

        void NotifyTable()
        {
            Html2UxmlTableSection.FindTable(this)?.ScheduleTableLayout();
        }
    }

    [UxmlElement]
    public partial class Html2UxmlTableCell : Html2UxmlPanel
    {
        int _colSpan = 1;
        int _rowSpan = 1;

        [UxmlAttribute("colspan")]
        public int ColSpan
        {
            get => _colSpan;
            set
            {
                _colSpan = Mathf.Max(1, value);
                NotifyTable();
            }
        }

        [UxmlAttribute("rowspan")]
        public int RowSpan
        {
            get => _rowSpan;
            set
            {
                _rowSpan = Mathf.Max(1, value);
                NotifyTable();
            }
        }

        [UxmlAttribute("header")]
        public bool Header { get; set; }

        public Html2UxmlTableCell()
        {
            RegisterCallback<AttachToPanelEvent>(_ => NotifyTable());
            RegisterCallback<GeometryChangedEvent>(_ => NotifyTable());
        }

        void NotifyTable()
        {
            Html2UxmlTableSection.FindTable(this)?.ScheduleTableLayout();
        }
    }
}
