using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace ODDGames.Html2Uxml
{
    public enum TableMode
    {
        Static,
        DataGrid
    }

    public enum TableVirtualization
    {
        None,
        Rows
    }

    public enum TableSortDirection
    {
        None,
        Ascending,
        Descending
    }

    public sealed class TableColumn
    {
        public string Key { get; }
        public string Header { get; set; }
        public bool Visible { get; set; } = true;
        public bool Sortable { get; set; }
        public bool Resizable { get; set; }
        public bool Reorderable { get; set; }
        public ColumnFilter Filter { get; set; }
        public object FilterValue { get; set; }
        public Func<object, object> ValueGetter { get; set; }

        public TableColumn(string key, string header = null)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Column key cannot be empty.", nameof(key));

            Key = key;
            Header = string.IsNullOrEmpty(header) ? key : header;
        }

        public object GetValue(object row)
        {
            if (row == null)
                return null;

            if (ValueGetter != null)
                return ValueGetter(row);

            if (row is IDictionary<string, object> genericDictionary &&
                genericDictionary.TryGetValue(Key, out var genericValue))
                return genericValue;

            if (row is IDictionary dictionary && dictionary.Contains(Key))
                return dictionary[Key];

            var type = row.GetType();
            var property = type.GetProperty(
                Key,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            if (property != null)
                return property.GetValue(row);

            var field = type.GetField(
                Key,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
            return field != null ? field.GetValue(row) : null;
        }
    }

    public sealed class ColumnFilter
    {
        readonly Func<object, object, object, bool> _predicate;

        public string Kind { get; }
        public IReadOnlyList<object> Options { get; }

        ColumnFilter(string kind, Func<object, object, object, bool> predicate, IReadOnlyList<object> options = null)
        {
            Kind = kind;
            _predicate = predicate ?? ((_, _, _) => true);
            Options = options ?? Array.Empty<object>();
        }

        public bool Matches(object row, object cellValue, object filterValue)
        {
            if (filterValue == null)
                return true;
            if (filterValue is string s && string.IsNullOrWhiteSpace(s))
                return true;
            return _predicate(row, cellValue, filterValue);
        }

        public static ColumnFilter TextContains(StringComparison comparison = StringComparison.OrdinalIgnoreCase)
        {
            return new ColumnFilter("text-contains", (_, cell, filter) =>
                (cell?.ToString() ?? string.Empty).IndexOf(filter?.ToString() ?? string.Empty, comparison) >= 0);
        }

        public static ColumnFilter TextEquals(StringComparison comparison = StringComparison.OrdinalIgnoreCase)
        {
            return new ColumnFilter("text-equals", (_, cell, filter) =>
                string.Equals(cell?.ToString() ?? string.Empty, filter?.ToString() ?? string.Empty, comparison));
        }

        public static ColumnFilter Select(params object[] options)
        {
            return new ColumnFilter("select", (_, cell, filter) => Equals(cell, filter), options ?? Array.Empty<object>());
        }

        public static ColumnFilter MultiSelect(params object[] options)
        {
            return new ColumnFilter("multi-select", (_, cell, filter) =>
            {
                if (filter is IEnumerable values && !(filter is string))
                {
                    foreach (var value in values)
                    {
                        if (Equals(cell, value))
                            return true;
                    }
                    return false;
                }

                return Equals(cell, filter);
            }, options ?? Array.Empty<object>());
        }

        public static ColumnFilter Boolean()
        {
            return new ColumnFilter("boolean", (_, cell, filter) =>
            {
                if (!TryBool(cell, out var cellBool) || !TryBool(filter, out var filterBool))
                    return false;
                return cellBool == filterBool;
            });
        }

        public static ColumnFilter Custom(Func<object, object, object, bool> predicate)
        {
            return new ColumnFilter("custom", predicate);
        }

        static bool TryBool(object value, out bool result)
        {
            if (value is bool b)
            {
                result = b;
                return true;
            }

            return bool.TryParse(value?.ToString(), out result);
        }
    }
}
