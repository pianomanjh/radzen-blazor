using System;
using System.Collections.Generic;
using Radzen.Documents.Spreadsheet;

namespace Radzen.FastGrid.Export
{
    /// <summary>
    /// §40: a grid, as a workbook.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Its own package, and not for tidiness.</strong> Rooting <c>XlsxWriter</c> pulls
    /// <c>System.IO.Compression</c> and <c>System.Xml.Linq</c> into the trim graph of every consumer of
    /// the grid, whether or not it exports - and trimming is a property the grid claims and a test
    /// enforces. A separate package keeps the claim true and makes the payload opt-in.
    /// </para>
    /// </remarks>
    public static class FastGridExport
    {
        /// <summary>
        /// The grid's rows and columns, as a <see cref="Workbook" /> the caller then owns.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>A model, not a stream and not a download</strong> - and the measurement is what turns
        /// that from a taste into an argument. At 50,000 rows over eleven columns, building the workbook
        /// costs about <strong>610 ms and 212 MB</strong>; <c>SaveToStream</c> costs a further
        /// <strong>3.6 s and 417 MB</strong>, and <c>SaveAsCsv</c> about <strong>280 ms and 71 MB</strong>.
        /// So the expensive step is the one this method does not take. A <c>ToXlsxBytes</c> would have
        /// baked three and a half seconds into the seam and taken the choice of format with it; handing
        /// back the model leaves the caller free to write CSV instead, to write on a background thread,
        /// or to put two grids in one file.
        /// </para>
        /// <para>
        /// <strong>What it exports is <see cref="RadzenFastGrid{TItem}.FilteredRows" />: every row the
        /// filters and the sort produce, not the page.</strong> On a <c>LoadData</c> or async-executor
        /// grid the grid holds one page and that is what comes out, because asking for more would mean
        /// running a query neither of those ran. §40 takes that as the answer rather than as a
        /// limitation to work around, and says so here rather than in a footnote.
        /// </para>
        /// <para>
        /// The columns are the ones the reader is looking at, in the order they arranged them:
        /// <see cref="RadzenFastGrid{TItem}.VisibleColumns" /> is already ordered and already excludes
        /// what is hidden, so a hidden column is absent and a dragged column is where it was dragged.
        /// </para>
        /// </remarks>
        /// <typeparam name="TItem">The grid's row type.</typeparam>
        /// <param name="grid">The grid to export.</param>
        /// <param name="options">What to call the sheet and which of the trimmings to apply.</param>
        /// <returns>A workbook holding one sheet.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="grid" /> is null.</exception>
        public static Workbook ToWorkbook<TItem>(this RadzenFastGrid<TItem> grid,
            FastGridExportOptions<TItem>? options = null)
        {
            ArgumentNullException.ThrowIfNull(grid);

            options ??= new FastGridExportOptions<TItem>();

            var columns = Exported(grid, options, out var declared);

            // Enumerated once, into a list, because the sheet has to be sized before a cell can be
            // written and FilteredRows composes onto the source rather than holding a copy - so
            // counting it and then walking it would run the query twice, which over a queryable is two
            // round trips.
            var rows = new List<TItem>(grid.FilteredRows);

            var header = options.IncludeHeader ? 1 : 0;
            var workbook = new Workbook();
            var sheet = workbook.AddSheet(options.SheetName, rows.Count + header, Math.Max(columns.Count, 1));

            // The longest text seen per column, so the widths below cost no second pass over the data.
            var widest = options.AutoFitColumns ? new int[columns.Count] : null;

            // Resolved once per column rather than once per cell. The review found the first draft
            // asking Declared - a type test and a dictionary lookup - inside the row loop, for a
            // property that cannot change between rows: 550,000 of them on the 50,000-row grid the
            // measurement above is taken on.
            var formats = new string?[columns.Count];

            for (var c = 0; c < columns.Count; c++)
            {
                formats[c] = declared[c]?.ExportFormat is { Length: > 0 } format ? format : null;
            }

            if (options.IncludeHeader)
            {
                for (var c = 0; c < columns.Count; c++)
                {
                    var title = TitleOf(columns[c], declared[c]);

                    sheet.Cells[0, c].Value = title;
                    sheet.Cells[0, c].Format.Bold = true;

                    Widen(widest, c, title);
                }
            }

            for (var r = 0; r < rows.Count; r++)
            {
                var item = rows[r];

                for (var c = 0; c < columns.Count; c++)
                {
                    var column = columns[c];
                    var cell = sheet.Cells[r + header, c];

                    // The column's own text, always: it is the fallback for a value the writer will not
                    // take, and it is what the width is measured from. §4's column already holds the
                    // accessor its cells are drawn from, so this is not reflection - which is the whole
                    // difference between this and the resolver §38 surveyed.
                    var text = column.CellTextOf(item);

                    ExportValue.Write(cell, ValueOf(column, item, text, declared[c]), text);

                    if (formats[c] is { } format)
                    {
                        cell.Format.NumberFormat = format;
                    }

                    Widen(widest, c, cell.GetDisplayText());
                }
            }

            if (options.FreezeHeader && options.IncludeHeader)
            {
                sheet.Rows.Frozen = 1;
            }

            // A header row on its own is still a table, which is what Excel does and what the first
            // draft did not: the review found an empty export losing its filter buttons.
            if (options.AddTable && columns.Count > 0 && rows.Count + header > 0)
            {
                sheet.AddTable(TableName(options.SheetName),
                    new RangeRef(new CellRef(0, 0), new CellRef(rows.Count + header - 1, columns.Count - 1)),
                    hasHeaders: options.IncludeHeader);
            }

            if (widest is not null)
            {
                for (var c = 0; c < columns.Count; c++)
                {
                    sheet.Columns[c] = WidthFor(widest[c]);
                }
            }

            return workbook;
        }

