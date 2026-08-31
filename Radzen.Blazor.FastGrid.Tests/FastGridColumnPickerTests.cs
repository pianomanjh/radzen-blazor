using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// The column picker: one drop-down above the table that decides which columns are drawn. Everything
    /// here is per column or per render - the picker never reaches a row.
    /// </summary>
    public class FastGridColumnPickerTests
    {
        static TestContext Context()
        {
            var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");
            return ctx;
        }

        static RenderFragment ThreeColumns(bool secondPickable = true, bool secondVisible = true) => Columns.Of(
            Columns.Property<Person, string>(x => x.First, title: "First"),
            Columns.Property<Person, string>(x => x.Last, title: "Last",
                pickable: secondPickable, visible: secondVisible),
            Columns.Property<Person, int>(x => x.Id, title: "Id"));

        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx,
            RenderFragment columns = null,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>> extra = null) =>
            ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, columns ?? ThreeColumns());
                p.Add(g => g.AllowColumnPicking, true);
                extra?.Invoke(p);
            });

        static string[] Headers(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindAll("thead th").Select(th => th.TextContent.Trim()).ToArray();

        /// <summary>The picker's drop-down, reached through the grid rather than by markup archaeology.</summary>
        static RadzenDropDown<IEnumerable<object>> Picker(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindComponent<RadzenDropDown<IEnumerable<object>>>().Instance;

        static void Pick(IRenderedComponent<RadzenFastGrid<Person>> cut, params string[] titles)
        {
            // Chosen out of what the picker offers, which is what a user picks from.
            var chosen = Picker(cut).Data
                .Cast<ColumnBase<Person>>()
                .Where(c => titles.Contains(c.Title))
                .Cast<object>()
                .ToList();

            cut.InvokeAsync(() => Picker(cut).Change.InvokeAsync(chosen)).Wait();
        }

        // --- what it draws ---------------------------------------------------------------------

        [Fact]
        public void NoPickerUnlessItIsAllowed()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, ThreeColumns());
            });

            Assert.Empty(cut.FindAll(".rz-column-picker"));
            Assert.Empty(cut.FindComponents<RadzenDropDown<IEnumerable<object>>>());
        }

        // RadzenDataGrid's own wrapper elements, so the themes style this unchanged.
        [Fact]
        public void ThePickerSitsInTheSameWrappersRadzenDataGridUses()
        {
            using var ctx = Context();

            var cut = Render(ctx);
            var picker = cut.Find(".rz-column-picker");

            Assert.Equal("rz-group-header", picker.ParentElement.ClassName);
            Assert.NotNull(picker.QuerySelector(".rz-dropdown"));
        }

        [Fact]
        public void EveryPickableColumnIsOfferedAndTheDrawnOnesAreTicked()
        {
            using var ctx = Context();

            var picker = Picker(Render(ctx));

            Assert.Equal(new[] { "First", "Last", "Id" },
                picker.Data.Cast<ColumnBase<Person>>().Select(c => c.Title).ToArray());
            Assert.Equal(3, ((IEnumerable<object>)picker.Value).Count());
        }

        [Fact]
        public void AColumnThatIsNotPickableIsNotOffered()
        {
            using var ctx = Context();

            var picker = Picker(Render(ctx, ThreeColumns(secondPickable: false)));

            Assert.Equal(new[] { "First", "Id" },
                picker.Data.Cast<ColumnBase<Person>>().Select(c => c.Title).ToArray());
        }

        // It is not offered, and it is also not hidden by being absent from the list.
        [Fact]
        public void AColumnThatIsNotPickableKeepsBeingDrawnWhenOthersArePicked()
        {
            using var ctx = Context();

            var cut = Render(ctx, ThreeColumns(secondPickable: false));

            Pick(cut, "First");

            Assert.Equal(new[] { "First", "Last" }, Headers(cut));
        }

        [Fact]
        public void APickableColumnHiddenInMarkupIsOfferedButNotTicked()
        {
            using var ctx = Context();

            var picker = Picker(Render(ctx, ThreeColumns(secondVisible: false)));

            Assert.Contains(picker.Data.Cast<ColumnBase<Person>>(), c => c.Title == "Last");
            Assert.DoesNotContain(((IEnumerable<object>)picker.Value).Cast<ColumnBase<Person>>(),
                c => c.Title == "Last");
        }

        // --- what picking does -----------------------------------------------------------------

        [Fact]
        public void UnpickingAColumnStopsItBeingDrawn()
        {
            using var ctx = Context();

            var cut = Render(ctx);

            Assert.Equal(new[] { "First", "Last", "Id" }, Headers(cut));

            Pick(cut, "First", "Id");

            Assert.Equal(new[] { "First", "Id" }, Headers(cut));
        }

        [Fact]
        public void PickingAHiddenColumnDrawsIt()
        {
            using var ctx = Context();

            var cut = Render(ctx, ThreeColumns(secondVisible: false));

            Assert.Equal(new[] { "First", "Id" }, Headers(cut));

            Pick(cut, "First", "Last", "Id");

            Assert.Equal(new[] { "First", "Last", "Id" }, Headers(cut));
        }

        [Fact]
        public void TheCellsGoWithTheHeader()
        {
            using var ctx = Context();

            var cut = Render(ctx);

            Pick(cut, "First");

            Assert.All(cut.FindAll("tbody tr"), row => Assert.Single(row.QuerySelectorAll("td")));
        }

        [Fact]
        public void PickedColumnsChangedReportsWhatIsDrawn()
        {
            using var ctx = Context();

            IEnumerable<ColumnBase<Person>> reported = null;
            var cut = Render(ctx, extra: p =>
                p.Add(g => g.PickedColumnsChanged, cols => reported = cols));

            Pick(cut, "Id");

            Assert.NotNull(reported);
            Assert.Equal(new[] { "Id" }, reported.Select(c => c.Title).ToArray());
        }

        // --- the picker's labels ---------------------------------------------------------------

        [Fact]
        public void ColumnPickerTitleNamesTheColumnWhenItIsSet()
        {
            using var ctx = Context();

            var picker = Picker(Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First",
                    columnPickerTitle: "Given name"))));

            Assert.Equal(new[] { "Given name" },
                picker.Data.Cast<ColumnBase<Person>>().Select(c => c.PickerTitle).ToArray());
        }

        [Fact]
        public void ColumnPickerTitleFallsBackToTheColumnTitle()
        {
            using var ctx = Context();

            var picker = Picker(Render(ctx));

            Assert.Equal(new[] { "First", "Last", "Id" },
                picker.Data.Cast<ColumnBase<Person>>().Select(c => c.PickerTitle).ToArray());
        }

        // --- against the markup --------------------------------------------------------------

        // Changing what the markup says beats what was picked, the same rule a declared filter value
        // follows: markup that now says Visible="false" is not asking to be overruled by an old tick.
        [Fact]
        public void ChangingTheDeclaredVisibilityOverridesWhatWasPicked()
        {
            using var ctx = Context();

            var cut = Render(ctx);

            Pick(cut, "First", "Last", "Id");

            Assert.Equal(new[] { "First", "Last", "Id" }, Headers(cut));

            cut.SetParametersAndRender(p => p.Add(g => g.ChildContent, ThreeColumns(secondVisible: false)));

            Assert.Equal(new[] { "First", "Id" }, Headers(cut));
        }

        // --- settings --------------------------------------------------------------------------

        [Fact]
        public void WhatWasPickedIsCarriedInTheSettings()
        {
            using var ctx = Context();

            FastGridSettings captured = null;
            var cut = Render(ctx, extra: p => p.Add(g => g.SettingsChanged, s => captured = s));

            Pick(cut, "First", "Id");

            Assert.NotNull(captured);
            Assert.Equal(false, captured.Columns.Single(c => c.Property == "Last").Visible);
            Assert.Equal(true, captured.Columns.Single(c => c.Property == "First").Visible);
        }

        [Fact]
        public void StoredVisibilityIsRestored()
        {
            using var ctx = Context();

            var settings = new FastGridSettings
            {
                Columns = new List<FastGridColumnSettings>
                {
                    new() { Property = "Last", Visible = false },
                },
            };

            var cut = Render(ctx, extra: p => p.Add(g => g.Settings, settings));

            Assert.Equal(new[] { "First", "Id" }, Headers(cut));
        }

        // A grid with no picker records no visibility at all, so restoring its settings cannot overrule
        // a later edit to the markup. That is the whole reason the stored value is nullable.
        [Fact]
        public void AGridWithNoPickerStoresNoVisibility()
        {
            using var ctx = Context();

            FastGridSettings captured = null;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, ThreeColumns());
                p.Add(g => g.AllowSorting, true);
                p.Add(g => g.SettingsChanged, s => captured = s);
            });

            cut.Find("thead th div").Click();

            Assert.NotNull(captured);
            Assert.All(captured.Columns, c => Assert.Null(c.Visible));
        }
    }
}
