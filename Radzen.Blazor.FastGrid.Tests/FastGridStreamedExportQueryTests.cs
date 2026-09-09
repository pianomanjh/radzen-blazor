using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Radzen.Documents.Spreadsheet;
using Radzen.FastGrid.Export;
using Xunit;
using Ctx = Radzen.FastGrid.Tests.EntityFrameworkTests.Ctx;
using Employee = Radzen.FastGrid.Tests.EntityFrameworkTests.Employee;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// §66: how many times a streamed export asks the database, against a real provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The question a paged grid over a large table raises is whether exporting it means a query per
    /// page, or per row, or one. A fake executor cannot answer it - it never issues a command - so this
    /// counts what SQLite is actually asked, through an interceptor, with the grid drawing a page of
    /// five over two hundred rows.
    /// </para>
    /// </remarks>
    public class FastGridStreamedExportQueryTests : IDisposable
    {
        const int Rows = 200;

        readonly SqliteConnection connection = new("DataSource=:memory:");
        readonly Counter counter = new();
        readonly Ctx context;

        public FastGridStreamedExportQueryTests()
        {
            connection.Open();

            context = new Ctx(new DbContextOptionsBuilder<Ctx>()
                .UseSqlite(connection)
                .AddInterceptors(counter)
                .Options);

            context.Database.EnsureCreated();

            context.People.AddRange(Enumerable.Range(1, Rows).Select(i => new Employee
            {
                Id = i,
                Name = "Name" + i,
                Department = i % 4 == 0 ? "Ops" : "Engineering",
                Salary = 1000 + i,
                Rating = i % 5,
            }));

            context.SaveChanges();

            // SQLite runs an INSERT ... RETURNING per row through the reader, so seeding is two hundred
            // commands. Counting starts after it.
            counter.Commands.Clear();
        }

        public void Dispose()
        {
            context.Dispose();
            connection.Dispose();
            GC.SuppressFinalize(this);
        }

        /// <summary>Counts the commands a provider is asked to run, and keeps their text.</summary>
        sealed class Counter : DbCommandInterceptor
        {
            public List<string> Commands { get; } = [];

            public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,
                CommandEventData eventData, InterceptionResult<DbDataReader> result)
            {
                Commands.Add(command.CommandText);

                return base.ReaderExecuting(command, eventData, result);
            }

            public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
                CommandEventData eventData, InterceptionResult<DbDataReader> result,
                CancellationToken cancellationToken = default)
            {
                Commands.Add(command.CommandText);

                return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
            }
        }

        IRenderedComponent<RadzenFastGrid<Employee>> Grid(TestContext ctx)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.RenderComponent<RadzenFastGrid<Employee>>(p =>
            {
                p.Add(g => g.Data, context.People);
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 5);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Employee, string>(x => x.Name),
                    Columns.Property<Employee, decimal>(x => x.Salary)));
            });
        }

        /// <summary>
        /// One query, whatever the row count and whatever the page size: the export composes the same
        /// filters and sort the grid did, drops the paging, and reads the result set once.
        /// </summary>
        [Fact]
        public async Task AStreamedExportAsksTheDatabaseOnce()
        {
            using var ctx = new TestContext();

            var cut = Grid(ctx);

            cut.WaitForState(() => cut.Instance.DrawnRows.Any());

            counter.Commands.Clear();

            using var stream = new MemoryStream();

            await cut.Instance.SaveToStreamAsync(stream);

            var command = Assert.Single(counter.Commands);

            // Every row, and no paging in the SQL - which is the difference between this and the page
            // the grid is drawing.
            Assert.DoesNotContain("LIMIT", command, StringComparison.OrdinalIgnoreCase);

            stream.Position = 0;

            Assert.Equal(Rows + 1, Workbook.LoadFromStream(stream).Sheets[0].RowCount);
        }

        /// <summary>
        /// The page the grid draws costs two - the window and the count - and that is what an export
        /// would repeat if it went page by page. It does not, which is the point of the test above.
        /// </summary>
        [Fact]
        public void DrawingAPageAsksTwice()
        {
            using var ctx = new TestContext();

            var cut = Grid(ctx);

            cut.WaitForState(() => cut.Instance.DrawnRows.Any());

            Assert.Equal(2, counter.Commands.Count);
            Assert.Contains(counter.Commands, c => c.Contains("LIMIT", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(counter.Commands, c => c.Contains("COUNT", StringComparison.OrdinalIgnoreCase));
        }
    }
}
