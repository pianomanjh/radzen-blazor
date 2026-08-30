using System.Collections.Generic;

namespace Radzen.FastGrid
{
    /// <summary>
    /// The part of a <see cref="RadzenFastGrid{TItem}" />'s state a user can change, in a form that can
    /// be stored and handed back.
    /// </summary>
    /// <remarks>
    /// Sort, filters and page - and nothing else, deliberately. Width, order and visibility are settings
    /// on a RadzenDataGrid because its user can drag, reorder and pick columns; this grid has no such UI,
    /// so persisting them would restore only what the markup already said. When those features arrive the
    /// type grows with them.
    /// </remarks>
    public class FastGridSettings
    {
        /// <summary>Per-column state, keyed by the column's property path.</summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only",
            Justification = "The type is deserialized from storage, which needs the setter.")]
        public IList<FastGridColumnSettings>? Columns { get; set; }

        /// <summary>The zero-based page, or null to leave it alone.</summary>
        public int? CurrentPage { get; set; }

        /// <summary>Rows per page, or null to leave it alone.</summary>
        public int? PageSize { get; set; }
    }

    /// <summary>
    /// One column's stored state. A column with no property path - a template column that names no
    /// member - cannot be identified across a reload and is not persisted.
    /// </summary>
    public class FastGridColumnSettings
    {
        /// <summary>The column's dotted property path, which is what identifies it.</summary>
        public string? Property { get; set; }

        /// <summary>The column's place in the sort, or null when it is not sorted.</summary>
        public SortOrder? SortOrder { get; set; }

        /// <summary>The column's filter value, or null when it is not filtered.</summary>
        public object? FilterValue { get; set; }

        /// <summary>How the filter value is compared.</summary>
        public FilterOperator? FilterOperator { get; set; }
    }
}