        /// <summary>The columns that go in the file, in the order the reader arranged them.</summary>
        static List<ColumnBase<TItem>> Exported<TItem>(RadzenFastGrid<TItem> grid,
            FastGridExportOptions<TItem> options, out IFastGridExportColumn<TItem>?[] declared)
        {
            var visible = grid.VisibleColumns;
            var columns = new List<ColumnBase<TItem>>(visible.Count);
            var settings = new List<IFastGridExportColumn<TItem>?>(visible.Count);

            for (var i = 0; i < visible.Count; i++)
            {
                var said = Declared(visible[i], options);

                if (said is { ExportIgnore: true })
                {
                    continue;
                }

                columns.Add(visible[i]);
                settings.Add(said);
            }

            declared = settings.ToArray();

            return columns;
        }

        /// <summary>
        /// What the column says about its own export, from whichever of the two routes has it.
        /// </summary>
        /// <remarks>
        /// The column's own type first, because implementing the interface is the more specific
        /// statement; then the options, keyed by the identity §27 gave the column - which is the route
        /// that exists at all because every built-in column is sealed. See
        /// <see cref="FastGridExportOptions{TItem}.Columns" />.
        /// </remarks>
        static IFastGridExportColumn<TItem>? Declared<TItem>(ColumnBase<TItem> column,
            FastGridExportOptions<TItem> options)
        {
            if (column is IFastGridExportColumn<TItem> own)
            {
                return own;
            }

            return column.UniqueID is { Length: > 0 } id && options.Columns.TryGetValue(id, out var declared)
                ? declared
                : null;
        }

        static string TitleOf<TItem>(ColumnBase<TItem> column, IFastGridExportColumn<TItem>? declared) =>
            declared?.ExportTitle ?? column.HeaderText ?? column.PickerTitle;

        /// <summary>
        /// What the column exports for a row: its own value, or the text it drew.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>A template column exports blank without an <c>ExportValue</c>, and §40 corrected
        /// itself about this before the build.</strong> An earlier draft claimed the grid could offer a
        /// template column's rendered text. It cannot: <c>TemplateColumn</c> does not override
        /// <c>CellTextOf</c> - it draws a <c>RenderFragment</c>, and the only route to text is a
        /// renderer pass per cell outside the one Blazor is running.
        /// </para>
        /// <para>
        /// So the improvement over the resolver §38 surveyed is narrower than the survey claimed: it
        /// removes the reflection and it covers every bound column properly, and a template column needs
        /// the same declaration it always needed.
        /// </para>
        /// </remarks>
        static object? ValueOf<TItem>(ColumnBase<TItem> column, TItem item, string? text,
            IFastGridExportColumn<TItem>? declared)
        {
            if (declared is { ExportValue: { } exportValue })
            {
                return exportValue(item);
            }

            // The typed value where the column has one, so Excel sorts and sums it, and the text
            // otherwise. A lookup column's text is the name it resolved, which is the case that makes
            // this better than the raw member value.
            return column.CellValueOf(item) ?? text;
        }

        static void Widen(int[]? widest, int column, string? text)
        {
            if (widest is not null && text is { Length: var length } && length > widest[column])
            {
                widest[column] = length;
            }
        }

        /// <summary>
        /// A column width in the pixels the sheet's axis is measured in.
        /// </summary>
        /// <remarks>
        /// <strong><c>Axis.IsAutoFit</c> is not the lever §40 thought it was.</strong> The section
        /// mapped ClosedXML's <c>AdjustToContents</c> onto it and said "the writer measures the widths
        /// itself"; <c>IsAutoFit</c> is a read-only query, and the only method that sets the flag -
        /// <c>Axis.SetAutoFit</c> - is <c>internal</c> to <c>Radzen.Blazor</c>. So nothing outside that
        /// assembly can ask for auto-fit at all.
        /// <para>
        /// The width is therefore computed here, from text this method already has in hand while
        /// writing each cell, which is why it costs no second pass. Seven pixels a character is the
        /// default font's rough advance; the floor keeps a one-character column readable and the ceiling
        /// stops one long note making a column nobody can see past.
        /// </para>
        /// </remarks>
        static double WidthFor(int characters) => Math.Clamp(characters * 7 + 16, 48, 520);

        /// <summary>
        /// A table name taken from the sheet's, because a workbook may hold two of these.
        /// </summary>
        /// <remarks>
        /// The first draft named every table <c>Export</c>. Nothing here objects - <c>AddTable</c>
        /// checks uniqueness within one worksheet, and two sheets each carrying an <c>Export</c> save
        /// and load without complaint - but Excel wants a table name unique across the workbook, and
        /// putting two exported grids in one file is the thing the README shows first. Excel also
        /// refuses spaces and a leading digit in a table name, hence the rest.
        /// </remarks>
        static string TableName(string sheet)
        {
            var name = new System.Text.StringBuilder(sheet.Length + 1);

            for (var i = 0; i < sheet.Length; i++)
            {
                name.Append(char.IsLetterOrDigit(sheet[i]) ? sheet[i] : '_');
            }

            return name.Length == 0 || !char.IsLetter(name[0]) ? "Export" + name : name.ToString();
        }
    }
}
