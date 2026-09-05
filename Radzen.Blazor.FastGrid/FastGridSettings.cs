using System.Collections.Generic;

namespace Radzen.FastGrid
{
    /// <summary>
    /// The part of a <see cref="RadzenFastGrid{TItem}" />'s state a user can change, in a form that can
    /// be stored and handed back.
    /// </summary>
    /// <remarks>
    /// Sort, filters, page - and column visibility once <c>AllowColumnPicking</c> is on, column width
    /// once <c>AllowColumnResize</c> is, and column order once <c>AllowColumnReorder</c> is, because
    /// those are the points at which a user can change them and storing one records a choice rather
    /// than repeating the markup. Nothing a user cannot change is stored, which is why each of the
    /// three is null until something records a choice.
    /// </remarks>
    public class FastGridSettings
    {
        /// <summary>Per-column state, keyed by the column's identity.</summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only",
            Justification = "The type is deserialized from storage, which needs the setter.")]
        public IList<FastGridColumnSettings>? Columns { get; set; }

        /// <summary>The zero-based page, or null to leave it alone.</summary>
        public int? CurrentPage { get; set; }

        /// <summary>Rows per page, or null to leave it alone.</summary>
        public int? PageSize { get; set; }
    }

    /// <summary>
    /// One column's stored state. A column that nothing names - a template column declaring neither a
    /// <c>UniqueID</c> nor a sort, or a column over a computed expression declaring no <c>UniqueID</c> -
    /// cannot be identified across a reload and is not persisted.
    /// </summary>
    public class FastGridColumnSettings
    {
        /// <summary>
        /// What identifies the column: its declared <c>UniqueID</c>, or the member it displays where
        /// nothing was declared.
        /// </summary>
        /// <remarks>
        /// Not the column's sort path, which is what this was before §27 and is why a column displaying
        /// one member and ordering by another was restored onto the wrong column.
        /// </remarks>
        public string? UniqueID { get; set; }

        /// <summary>The column's place in the sort, or null when it is not sorted.</summary>
        public SortOrder? SortOrder { get; set; }

        /// <summary>The column's filter value, or null when it is not filtered.</summary>
        public object? FilterValue { get; set; }

        /// <summary>How the filter value is compared.</summary>
        public FilterOperator? FilterOperator { get; set; }

        /// <summary>
        /// What was typed into the filter box to produce the value, or null when the filter came from
        /// anywhere else.
        /// </summary>
        /// <remarks>
        /// The value cannot stand in for it. "3.0" and "3" are one value and two different things to
        /// have typed, and on a lookup column - whose box matches names and filters by the ids they
        /// carry - the text is the only thing that tells a name nothing answered to from a check-box
        /// list with nothing ticked. Both are <c>In</c> over an empty list.
        /// </remarks>
        public string? FilterText { get; set; }

        /// <summary>
        /// Whether the column is drawn, or null when nothing recorded a choice - which is the case for
        /// every column on a grid without a column picker, and for a column the picker does not offer.
        /// Null restores nothing, so the markup's own <c>Visible</c> stands.
        /// </summary>
        public bool? Visible { get; set; }

        /// <summary>
        /// The width a user dragged the column to, as a CSS length, or null when none did. Null
        /// restores nothing, so the markup's own <c>Width</c> stands.
        /// </summary>
        public string? Width { get; set; }

        /// <summary>
        /// The position a user dragged the column to, or null when none did. Null restores nothing, so
        /// the markup's own <c>OrderIndex</c> stands.
        /// </summary>
        public int? OrderIndex { get; set; }
    }
}
