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

        /// <summary>
        /// The values the column's first condition compares against, as canonical text, or null when it
        /// is not filtered.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Text, and §33 argues why at length. This was <c>object?</c> and the whole of §32 was the
        /// price: a serializer flattens an <c>object</c> into whatever it uses for "some value" - a
        /// <c>JsonElement</c> for <c>System.Text.Json</c> - and four heuristic attempts then tried to
        /// guess the type back, with a check-box list stored as a JSON array left unguessable. No
        /// serializer can turn a string into anything but a string.
        /// </para>
        /// <para>
        /// A list rather than one value because the operator decides how many it takes: none for
        /// <c>IsNull</c>, one for <c>Equals</c>, two for <c>Between</c>, and one per ticked box for
        /// <c>In</c>. That is the same rule the model uses, so nothing is reshaped on the way in or out.
        /// </para>
        /// </remarks>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only",
            Justification = "The type is deserialized from storage, which needs the setter.")]
        public IList<string?>? FilterValues { get; set; }

        /// <summary>How the first condition compares.</summary>
        public FastGridFilterOperator? FilterOperator { get; set; }

        /// <summary>The values the second condition compares against, or null where there is none.</summary>
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only",
            Justification = "The type is deserialized from storage, which needs the setter.")]
        public IList<string?>? SecondFilterValues { get; set; }

        /// <summary>How the second condition compares, or null where there is none.</summary>
        public FastGridFilterOperator? SecondFilterOperator { get; set; }

        /// <summary>
        /// How the two conditions are joined, or null when nothing recorded a choice - which is every
        /// column with one condition.
        /// </summary>
        public LogicalFilterOperator? LogicalFilterOperator { get; set; }

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
        /// The same for the second condition's box - a <c>Between</c>'s upper bound, usually.
        /// </summary>
        /// <remarks>
        /// It exists because §33 drops a compound whole when any part of it cannot be rebuilt, so
        /// without a text of its own the second half of a date range would be the likeliest thing to
        /// take a whole filter down.
        /// </remarks>
        public string? SecondFilterText { get; set; }

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
