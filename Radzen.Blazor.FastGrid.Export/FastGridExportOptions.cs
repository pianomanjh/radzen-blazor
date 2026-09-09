using System;
using System.Collections.Generic;

namespace Radzen.FastGrid.Export
{
    /// <summary>
    /// What an export can be told, which is deliberately not much.
    /// </summary>
    /// <remarks>
    /// Everything else an application might want - a title row, a second grid on the same sheet, its
    /// own colours - it does to the <see cref="Radzen.Documents.Spreadsheet.Workbook" /> it is handed.
    /// That is why the method answers the model rather than bytes, and it is why this type does not grow
    /// to meet each of those.
    /// </remarks>
    /// <typeparam name="TItem">The grid's row type, which <see cref="Columns" /> needs.</typeparam>
    public sealed class FastGridExportOptions<TItem>
    {
        /// <summary>The sheet's name.</summary>
        public string SheetName { get; set; } = "Sheet1";

        /// <summary>
        /// The rows to export, or null to export the grid's own.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A grid's own rows are its <c>FilteredRows</c>: everything the filters and the sort produce.
        /// On a client-bound grid that is already every row there is, and this is not needed. On a
        /// <c>LoadData</c> or async-executor grid the grid holds one page, because asking for more would
        /// mean running a query neither of those ran - so an application wanting the whole filtered set
        /// out of such a grid runs that query itself and hands the result over here.
        /// </para>
        /// <para>
        /// <strong>Nothing is re-filtered or re-sorted.</strong> These rows are written as they arrive,
        /// in the order they arrive, through the same columns. Handing over rows that do not match what
        /// the reader is looking at exports something they did not ask for, and this will not notice.
        /// </para>
        /// </remarks>
        public IEnumerable<TItem>? Rows { get; set; }

        /// <summary>
        /// The rows to stream, or null to take them from the grid.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Read only by the streaming export, and first when it is: a caller who owns the query outranks
        /// the executor the grid would otherwise run over
        /// <see cref="RadzenFastGrid{TItem}.FilteredQuery" />, for the reason <see cref="Rows" /> gives -
        /// the application owns that query and handing the result over is one property.
        /// </para>
        /// <para>
        /// Everything <see cref="Rows" /> says about what is not re-filtered and not re-sorted holds
        /// here too, and holds harder: these rows are written as they arrive and are never held, so
        /// nothing downstream is in a position to notice.
        /// </para>
        /// </remarks>
        public IAsyncEnumerable<TItem>? RowsAsync { get; set; }

        /// <summary>Whether the header row is written at all.</summary>
        public bool IncludeHeader { get; set; } = true;

        /// <summary>Whether the header row is frozen, so it stays in view while the rows scroll.</summary>
        public bool FreezeHeader { get; set; } = true;

        /// <summary>
        /// Whether the written range becomes a table, which is what gives Excel its filter buttons.
        /// </summary>
        /// <remarks>
        /// <strong>A table is not <c>SetAutoFilter</c>, and §40 flagged the difference.</strong> They
        /// look the same to someone in Excel and they are not the same in the file: a table carries a
        /// name, a style and a structured range that formulas can refer to. It is on by default because
        /// filter buttons are what the consuming application's users have, and it is a switch because an
        /// application whose readers open these somewhere other than Excel may find the difference.
        /// </remarks>
        public bool AddTable { get; set; } = true;

        /// <summary>Whether columns are sized to what is in them.</summary>
        public bool AutoFitColumns { get; set; } = true;

        /// <summary>
        /// Sizes each column the way the reader sized it, rather than to what is in it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>RadzenFastGrid</c> columns carry a width - declared in the markup, measured by the grid's
        /// own auto-fit, or settled on by a drag - and <see cref="ColumnBase{TItem}.EffectiveWidth" />
        /// is the one the column is drawn at. This exports that, which is the same rule the columns'
        /// order and visibility already follow: what comes out is what the reader arranged.
        /// </para>
        /// <para>
        /// <strong>For a streamed export it is also the cheaper answer.</strong> A width is written
        /// before any row is, so measuring one means buffering a sample of the rows to measure it
        /// against; a width the grid already knows needs no rows at all. Where every exported column has
        /// one the streaming path buffers nothing.
        /// </para>
        /// <para>
        /// Only a width in pixels can be used - <c>"200px"</c>, which is what a drag and an auto-fit
        /// both write. A column whose width is a percentage, an <c>em</c> or unset has nothing to copy
        /// and falls back to <see cref="AutoFitColumns" />.
        /// </para>
        /// </remarks>
        public bool UseGridColumnWidths { get; set; }

        /// <summary>
        /// What a particular column should do, by the identity §27 gave it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>This exists because every built-in column is <c>sealed</c>, which §40 did not
        /// notice.</strong> The section's whole answer for a column that wants to export something else
        /// was <see cref="IFastGridExportColumn{TItem}" /> - <em>"a column controls its own export
        /// through one interface"</em> - carried over from the consuming application, where the columns
        /// are the application's own types and implementing an interface on one is ordinary.
        /// <c>PropertyColumn</c>, <c>LookupColumn</c>, <c>CollectionColumn</c> and <c>TemplateColumn</c>
        /// here are all sealed, so nobody can implement it on the columns they actually declare.
        /// </para>
        /// <para>
        /// The interface is kept, because a custom column deriving from <c>ColumnBase</c> or
        /// <c>LookupColumnBase</c> can implement it and that is the shape the application had. This is
        /// the way in for the ordinary case: the key is the column's <c>UniqueID</c>, which §27 made the
        /// name a column already answers to and which the stored settings already key on. A column that
        /// implements the interface wins over an entry here, because the type is the more specific
        /// statement.
        /// </para>
        /// </remarks>
        public IDictionary<string, FastGridExportColumn<TItem>> Columns { get; } =
            new Dictionary<string, FastGridExportColumn<TItem>>(StringComparer.Ordinal);
    }

    /// <summary>
    /// What one column exports, said from outside the column.
    /// </summary>
    /// <typeparam name="TItem">The grid's row type.</typeparam>
    public sealed class FastGridExportColumn<TItem> : IFastGridExportColumn<TItem>
    {
        /// <inheritdoc />
        public Func<TItem, object?>? ExportValue { get; set; }

        /// <inheritdoc />
        public string? ExportTitle { get; set; }

        /// <inheritdoc />
        public string? ExportFormat { get; set; }

        /// <inheritdoc />
        public bool ExportIgnore { get; set; }
    }
}
