using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
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
    /// §65: the same export without the workbook in the middle, held to the file the workbook produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The claim is not that a streamed export writes a valid file - that is the floor - but that it
    /// writes the <em>same</em> file, so that a menu entry switching to it is not a change anyone opening
    /// the result can see. So the first test here compares the two cell for cell, and the rest are the
    /// things only the streaming path can do.
    /// </para>
    /// </remarks>
    public class FastGridStreamedExportTests
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

        static Worksheet Built(RadzenFastGrid<Person> grid, FastGridExportOptions<Person> options = null)
        {
            using var stream = new MemoryStream();

            grid.ToWorkbook(options).SaveToStream(stream);
            stream.Position = 0;

            return Workbook.LoadFromStream(stream).Sheets[0];
        }

        static async Task<Worksheet> Streamed(RadzenFastGrid<Person> grid,
            FastGridExportOptions<Person> options = null)
        {
            using var stream = new MemoryStream();

            await grid.SaveToStreamAsync(stream, options);
            stream.Position = 0;

            return Workbook.LoadFromStream(stream).Sheets[0];
        }

        static string[] Row(Worksheet sheet, int row, int columns) =>
            Enumerable.Range(0, columns).Select(c => sheet.Cells[row, c].GetDisplayText() ?? "").ToArray();

        // --- the equivalence ---------------------------------------------------------------------

        /// <summary>
        /// Every cell, every width, the freeze and the table - the same from both paths.
        /// </summary>
        [Fact]
        public async Task StreamedAndBuiltAgreeCellForCell()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First name"),
                Columns.Property<Person, decimal>(p => p.Salary, title: "Salary"),
                Columns.Property<Person, DateTime>(p => p.Hired, title: "Hired"),
                Columns.Property<Person, bool>(p => p.Remote, title: "Remote")));

            var built = Built(cut.Instance);
            var streamed = await Streamed(cut.Instance);

            Assert.Equal(built.RowCount, streamed.RowCount);
            Assert.Equal(built.ColumnCount, streamed.ColumnCount);
            Assert.Equal(built.Rows.Frozen, streamed.Rows.Frozen);

            for (var row = 0; row < built.RowCount; row++)
            {
                Assert.Equal(Row(built, row, built.ColumnCount), Row(streamed, row, streamed.ColumnCount));
            }

            for (var column = 0; column < built.ColumnCount; column++)
            {
                Assert.True(Math.Abs(built.Columns[column] - streamed.Columns[column]) < 0.001,
                    $"column {column}: built {built.Columns[column]}, streamed {streamed.Columns[column]}");
            }

            var builtTable = Assert.Single(built.Tables);
            var streamedTable = Assert.Single(streamed.Tables);

            Assert.Equal($"{builtTable.Range.Start}:{builtTable.Range.End}",
                $"{streamedTable.Range.Start}:{streamedTable.Range.End}");
        }

        [Fact]
        public async Task TheHeaderIsBoldAndFrozen()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, string>(p => p.First, title: "First")));

            var sheet = await Streamed(cut.Instance);

            Assert.True(sheet.Cells[0, 0].Format.Bold);
            Assert.Equal(1, sheet.Rows.Frozen);
        }

        /// <summary>A lookup column exports its name here too, because the accessors are the same.</summary>
        [Fact]
        public async Task ALookupColumnExportsItsNameRatherThanItsId()
        {
            using var ctx = Context();

            var lookup = FastGridLookup.Items(
                new[] { new Category { Id = 10, Name = "Hardware" }, new Category { Id = 20, Name = "Software" } },
                c => c.Id, c => c.Name);

            var cut = Render(ctx, Columns.Of(
                Columns.Lookup<Person, int>(p => p.CategoryId, lookup, title: "Category")));

            var sheet = await Streamed(cut.Instance);

            Assert.Equal(new[] { "Hardware", "Software", "Hardware", "30" },
                Enumerable.Range(1, 4).Select(r => sheet.Cells[r, 0].GetDisplayText()).ToArray());
        }

        /// <summary>
        /// A code that looks like a number stays the code, which is what the built path insists on with a
        /// cell it can set a quote prefix on and this one has to say up front.
        /// </summary>
        [Fact]
        public async Task TextThatLooksLikeANumberIsStillText()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, new List<Person>
                {
                    new() { Id = 1, First = "007" },
                    new() { Id = 2, First = "Alice" },
                });
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First, title: "Code")));
            });

            var sheet = await Streamed(cut.Instance);

            Assert.Equal("007", sheet.Cells[1, 0].Value);
            Assert.Equal("Alice", sheet.Cells[2, 0].Value);
        }

        // --- the widths -----------------------------------------------------------------------------

        /// <summary>
        /// The column is as wide as the reader made it, not as wide as what is in it.
        /// </summary>
        [Fact]
        public async Task TheGridsOwnWidthIsWhatTheColumnGets()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First", width: "240px"),
                Columns.Property<Person, string>(p => p.Last, title: "Last", width: "60px")));

            var sheet = await Streamed(cut.Instance,
                new FastGridExportOptions<Person> { UseGridColumnWidths = true });

            // To the pixel, not to the bit: a width is stored in the file's character units and comes
            // back about half a pixel out.
            Assert.Equal(240, sheet.Columns[0], 0);
            Assert.Equal(60, sheet.Columns[1], 0);
        }

        /// <summary>
        /// A width the grid knows is a width no row has to be read for, and a long value past the sample
        /// cannot widen it - which is what says the sample was never taken.
        /// </summary>
        [Fact]
        public async Task AKnownWidthIsNotWidenedByWhatIsInTheColumn()
        {
            using var ctx = Context();

            var rows = new List<Person> { new() { Id = 1, First = new string('x', 300) } };

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, rows);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First, title: "First", width: "90px")));
            });

            var sheet = await Streamed(cut.Instance,
                new FastGridExportOptions<Person> { UseGridColumnWidths = true });

            Assert.Equal(90, sheet.Columns[0], 0);
        }

        /// <summary>
        /// A column the grid never sized has nothing to copy, so it is measured as before - and the two
        /// rules live side by side in one sheet.
        /// </summary>
        [Fact]
        public async Task AColumnWithNoWidthIsStillMeasured()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First", width: "240px"),
                Columns.Property<Person, string>(p => p.Last, title: "A much longer heading")));

            var sheet = await Streamed(cut.Instance,
                new FastGridExportOptions<Person> { UseGridColumnWidths = true });

            Assert.Equal(240, sheet.Columns[0], 0);
            Assert.True(sheet.Columns[1] > 100);
        }

        /// <summary>The builder follows the same rule, so the two files still agree.</summary>
        [Fact]
        public async Task StreamedAndBuiltAgreeOnTheGridsWidths()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First", width: "240px"),
                Columns.Property<Person, decimal>(p => p.Salary, title: "Salary", width: "75px"),
                Columns.Property<Person, string>(p => p.Last, title: "Last")));

            var options = new FastGridExportOptions<Person> { UseGridColumnWidths = true };

            var built = Built(cut.Instance, options);
            var streamed = await Streamed(cut.Instance, options);

            for (var column = 0; column < built.ColumnCount; column++)
            {
                Assert.Equal(built.Columns[column], streamed.Columns[column], 3);
            }
        }

        /// <summary>A percentage is of a viewport a spreadsheet has not got, so it is measured instead.</summary>
        [Fact]
        public async Task APercentageWidthIsNotCopied()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First", width: "30%")));

            var sheet = await Streamed(cut.Instance,
                new FastGridExportOptions<Person> { UseGridColumnWidths = true });

            // Measured from "First" and the names under it, which is nothing like 30 of anything.
            Assert.True(sheet.Columns[0] >= 48);
        }

        // --- where the rows come from -------------------------------------------------------------

        /// <summary>
        /// The payoff: a grid whose executor owns its query holds one page, and the streamed export
        /// writes every filtered row - because it runs the query the grid composed and declined to run.
        /// </summary>
        [Fact]
        public async Task TheExecutorStreamsEveryRowWhereTheBuiltExportGetsThePage()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, new AsyncQueryable<Person>(People.Many(30).AsQueryable()));
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 5);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(p => p.First, title: "First")));
            });

            cut.WaitForState(() => cut.Instance.DrawnRows.Any());

            var built = Built(cut.Instance);
            var streamed = await Streamed(cut.Instance);

            // The page, and then all of it. The header is the row above each.
            Assert.Equal(6, built.RowCount);
            Assert.Equal(31, streamed.RowCount);
        }

        [Fact]
        public async Task RowsAsyncOutranksTheExecutor()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, new AsyncQueryable<Person>(People.Many(30).AsQueryable()));
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(p => p.First, title: "First")));
            });

            var sheet = await Streamed(cut.Instance, new FastGridExportOptions<Person>
            {
                RowsAsync = Only(new Person { Id = 99, First = "Handed over" }),
            });

            Assert.Equal(2, sheet.RowCount);
            Assert.Equal("Handed over", sheet.Cells[1, 0].Value);
        }

        [Fact]
        public async Task RowsIsTakenWhenThereIsNoQueryToRun()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, string>(p => p.First, title: "First")));

            var sheet = await Streamed(cut.Instance, new FastGridExportOptions<Person>
            {
                Rows = new List<Person> { new() { Id = 99, First = "Handed over" } },
            });

            Assert.Equal(2, sheet.RowCount);
            Assert.Equal("Handed over", sheet.Cells[1, 0].Value);
        }

        /// <summary>
        /// A <c>LoadData</c> grid has no query to hand over: its handler already sorted and paged, so
        /// what it holds is the page and that is what comes out.
        /// </summary>
        [Fact]
        public void ALoadDataGridHasNoFilteredQuery()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample().AsQueryable());
                p.Add(g => g.LoadData, EventCallback.Factory.Create<LoadDataArgs>(this, _ => { }));
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(p => p.First, title: "First")));
            });

            Assert.Null(cut.Instance.FilteredQuery);
        }

        [Fact]
        public void AQueryableGridHandsOverItsComposedQuery()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample().AsQueryable());
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First, title: "First")));
            });

            Assert.NotNull(cut.Instance.FilteredQuery);
        }

        static async IAsyncEnumerable<Person> Only(Person person)
        {
            await Task.CompletedTask;

            yield return person;
        }

        /// <summary>
        /// A queryable that is also an <see cref="IAsyncEnumerable{T}" />, which is the shape the
        /// built-in executor supports and the reason a menu entry streams with no application code.
        /// </summary>
        sealed class AsyncQueryable<T>(IQueryable<T> inner) : IQueryable<T>, IAsyncEnumerable<T>, IQueryProvider
        {
            public Type ElementType => inner.ElementType;

            public Expression Expression => inner.Expression;

            public IQueryProvider Provider => this;

            public IEnumerator<T> GetEnumerator() => inner.GetEnumerator();

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

            public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            {
                foreach (var item in inner)
                {
                    await Task.Yield();

                    yield return item;
                }
            }

            public IQueryable CreateQuery(Expression expression) =>
                (IQueryable)Activator.CreateInstance(
                    typeof(AsyncQueryable<>).MakeGenericType(expression.Type.GetGenericArguments()[0]),
                    inner.Provider.CreateQuery(expression))!;

            public IQueryable<TElement> CreateQuery<TElement>(Expression expression) =>
                new AsyncQueryable<TElement>(inner.Provider.CreateQuery<TElement>(expression));

            public object Execute(Expression expression) => inner.Provider.Execute(expression)!;

            public TResult Execute<TResult>(Expression expression) => inner.Provider.Execute<TResult>(expression);
        }
    }
}
