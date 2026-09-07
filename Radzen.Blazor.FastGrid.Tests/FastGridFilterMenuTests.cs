using System;
using System.Collections.Generic;
using System.Linq;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// §35's panel, asked of a rendered grid - which is what the pure rules in
    /// <see cref="FastGridFilterMenuRuleTests" /> deliberately are not.
    /// </summary>
    public class FastGridFilterMenuTests
    {
        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx,
            RenderFragment? columns = null,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>>? extra = null,
            bool filtering = true, FilterUI ui = FilterUI.Menu)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, columns ?? Columns.Of(
                    Columns.Property<Person, string>(x => x.First),
                    Columns.Property<Person, DateTime>(x => x.Hired)));
                p.Add(g => g.AllowFiltering, filtering);
                p.Add(g => g.FilterUI, ui);
                extra?.Invoke(p);
            });
        }

        static void OpenMenu(IRenderedComponent<RadzenFastGrid<Person>> cut, int column) =>
            cut.FindAll("thead button.rz-filter-button")[column].Click();

        static IElement[] MenuItems(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindAll("button.rz-filter-menu-item").ToArray();

        static string[] MenuLabels(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            MenuItems(cut).Select(item => item.TextContent).ToArray();

        static void PickItem(IRenderedComponent<RadzenFastGrid<Person>> cut, string label) =>
            MenuItems(cut).Single(item => item.TextContent == label).Click();

        static string[] Cells(IRenderedComponent<RadzenFastGrid<Person>> cut, int column) =>
            cut.FindAll("tbody tr").Select(row => row.QuerySelectorAll("td")[column].TextContent)
                .ToArray();

        [Fact]
        public void TheMenuReplacesTheFilterRowRatherThanJoiningIt()
        {
            using var ctx = new TestContext();

            var row = Render(ctx, ui: FilterUI.Row);

            Assert.NotEmpty(row.FindAll("th div.rz-cell-filter"));
            Assert.Empty(row.FindAll("button.rz-filter-button"));

            var menu = Render(ctx);

            // Not a hidden filter row and not an empty one: none at all.
            Assert.Empty(menu.FindAll("th div.rz-cell-filter"));
            Assert.Equal(2, menu.FindAll("thead button.rz-filter-button").Count);
        }

        [Fact]
        public void NothingIsWrittenForAGridThatDoesNotFilter()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, filtering: false);

            Assert.Empty(cut.FindAll("button.rz-filter-button"));
            Assert.Empty(cut.FindAll("div.rz-overlaypanel"));
        }

        [Fact]
        public void AClosedMenuHasNoPanelAtAll()
        {
            // §31's "a popup that costs nothing while closed", and the half of §35's gate that is about
            // a grid nobody has clicked.
            using var ctx = new TestContext();

            var cut = Render(ctx);

            Assert.Empty(cut.FindAll("div.rz-overlaypanel"));

            OpenMenu(cut, 0);

            Assert.Single(cut.FindAll("div.rz-overlaypanel"));
        }

        [Fact]
        public void TheIconSaysWhetherItsColumnIsFiltered()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);
            var column = cut.FindComponent<PropertyColumn<Person, string>>().Instance;

            Assert.DoesNotContain("rz-grid-filter-active",
                cut.FindAll("thead button.rz-filter-button")[0].ClassName);

            cut.InvokeAsync(() => cut.Instance.Filter(column, "A", Radzen.FilterOperator.Contains));

            Assert.Contains("rz-grid-filter-active",
                cut.FindAll("thead button.rz-filter-button")[0].ClassName);
        }

        [Fact]
        public void TheIconIsOutsideTheTabOrderAndNamesItsColumn()
        {
            using var ctx = new TestContext();

            var icon = Render(ctx).FindAll("thead button.rz-filter-button")[0];

            // §12 settled that this grid is one tab stop. Eight icons in the tab order would undo it.
            Assert.Equal("-1", icon.GetAttribute("tabindex"));
            Assert.Equal("menu", icon.GetAttribute("aria-haspopup"));
            Assert.StartsWith("First", icon.GetAttribute("aria-label"));
        }

        [Fact]
        public void AStringColumnOffersTheTextOperatorsAndADateColumnOffersThePresets()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);

            OpenMenu(cut, 0);

            var text = MenuLabels(cut);

            Assert.Contains("Contains", text);
            Assert.DoesNotContain("Between", text);
            Assert.DoesNotContain("Last 7 days", text);

            OpenMenu(cut, 1);

            var dates = MenuLabels(cut);

            Assert.Contains("Between", dates);
            Assert.Contains("Last 7 days", dates);
            Assert.Contains("This year", dates);
            Assert.DoesNotContain("Contains", dates);
        }

        [Fact]
        public void TheBodyIsRekeyedWhenThePanelIsPointedAtAnotherColumn()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);

            OpenMenu(cut, 0);

            Assert.Contains("Contains", MenuLabels(cut));

            OpenMenu(cut, 1);

            // The last column's operators are gone rather than sitting under the new ones.
            Assert.DoesNotContain("Contains", MenuLabels(cut));
        }

        [Fact]
        public void NoEditorIsDrawnUntilAnOperatorIsPicked()
        {
            // A typed control cannot show empty for a non-nullable value type, so an editor drawn
            // before the operator opens showing a value nobody chose.
            using var ctx = new TestContext();

            var cut = Render(ctx);

            OpenMenu(cut, 0);

            Assert.Empty(cut.FindAll("div.rz-filter-menu-editor"));

            PickItem(cut, "Contains");

            Assert.Single(cut.FindAll("div.rz-filter-menu-editor"));
        }

        [Fact]
        public void ARangeGetsTwoEditorsAndAnOperatorWithNoValueGetsNone()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, int?>(x => x.RegionId)));

            OpenMenu(cut, 0);
            PickItem(cut, "Between");

            Assert.Equal(2, cut.FindAll("div.rz-filter-menu-editor input").Count);

            PickItem(cut, "Is null");

            Assert.Empty(cut.FindAll("div.rz-filter-menu-editor"));
        }

        [Fact]
        public void ApplyCommitsTheDraftAndClosesThePanel()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);
            var column = cut.FindComponent<PropertyColumn<Person, string>>().Instance;

            OpenMenu(cut, 0);
            PickItem(cut, "Contains");

            cut.Find("div.rz-filter-menu-editor input").Change("Ali");
            cut.Find("div.rz-filter-menu-buttons button.rz-primary").Click();

            Assert.True(column.HasFilter);
            Assert.Equal(FastGridFilterOperator.Contains, column.CurrentFilter!.First.Operator);
            Assert.Equal("Ali", column.CurrentFilter.First.Value);
            Assert.Equal(new[] { "Alice" }, Cells(cut, 0));
        }

        [Fact]
        public void TypingDoesNotFilterUntilApply()
        {
            // §31: a menu holding an operator and up to two values cannot filter as you type, because a
            // half-typed lower bound filters to nothing on every keystroke.
            using var ctx = new TestContext();

            var cut = Render(ctx);
            var column = cut.FindComponent<PropertyColumn<Person, string>>().Instance;

            OpenMenu(cut, 0);
            PickItem(cut, "Contains");

            cut.Find("div.rz-filter-menu-editor input").Change("Ali");

            Assert.False(column.HasFilter);
            Assert.Equal(4, Cells(cut, 0).Length);
        }

        [Fact]
        public void ClearRemovesTheFilterInOneReload()
        {
            // §31: clear is one operation at three scopes, and each costs exactly one reload. The naive
            // shape - clear, then reload, then let something else reload again - is what that rule bans.
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var loads = 0;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent,
                    Columns.Of(Columns.Property<Person, string>(x => x.First)));
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.FilterUI, FilterUI.Menu);
                p.Add(g => g.LoadData, EventCallback.Factory.Create<LoadDataArgs>(new object(),
                    args => loads++));
            });

            var column = cut.FindComponent<PropertyColumn<Person, string>>().Instance;

            cut.InvokeAsync(() => cut.Instance.Filter(column, "Ali", Radzen.FilterOperator.Contains));

            Assert.True(column.HasFilter);

            var before = loads;

            OpenMenu(cut, 0);
            cut.Find("div.rz-filter-menu-buttons button.rz-light").Click();

            Assert.False(column.HasFilter);
            Assert.Equal(before + 1, loads);
        }

        [Fact]
        public void APresetAppliesOnPickAndComesBackAsThePreset()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);
            var column = cut.FindComponent<PropertyColumn<Person, DateTime>>().Instance;

            OpenMenu(cut, 1);
            PickItem(cut, "Last 7 days");

            // No Apply step: a preset is a complete sentence.
            Assert.True(column.HasFilter);
            Assert.Equal(FastGridFilterPreset.Last7Days,
                FastGridFilterPresets.Recognize(column.CurrentFilter));

            // Tokens, not two resolved dates - §34's CurrentFilter is what the user authored.
            Assert.IsType<FastGridRelativeDate>(column.CurrentFilter!.First.Value);

            OpenMenu(cut, 1);

            var selected = MenuItems(cut)
                .Where(item => item.ClassName!.Contains("rz-state-highlight"))
                .Select(item => item.TextContent)
                .ToArray();

            Assert.Equal(new[] { "Last 7 days" }, selected);
        }

        [Fact]
        public void ADateEqualsIsCommittedAsTheWholeDay()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);
            var column = cut.FindComponent<PropertyColumn<Person, DateTime>>().Instance;

            OpenMenu(cut, 1);
            PickItem(cut, "Equals");
            cut.Find("div.rz-filter-menu-buttons button.rz-primary").Click();

            // Nothing was picked, so nothing was committed - and the operator did not survive as an
            // Equals waiting to match midnight.
            Assert.False(column.HasFilter);

            OpenMenu(cut, 1);
            PickItem(cut, "Today");

            Assert.Equal(FastGridFilterOperator.Between, column.CurrentFilter!.First.Operator);
        }

        [Fact]
        public void AFilterTemplateIsTheWholeBody()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, string>(x => x.First,
                filterTemplate: _ => builder => builder.AddMarkupContent(0, "<i id=\"own\"></i>"))));

            OpenMenu(cut, 0);

            Assert.Single(cut.FindAll("#own"));
            Assert.Empty(MenuItems(cut));
        }

        [Fact]
        public void AltDownOpensTheMenuForTheFocusedHeaderColumn()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, extra: p => p.Add(g => g.AllowKeyboardNavigation, true));
            var view = cut.Find("div.rz-data-grid-data");

            view.Focus();
            view.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs
            {
                Key = "ArrowUp",
            });
            view.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs
            {
                Key = "ArrowDown",
                AltKey = true,
            });

            Assert.Contains("Contains", MenuLabels(cut));
        }
    }
}
