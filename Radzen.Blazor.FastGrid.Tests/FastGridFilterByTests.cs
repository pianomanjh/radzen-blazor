using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// Filtering by a key the column is not typed at, through <see cref="FastGridFilterBy{TItem}" />.
    /// </summary>
    /// <remarks>
    /// The filter counterpart of <see cref="FastGridSort{TItem}" />, and it exists for the same reason a
    /// sort carrier does: <see cref="TemplateColumn{TItem}" /> has no expression of its own. Until this,
    /// a template column could be told how to sort and not how to filter, so
    /// <c>ColumnBase.CanFilter</c> — which reads <c>FilterPropertyPath</c> — was false for one by
    /// construction.
    /// </remarks>
    public class FastGridFilterByTests
    {
        [Fact]
        public void ACarrierReportsThePathAndTypeOfItsKey()
        {
            var filterBy = FastGridFilterBy<Person>.By(p => p.First);

            Assert.Equal("First", filterBy.Path);
            Assert.Equal(typeof(string), filterBy.PropertyType);
        }

        [Fact]
        public void ACarrierReportsADottedPathForAMemberChain()
        {
            var filterBy = FastGridFilterBy<Person>.By(p => p.Customer.Name);

            Assert.Equal("Customer.Name", filterBy.Path);
            Assert.Equal(typeof(string), filterBy.PropertyType);
        }

        /// <summary>
        /// A computed key has no path, which is what <see cref="FastGridSort{TItem}.Path" /> already
        /// says of a sort — and here it carries a consequence a sort does not have: the path is what
        /// <c>CanFilter</c> reads, so a column filtering by a computed key does not filter at all.
        /// </summary>
        [Fact]
        public void AComputedKeyHasNoPath()
        {
            var filterBy = FastGridFilterBy<Person>.By(p => p.First + p.Last);

            Assert.Null(filterBy.Path);
            Assert.Equal(typeof(string), filterBy.PropertyType);
        }

        [Fact]
        public void TheKeysTypeIsCapturedRatherThanErased()
        {
            Assert.Equal(typeof(decimal), FastGridFilterBy<Person>.By(p => p.Salary).PropertyType);
            Assert.Equal(typeof(decimal?), FastGridFilterBy<Person>.By(p => p.Bonus).PropertyType);
            Assert.Equal(typeof(DateTime), FastGridFilterBy<Person>.By(p => p.Hired).PropertyType);
            Assert.Equal(typeof(Grade), FastGridFilterBy<Person>.By(p => p.Grade).PropertyType);
        }

        [Fact]
        public void ANullKeyIsRefusedAtTheFactory()
        {
            Assert.Throws<ArgumentNullException>(() => FastGridFilterBy<Person>.By<string>(null));
        }

        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx, bool queryable,
            FastGridFilterBy<Person> filterBy)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var people = People.Sample();

            // Draws Last and filters by First, so a passing test cannot be one where the column happens
            // to be filtering by what it draws.
            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, queryable ? people.AsQueryable() : people);
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Template<Person>(person => builder => builder.AddContent(0, person.Last),
                        title: "Who", filterBy: filterBy)));
            });
        }

        static ColumnBase<Person> Column(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindComponents<TemplateColumn<Person>>().Single().Instance;

        static string[] Drawn(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindAll("tbody tr.rz-data-row td").Select(c => c.TextContent.Trim()).ToArray();

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ATemplateColumnFiltersByTheKeyItWasHanded(bool queryable)
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, queryable, FastGridFilterBy<Person>.By(x => x.First));
            var column = Column(cut);

            Assert.True(column.CanFilter);
            Assert.Equal("First", column.FilterPropertyPath);
            Assert.Equal(typeof(string), column.FilterPropertyType);

            var filter = new FastGridFilter(
                new FastGridFilterCondition(FastGridFilterOperator.Equals, "Alice"));

            cut.InvokeAsync(() => cut.Instance.Filter(column, filter)).Wait();

            // Alice's surname, from the row the filter kept - so both halves are asserted at once: the
            // filter compared First, and the cell drew Last.
            Assert.Equal(new[] { "Draper" }, Drawn(cut));
        }

        [Fact]
        public void ATemplateColumnWithNoFilterByCannotFilter()
        {
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Template<Person>(person => builder => builder.AddContent(0, person.Last),
                        title: "Who")));
            });

            var column = Column(cut);

            Assert.False(column.CanFilter);
            Assert.Null(column.FilterPropertyPath);
        }

        /// <summary>
        /// The consequence <see cref="FastGridFilterBy{TItem}.Path" /> documents, pinned: a computed key
        /// has no path, <c>CanFilter</c> reads the path, so such a column does not filter - where a
        /// computed <em>sort</em> key still sorts in memory.
        /// </summary>
        [Fact]
        public void AComputedFilterKeyLeavesTheColumnUnfilterable()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, queryable: false,
                FastGridFilterBy<Person>.By(x => x.First + x.Last));

            Assert.False(Column(cut).CanFilter);
        }

        /// <summary>
        /// The check-box list's values come from the key, not from what the cell draws - which is the
        /// only thing that makes the list usable on a column drawing something else.
        /// </summary>
        [Fact]
        public void TheChecklistOffersTheKeysValuesRatherThanTheCellsText()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, queryable: true, FastGridFilterBy<Person>.By(x => x.First));

            var values = Column(cut)
                .DistinctValues(People.Sample().AsQueryable())!
                .Cast<string>()
                .OrderBy(x => x)
                .ToArray();

            Assert.Equal(new[] { "Alice", "Bob", "Carol", "Dave" }, values);
        }

        /// <summary>
        /// What a carrier buys over the path a <see cref="TemplateColumn{TItem}.SortProperty" /> already
        /// gave: the check-box list.
        /// </summary>
        /// <remarks>
        /// A path column filters - it always could, reflectively - but <c>ColumnBase.DistinctValues</c>
        /// answers null, so its list has nothing in it. Under <c>FilterMode.CheckBoxList</c> that is a
        /// column the reader cannot filter at all, whatever the row filter would have done.
        /// </remarks>
        [Fact]
        public void APathColumnFiltersButOffersNoValuesToTickWhereACarrierDoes()
        {
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var people = People.Sample();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, people);
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Template<Person>(x => b => b.AddContent(0, x.Last),
                        title: "ByPath", sortProperty: "First"),
                    Columns.Template<Person>(x => b => b.AddContent(0, x.Last),
                        title: "ByCarrier", filterBy: FastGridFilterBy<Person>.By(x => x.First))));
            });

            var columns = cut.FindComponents<TemplateColumn<Person>>().Select(c => c.Instance).ToArray();

            // Both filter, and both by the same path - the carrier is not what makes it filterable.
            Assert.True(columns[0].CanFilter);
            Assert.True(columns[1].CanFilter);
            Assert.Equal("First", columns[0].FilterPropertyPath);
            Assert.Equal("First", columns[1].FilterPropertyPath);

            // The difference: only the carrier can say what there is to tick.
            Assert.Null(columns[0].DistinctValues(people.AsQueryable()));
            Assert.NotNull(columns[1].DistinctValues(people.AsQueryable()));

            // And only the carrier knows the type without going and looking for it.
            Assert.Equal(typeof(object), columns[0].FilterPropertyType);
            Assert.Equal(typeof(string), columns[1].FilterPropertyType);
        }
    }
}
