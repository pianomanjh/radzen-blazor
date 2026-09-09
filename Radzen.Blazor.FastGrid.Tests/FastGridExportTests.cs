using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Radzen.Documents.Spreadsheet;
using Radzen.FastGrid.Export;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// §40: a grid, as a workbook - asserted about the file rather than about the code that made it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every assertion here is made <em>after</em> a save and a load. <c>Workbook.LoadFromStream</c> is
    /// the seam that makes that possible - <c>XlsxReader</c> itself is internal - and the reason to go
    /// through it is that the writer is where the surprises live. Two of this section's findings are
    /// invisible before the round trip: a date is stored as a number carrying a date format, and a
    /// <c>bool</c> is stored as the number 1 carrying none.
    /// </para>
    /// <para>
    /// So <c>GetDisplayText()</c> rather than <c>Value</c> is what most of these read, because it is the
    /// number and its format together - which is what someone opening the file in Excel sees.
    /// </para>
    /// </remarks>
    public class FastGridExportTests
    {
        static TestContext Context()
        {
            var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            return ctx;
        }

        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx, RenderFragment columns,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>> extra = null) =>
            ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.AllowSorting, true);
                p.Add(g => g.ChildContent, columns);
                extra?.Invoke(p);
            });

        /// <summary>Saves and reloads, so what is asserted is what the file holds.</summary>
        static Worksheet RoundTrip(Workbook workbook, string sheet = "Sheet1")
        {
            using var stream = new MemoryStream();

            workbook.SaveToStream(stream);
            stream.Position = 0;

            return Workbook.LoadFromStream(stream).GetSheet(sheet)!;
        }

        static string[] Row(Worksheet sheet, int row, int columns) =>
            Enumerable.Range(0, columns).Select(c => sheet.Cells[row, c].GetDisplayText() ?? "").ToArray();

        static string[] Column(Worksheet sheet, int column, int rows) =>
            Enumerable.Range(0, rows).Select(r => sheet.Cells[r, column].GetDisplayText() ?? "").ToArray();

        // --- the round trip ---------------------------------------------------------------------

        [Fact]
        public void AGridBecomesAHeaderRowAndOneRowPerItem()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First name"),
                Columns.Property<Person, decimal>(p => p.Salary, title: "Salary")));

            var sheet = RoundTrip(cut.Instance.ToWorkbook());

            Assert.Equal(new[] { "First name", "Salary" }, Row(sheet, 0, 2));

            // Four sample rows, in the grid's own order.
            Assert.Equal(new[] { "Carol", "Alice", "Dave", "Bob" }, Column(sheet, 0, 5).Skip(1).ToArray());
        }

        [Fact]
        public void TheHeaderIsBoldAndFrozen()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, string>(p => p.First, title: "First")));

            var sheet = RoundTrip(cut.Instance.ToWorkbook());

            Assert.True(sheet.Cells[0, 0].Format.Bold);
            Assert.Equal(1, sheet.Rows.Frozen);
        }

        // --- the cases the resolver §38 surveyed gets wrong ---------------------------------------

        /// <summary>A lookup column exports the name it resolved, not the id behind it.</summary>
        /// <remarks>
        /// The case that motivates the whole <c>CellValueOf</c> / <c>CellTextOf</c> split: a lookup
        /// column has a typed value and it is an id nobody asked to see, so it returns null from the
        /// first and the exporter falls back to the second.
        /// </remarks>
        [Fact]
        public void ALookupColumnExportsItsNameRatherThanItsId()
        {
            using var ctx = Context();

            var lookup = FastGridLookup.Items(
                new[] { new Category { Id = 10, Name = "Hardware" }, new Category { Id = 20, Name = "Software" } },
                c => c.Id, c => c.Name);

            var cut = Render(ctx, Columns.Of(
                Columns.Lookup<Person, int>(p => p.CategoryId, lookup, title: "Category")));

            var sheet = RoundTrip(cut.Instance.ToWorkbook());

            // The fourth row's id is not in the map, and an unnamed id exports as the id - which is
            // what the column draws for it too, so the file and the screen still agree.
            Assert.Equal(new[] { "Hardware", "Software", "Hardware", "30" },
                Column(sheet, 0, 5).Skip(1).ToArray());
        }

        [Fact]
        public void AHiddenColumnIsAbsent()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First"),
                Columns.Property<Person, string>(p => p.Last, title: "Last"),
                Columns.Property<Person, decimal>(p => p.Salary, title: "Salary")));

            cut.InvokeAsync(() => cut.Instance.VisibleColumns[1].SetPicked(false));
            cut.Render();

            var sheet = RoundTrip(cut.Instance.ToWorkbook());

            Assert.Equal(new[] { "First", "Salary" }, Row(sheet, 0, 2));
        }

        [Fact]
        public async Task AReorderedColumnIsInTheReadersOrder()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First"),
                Columns.Property<Person, string>(p => p.Last, title: "Last"),
                Columns.Property<Person, decimal>(p => p.Salary, title: "Salary")),
                extra: p => p.Add(g => g.AllowColumnReorder, true));

            await cut.InvokeAsync(() => cut.Instance.ReorderColumn(0, 2));
            cut.Render();

            var sheet = RoundTrip(cut.Instance.ToWorkbook());

            Assert.Equal(new[] { "Last", "Salary", "First" }, Row(sheet, 0, 3));
        }

        [Fact]
        public async Task AFilteredGridExportsTheFilteredRows()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First")));

            await cut.InvokeAsync(() => cut.Instance.Filter(cut.Instance.VisibleColumns[0],
                new FastGridFilter(new FastGridFilterCondition(FastGridFilterOperator.StartsWith, "A"))));

            var sheet = RoundTrip(cut.Instance.ToWorkbook());

            Assert.Equal(new[] { "First", "Alice" }, Column(sheet, 0, 2));
        }

        /// <summary>
        /// A paged grid exports every filtered row, not the page on screen.
        /// </summary>
        /// <remarks>
        /// <strong>§40's central decision, and the mutation sweep found nothing pinning it.</strong> The
        /// section chose <em>"<c>View</c>, not the page - filtered and sorted, every matching row"</em>,
        /// and swapping <c>FilteredRows</c> for <c>DrawnRows</c> changed no test: every other grid here
        /// has paging off, so the two are the same list. They differ only here.
        /// <para>
        /// The other half of that decision - that a <c>LoadData</c> or async-executor grid holds one
        /// page and so exports one page - is <c>FilteredRows</c>' own documented behaviour and is tested
        /// where that property lives.
        /// </para>
        /// </remarks>
        [Fact]
        public void APagedGridExportsEveryRowAndNotThePage()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                    Columns.Property<Person, string>(p => p.First, title: "First")),
                extra: p =>
                {
                    p.Add(g => g.AllowPaging, true);
                    p.Add(g => g.PageSize, 2);
                });

            // Two rows on screen, so the arms genuinely differ.
            Assert.Equal(2, cut.FindAll("tbody tr").Count);

            var sheet = RoundTrip(cut.Instance.ToWorkbook());

            Assert.Equal(new[] { "First", "Carol", "Alice", "Dave", "Bob" }, Column(sheet, 0, 5));
        }

        /// <summary>
        /// A sorted grid exports in the sorted order, and the arms move.
        /// </summary>
        /// <remarks>
        /// Ascending by first name, because the sample's own leading row is Carol: descending would put
        /// Dave first and unsorted would leave Carol, so only ascending tells a restored sort from no
        /// sort at all. §39's finding, applied one section later.
        /// </remarks>
        [Fact]
        public async Task ASortedGridExportsInTheSortedOrder()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First")));

            Assert.Equal("Carol", Column(RoundTrip(cut.Instance.ToWorkbook()), 0, 2)[1]);

            await cut.InvokeAsync(() => cut.Instance.SortBy(cut.Instance.VisibleColumns[0]));

            Assert.Equal(new[] { "First", "Alice", "Bob", "Carol", "Dave" },
                Column(RoundTrip(cut.Instance.ToWorkbook()), 0, 5));
        }

        /// <summary>
        /// A template column exports blank without an <c>ExportValue</c> - §40 corrected itself here.
        /// </summary>
        /// <remarks>
        /// An earlier draft of the section claimed the grid could offer a template column's rendered
        /// text where the application's resolver had nothing. It cannot: <c>TemplateColumn</c> draws a
        /// <c>RenderFragment</c> and does not override <c>CellTextOf</c>, so the only route to text is a
        /// renderer pass per cell outside the one Blazor is running. The test asserts the correction
        /// rather than the claim.
        /// </remarks>
        [Fact]
        public void ATemplateColumnExportsBlank()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First"),
                Columns.Template<Person>(item => builder => builder.AddContent(0, "drawn " + item.Id),
                    title: "Actions", uniqueId: "Actions")));

            var sheet = RoundTrip(cut.Instance.ToWorkbook());

            Assert.Equal("Actions", sheet.Cells[0, 1].GetDisplayText());
            Assert.Equal(new[] { "", "", "", "" }, Column(sheet, 1, 5).Skip(1).ToArray());
        }

        // --- the types the writer refuses, which §40 did not know it refused ---------------------

        /// <summary>
        /// An enum column exports its text instead of throwing.
        /// </summary>
        /// <remarks>
        /// <strong>Without the coercion this does not export wrongly, it throws.</strong>
        /// <c>CellData</c>'s type switch ends in <c>throw new NotSupportedException</c> and its whole
        /// accepted set is null, string, bool, DateTime and the numeric primitives - so an enum, which
        /// is an ordinary thing for a grid to show, took the export down. §40 expected at worst a
        /// formatting difference here.
        /// </para>
        /// <para>
        /// <em>Senior</em> rather than <em>Senior engineer</em>: the <c>[Display]</c> name is §36's
        /// filter vocabulary, and a cell draws <c>ToString</c>. That is the right answer here for the
        /// reason the fallback exists at all - the export says what the cell said - but it is worth
        /// pinning, because the two names for one member are exactly the kind of thing a later edit
        /// unifies in the wrong direction.
        /// </para>
        /// </remarks>
        [Fact]
        public void AnEnumColumnExportsItsDisplayText()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, Grade>(p => p.Grade, title: "Grade")));

            var sheet = RoundTrip(cut.Instance.ToWorkbook());

            Assert.Equal(new[] { "Senior", "Junior", "Junior", "Senior" },
                Column(sheet, 0, 5).Skip(1).ToArray());
        }

        /// <summary>A Guid column exports its text instead of throwing, for the same reason.</summary>
        [Fact]
        public void AGuidColumnExportsItsText()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, Guid>(p => p.Reference, title: "Reference")));

            var sheet = RoundTrip(cut.Instance.ToWorkbook());

            Assert.All(Column(sheet, 0, 5).Skip(1), text => Assert.True(Guid.TryParse(text, out _), text));
        }

        /// <summary>
        /// A bool exports as a bool, and the file says so.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>This asserted the opposite until the file was read rather than the round trip.</strong>
        /// Loading the saved workbook back gives the number 1, which read as the format being unable to
        /// keep a boolean, so the export demoted bools to the column's text. The bytes say otherwise:
        /// <c>&lt;c r="A1" t="b"&gt;&lt;v&gt;1&lt;/v&gt;&lt;/c&gt;</c> is ECMA-376's boolean and is what
        /// Excel shows as TRUE. The fault is in <c>XlsxReader</c>, which drops the attribute - fixed on
        /// a branch offered upstream.
        /// </para>
        /// <para>
        /// So this reads the workbook before it is saved, and the saved bytes, rather than going through
        /// the reader that loses the answer. It goes back to the round trip when upstream takes the fix.
        /// </para>
        /// </remarks>
        [Fact]
        public void ABoolColumnExportsATypedBoolean()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, bool>(p => p.Remote, title: "Remote")));

            var sheet = cut.Instance.ToWorkbook().Sheets[0];

            Assert.Equal(CellDataType.Boolean, sheet.Cells[1, 0].ValueType);
            Assert.Equal(true, sheet.Cells[1, 0].Value);

            // And in the file, which is what Excel opens.
            using var stream = new MemoryStream();
            cut.Instance.ToWorkbook().SaveToStream(stream);
            stream.Position = 0;

            using var zip = new System.IO.Compression.ZipArchive(stream);
            using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());

            Assert.Contains("t=\"b\"", reader.ReadToEnd());
        }

        /// <summary>
        /// A number stays a number and a date stays a date, which is what an export is for.
        /// </summary>
        /// <remarks>
        /// The counterweight to the four tests above: everything becoming text would pass all of them
        /// and would be a worse exporter than the one §38 surveyed. A spreadsheet that cannot sum a
        /// salary column has lost the argument.
        /// </remarks>
        [Fact]
        public void ANumberIsStillANumberAndADateIsStillADate()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, decimal>(p => p.Salary, title: "Salary"),
                Columns.Property<Person, DateTime>(p => p.Hired, title: "Hired")));

            var sheet = RoundTrip(cut.Instance.ToWorkbook());

            Assert.Equal(CellDataType.Number, sheet.Cells[1, 0].ValueType);
            Assert.Equal(4000d, Convert.ToDouble(sheet.Cells[1, 0].Value));

            // A date is a number carrying a date format - that is what an .xlsx date is - so the type is
            // Number here and the format is what makes Excel draw a date.
            Assert.Equal(CellDataType.Number, sheet.Cells[1, 1].ValueType);
            Assert.Contains("yy", sheet.Cells[1, 1].Format.NumberFormat ?? "");
            Assert.Equal(new DateTime(2019, 5, 4), DateTime.FromOADate(Convert.ToDouble(sheet.Cells[1, 1].Value)));
        }

        // --- what a column can say about itself ---------------------------------------------------

        /// <summary>
        /// A column's export settings, said from the options - which is the only way a built-in column
        /// can be told, because every one of them is sealed.
        /// </summary>
        static FastGridExportOptions<Person> Saying(string uniqueId, Action<FastGridExportColumn<Person>> say)
        {
            var options = new FastGridExportOptions<Person>();
            var column = new FastGridExportColumn<Person>();

            say(column);
            options.Columns[uniqueId] = column;

            return options;
        }

        [Fact]
        public void AColumnCanBeRenamedForTheExport()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, decimal>(p => p.Salary, title: "Salary", uniqueId: "Salary")));

            var sheet = RoundTrip(cut.Instance.ToWorkbook(
                Saying("Salary", c => c.ExportTitle = "Annual salary")));

            Assert.Equal("Annual salary", sheet.Cells[0, 0].GetDisplayText());
        }

        [Fact]
        public void AColumnCanBeExcludedFromTheExport()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First", uniqueId: "First"),
                Columns.Property<Person, decimal>(p => p.Salary, title: "Salary", uniqueId: "Salary")));

            var sheet = RoundTrip(cut.Instance.ToWorkbook(Saying("Salary", c => c.ExportIgnore = true)));

            Assert.Equal("First", sheet.Cells[0, 0].GetDisplayText());

            // One column wide, so the excluded one is absent rather than blank.
            Assert.Equal(1, sheet.Columns.Count);
        }

        [Fact]
        public void AColumnCanSayHowItsNumbersAreFormatted()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, decimal>(p => p.Salary, title: "Salary", uniqueId: "Salary")));

            var sheet = RoundTrip(cut.Instance.ToWorkbook(
                Saying("Salary", c => c.ExportFormat = "#,##0.00")));

            Assert.Equal("#,##0.00", sheet.Cells[1, 0].Format.NumberFormat);
            Assert.Equal("4,000.00", sheet.Cells[1, 0].GetDisplayText());
        }

        /// <summary>An <c>ExportValue</c> wins over what the column would otherwise give.</summary>
        [Fact]
        public void AColumnCanExportSomethingElseEntirely()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, decimal>(p => p.Salary, title: "Salary", uniqueId: "Salary")));

            var sheet = RoundTrip(cut.Instance.ToWorkbook(
                Saying("Salary", c => c.ExportValue = person => person.Last)));

            Assert.Equal("Adams", sheet.Cells[1, 0].GetDisplayText());
        }

        /// <summary>
        /// A template column exports what the options give it, which is the one case that needs them.
        /// </summary>
        /// <remarks>
        /// §40 called this the case <see cref="IFastGridExportColumn{TItem}" /> exists for, and it is -
        /// but <c>TemplateColumn</c> is sealed, so the interface alone could never have reached it. The
        /// two halves of that finding meet here.
        /// </remarks>
        [Fact]
        public void ATemplateColumnExportsWhatItIsGiven()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Template<Person>(item => builder => builder.AddContent(0, "drawn " + item.Id),
                    title: "Actions", uniqueId: "Actions")));

            var sheet = RoundTrip(cut.Instance.ToWorkbook(
                Saying("Actions", c => c.ExportValue = person => "row " + person.Id)));

            Assert.Equal(new[] { "row 3", "row 1", "row 4", "row 2" }, Column(sheet, 0, 5).Skip(1).ToArray());
        }

        /// <summary>
        /// A custom column implementing the interface is obeyed, and beats an options entry.
        /// </summary>
        /// <remarks>
        /// The route §40 designed, which survives for a column an application writes itself - the shape
        /// the surveyed application's columns had. The type is the more specific statement, so it wins.
        /// </remarks>
        [Fact]
        public void AColumnThatImplementsTheInterfaceWinsOverTheOptions()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of((builder, seq) =>
            {
                builder.OpenComponent<SayingColumn>(seq);
                builder.AddAttribute(seq + 1, nameof(ColumnBase<Person>.Title), "Grade");
                builder.AddAttribute(seq + 2, nameof(ColumnBase<Person>.UniqueID), "Grade");
                builder.CloseComponent();
            }));

            var sheet = RoundTrip(cut.Instance.ToWorkbook(
                Saying("Grade", c => c.ExportTitle = "from the options")));

            Assert.Equal("from the column", sheet.Cells[0, 0].GetDisplayText());
            Assert.Equal("said by the column", sheet.Cells[1, 0].GetDisplayText());
        }

        sealed class SayingColumn : ColumnBase<Person>, IFastGridExportColumn<Person>
        {
            public Func<Person, object> ExportValue => _ => "said by the column";

            public string ExportTitle => "from the column";

            public string ExportFormat => null;

            public bool ExportIgnore => false;
        }

        // --- the sheet's own furniture --------------------------------------------------------

        /// <summary>
        /// Columns are sized to what is in them, which nothing was checking.
        /// </summary>
        /// <remarks>
        /// The review's finding, quoted: <em>"mutating <c>* 7</c> to <c>* 70</c> would pass the
        /// suite"</em>. The widths are computed here rather than asked for because <c>Axis.SetAutoFit</c>
        /// is internal to <c>Radzen.Blazor</c>, so this is the grid's own arithmetic and the only thing
        /// standing behind it is this test. It asserts the property that matters - a column of long text
        /// is wider than a column of short - and the clamps, rather than the constants, which are a
        /// guess at a font and should be free to move.
        /// </remarks>
        [Fact]
        public void ColumnsAreSizedToWhatIsInThem()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, int>(p => p.Id, title: "Id"),
                Columns.Property<Person, string>(p => p.First, title: "A rather longer heading")));

            var sheet = RoundTrip(cut.Instance.ToWorkbook());

            Assert.True(sheet.Columns[1] > sheet.Columns[0],
                $"the wider column measured {sheet.Columns[1]}, the narrow one {sheet.Columns[0]}");

            Assert.InRange(sheet.Columns[0], 48, 520);
            Assert.InRange(sheet.Columns[1], 48, 520);
        }

        [Fact]
        public void AutoFitCanBeSwitchedOffAndThenEveryColumnIsTheDefault()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, int>(p => p.Id, title: "Id"),
                Columns.Property<Person, string>(p => p.First, title: "A rather longer heading")));

            var sheet = RoundTrip(cut.Instance.ToWorkbook(
                new FastGridExportOptions<Person> { AutoFitColumns = false }));

            Assert.Equal(sheet.Columns[0], sheet.Columns[1]);
        }

        /// <summary>The range becomes a table, which is what draws Excel's filter buttons.</summary>
        [Fact]
        public void TheRangeBecomesATableUnlessItIsTurnedOff()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, string>(p => p.First, title: "First")));

            Assert.Single(RoundTrip(cut.Instance.ToWorkbook()).Tables);

            Assert.Empty(RoundTrip(cut.Instance.ToWorkbook(
                new FastGridExportOptions<Person> { AddTable = false })).Tables);
        }

        /// <summary>
        /// Two exported grids in one workbook do not collide, which is the README's first example.
        /// </summary>
        /// <remarks>
        /// The table name is taken from the sheet's for this reason. Nothing in the model objects to two
        /// tables called <c>Export</c> - they save and load - but Excel wants the name unique across the
        /// workbook, and a file it refuses is not an export.
        /// </remarks>
        [Fact]
        public void TwoGridsInOneWorkbookGetDifferentTableNames()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, string>(p => p.First, title: "First")));

            var open = cut.Instance.ToWorkbook(new FastGridExportOptions<Person> { SheetName = "Open" });
            var closed = cut.Instance.ToWorkbook(new FastGridExportOptions<Person> { SheetName = "Closed orders" });

            open.AddSheet(closed.Sheets[0]);

            using var stream = new MemoryStream();
            open.SaveToStream(stream);
            stream.Position = 0;

            var names = Workbook.LoadFromStream(stream).Sheets
                .SelectMany(s => s.Tables.Select(t => t.Name)).ToArray();

            Assert.Equal(2, names.Length);
            Assert.Equal(names.Length, names.Distinct().Count());

            // A space is not allowed in an Excel table name.
            Assert.All(names, name => Assert.DoesNotContain(' ', name));
        }

        /// <summary>A grid with no rows still exports its headings.</summary>
        [Fact]
        public async Task AnEmptyGridExportsItsHeaderAndNothingElse()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, string>(p => p.First, title: "First")));

            await cut.InvokeAsync(() => cut.Instance.Filter(cut.Instance.VisibleColumns[0],
                new FastGridFilter(new FastGridFilterCondition(FastGridFilterOperator.Equals, "nobody"))));

            var sheet = RoundTrip(cut.Instance.ToWorkbook());

            Assert.Equal("First", sheet.Cells[0, 0].GetDisplayText());

            // Still a table, as Excel would leave one: the filter buttons are what someone clears the
            // filter with, and an export that lost them on an empty result would be hard to recover.
            Assert.Single(sheet.Tables);
        }

        /// <summary>
        /// A column's own <c>Format</c> becomes the spreadsheet's.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>This asserted the opposite, and passed after the behaviour changed.</strong> §40
        /// exported a bare <c>4000</c> from a column drawing <em>$4,000.00</em> and called it a
        /// decision, on the argument that bridging .NET's format strings to the file's would be a
        /// second vocabulary to keep correct forever. Comparing against the exporter §38 surveyed - 62
        /// lines, with a null escape hatch - showed the argument was borrowed from §39's refusal of a
        /// <em>bidirectional, lossless</em> settings converter and does not carry to a one-way,
        /// best-effort mapping.
        /// </para>
        /// <para>
        /// The old test asserted the number and its type, both of which are still true, so it kept
        /// passing while its name became a lie. It reads the format now, which is what changed.
        /// </para>
        /// </remarks>
        [Fact]
        public void AColumnsOwnFormatBecomesTheSpreadsheets()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, decimal>(p => p.Salary, title: "Salary", format: "C",
                    uniqueId: "Salary")));

            var sheet = RoundTrip(cut.Instance.ToWorkbook());

            // Still a number, so Excel can still sum the column - which is the half that matters.
            Assert.Equal(CellDataType.Number, sheet.Cells[1, 0].ValueType);
            Assert.Equal(4000d, Convert.ToDouble(sheet.Cells[1, 0].Value));

            // And now wearing the format the grid drew it in.
            Assert.Equal(cut.Instance.VisibleColumns[0].CellTextOf(People.Sample()[0]),
                sheet.Cells[1, 0].GetDisplayText());
        }

        /// <summary>An explicit <c>ExportFormat</c> still wins over the column's own.</summary>
        [Fact]
        public void AnExplicitExportFormatWinsOverTheColumnsOwn()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, decimal>(p => p.Salary, title: "Salary", format: "C",
                    uniqueId: "Salary")));

            var sheet = RoundTrip(cut.Instance.ToWorkbook(Saying("Salary", c => c.ExportFormat = "0.000")));

            Assert.Equal("0.000", sheet.Cells[1, 0].Format.NumberFormat);
        }

        /// <summary>
        /// A format with no spreadsheet equivalent exports the text rather than a bare number.
        /// </summary>
        /// <remarks>
        /// The escape hatch, and the reason the mapper is allowed to exist: where it cannot be honest it
        /// says so, and the caller writes what the reader saw. Losing the sum on that one column is a
        /// smaller loss than a figure formatted differently from the screen.
        /// </remarks>
        [Fact]
        public void AFormatWithNoEquivalentExportsTheTextInstead()
        {
            using var ctx = Context();

            // "E2" is scientific notation - .NET has it and a spreadsheet number format does not.
            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, decimal>(p => p.Salary, title: "Salary", format: "E2",
                    uniqueId: "Salary")));

            var drawn = cut.Instance.VisibleColumns[0].CellTextOf(People.Sample()[0]);

            // The workbook rather than a round trip, for the boolean's reason one type over: the file
            // carries this as quote-prefixed text and XlsxReader re-infers it back into the number
            // 4000, so reading it back would test the reader rather than the export.
            var sheet = cut.Instance.ToWorkbook().Sheets[0];

            Assert.Equal(drawn, sheet.Cells[1, 0].Value);
            Assert.Equal(CellDataType.String, sheet.Cells[1, 0].ValueType);
        }

        /// <summary>The mapper's own table, at the specifiers a grid actually declares.</summary>
        [Theory]
        [InlineData("N", "#,##0.00")]
        [InlineData("N0", "#,##0")]
        [InlineData("N2", "#,##0.00")]
        [InlineData("F1", "0.0")]
        [InlineData("P", "0.00%")]
        [InlineData("P0", "0%")]
        [InlineData("D4", "0000")]
        [InlineData("yyyy-MM-dd", "yyyy-MM-dd")]
        [InlineData("#,##0.00", "#,##0.00")]
        // No spreadsheet equivalent, so the caller falls back to the text.
        [InlineData("E2", null)]
        [InlineData("G", null)]
        [InlineData("X", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        // A bare "d" or "D" is a date, and the writer already gives a date cell a locale-aware format.
        [InlineData("d", null)]
        public void TheFormatMapperSaysWhatItCanAndNothingMore(string format, string expected) =>
            Assert.Equal(expected, ExportFormat.ToNumberFormat(format));

        /// <summary>
        /// Everything at once, which is the test §40 actually asked for.
        /// </summary>
        /// <remarks>
        /// <em>"a test writes a grid with a filter, a sort, a hidden column, a reordered column, a
        /// formatted decimal, a date and a lookup, saves it, loads it, and asserts the cells."</em> The
        /// separate tests above each hold one variable still; this is where they interact, and the
        /// interaction the review named is the one it would catch - an index skewed by a hidden column
        /// and a reorder applying to different lists.
        /// </remarks>
        [Fact]
        public async Task AGridWithAllOfItAtOnceRoundTrips()
        {
            using var ctx = Context();

            var lookup = FastGridLookup.Items(
                new[] { new Category { Id = 10, Name = "Hardware" }, new Category { Id = 20, Name = "Software" } },
                c => c.Id, c => c.Name);

            var cut = Render(ctx, Columns.Of(
                    Columns.Property<Person, string>(p => p.First, title: "First", uniqueId: "First"),
                    Columns.Property<Person, string>(p => p.Last, title: "Last", uniqueId: "Last"),
                    Columns.Property<Person, decimal>(p => p.Salary, title: "Salary", uniqueId: "Salary"),
                    Columns.Property<Person, DateTime>(p => p.Hired, title: "Hired", uniqueId: "Hired"),
                    Columns.Lookup<Person, int>(p => p.CategoryId, lookup, title: "Category", uniqueId: "Category")),
                extra: p => p.Add(g => g.AllowColumnReorder, true));

            // Hide one, move another past where it was, filter, and sort.
            await cut.InvokeAsync(() => cut.Instance.VisibleColumns[1].SetPicked(false));
            cut.Render();
            await cut.InvokeAsync(() => cut.Instance.ReorderColumn(0, 3));
            await cut.InvokeAsync(() => cut.Instance.Filter(cut.Instance.VisibleColumns
                .Single(c => c.Title == "Salary"),
                new FastGridFilter(new FastGridFilterCondition(FastGridFilterOperator.GreaterThan, 1500m))));
            await cut.InvokeAsync(() => cut.Instance.SortBy(cut.Instance.VisibleColumns
                .Single(c => c.Title == "First")));
            cut.Render();

            var sheet = RoundTrip(cut.Instance.ToWorkbook(
                Saying("Salary", c => c.ExportFormat = "$#,##0.00")));

            // Last is hidden, so four columns; First was dragged to the end.
            Assert.Equal(new[] { "Salary", "Hired", "Category", "First" }, Row(sheet, 0, 4));

            // Salary > 1500 drops Dave at 1000, and ascending by First orders the rest.
            Assert.Equal(new[] { "Alice", "Bob", "Carol" }, Column(sheet, 3, 4).Skip(1).ToArray());

            // The formatted decimal, the date and the lookup, on the row Alice is now on.
            Assert.Equal("$2,000.00", sheet.Cells[1, 0].GetDisplayText());
            Assert.Equal(new DateTime(2021, 1, 2), DateTime.FromOADate(Convert.ToDouble(sheet.Cells[1, 1].Value)));
            Assert.Equal("Software", sheet.Cells[1, 2].GetDisplayText());
        }

        // --- the options ---------------------------------------------------------------------------

        [Fact]
        public void TheSheetCanBeNamedAndTheHeaderLeftOut()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, string>(p => p.First, title: "First")));

            var sheet = RoundTrip(
                cut.Instance.ToWorkbook(new FastGridExportOptions<Person> { SheetName = "People", IncludeHeader = false }),
                "People");

            Assert.Equal("Carol", sheet.Cells[0, 0].GetDisplayText());
            Assert.Equal(0, sheet.Rows.Frozen);
        }

        // --- the registered service, which is what puts the entry in the band menu ----------------

        /// <summary>
        /// One line of registration, and the grid resolves something to export with.
        /// </summary>
        /// <remarks>
        /// The application-facing half of the seam. Scoped rather than singleton because the
        /// implementation holds a JavaScript module reference, which belongs to one circuit on Blazor
        /// Server.
        /// </remarks>
        [Fact]
        public void RegisteringTheExportMakesItResolvable()
        {
            var services = new ServiceCollection();

            services.AddRadzenFastGridExport();

            using var provider = services.BuildServiceProvider();
            using var scope = provider.CreateScope();

            Assert.NotNull(scope.ServiceProvider.GetService<IFastGridExporter>());
        }

        /// <summary>
        /// A handler takes the workbook instead of the browser, and gets what the grid holds.
        /// </summary>
        /// <remarks>
        /// <para>
        /// §40 refused a download outright - <em>"bytes to a browser is <c>IJSRuntime</c> and a data URL
        /// or a stream reference, it differs between Server and WebAssembly, and it is the application's
        /// to own"</em>. That refusal is reversed for the default, because a menu entry producing
        /// nothing a user can open is not a feature; what the section got right is that some
        /// applications want the bytes elsewhere, and this is the way out.
        /// </para>
        /// <para>
        /// The workbook is asserted rather than the call, because a handler that runs and is handed an
        /// empty workbook is the failure this is guarding against.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task AHandlerTakesTheWorkbookInsteadOfTheBrowser()
        {
            using var ctx = Context();

            Workbook handed = null;

            ctx.Services.AddRadzenFastGridExport(o =>
            {
                o.SheetName = "People";
                o.OnExport = (workbook, _) =>
                {
                    handed = workbook;

                    return Task.CompletedTask;
                };
            });

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, string>(p => p.First, title: "First")),
                extra: p => p.Add(g => g.ShowGridMenu, true));

            cut.Find(".rz-filter-pills .rz-menu-toggle").Click();
            await cut.InvokeAsync(() =>
                cut.FindAll("#" + cut.Instance.GridMenuElementId + " [role=menuitem]")[1].Click());

            Assert.NotNull(handed);

            var sheet = handed.GetSheet("People");

            Assert.NotNull(sheet);
            Assert.Equal("First", sheet.Cells[0, 0].GetDisplayText());
            Assert.Equal("Carol", sheet.Cells[1, 0].GetDisplayText());
        }

        /// <summary>
        /// With no JavaScript runtime the export builds the workbook and saves nothing.
        /// </summary>
        /// <remarks>
        /// A prerendering pass has no runtime, and so does a test host. Throwing there would make a
        /// rendering test of a grid carrying the entry fail for a reason that has nothing to do with the
        /// grid - so the workbook is built, there is nowhere to put it, and that is the end of it.
        /// <para>
        /// <strong>The first draft of this asserted nothing at all</strong> - its body was a bare call
        /// with a comment saying the assertion was that it returned, which the review pointed out would
        /// pass with the whole save deleted. It goes through the handler now, so it can say the workbook
        /// was built and that nothing was asked of the browser.
        /// </para>
        /// </remarks>
        [Fact]
        public async Task WithNoBrowserTheExportStillBuildsTheWorkbook()
        {
            using var ctx = Context();

            Workbook built = null;

            ctx.Services.AddRadzenFastGridExport(o => o.OnExport = (workbook, _) =>
            {
                built = workbook;

                return Task.CompletedTask;
            });

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, string>(p => p.First, title: "First")),
                extra: p => p.Add(g => g.ShowGridMenu, true));

            var exporter = ctx.Services.GetRequiredService<IFastGridExporter>();

            await cut.InvokeAsync(() => exporter.ExportAsync(cut.Instance));

            Assert.NotNull(built);
            Assert.Equal("Carol", built.Sheets[0].Cells[1, 0].GetDisplayText());

            // And nothing was asked of the browser: the download is upstream's Radzen.downloadFile, and
            // a test host has no runtime to call it with.
            Assert.DoesNotContain(ctx.JSInterop.Invocations,
                i => i.Identifier.Contains("download", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Two grids on one page do not download two files with the same name.
        /// </summary>
        /// <remarks>
        /// The review's finding, and the reason <see cref="FastGridExportServiceOptions.FileName" /> is
        /// handed the grid at all: registration is application-wide, so without an argument every grid
        /// in the application names its file identically. The first draft's own remark claimed the
        /// per-grid case was what <c>OnExport</c> was for, and <c>OnExport</c> could not see the grid
        /// either.
        /// </remarks>
        [Fact]
        public async Task TwoGridsCanNameTheirFilesDifferently()
        {
            using var ctx = Context();

            var names = new List<string>();

            ctx.Services.AddRadzenFastGridExport(o =>
            {
                o.FileName = grid => $"{((RadzenFastGrid<Person>)grid).CssClass}.xlsx";
                o.OnExport = (_, grid) =>
                {
                    names.Add(o.FileName(grid));

                    return Task.CompletedTask;
                };
            });

            var exporter = ctx.Services.GetRequiredService<IFastGridExporter>();

            foreach (var which in new[] { "open", "closed" })
            {
                var cut = Render(ctx, Columns.Of(Columns.Property<Person, string>(p => p.First, title: "First")),
                    extra: p =>
                    {
                        p.Add(g => g.ShowGridMenu, true);
                        p.Add(g => g.CssClass, which);
                    });

                await cut.InvokeAsync(() => exporter.ExportAsync(cut.Instance));
            }

            Assert.Equal(new[] { "open.xlsx", "closed.xlsx" }, names);
        }

        /// <summary>
        /// The name the browser is actually told, on the path that tells it.
        /// </summary>
        /// <remarks>
        /// <strong>The sweep found the test above proving nothing about the exporter.</strong> It calls
        /// <c>FileName</c> itself from inside <c>OnExport</c>, so it exercises the option and not the
        /// line that uses it - and making <c>WorkbookExporter</c> ignore the grid entirely left it
        /// passing. This reads the argument handed to <c>Radzen.downloadFile</c>, which is the only
        /// place the name is really used.
        /// </remarks>
        [Fact]
        public async Task TheBrowserIsToldTheNameTheGridEarned()
        {
            using var ctx = Context();

            ctx.Services.AddRadzenFastGridExport(o =>
                o.FileName = grid => $"{((RadzenFastGrid<Person>)grid).CssClass}.xlsx");

            var exporter = ctx.Services.GetRequiredService<IFastGridExporter>();

            foreach (var which in new[] { "open", "closed" })
            {
                var cut = Render(ctx, Columns.Of(Columns.Property<Person, string>(p => p.First, title: "First")),
                    extra: p =>
                    {
                        p.Add(g => g.ShowGridMenu, true);
                        p.Add(g => g.CssClass, which);
                    });

                await cut.InvokeAsync(() => exporter.ExportAsync(cut.Instance));
            }

            var told = ctx.JSInterop.Invocations["Radzen.downloadFile"]
                .Select(i => i.Arguments[0]).ToArray();

            Assert.Equal(new object[] { "open.xlsx", "closed.xlsx" }, told);
        }

        /// <summary>The registration's sheet and table settings reach the workbook.</summary>
        /// <remarks>
        /// The review found only <c>SheetName</c> being forwarded, which left an application registering
        /// globally unable to turn off the table or the header it can turn off when calling
        /// <c>ToWorkbook</c> directly.
        /// </remarks>
        [Fact]
        public async Task TheRegistrationsSettingsReachTheWorkbook()
        {
            using var ctx = Context();

            Workbook built = null;

            ctx.Services.AddRadzenFastGridExport(o =>
            {
                o.SheetName = "People";
                o.AddTable = false;
                o.IncludeHeader = false;
                o.OnExport = (workbook, _) =>
                {
                    built = workbook;

                    return Task.CompletedTask;
                };
            });

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, string>(p => p.First, title: "First")),
                extra: p => p.Add(g => g.ShowGridMenu, true));

            await cut.InvokeAsync(() =>
                ctx.Services.GetRequiredService<IFastGridExporter>().ExportAsync(cut.Instance));

            var sheet = built.GetSheet("People");

            Assert.NotNull(sheet);
            Assert.Empty(sheet.Tables);
            Assert.Equal("Carol", sheet.Cells[0, 0].GetDisplayText());
        }

        /// <summary>
        /// The value coercion, at the types the grid's own model has no column for.
        /// </summary>
        /// <remarks>
        /// Directly rather than through a grid, because the row type would need a property of each and
        /// the rule under test is one method. Every one of these throws out of <c>Cell.Value</c>
        /// untouched, which is what makes the mapping load-bearing rather than cosmetic.
        /// </remarks>
        [Theory]
        [MemberData(nameof(RefusedTypes))]
        public void TheWriterRefusesTheseAndTheCellStillGetsAValue(object value, object expected)
        {
            var workbook = new Workbook();
            var sheet = workbook.AddSheet("T", 1, 1);

            // The fault this guards, stated as an assertion: handed over untouched, every one of these
            // throws out of Cell.Value rather than formatting oddly.
            Assert.Throws<NotSupportedException>(() => sheet.Cells[0, 0].Value = value);

            // Through Write rather than Coerce, because the two halves of the mapping are both needed:
            // Coerce decides what the cell is handed, and Write is what stops the writer reading a
            // string back into something else. "02:00:00" is a date without it.
            ExportValue.Write(sheet.Cells[0, 0], value, value?.ToString());

            Assert.Equal(expected, sheet.Cells[0, 0].Value);
        }

        public static TheoryData<object, object> RefusedTypes => new()
        {
            { Grade.Senior, "Senior" },
            { (Grade?)Grade.Junior, "Junior" },
            { Guid.Empty, Guid.Empty.ToString() },
            // Both of these read back as dates without the apostrophe in ExportValue.Write - the
            // writer parses "02:00:00" as a time of day - so they are here twice over: refused by the
            // writer as values, and retyped by it as text.
            { TimeSpan.FromHours(2), TimeSpan.FromHours(2).ToString() },
            { new TimeOnly(5, 6), new TimeOnly(5, 6).ToString() },

            // Not text: a date the writer does not know is still a date, so it is converted rather than
            // stringified - which is what keeps it sortable and gets it a date format in the file.
            { new DateOnly(2020, 3, 4), new DateTime(2020, 3, 4) },
            { new DateTimeOffset(new DateTime(2020, 3, 4), TimeSpan.Zero), new DateTime(2020, 3, 4) },
        };
        // --- rows the caller supplies -----------------------------------------------------------

        /// <summary>
        /// A grid holding one page can still export more, because the caller can hand over what it
        /// fetched.
        /// </summary>
        /// <remarks>
        /// The case is a <c>LoadData</c> grid: it only ever holds the page it was given, and the
        /// exporter cannot ask for the rest without running a query the grid never ran. So the
        /// application runs it and passes the result. Nothing is re-filtered or re-sorted here - these
        /// rows are written as they arrive, through the same columns.
        /// </remarks>
        [Fact]
        public void RowsGivenInTheOptionsAreWrittenInsteadOfTheGridsOwn()
        {
            using var ctx = Context();

            var page = People.Sample().Take(2).ToList();
            var everything = People.Sample();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, page);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First, title: "First")));
            });

            var sheet = RoundTrip(cut.Instance.ToWorkbook(new FastGridExportOptions<Person>
            {
                Rows = everything,
            }));

            Assert.Equal(new[] { "Carol", "Alice", "Dave", "Bob" },
                Column(sheet, 0, everything.Count + 1).Skip(1).ToArray());
        }

        /// <summary>Null rows are the ordinary case, and mean the grid's own.</summary>
        [Fact]
        public void NoRowsInTheOptionsLeavesTheGridsOwnRows()
        {
            using var ctx = Context();

            var page = People.Sample().Take(2).ToList();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, page);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First, title: "First")));
            });

            var sheet = RoundTrip(cut.Instance.ToWorkbook(new FastGridExportOptions<Person>()));

            Assert.Equal(new[] { "Carol", "Alice" }, Column(sheet, 0, 3).Skip(1).ToArray());
        }

    }
}
