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
            cut.FindAll("thead button.rz-grid-filter-icon")[column].Click();

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
            Assert.Empty(row.FindAll("button.rz-grid-filter-icon"));

            var menu = Render(ctx);

            // Not a hidden filter row and not an empty one: none at all.
            Assert.Empty(menu.FindAll("th div.rz-cell-filter"));
            Assert.Equal(2, menu.FindAll("thead button.rz-grid-filter-icon").Count);
        }

        [Fact]
        public void NothingIsWrittenForAGridThatDoesNotFilter()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, filtering: false);

            Assert.Empty(cut.FindAll("button.rz-grid-filter-icon"));
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
                cut.FindAll("thead button.rz-grid-filter-icon")[0].ClassName);

            cut.InvokeAsync(() => cut.Instance.Filter(column, "A", Radzen.FilterOperator.Contains));

            Assert.Contains("rz-grid-filter-active",
                cut.FindAll("thead button.rz-grid-filter-icon")[0].ClassName);
        }

        [Fact]
        public void TheIconIsOutsideTheTabOrderAndNamesItsColumn()
        {
            using var ctx = new TestContext();

            var icon = Render(ctx).FindAll("thead button.rz-grid-filter-icon")[0];

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

        [Fact]
        public void TickingABoxDraftsAndApplyKeepsWhatWasTicked()
        {
            // The review's first finding: the panel was reusing the filter row's multiselect, which is
            // bound to the committed filter and applies on every tick - so ticking filtered at once and
            // Apply then wrote the stale draft back over it. Confirming a selection undid it.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, Grade>(x => x.Grade)));
            var column = cut.FindComponent<PropertyColumn<Person, Grade>>().Instance;

            OpenMenu(cut, 0);
            PickItem(cut, "In");

            var list = cut.FindComponents<RadzenListBox<System.Collections.IEnumerable>>()[0];

            cut.InvokeAsync(() => list.Instance.Change.InvokeAsync(new List<object> { Grade.Senior }));

            // Nothing applied yet - §31's rule holds for the set editor too.
            Assert.False(column.HasFilter);

            cut.Find("div.rz-filter-menu-buttons button.rz-primary").Click();

            Assert.True(column.HasFilter);
            Assert.Equal(FastGridFilterOperator.In, column.CurrentFilter!.First.Operator);
            Assert.Equal(new[] { Grade.Senior },
                ((System.Collections.IEnumerable)column.CurrentFilter.First.Value!).Cast<Grade>());
        }

        [Fact]
        public void NotInIsReachableThroughTheList()
        {
            // The row's handler hard-codes In, so NotIn was offered by the menu and unreachable from it.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, Grade>(x => x.Grade)));
            var column = cut.FindComponent<PropertyColumn<Person, Grade>>().Instance;

            OpenMenu(cut, 0);
            PickItem(cut, "Not in");

            var list = cut.FindComponents<RadzenListBox<System.Collections.IEnumerable>>()[0];

            cut.InvokeAsync(() => list.Instance.Change.InvokeAsync(new List<object> { Grade.Senior }));
            cut.Find("div.rz-filter-menu-buttons button.rz-primary").Click();

            Assert.Equal(FastGridFilterOperator.NotIn, column.CurrentFilter!.First.Operator);
        }

        [Fact]
        public void EnterCommitsWhatWasTypedWithoutWaitingForABlur()
        {
            // §31 says the menu applies on Apply or Enter. Enter was not implemented at all, and the
            // comment claiming it was documented a feature that did not exist.
            using var ctx = new TestContext();

            var cut = Render(ctx);
            var column = cut.FindComponent<PropertyColumn<Person, string>>().Instance;

            OpenMenu(cut, 0);
            PickItem(cut, "Contains");

            // Input, not change: a keydown arrives before the change event, so committing on Enter is
            // only correct because the draft is kept current as the user types.
            cut.Find("div.rz-filter-menu-editor input").Input("Ali");
            cut.Find("div.rz-overlaypanel-content").KeyDown(
                new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });

            Assert.True(column.HasFilter);
            Assert.Equal("Ali", column.CurrentFilter!.First.Value);
        }

        [Fact]
        public void TheIconSaysWhatItControlsAndWhetherItIsOpen()
        {
            // Without aria-controls upstream's own setPopupAriaExpanded cannot find the anchor, so the
            // button claimed to have a menu and never said whether it was open.
            using var ctx = new TestContext();

            var cut = Render(ctx);
            var icon = cut.FindAll("thead button.rz-grid-filter-icon")[0];

            Assert.Equal("false", icon.GetAttribute("aria-expanded"));
            Assert.False(string.IsNullOrEmpty(icon.GetAttribute("aria-controls")));

            OpenMenu(cut, 0);

            Assert.Equal(icon.GetAttribute("aria-controls"),
                cut.Find("div.rz-overlaypanel").GetAttribute("id"));
        }

        [Fact]
        public void AnOperatorTheMenuDoesNotOfferIsNotSeededIntoTheDraft()
        {
            // A restored or markup-declared LessThanOrEquals on a date reached the draft, showed an
            // editor under no selection, and committed through the arm of the whole-day rule that leaves
            // a date literal - which is the boundary loss §34 built @end to prevent.
            using var ctx = new TestContext();

            var cut = Render(ctx);
            var column = cut.FindComponent<PropertyColumn<Person, DateTime>>().Instance;

            cut.InvokeAsync(() => cut.Instance.Filter(column, new DateTime(2026, 3, 31),
                FastGridFilterOperator.LessThanOrEquals));

            OpenMenu(cut, 1);

            // Nothing highlighted, and no editor under it: the user is asked to pick.
            Assert.Empty(cut.FindAll("button.rz-filter-menu-item.rz-state-highlight"));
            Assert.Empty(cut.FindAll("div.rz-filter-menu-editor"));

            // And the stored filter is left alone until they do.
            Assert.Equal(FastGridFilterOperator.LessThanOrEquals, column.CurrentFilter!.First.Operator);
        }

        [Fact]
        public void ANumberGetsANumericEditorAndADateGetsAPicker()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, int>(x => x.Id),
                Columns.Property<Person, DateTime>(x => x.Hired)));

            OpenMenu(cut, 0);
            PickItem(cut, "Equals");

            // Over decimal? rather than over int: a numeric control closed over a non-nullable TProp
            // cannot show empty, and would open offering Apply on a zero nobody chose.
            var numeric = cut.FindComponents<RadzenNumeric<decimal?>>().Single();

            Assert.Null(numeric.Instance.Value);

            cut.InvokeAsync(() => numeric.Instance.Change.InvokeAsync(3m));
            cut.Find("div.rz-filter-menu-buttons button.rz-primary").Click();

            var id = cut.FindComponent<PropertyColumn<Person, int>>().Instance;

            // Back in the column's own type, not left a decimal.
            Assert.Equal(3, id.CurrentFilter!.First.Value);

            OpenMenu(cut, 1);
            PickItem(cut, "Between");

            Assert.Equal(2, cut.FindComponents<RadzenDatePicker<DateTime>>().Count);
        }

        [Fact]
        public void ABoolEditorOpensChoosingNeither()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, bool>(x => x.Remote)));

            OpenMenu(cut, 0);
            PickItem(cut, "Equals");

            // Over object, so "neither" is a state it can hold - closed over bool it would open already
            // saying false.
            Assert.Null(cut.FindComponents<RadzenDropDown<object>>().Single().Instance.Value);
        }

        [Fact]
        public void TheDraftDoesNotOutliveThePanel()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, DateTime>(x => x.Hired)));

            OpenMenu(cut, 0);
            PickItem(cut, "Between");

            Assert.Equal(2, cut.FindAll("div.rz-filter-menu-editor").Count > 0 ? 2 : 0);

            cut.Find("div.rz-filter-menu-buttons button.rz-light").Click();

            OpenMenu(cut, 0);

            Assert.Empty(cut.FindAll("div.rz-filter-menu-editor"));
        }


        [Fact]
        public void ALookupColumnOffersASetRatherThanItsKeysNumericOperators()
        {
            // The browser found this: a lookup's EffectiveFilterType is its key type, so asking the type
            // alone offered "Less than" and "Between" over ids the reader never sees, on the one column
            // whose whole point is that it filters by In and shows names.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Lookup<Person, int?>(x => x.RegionId,
                FastGridLookup.Map(Lookups.Regions()))));

            OpenMenu(cut, 0);

            Assert.Equal(new[] { "In", "Not in", "Is null", "Is not null" }, MenuLabels(cut));
        }


        [Fact]
        public void ClickingTheIconDoesNotAlsoSortTheColumn()
        {
            // The icon sits inside the header's click target, which is what sorts. The same rule the
            // resize and drag handles already follow.
            using var ctx = new TestContext();

            var cut = Render(ctx, extra: p => p.Add(g => g.AllowSorting, true));

            Assert.Empty(cut.Instance.Sorts);

            OpenMenu(cut, 0);

            Assert.Empty(cut.Instance.Sorts);
        }

        [Fact]
        public void TheIconSaysItIsOpenWhileItIs()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx);

            Assert.Equal("false", cut.FindAll("thead button.rz-grid-filter-icon")[0].GetAttribute("aria-expanded"));

            OpenMenu(cut, 0);

            Assert.Equal("true", cut.FindAll("thead button.rz-grid-filter-icon")[0].GetAttribute("aria-expanded"));
            Assert.Equal("false", cut.FindAll("thead button.rz-grid-filter-icon")[1].GetAttribute("aria-expanded"));
        }

        [Fact]
        public void AStringColumnOffersTheAbsenceOperators()
        {
            // A reference type is nullable in the sense that matters - a string column has rows with no
            // string - and the annotation that would say otherwise is erased by the time a Type is all
            // there is. FilterNullable is what says so.
            using var ctx = new TestContext();

            var cut = Render(ctx);

            OpenMenu(cut, 0);

            Assert.Contains("Is null", MenuLabels(cut));
            Assert.Contains("Is not null", MenuLabels(cut));
        }

        [Fact]
        public void ARangeKeepsItsUpperBoundAcrossADetourThroughAnotherOperator()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, int>(x => x.Id)));

            OpenMenu(cut, 0);
            PickItem(cut, "Between");

            var editors = cut.FindComponents<RadzenNumeric<decimal?>>();

            cut.InvokeAsync(() => editors[0].Instance.Change.InvokeAsync(2m));
            cut.InvokeAsync(() => editors[1].Instance.Change.InvokeAsync(3m));

            // Away and back. The upper bound is what only Between can hold, so it is the one that says
            // whether the draft keeps values an intervening operator has no use for.
            PickItem(cut, "Equals");
            PickItem(cut, "Between");
            cut.Find("div.rz-filter-menu-buttons button.rz-primary").Click();

            var column = cut.FindComponent<PropertyColumn<Person, int>>().Instance;

            Assert.Equal(2, column.CurrentFilter!.First.Value);
            Assert.Equal(3, column.CurrentFilter.First.SecondValue);
        }

        [Fact]
        public void ReopeningAFilteredColumnShowsTheValueInTheEditor()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, int>(x => x.Id)));
            var column = cut.FindComponent<PropertyColumn<Person, int>>().Instance;

            cut.InvokeAsync(() => cut.Instance.Filter(column, 3, Radzen.FilterOperator.Equals));

            OpenMenu(cut, 0);

            Assert.Equal(3m, cut.FindComponents<RadzenNumeric<decimal?>>().Single().Instance.Value);
        }

        [Fact]
        public void TickingALookupEntryCommitsItsIdRatherThanTheEntry()
        {
            // The list offers names carrying ids and the column filters by ids. Passing what was ticked
            // straight through would put the entry objects into the filter.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Lookup<Person, int?>(x => x.RegionId,
                FastGridLookup.Map(Lookups.Regions()))));

            OpenMenu(cut, 0);
            PickItem(cut, "In");

            var list = cut.FindComponents<RadzenListBox<System.Collections.IEnumerable>>()[0];
            var offered = list.Instance.Data.Cast<object>().ToArray();

            cut.InvokeAsync(() => list.Instance.Change.InvokeAsync(
                new List<object> { offered.Last() }));
            cut.Find("div.rz-filter-menu-buttons button.rz-primary").Click();

            var column = cut.FindComponent<LookupColumn<Person, int?>>().Instance;
            var values = ((System.Collections.IEnumerable)column.CurrentFilter!.First.Value!)
                .Cast<object>().ToArray();

            Assert.All(values, value => Assert.IsType<int>(value));
        }

        [Fact]
        public void AScalarFilterIsNeverOfferedToASetEditor()
        {
            // The rule at the layer it lives at. The transition screening clears the case that found it,
            // which is exactly why this needs asking of the column directly: with only the screening
            // tested, the guard could go and nothing would notice until a route that does not transition
            // reached it.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, decimal>(x => x.Salary)));
            var column = cut.FindComponent<PropertyColumn<Person, decimal>>().Instance;

            cut.InvokeAsync(() => cut.Instance.Filter(column, 100m, Radzen.FilterOperator.Equals));

            Assert.True(column.HasFilter);
            Assert.Null(column.FilterSelection);
        }


        // ---- the default ----

        [Fact]
        public void TheMenuIsTheDefaultFilterUI()
        {
            // The user's call, and a deliberate break with §31's "everything defaults to today's
            // behaviour". A grid that switches filtering on and says nothing else gets the icons.
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First)));
                p.Add(g => g.AllowFiltering, true);
            });

            Assert.Equal(FilterUI.Menu, cut.Instance.FilterUI);
            Assert.NotEmpty(cut.FindAll("thead button.rz-grid-filter-icon"));

            // And no filter row under it - not a hidden one and not an empty one.
            Assert.Single(cut.FindAll("thead tr"));
        }

        [Fact]
        public void TheRowIsStillThereForAGridThatAsksForIt()
        {
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First)));
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.FilterUI, FilterUI.Row);
            });

            Assert.Equal(2, cut.FindAll("thead tr").Count);
            Assert.Empty(cut.FindAll("thead button.rz-grid-filter-icon"));
        }

        [Fact]
        public void TheIconIsUpstreamsOwnControlRatherThanAButton()
        {
            // RadzenDataGridHeaderCell draws a bare button carrying `notranslate rzi
            // rz-grid-filter-icon` with the glyph as its text, and the themes size it through
            // --rz-grid-header-filter-icon-font-size. §35 wrote a full rz-button with a nested i
            // instead, which rendered a header-row-tall box.
            using var ctx = new TestContext();

            var icon = Render(ctx).FindAll("thead button.rz-grid-filter-icon")[0];

            Assert.Equal("notranslate rzi rz-grid-filter-icon", icon.ClassName);
            Assert.DoesNotContain("rz-button", icon.ClassName, StringComparison.Ordinal);
            Assert.Empty(icon.QuerySelectorAll("i"));
            Assert.Equal("filter_alt", icon.TextContent.Trim());
        }

    }
}
