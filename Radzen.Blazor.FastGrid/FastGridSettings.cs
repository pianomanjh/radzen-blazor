using System.Collections.Generic;

namespace Radzen.FastGrid
{
    /// <summary>
    /// The part of a <see cref="RadzenFastGrid{TItem}" />'s state a user can change, in a form that can
    /// be stored and handed back.
    /// </summary>
    /// <remarks>
    /// Sort, filters, page - and column visibility once <c>AllowColumnPicking</c> is on, because that is
    /// the point at which a user can change it and storing it records a choice rather than repeating the
    /// markup. Width and order are still absent for that reason: nothing in this grid lets a user drag or
    /// resize a column, so persisting them would restore only what the markup already said. When those
    /// features arrive the type grows with them.
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

        /// <summary>
        /// Whether the column is drawn, or null when nothing recorded a choice - which is the case for
        /// every column on a grid without a column picker, and for a column the picker does not offer.
        /// Null restores nothing, so the markup's own <c>Visible</c> stands.
        /// </summary>
        public bool? Visible { get; set; }
    }
}
