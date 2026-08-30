using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// Header and footer templates, sorting by more than one column, and storing what a user changed.
    /// All three are per column or per grid; none of them touches a row.
    /// </summary>
    public class FastGridTemplateSortSettingsTests
    {
        static TestContext Context()
        {
            var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");
            return ctx;
        }

        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx, RenderFragment columns,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>> extra = null,
            IEnumerable<Person> data = null) =>
            ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data ?? People.Sample());
                p.Add(g => g.ChildContent, columns);
                extra?.Invoke(p);
            });

        static string[] Names(IRenderedComponent<RadzenFastGrid<Person>> cut, int column = 1) =>
            cut.FindAll($"tbody tr td:nth-child({column})").Select(c => c.TextContent).ToArray();

        // The sort handler is on the header's inner div, not the th: the theme gives th padding:0 and
        // hangs the header's own padding off that div, so clicking the th's padding is clicking nothing.
        static void ClickHeader(IRenderedComponent<RadzenFastGrid<Person>> cut, int index) =>
            cut.FindAll("thead th > div")[index].Click();

        // --- header and footer templates -------------------------------------------------------

        // Inside the theme's title spans, not instead of them: content placed outside loses the header's
        // truncation and spacing.
        [Fact]
        public void HeaderTemplateReplacesTheTitleInsideTheThemeWrapper()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First",
                    headerTemplate: column => builder => builder.AddMarkupContent(0, "<em>Given</em>"))));

            var content = cut.Find("thead th .rz-column-title .rz-column-title-content");

            Assert.Equal("Given", content.TextContent);
            Assert.NotNull(content.QuerySelector("em"));
            Assert.DoesNotContain("First", cut.Find("thead").TextContent);
        }

        [Fact]
        public void TheHeaderTemplateIsGivenItsColumn()
        {
            using var ctx = Context();

            ColumnBase<Person> seen = null;

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First",
                    headerTemplate: column => builder =>
                    {
                        seen = column;
                        builder.AddContent(0, column.Title);
                    })));

            Assert.Equal("First", seen.Title);
        }

        [Fact]
        public void NoFooterTemplateMeansNoFooterAtAll()
        {
            using var ctx = Context();

            Assert.Empty(Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First"))).FindAll("tfoot"));
        }

        [Fact]
        public void TheFooterRowHasACellForEveryColumn()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First"),
                Columns.Property<Person, decimal>(p => p.Salary, title: "Salary",
                    footerCssClass: "total",
                    footerTemplate: column => builder => builder.AddContent(0, "10,000"))));

            var cells = cut.FindAll("tfoot tr td");

            Assert.Equal(2, cells.Count);

            // The column with no template still gets its cell, or the footer would be short a column
            // and everything after it would sit under the wrong header.
            Assert.Equal("", cells[0].TextContent);
            Assert.NotNull(cells[0].QuerySelector(".rz-column-footer"));
            Assert.Equal("10,000", cells[1].TextContent);
            Assert.Equal("total", cells[1].GetAttribute("class"));
        }

        // --- multi-column sorting --------------------------------------------------------------

        [Fact]
        public void ASecondColumnReplacesTheSortByDefault()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, Grade>(p => p.Grade, title: "Grade"),
                Columns.Property<Person, string>(p => p.First, title: "First")),
                p => p.Add(g => g.AllowSorting, true));

            ClickHeader(cut, 0);
            ClickHeader(cut, 1);

            var headers = cut.FindAll("thead th");

            Assert.Null(headers[0].GetAttribute("aria-sort"));
            Assert.Equal("ascending", headers[1].GetAttribute("aria-sort"));
        }

        [Fact]
        public void ASecondColumnAddsToTheSortWhenMultiColumnIsOn()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, Grade>(p => p.Grade, title: "Grade"),
                Columns.Property<Person, string>(p => p.First, title: "First")),
                p =>
                {
                    p.Add(g => g.AllowSorting, true);
                    p.Add(g => g.AllowMultiColumnSorting, true);
                });

            ClickHeader(cut, 0);
            ClickHeader(cut, 1);

            var headers = cut.FindAll("thead th");

            Assert.Equal("ascending", headers[0].GetAttribute("aria-sort"));
            Assert.Equal("ascending", headers[1].GetAttribute("aria-sort"));

            // Junior before Senior, and within each grade by first name - which is a different order
            // from either column on its own, so it can only come from both being applied.
            Assert.Equal(new[] { "Alice", "Dave", "Bob", "Carol" }, Names(cut, 2));
        }

        // Ascending, descending, gone. Removing is only reachable this way, since there is nowhere else
        // to click - and without it a column joins the sort and can never leave.
        [Fact]
        public void ClickingCyclesAColumnOutOfTheSort()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, Grade>(p => p.Grade, title: "Grade"),
                Columns.Property<Person, string>(p => p.First, title: "First")),
                p =>
                {
                    p.Add(g => g.AllowSorting, true);
                    p.Add(g => g.AllowMultiColumnSorting, true);
                });

            ClickHeader(cut, 0);
            Assert.Equal("ascending", cut.FindAll("thead th")[0].GetAttribute("aria-sort"));

            ClickHeader(cut, 0);
            Assert.Equal("descending", cut.FindAll("thead th")[0].GetAttribute("aria-sort"));

            ClickHeader(cut, 0);
            Assert.Null(cut.FindAll("thead th")[0].GetAttribute("aria-sort"));
            Assert.Empty(cut.Instance.Sorts);
        }

        [Fact]
        public void TheSortIndexIsShownOnlyWhenThereIsMoreThanOne()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, Grade>(p => p.Grade, title: "Grade"),
                Columns.Property<Person, string>(p => p.First, title: "First")),
                p =>
                {
                    p.Add(g => g.AllowSorting, true);
                    p.Add(g => g.AllowMultiColumnSorting, true);
                    p.Add(g => g.ShowMultiColumnSortingIndex, true);
                });

            ClickHeader(cut, 0);

            // One sorted column: the number would say nothing it does not already say.
            Assert.Empty(cut.FindAll("thead .rz-badge"));

            ClickHeader(cut, 1);

            var badges = cut.FindAll("thead .rz-badge");

            Assert.Equal(2, badges.Count);
            Assert.Equal("1", badges[0].TextContent);
            Assert.Equal("2", badges[1].TextContent);
        }

        [Fact]
        public void DeclaredSortOrdersComposeInDeclarationOrder()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, Grade>(p => p.Grade, title: "Grade", sortOrder: SortOrder.Ascending),
                Columns.Property<Person, string>(p => p.First, title: "First", sortOrder: SortOrder.Descending)),
                p =>
                {
                    p.Add(g => g.AllowSorting, true);
                    p.Add(g => g.AllowMultiColumnSorting, true);
                });

            Assert.Equal(new[] { "Dave", "Alice", "Carol", "Bob" }, Names(cut, 2));
        }

        [Fact]
        public void TheSortIsExposedAsDescriptorsInPrecedenceOrder()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, Grade>(p => p.Grade, title: "Grade"),
                Columns.Property<Person, string>(p => p.First, title: "First")),
                p =>
                {
                    p.Add(g => g.AllowSorting, true);
                    p.Add(g => g.AllowMultiColumnSorting, true);
                });

            ClickHeader(cut, 1);
            ClickHeader(cut, 1);
            ClickHeader(cut, 0);

            var sorts = cut.Instance.Sorts;

            Assert.Equal(2, sorts.Count);
            Assert.Equal("First", sorts[0].Property);
            Assert.Equal(SortOrder.Descending, sorts[0].SortOrder);
            Assert.Equal("Grade", sorts[1].Property);
            Assert.Equal(SortOrder.Ascending, sorts[1].SortOrder);
        }

        // --- settings ---------------------------------------------------------------------------

        [Fact]
        public void SortingRaisesTheSettingsThatWouldRestoreIt()
        {
            using var ctx = Context();

            FastGridSettings raised = null;

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First")),
                p =>
                {
                    p.Add(g => g.AllowSorting, true);
                    p.Add(g => g.SettingsChanged, (FastGridSettings s) => raised = s);
                });

            ClickHeader(cut, 0);

            var column = Assert.Single(raised.Columns);

            Assert.Equal("First", column.Property);
            Assert.Equal(SortOrder.Ascending, column.SortOrder);
            Assert.Equal(0, raised.CurrentPage);
        }

        [Fact]
        public void StoredSettingsAreRestoredOnTheFirstRender()
        {
            using var ctx = Context();

            var settings = new FastGridSettings
            {
                Columns = new List<FastGridColumnSettings>
                {
                    new() { Property = "First", SortOrder = SortOrder.Descending },
                },
            };

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First")),
                p =>
                {
                    p.Add(g => g.AllowSorting, true);
                    p.Add(g => g.Settings, settings);
                });

            // No second render needed: the pass that applied the settings is the pass that composed
            // the view from them.
            Assert.Equal(new[] { "Dave", "Carol", "Bob", "Alice" }, Names(cut));
            Assert.Equal("descending", cut.Find("thead th").GetAttribute("aria-sort"));
        }

        [Fact]
        public void StoredFiltersAndPagingAreRestored()
        {
            using var ctx = Context();

            var settings = new FastGridSettings
            {
                Columns = new List<FastGridColumnSettings>
                {
                    new() { Property = "Grade", FilterValue = Grade.Junior, FilterOperator = FilterOperator.Equals },
                },
                PageSize = 1,
                CurrentPage = 1,
            };

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(p => p.First, title: "First"),
                Columns.Property<Person, Grade>(p => p.Grade, title: "Grade")),
                p =>
                {
                    p.Add(g => g.AllowFiltering, true);
                    p.Add(g => g.AllowPaging, true);
                    p.Add(g => g.Settings, settings);
                });

            // Two juniors match; page two of one row each is the second of them.
            Assert.Equal(new[] { "Dave" }, Names(cut));
        }

        // A round trip is the property that matters: whatever the grid hands out has to put the grid
        // back the way it was.
        [Fact]
        public void CapturedSettingsRestoreTheGrid()
        {
            using var ctx = Context();

            RenderFragment columns = Columns.Of(
                Columns.Property<Person, Grade>(p => p.Grade, title: "Grade"),
                Columns.Property<Person, string>(p => p.First, title: "First"));

            void Configure(ComponentParameterCollectionBuilder<RadzenFastGrid<Person>> p)
            {
                p.Add(g => g.AllowSorting, true);
                p.Add(g => g.AllowMultiColumnSorting, true);
            }

            var first = Render(ctx, columns, Configure);

            ClickHeader(first, 0);
            ClickHeader(first, 1);
            ClickHeader(first, 1);

            var expected = Names(first, 2);
            var captured = first.Instance.CaptureSettings();

            using var other = Context();

            var restored = Render(other, columns, p =>
            {
                Configure(p);
                p.Add(g => g.Settings, captured);
            });

            Assert.Equal(expected, Names(restored, 2));
        }
    }
}
