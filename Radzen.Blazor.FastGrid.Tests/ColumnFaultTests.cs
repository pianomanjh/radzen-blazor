using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// Column-level faults a code review found, each one a case the column model got wrong rather than
    /// a feature of its own.
    /// </summary>
    public class ColumnFaultTests
    {
        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx, RenderFragment columns,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>>? extra = null,
            IEnumerable<Person>? data = null)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data ?? People.Sample());
                p.Add(g => g.ChildContent, columns);
                extra?.Invoke(p);
            });
        }

        static string[] Cells(IRenderedComponent<RadzenFastGrid<Person>> cut, int index) =>
            cut.FindAll("tbody tr").Select(row => row.QuerySelectorAll("td")[index].TextContent).ToArray();

        // The separator is baked into the compiled cell delegate, but was not in the guard that decides
        // whether to rebuild it - so a column bound to a chosen separator kept the first one for good.
        [Fact]
        public void ChangingTheSeparatorChangesTheCells()
        {
            using var ctx = new TestContext();
            var separator = " / ";

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, List<string>>(p => p.Regions, separator: separator)));

            Assert.Equal("North / West", Cells(cut, 0)[0]);

            cut.SetParametersAndRender(p => p.Add(g => g.ChildContent, Columns.Of(
                Columns.Property<Person, List<string>>(p => p.Regions, separator: " | "))));

            Assert.Equal("North | West", Cells(cut, 0)[0]);
        }

        [Fact]
        public void AColumnWithNoPropertyRendersEmptyCellsRatherThanThrowing()
        {
            // Property is EditorRequired, which is a warning rather than a guarantee. The compiled
            // delegate was never built, and the first cell dereferenced it.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(null, title: "Nothing"),
                Columns.Property<Person, string>(p => p.First)));

            Assert.Equal(new[] { "", "", "", "" }, Cells(cut, 0));
            Assert.Equal("Carol", Cells(cut, 1)[0]);
        }

        [Fact]
        public void AnUntitledColumnIsHeadedByWhatItShows()
        {
            // path is the sort key once SortBy is set, and the header fell back to it - so a column of
            // first names sorted by surname was headed "Last".
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, sortBy: p => p.Last)));

            Assert.Equal("First", cut.Find("thead th").TextContent);
        }

        [Fact]
        public void ADeclaredFilterOnAnObjectTypedColumnUsesContains()
        {
            // The base picks the default operator from the filter path, and the path was derived after
            // the base had run - so such a column defaulted to Equals, and nothing recomputed it.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, object>(p => p.First, filterValue: "Ca"),
                Columns.Property<Person, string>(p => p.Last)),
                p => p.Add(g => g.AllowFiltering, true));

            Assert.Equal(new[] { "Carol" }, Cells(cut, 0));
        }

        [Fact]
        public void ACheckBoxListOffersTheValuesItFiltersBy()
        {
            // The values offered came from Property while the filter compared FilterBy, so the list
            // showed one column's values and every choice filtered another column by them.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, filterBy: p => p.Last)),
                p =>
                {
                    p.Add(g => g.AllowFiltering, true);
                    p.Add(g => g.FilterMode, FilterMode.CheckBoxList);
                });

            var offered = cut.FindComponent<RadzenDropDown<IEnumerable>>()
                .Instance.Data.Cast<object>().Select(v => v.ToString()).ToArray();

            Assert.Equal(new[] { "Adams", "Bell", "Cook", "Draper" }, offered);
        }

        [Fact]
        public void AScalarFilterValueOnACheckBoxListColumnDoesNotThrow()
        {
            // A review claimed the drop-down's IEnumerable-typed Value would fail the parameter cast.
            // It does not - this passes against the code as it was - but a scalar can reach that Value
            // through ApplyFilters, Filter(column, value) or a declared FilterValue, so the path is
            // worth holding still: the filter applies and the render survives.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, int>(p => p.Id),
                Columns.Property<Person, string>(p => p.First)),
                p =>
                {
                    p.Add(g => g.AllowFiltering, true);
                    p.Add(g => g.FilterMode, FilterMode.CheckBoxList);
                });

            var applied = Record.Exception(() => cut.InvokeAsync(() =>
                cut.Instance.ApplyFilters(new[]
                {
                    new FilterDescriptor { Property = "Id", FilterValue = 3 },
                })).GetAwaiter().GetResult());

            Assert.Null(applied);
            Assert.Equal(new[] { "Carol" }, Cells(cut, 1));
        }

        [Fact]
        public void ALookupOfValuesThatCannotBeComparedToEachOtherStillRenders()
        {
            // The first value being comparable says nothing about the rest. Int32.CompareTo(object)
            // throws, and List.Sort wrapped it as "failed to compare two elements" - out of a render.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, object>(p => p.Mixed)),
                p =>
                {
                    p.Add(g => g.AllowFiltering, true);
                    p.Add(g => g.FilterMode, FilterMode.CheckBoxList);
                });

            var offered = cut.FindComponent<RadzenDropDown<IEnumerable>>()
                .Instance.Data.Cast<object>().Select(v => v.ToString()).ToArray();

            Assert.Equal(4, offered.Length);
            Assert.Contains("n/a", offered);
        }

        [Fact]
        public void AnEmptyVirtualizedGridStillShowsItsEmptyTemplate()
        {
            // Virtualize owns the body while it is on, so the empty row the inline path writes is
            // unreachable and the grid showed a header over nothing.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, string>(p => p.First)),
                p =>
                {
                    p.Add(g => g.AllowVirtualization, true);
                    p.Add(g => g.EmptyTemplate, (RenderFragment)(b => b.AddContent(0, "Nothing here")));
                },
                data: new List<Person>());

            cut.WaitForAssertion(() =>
                Assert.Contains("Nothing here", cut.Find(".rz-datatable-emptymessage").TextContent,
                    StringComparison.Ordinal));
        }

        [Fact]
        public void ACollectionOfPartlyPopulatedMembersStillRenders()
        {
            // A null in the collection is a partly populated graph, not a reason to take a render down.
            using var ctx = new TestContext();

            var people = new List<Person>
            {
                new()
                {
                    First = "Ada",
                    Accounts = new List<Company> { new() { Name = "Acme" }, null!, new() { Name = "Zeta" } },
                },
            };

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                ctx.JSInterop.Mode = JSRuntimeMode.Loose;
                p.Add(g => g.Data, people);
                p.Add(g => g.ChildContent, Columns.Of(Columns.Collection<Person, Company>(
                    x => x.Accounts, displayProperty: a => a.Name)));
            });

            Assert.Equal("Acme, , Zeta", cut.Find("tbody td span").TextContent);
        }
    }
}
