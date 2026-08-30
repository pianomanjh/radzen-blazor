using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// The drop-down whose popup is a RadzenFastGrid. Its columns are the grid's own, so the row type
    /// is a type parameter and the authoring is checked at compile time - which is what makes it not a
    /// drop-in for RadzenDropDownDataGrid, whose columns name their property with a string.
    /// </summary>
    public class FastDropDownDataGridTests
    {
        static RenderFragment Columns => FastGrid.Tests.Columns.Of(
            FastGrid.Tests.Columns.Property<Person, string>(p => p.First, title: "First"),
            FastGrid.Tests.Columns.Property<Person, string>(p => p.Last, title: "Last"));

        static IRenderedComponent<RadzenFastDropDownDataGrid<Person, object>> Render(TestContext ctx,
            Action<ComponentParameterCollectionBuilder<RadzenFastDropDownDataGrid<Person, object>>>? extra = null,
            IEnumerable<Person>? data = null)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.RenderComponent<RadzenFastDropDownDataGrid<Person, object>>(p =>
            {
                p.Add(d => d.Data, data ?? People.Sample());
                p.Add(d => d.ChildContent, Columns);
                p.Add(d => d.TextProperty, "First");
                extra?.Invoke(p);
            });
        }

        static void Open(IRenderedComponent<RadzenFastDropDownDataGrid<Person, object>> cut) =>
            cut.Find(".rz-dropdown").Click();

        static void ClickRow(IRenderedComponent<RadzenFastDropDownDataGrid<Person, object>> cut, int index) =>
            cut.FindAll("tbody tr")[index].Click();

        [Fact]
        public void TheClosedDropDownShowsThePlaceholderAndNoGrid()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p => p.Add(d => d.Placeholder, "Pick someone"));

            Assert.Equal("Pick someone", cut.Find(".rz-placeholder").TextContent);

            // A closed drop-down builds no grid at all: its columns should not be registering against
            // something nobody can see, and a lookup on a busy form should cost nothing until opened.
            Assert.Empty(cut.FindAll("table"));
        }

        [Fact]
        public void OpeningItBuildsTheGrid()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);

            Open(cut);

            Assert.Equal(new[] { "First", "Last" }, cut.FindAll("thead tr")[0]
                .QuerySelectorAll("th").Select(th => th.TextContent).ToArray());
            Assert.Equal(4, cut.FindAll("tbody tr").Count);
        }

        [Fact]
        public void ChoosingARowSetsTheValueAndClosesThePopup()
        {
            using var ctx = new TestContext();
            object? changed = null;

            var cut = Render(ctx, p =>
            {
                p.Add(d => d.ValueProperty, "Id");
                p.Add(d => d.Change, EventCallback.Factory.Create<object?>(new object(), v => changed = v));
            });

            Open(cut);
            ClickRow(cut, 0);

            // Carol is the first row of the sample and has Id 3.
            Assert.Equal(3, changed);
            Assert.Equal(3, cut.Instance.Value);
            Assert.False(cut.Instance.Open);
            Assert.Equal("Carol", cut.Find(".rz-dropdown-label").TextContent);
        }

        [Fact]
        public void WithNoValuePropertyTheRowItselfIsTheValue()
        {
            using var ctx = new TestContext();
            var people = People.Sample();

            var cut = Render(ctx, data: people);

            Open(cut);
            ClickRow(cut, 1);

            Assert.Same(people[1], cut.Instance.Value);
        }

        [Fact]
        public void ABoundValueIsShownWithoutOpeningThePopup()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p =>
            {
                p.Add(d => d.ValueProperty, "Id");
                p.Add(d => d.Value, 1);
            });

            Assert.Equal("Alice", cut.Find(".rz-dropdown-label").TextContent);
        }

        [Fact]
        public void ChoosingSeveralRowsKeepsThePopupOpenAndListsThem()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p =>
            {
                p.Add(d => d.Multiple, true);
                p.Add(d => d.ValueProperty, "Id");
            });

            Open(cut);
            ClickRow(cut, 0);
            ClickRow(cut, 1);

            Assert.True(cut.Instance.Open);
            Assert.Equal("Carol, Alice", cut.Find(".rz-dropdown-label").TextContent);
            Assert.Equal(new object[] { 3, 1 }, ((IEnumerable<object>)cut.Instance.Value!).ToArray());
        }

        [Fact]
        public void MultipleBindsToATypedList()
        {
            // A List<object> is not an IEnumerable<int>, however assignable its contents are, so binding
            // Multiple to anything but object would have failed the cast on the first selection.
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            IEnumerable<int>? bound = null;

            var cut = ctx.RenderComponent<RadzenFastDropDownDataGrid<Person, IEnumerable<int>>>(p =>
            {
                p.Add(d => d.Data, People.Sample());
                p.Add(d => d.ChildContent, Columns);
                p.Add(d => d.TextProperty, "First");
                p.Add(d => d.ValueProperty, "Id");
                p.Add(d => d.Multiple, true);
                p.Add(d => d.ValueChanged,
                    EventCallback.Factory.Create<IEnumerable<int>?>(new object(), v => bound = v));
            });

            cut.Find(".rz-dropdown").Click();
            cut.FindAll("tbody tr")[0].Click();

            Assert.Equal(new[] { 3 }, bound!.ToArray());
        }

        [Fact]
        public void ClickingAChosenRowAgainUnchoosesIt()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p =>
            {
                p.Add(d => d.Multiple, true);
                p.Add(d => d.ValueProperty, "Id");
            });

            Open(cut);
            ClickRow(cut, 0);
            ClickRow(cut, 0);

            Assert.Empty(cut.Instance.SelectedItems);
            Assert.Empty((IEnumerable<object>)cut.Instance.Value!);
        }

        [Fact]
        public void AChosenRowIsMarkedInTheGrid()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p => p.Add(d => d.Multiple, true));

            Open(cut);
            ClickRow(cut, 0);

            Assert.Contains("rz-state-highlight", cut.FindAll("tbody tr")[0].ClassName);
        }

        [Fact]
        public void TheGridInThePopupSortsAndFilters()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);

            Open(cut);

            cut.FindAll("thead th")[0].QuerySelector("div")!.Click();

            Assert.Equal("Alice", cut.FindAll("tbody tr")[0].QuerySelectorAll("td")[0].TextContent);

            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[0].Change("Bob");

            Assert.Single(cut.FindAll("tbody tr"));
        }

        [Fact]
        public void EscapeClosesThePopup()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);

            Open(cut);

            Assert.True(cut.Instance.Open);

            cut.Find(".rz-lookup-panel").KeyDown("Escape");

            Assert.False(cut.Instance.Open);
        }

        [Fact]
        public void ADisabledDropDownDoesNotOpen()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, p => p.Add(d => d.Disabled, true));

            Open(cut);

            Assert.False(cut.Instance.Open);
            Assert.Contains("rz-state-disabled", cut.Find(".rz-dropdown").ClassName);
        }

        [Fact]
        public void ItEmitsTheClassNamesTheThemeStyles()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);

            Assert.NotNull(cut.Find(".rz-dropdown"));
            Assert.NotNull(cut.Find(".rz-dropdown-trigger .rzi-chevron-down"));
            Assert.NotNull(cut.Find(".rz-dropdown-panel"));
            Assert.NotNull(cut.Find(".rz-lookup-panel"));

            // Multiple gets the wider panel, exactly as the Radzen drop-down family does.
            cut.SetParametersAndRender(p => p.Add(d => d.Multiple, true));

            Assert.NotNull(cut.Find(".rz-multiselect-panel"));
        }
    }
}
