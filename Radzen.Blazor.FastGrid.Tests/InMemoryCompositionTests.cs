using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Bunit;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// Composing over a list against composing over a queryable.
    /// </summary>
    /// <remarks>
    /// A list is filtered and sorted with delegates, a queryable with expression trees the provider is
    /// meant to translate. Two routes through one composition is two chances to answer differently, so
    /// what these check is that they do not: same rows, same filters, same sorts, same answer in the
    /// same order.
    /// <para>
    /// This used to stand up two <c>TestContext</c>s, render two grids and diff their rendered rows,
    /// because there was no function to call twice with the same arguments. There is one now, so these
    /// call it twice with the same arguments. The columns still need a renderer - they are components,
    /// and only a renderer sets a component's parameters - but one column at a time, with no grid, no
    /// table and no DOM. What that buys beyond brevity is the route itself: <c>Composed.InMemory</c> is
    /// the claim these tests are really about, and a diff of two tables cannot see it at all.
    /// </para>
    /// </remarks>
    public class InMemoryCompositionTests
    {
        /// <summary>A renderer, and a grid for columns to be born into.</summary>
        /// <remarks>
        /// Nothing reads that grid. A column refuses to initialize outside one and registers itself with
        /// the one it finds, so there has to be a grid; but the module takes the columns, so the test
        /// hands them over itself rather than asking the grid for them.
        /// </remarks>
        sealed class Bench : IDisposable
        {
            readonly TestContext ctx = new TestContext();
            readonly RadzenFastGrid<Person> grid = new RadzenFastGrid<Person>();

            internal PropertyColumn<Person, TProp> Property<TProp>(
                Expression<Func<Person, TProp>> property, object? filterValue = null,
                FilterOperator? filterOperator = null) =>
                ctx.RenderComponent<PropertyColumn<Person, TProp>>(p =>
                {
                    p.AddCascadingValue(grid);
                    p.Add(c => c.Property, property);

                    if (filterValue is not null)
                    {
                        p.Add(c => c.FilterValue, filterValue);
                    }

                    if (filterOperator is { } op)
                    {
                        p.Add(c => c.FilterOperator, op);
                    }
                }).Instance;

            internal TemplateColumn<Person> Template(string sortProperty) =>
                ctx.RenderComponent<TemplateColumn<Person>>(p =>
                {
                    p.AddCascadingValue(grid);
                    p.Add(c => c.Template, person => builder => builder.AddContent(0, person.Id));
                    p.Add(c => c.SortProperty, sortProperty);
                }).Instance;

            public void Dispose() => ctx.Dispose();
        }

        static ColumnBase<Person>[] Of(params ColumnBase<Person>[] columns) => columns;

        static readonly (ColumnBase<Person> Column, bool Descending)[] Unsorted =
            Array.Empty<(ColumnBase<Person>, bool)>();

        static (ColumnBase<Person> Column, bool Descending)[] SortedBy(ColumnBase<Person> column,
            bool descending = false) => new[] { (column, descending) };

        // Descending on purpose wherever a declining column is involved: People.Many builds its rows in
        // ascending Id order, so an ascending assertion is one an unsorted source also passes, and a
        // test for "the rest of the ordering stands" that a missing ordering satisfies is no test. The
        // declining column's own direction is never read - it declines before anything looks at it.
        static (ColumnBase<Person> Column, bool Descending)[] SortedByDescending(
            ColumnBase<Person> first, ColumnBase<Person> second) =>
            new[] { (first, true), (second, true) };

        /// <summary>The rows People20 makes, in the order a working descending sort by Id leaves them.</summary>
        static Person[] ByIdDescending(List<Person> people) =>
            people.OrderByDescending(person => person.Id).ToArray();

        static CompositionOptions Options(LogicalFilterOperator logical = LogicalFilterOperator.And) =>
            new CompositionOptions(true, FilterCaseSensitivity.Default, logical);

        static List<Person> People20()
        {
            var people = Tests.People.Many(20);

            people[3].First = null;
            people[4].Bonus = null;
            people[5].First = "FIRST6";

            return people;
        }

        /// <summary>
        /// The same columns and sorts over the same rows, once as a list and once as a queryable.
        /// Answers the route the list took, which is the half the rows cannot show.
        /// </summary>
        static bool BothRoutesAgree(IReadOnlyList<ColumnBase<Person>> columns,
            IReadOnlyList<(ColumnBase<Person> Column, bool Descending)> sorts,
            CompositionOptions options)
        {
            var people = People20();
            var pass = default(DrawPass<Person>);

            var overList = Composition.Compose(columns, sorts, people, options, ref pass);
            var overQueryable = Composition.Compose(columns, sorts, people.AsQueryable(), options,
                ref pass);

            // The rows themselves, not their rendered text: these are the same Person instances on both
            // routes, so an ordering or a predicate that differs shows up as a different sequence.
            Assert.Equal(overQueryable.Rows.ToArray(), overList.Rows.ToArray());
            Assert.NotEmpty(overList.Rows);

            // The other route is the expression one by construction, and says so.
            Assert.False(overQueryable.InMemory);

            return overList.InMemory;
        }

        [Fact]
        public void Unfiltered()
        {
            using var bench = new Bench();

            // Nothing to do to the source, so the source is the answer - which is neither route, and
            // the flag says so rather than claiming the delegate one ran.
            Assert.False(BothRoutesAgree(
                Of(bench.Property<string>(x => x.First), bench.Property<int>(x => x.Id)),
                Unsorted, Options()));
        }

        [Fact]
        public void FilteredByAString()
        {
            using var bench = new Bench();

            Assert.True(BothRoutesAgree(
                Of(bench.Property<string>(x => x.First, filterValue: "First1"),
                    bench.Property<int>(x => x.Id)),
                Unsorted, Options()));
        }

        [Fact]
        public void FilteredByANullableNumber()
        {
            using var bench = new Bench();

            Assert.True(BothRoutesAgree(
                Of(bench.Property<string>(x => x.First),
                    bench.Property<decimal?>(x => x.Bonus, filterValue: 9m,
                        filterOperator: FilterOperator.GreaterThan)),
                Unsorted, Options()));
        }

        [Fact]
        public void SortedByOneColumn()
        {
            using var bench = new Bench();

            var first = bench.Property<string>(x => x.First);

            Assert.True(BothRoutesAgree(Of(first, bench.Property<int>(x => x.Id)),
                SortedBy(first, descending: true), Options()));
        }

        // The nullable column is the one the two routes could most easily disagree about: a comparer
        // sorts a missing value below everything, and so does a lifted comparison, but only if both
        // were asked the same question.
        [Fact]
        public void SortedByANullableColumn()
        {
            using var bench = new Bench();

            var bonus = bench.Property<decimal?>(x => x.Bonus);

            Assert.True(BothRoutesAgree(Of(bench.Property<string>(x => x.First), bonus),
                SortedBy(bonus), Options()));
        }

        [Fact]
        public void FilteredAndSortedAtOnce()
        {
            using var bench = new Bench();

            var id = bench.Property<int>(x => x.Id);

            Assert.True(BothRoutesAgree(
                Of(bench.Property<string>(x => x.First, filterValue: "1"), id),
                SortedBy(id, descending: true), Options()));
        }

        [Fact]
        public void FilteredOnTwoColumnsWithOr()
        {
            using var bench = new Bench();

            Assert.True(BothRoutesAgree(
                Of(bench.Property<string>(x => x.First, filterValue: "First2"),
                    bench.Property<int>(x => x.Id, filterValue: 100005,
                        filterOperator: FilterOperator.Equals)),
                Unsorted, Options(LogicalFilterOperator.Or)));
        }

        // A template column sorts by a string path, which the in-memory route cannot compose - so the
        // whole composition has to go back to the expression route rather than half of it. That is the
        // claim, and until there was a seam to ask it at, nothing could: both routes produce the same
        // rows, and the slower one costs about 1.1 ms per render at a thousand of them.
        [Fact]
        public void AColumnThatCannotComposeSendsItBackToTheOtherRoute()
        {
            using var bench = new Bench();

            var template = bench.Template(nameof(Person.Id));

            Assert.False(BothRoutesAgree(
                Of(bench.Property<string>(x => x.First, filterValue: "First2"), template),
                SortedBy(template), Options(LogicalFilterOperator.Or)));
        }

        // What a declining column costs, on each route, and neither of these was pinned by anything
        // before §17 went looking. Both use the column that already declines - a template column told a
        // path and no sort - so neither needs a type that does not ship.
        //
        // The delegate route hands the whole composition over when a column declines, but only while
        // handing over is still possible, which is before any ordering has begun. A *later* column is
        // past that point: it is left out, and the composition stays where it is.
        [Fact]
        public void OnTheDelegateRouteALaterDecliningColumnIsLeftOut()
        {
            using var bench = new Bench();

            var id = bench.Property<int>(x => x.Id);
            var declines = bench.Template(nameof(Person.Id));
            var people = People20();
            var pass = default(DrawPass<Person>);

            var composed = Composition.Compose(Of(id, declines), SortedByDescending(id, declines),
                people, Options(), ref pass);

            // Still the cheap route - the whole point of the rule. Handing over here would have been
            // the same rows about a millisecond later at a thousand of them.
            Assert.True(composed.InMemory);

            // And ordered by the column that could, rather than not ordered at all.
            Assert.Equal(ByIdDescending(people), composed.Rows.ToArray());
        }

        // The expression route absorbs a decline instead of handing anything over, because it has
        // somewhere to put it: the column is left out and the rest of the ordering stands. One
        // uncomparable column must not cost the sort the caller asked for.
        [Fact]
        public void OnTheExpressionRouteADecliningColumnIsLeftOutAndTheRestStands()
        {
            using var bench = new Bench();

            var id = bench.Property<int>(x => x.Id);
            var declines = bench.Template(nameof(Person.Id));
            var people = People20();
            var pass = default(DrawPass<Person>);

            var composed = Composition.Compose(Of(id, declines), SortedByDescending(id, declines),
                people.AsQueryable(), Options(), ref pass);

            Assert.False(composed.InMemory);
            Assert.Equal(ByIdDescending(people), composed.Rows.ToArray());
        }

        // The expression route's loop tests whether an ordering has begun, and that is not the same
        // question as "is this the first column" - which is exactly what it is on the delegate route.
        // Here a *first* column that declines leaves nothing begun, so the second column has to start
        // the ordering rather than add to one. Get that wrong and the second column is handed a null
        // ordering to append to.
        //
        // Nothing reached this until it was written: both of the tests above put the declining column
        // second, which only ever exercises the other branch.
        [Fact]
        public void OnTheExpressionRouteAFirstDecliningColumnLetsTheNextOneStartTheOrdering()
        {
            using var bench = new Bench();

            var declines = bench.Template(nameof(Person.Id));
            var id = bench.Property<int>(x => x.Id);
            var people = People20();
            var pass = default(DrawPass<Person>);

            var composed = Composition.Compose(Of(declines, id), SortedByDescending(declines, id),
                people.AsQueryable(), Options(), ref pass);

            Assert.Equal(ByIdDescending(people), composed.Rows.ToArray());
        }

        // Switching filtering off is not the same as having nothing to filter by. A column can be
        // carrying a filter value the whole time - declared as a parameter, or left behind when the
        // feature was switched off - and the source has to come back untouched.
        //
        // Exactly one place asks, and this is what says it is asked. Nothing downstream re-asks:
        // ComposeInMemory builds its predicate from whatever columns report and would filter a grid
        // that has filtering switched off, which is the shape §10b keeps finding - a rule applied in
        // one place and not in its neighbour. Removing the gate passed the whole suite before this.
        [Fact]
        public void WithFilteringOffAColumnCarryingAFilterDoesNotFilter()
        {
            using var bench = new Bench();

            var columns = Of(bench.Property<string>(x => x.First, filterValue: "First1"));
            var people = People20();
            var off = new CompositionOptions(false, FilterCaseSensitivity.Default,
                LogicalFilterOperator.And);
            var pass = default(DrawPass<Person>);

            var composed = Composition.Compose(columns, Unsorted, people, off, ref pass);

            // The source itself, not a sequence equal to it: nothing was composed onto it at all.
            Assert.Same(people, composed.Rows);
        }

        // Two calls inside one pass, which is what a render is: the body enumerates the rows and the
        // pager counts what the filter left, over the same source instance. The second is answered from
        // the memo rather than composed again - so the memo has to hold the route as well as the rows,
        // or the second caller is told the composition took a route it did not take.
        [Fact]
        public void TheSecondCallInAPassIsToldTheFirstCallsRoute()
        {
            using var bench = new Bench();

            var columns = Of(bench.Property<string>(x => x.First, filterValue: "First1"));
            var people = People20();
            var pass = DrawPass<Person>.Begin(Composition.DeclaredFilters(columns, Options()));

            var first = Composition.Compose(columns, Unsorted, people, Options(), ref pass);
            var second = Composition.Compose(columns, Unsorted, people, Options(), ref pass);

            Assert.True(first.InMemory);
            Assert.Same(first.Rows, second.Rows);
            Assert.Equal(first.InMemory, second.InMemory);
        }
    }
}
