using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Bunit;
using Radzen.Documents.Spreadsheet;
using Radzen.FastGrid.Export;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// §66: what an export costs per row, and that it is a cost per row rather than a cost that grows
    /// with them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Two row counts, because one figure is one a worse path would also produce.</strong> What
    /// this asserts is the <em>slope</em>: the bytes between 2,000 rows and 20,000 divided by the rows
    /// between them, which is the marginal cost of a row and the only number that says whether the
    /// export holds anything. A path that retained a row would bend that line and a single measurement
    /// would not show it.
    /// </para>
    /// <para>
    /// The builder is measured beside it for the same reason the benchmark keeps a built arm: it is
    /// known to hold a cell per value, so if the two slopes come out alike the instrument is not
    /// resolving anything and neither bound below means a thing.
    /// </para>
    /// <para>
    /// The bounds are loose - four times the measured figures - because this runs wherever the tests
    /// run. It is a guard against a regression in kind, not a benchmark; the numbers themselves are in
    /// <c>benchmarks/Spreadsheet</c>, taken by an instrument built for them.
    /// </para>
    /// </remarks>
    public class FastGridExportCostTests
    {
        const int Small = 2_000;
        const int Large = 20_000;

        static List<Person> Rows(int count)
        {
            var rows = new List<Person>(count);
            var seed = new DateTime(2020, 1, 1);

            for (var i = 0; i < count; i++)
            {
                rows.Add(new Person
                {
                    Id = i,
                    First = "name " + (i % 100),
                    Last = "last " + (i % 100),
                    Salary = 1000m + i % 500,
                    Hired = seed.AddDays(i % 900),
                    Remote = i % 2 == 0,
                });
            }

            return rows;
        }

        static IRenderedComponent<RadzenFastGrid<Person>> Grid(TestContext ctx, List<Person> rows) =>
            ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, rows);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First, title: "First"),
                    Columns.Property<Person, string>(x => x.Last, title: "Last"),
                    Columns.Property<Person, decimal>(x => x.Salary, title: "Salary"),
                    Columns.Property<Person, DateTime>(x => x.Hired, title: "Hired"),
                    Columns.Property<Person, bool>(x => x.Remote, title: "Remote")));
            });

        /// <summary>
        /// Allocated bytes on this thread, which is the only counter this can use.
        /// </summary>
        /// <remarks>
        /// <strong><c>GC.GetTotalAllocatedBytes</c> is process-wide and xunit runs collections in
        /// parallel</strong>, so the first version of this read 104 bytes a row on its own and 1,170
        /// inside the suite - the difference being every other test allocating while it counted. It
        /// passed in isolation and failed in the run that matters, which is the wrong way round for a
        /// gate. The export is driven to completion on this thread here: the row source is a list, so
        /// nothing in it suspends, and the awaits complete synchronously.
        /// </remarks>
        static long Measure(Action work)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var before = GC.GetAllocatedBytesForCurrentThread();

            work();

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        /// <summary>Allocated bytes for one export of a grid over <paramref name="count" /> rows.</summary>
        static long Streamed(int count)
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var grid = Grid(ctx, Rows(count)).Instance;

            // Warm, so what is measured is the export rather than the JIT reaching it.
            grid.SaveToStreamAsync(Stream.Null).GetAwaiter().GetResult();

            return Measure(() => grid.SaveToStreamAsync(Stream.Null).GetAwaiter().GetResult());
        }

        static long Built(int count)
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var grid = Grid(ctx, Rows(count)).Instance;

            grid.ToWorkbook().SaveToStream(Stream.Null);

            return Measure(() => grid.ToWorkbook().SaveToStream(Stream.Null));
        }

        [Fact]
        public void AnExportedRowCostsTheSameWhicheverRowItIs()
        {
            var slope = (Streamed(Large) - Streamed(Small)) / (double)(Large - Small);

            // Measured at about 104 bytes a row over five columns: a box per typed cell and the
            // characters written for it. Four times that is a regression in kind rather than in degree.
            Assert.InRange(slope, 0, 420);
        }

        /// <summary>
        /// The builder beside it, so the bound above is known to be measuring something.
        /// </summary>
        [Fact]
        public void TheBuilderCostsAnOrderOfMagnitudeMorePerRow()
        {
            var streamed = (Streamed(Large) - Streamed(Small)) / (double)(Large - Small);
            var built = (Built(Large) - Built(Small)) / (double)(Large - Small);

            // Measured at about 1,084 bytes a row against 104 - a cell object, its value and the
            // sheet's slot for it. Asserted at four, not eleven, so the gate survives a machine.
            Assert.True(built > streamed * 4,
                $"streamed {streamed:F0} B/row, built {built:F0} B/row - the two paths should not be alike");
        }
    }
}
