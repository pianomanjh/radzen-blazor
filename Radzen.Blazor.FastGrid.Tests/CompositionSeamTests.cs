using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// That the grid composes through <see cref="Composition" /> rather than beside it.
    /// </summary>
    /// <remarks>
    /// A module can be correct and unused, and everything stays green: the seam tests pass because the
    /// module is right, and the grid goes on doing its own thing. This branch's recorded failure mode is
    /// a silent wrong answer and that is exactly its shape - so these render a real grid and hold what
    /// it shows against what the module says about the same rows, the same column and the same sort.
    /// <para>
    /// The rows are the obvious half. The route is the load-bearing one: it is invisible in the rows,
    /// the two ways of getting them differ by about 1.1 ms per render at a thousand, and it is the one
    /// answer a grid that had quietly kept a composition of its own would have to keep in step by hand.
    /// </para>
    /// <para>
    /// Which is why the third case is here and is not redundant. A grid that answered the route from the
    /// shape of its source - in memory if the source is not a queryable, which is what the flag looks
    /// like it means - agrees with the module about the first two and is caught only by the third: a
    /// list whose column cannot compose in memory, where the source is a list and the route is not the
    /// in-memory one. That mutation passed the whole suite before this case existed.
    /// </para>
    /// </remarks>
    public class CompositionSeamTests
    {
        /// <summary>A column, declared for a grid and built again loose, so both are asked the same thing.</summary>
        sealed class Case
        {
            internal RenderFragment Declared { get; init; } = default!;

            internal Func<TestContext, ColumnBase<Person>> Loose { get; init; } = default!;

            internal Func<Person, string> CellText { get; init; } = default!;
        }

        static Case PropertyColumn => new Case
        {
            Declared = Columns.Of(Columns.Property<Person, string>(x => x.First, title: "First")),
            Loose = ctx => ctx.RenderComponent<PropertyColumn<Person, string>>(p =>
            {
                p.AddCascadingValue(new RadzenFastGrid<Person>());
                p.Add(c => c.Property, x => x.First);
            }).Instance,
            CellText = person => person.First!,
        };

        // A template column with a path and no sort of its own cannot order an in-memory sequence, so
        // the whole composition goes back to the expression route - over a list.
        static Case TemplateColumnSortedByAPath => new Case
        {
            Declared = Columns.Of(Columns.Template<Person>(
                person => builder => builder.AddContent(0, person.Id), title: "Id",
                sortProperty: nameof(Person.Id))),
            Loose = ctx => ctx.RenderComponent<TemplateColumn<Person>>(p =>
            {
                p.AddCascadingValue(new RadzenFastGrid<Person>());
                p.Add(c => c.Template, person => builder => builder.AddContent(0, person.Id));
                p.Add(c => c.SortProperty, nameof(Person.Id));
            }).Instance,
            CellText = person => person.Id.ToString(CultureInfo.InvariantCulture),
        };

        /// <summary>
        /// One grid sorted by its only column, against one call to the module with the same rows, the
        /// same column and the same sort.
        /// </summary>
        static void TheGridShowsWhatTheModuleComposes(Case column,
            Func<List<Person>, IEnumerable<Person>> source)
        {
            var people = People.Many(8);
            var data = source(people);

            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data);
                p.Add(g => g.ChildContent, column.Declared);
                p.Add(g => g.AllowSorting, true);
            });

            // Ascending, which is what a first click on an unsorted column means.
            cut.FindAll("thead th")[0].QuerySelector("div").Click();

            // The same column again, built outside any grid, so that what the module is asked is what
            // the grid was asked and not what the grid decided to pass on.
            var loose = column.Loose(ctx);
            var pass = default(DrawPass<Person>);

            var composed = Composition.Compose(new[] { loose }, new[] { (loose, false) }, data,
                new CompositionOptions(false, FilterCaseSensitivity.Default,
                    LogicalFilterOperator.And), ref pass);

            Assert.Equal(composed.Rows.Select(column.CellText).ToArray(),
                cut.FindAll("tbody tr td:first-child").Select(cell => cell.TextContent).ToArray());

            Assert.Equal(composed.InMemory, cut.Instance.ComposedInMemory);
        }

        // A list is composed with delegates, and the grid should be reporting the module's answer about
        // that rather than one of its own.
        [Fact]
        public void OverAListTheGridShowsWhatTheModuleComposes() =>
            TheGridShowsWhatTheModuleComposes(PropertyColumn, people => people);

        // The other value of the same flag, so that a grid hard-wiring either answer fails one of these.
        [Fact]
        public void OverAQueryableTheGridShowsWhatTheModuleComposes() =>
            TheGridShowsWhatTheModuleComposes(PropertyColumn, people => people.AsQueryable());

        // The case that separates "what the module decided" from "what the source looks like", which is
        // the only difference a grid composing beside the module would show.
        [Fact]
        public void OverAListThatCannotComposeInMemoryTheGridStillShowsTheModulesRoute() =>
            TheGridShowsWhatTheModuleComposes(TemplateColumnSortedByAPath, people => people);

        // The same rule as the seam's own memo test, asked of a real grid, because that is what says it
        // is reachable rather than merely representable: a filtered, paged list is composed twice in one
        // render - the body enumerates it and the pager counts what the filter left, over the one source
        // instance.
        //
        // Which of the two goes first does not matter, and that is what makes this discriminate rather
        // than depend on the pager's position: whichever composes first composes for real, every one
        // after it is answered from the memo, and so the last write to the flag is always the memoized
        // answer. A memo that dropped the route ends the render having told the grid the wrong thing
        // about the render it just did.
        [Fact]
        public void TheSecondCallerOfARenderIsToldTheRouteTheFirstOneTook()
        {
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Many(20));
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.AllowPaging, true);
                p.Add(g => g.PageSize, 5);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First, title: "First",
                        filterValue: "First1")));
            });

            Assert.True(cut.Instance.ComposedInMemory);
        }
    }
}
