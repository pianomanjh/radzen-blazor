using System;
using System.Collections.Generic;
using System.Globalization;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Radzen.Blazor;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// The strings the grid puts on screen itself, and the accessible names it gives its two icon-only
    /// buttons - which, until this landed, announced as the icon ligature "close" and as nothing at all.
    /// </summary>
    /// <remarks>
    /// Every key here is one RadzenDataGrid already owns, so what these pin is that the reuse actually
    /// works: the five cultures Radzen ships translate this grid too, with nothing added to any resx.
    /// </remarks>
    public class FastGridLocalizationTests
    {
        static TestContext Context()
        {
            var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");
            return ctx;
        }

        static RenderFragment TwoColumns => Columns.Of(
            Columns.Property<Person, string>(p => p.First, title: "First"),
            Columns.Property<Person, int>(p => p.Id, title: "Id"));

        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>> extra = null) =>
            ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, TwoColumns);
                p.Add(g => g.AllowFiltering, true);
                extra?.Invoke(p);
            });

        static RenderFragment<Person> Detail =>
            person => builder => builder.AddContent(0, "detail for " + person.First);

        static void Type(IRenderedComponent<RadzenFastGrid<Person>> cut, string value) =>
            cut.FindAll("thead input.rz-textbox")[0].Change(value);

        // --- the filter box ---------------------------------------------------------------------

        // A box labelled only with the column's title says nothing about being a filter; a box labelled
        // only "filter value" says nothing about which column. RadzenDataGrid joins the two, and the
        // shipped string carries the spaces on both sides that make the join read as a sentence.
        [Fact]
        public void TheFilterBoxIsNamedForItsColumnAndItsValue()
        {
            using var ctx = Context();

            var cut = Render(ctx);

            Assert.Equal("First filter value ",
                cut.FindAll("thead input.rz-textbox")[0].GetAttribute("aria-label"));
        }

        [Fact]
        public void TheFilterBoxNameCarriesTheValueOnceOneIsSet()
        {
            using var ctx = Context();

            var cut = Render(ctx);

            Type(cut, "Ada");

            Assert.Equal("First filter value Ada",
                cut.FindAll("thead input.rz-textbox")[0].GetAttribute("aria-label"));
        }

        // --- the clear button -------------------------------------------------------------------

        // Its content is the ligature "close", which is what a screen reader read out before this.
        [Fact]
        public void TheClearButtonIsNamedClear()
        {
            using var ctx = Context();

            var cut = Render(ctx);

            Type(cut, "Ada");

            Assert.Equal("Clear", cut.Find("button.rz-cell-filter-clear").GetAttribute("aria-label"));
        }

        [Fact]
        public void TheClearButtonsNameCanBeOverriddenOnTheGrid()
        {
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.ClearFilterText, "Wipe it"));

            Type(cut, "Ada");

            Assert.Equal("Wipe it", cut.Find("button.rz-cell-filter-clear").GetAttribute("aria-label"));
        }

        // --- the row detail toggle --------------------------------------------------------------

        [Fact]
        public void TheRowTogglerIsNamed()
        {
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.Template, Detail));

            Assert.Equal("Expand child item",
                cut.FindAll("td.rz-col-icon button")[0].GetAttribute("aria-label"));
        }

        // One name for both states. The button does not rename itself on expand, because aria-expanded
        // is what carries the state and a name that moves under the user is the thing to avoid.
        [Fact]
        public void TheRowTogglerKeepsItsNameWhenExpandedAndSaysSoThroughAriaExpanded()
        {
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.Template, Detail));

            cut.FindAll("td.rz-col-icon button")[0].Click();

            var toggle = cut.FindAll("td.rz-col-icon button")[0];

            Assert.Equal("Expand child item", toggle.GetAttribute("aria-label"));
            Assert.Equal("true", toggle.GetAttribute("aria-expanded"));
        }

        // --- the column picker ------------------------------------------------------------------

        [Fact]
        public void ThePickerIsNamedAndCarriesItsLabels()
        {
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.AllowColumnPicking, true));
            var picker = cut.FindComponent<RadzenDropDown<IEnumerable<object>>>()
                .Instance;

            Assert.Equal("Columns", picker.Placeholder);
            Assert.Equal("All", picker.SelectAllText);
            Assert.Equal("columns showing", picker.SelectedItemsText);
            Assert.Equal("select visible columns", picker.InputAttributes["aria-label"]);
        }

        // --- culture --------------------------------------------------------------------------

        // Nothing was added to any resx for this grid. These are the translations RadzenDataGrid ships.
        [Theory]
        [InlineData("de", "Löschen", "Untergeordnetes Element erweitern", "Spalten")]
        [InlineData("es", "Borrar", "Expandir elemento secundario", "Columnas")]
        [InlineData("fr", "Effacer", "Développer l'élément enfant", "Colonnes")]
        [InlineData("it", "Cancella", "Espandi elemento figlio", "Colonne")]
        [InlineData("ja", "クリア", "子項目を展開", "列")]
        public void TheShippedTranslationsReachThisGridToo(string culture, string clear, string expand,
            string columns)
        {
            using var ctx = Context();

            var cut = Render(ctx, p =>
            {
                p.Add(g => g.UICulture, new CultureInfo(culture));
                p.Add(g => g.Template, Detail);
                p.Add(g => g.AllowColumnPicking, true);
            });

            Assert.Equal(expand, cut.FindAll("td.rz-col-icon button")[0].GetAttribute("aria-label"));

            // After the toggler, because the clear button only exists once a filter does - and this
            // filter matches nothing, which is exactly why it leaves no row to carry a toggler.
            Type(cut, "no such person");

            Assert.Equal(clear, cut.Find("button.rz-cell-filter-clear").GetAttribute("aria-label"));
            Assert.Equal(columns,
                cut.FindComponent<RadzenDropDown<IEnumerable<object>>>()
                    .Instance.Placeholder);
        }

        // The picker's aria-label is the one string that does not reach the DOM through the grid's own
        // markup, so it is the one worth pinning separately.
        [Fact]
        public void ThePickersNameFollowsACultureChange()
        {
            using var ctx = Context();

            var cut = Render(ctx, p => p.Add(g => g.AllowColumnPicking, true));
            var picker = cut.FindComponent<RadzenDropDown<IEnumerable<object>>>();

            Assert.Equal("select visible columns", picker.Instance.InputAttributes["aria-label"]);

            cut.SetParametersAndRender(p => p.Add(g => g.UICulture, new CultureInfo("fr")));

            Assert.Equal("sélectionner les colonnes visibles",
                picker.Instance.InputAttributes["aria-label"]);
        }

        // The documented extension point: one ILocalizer in the container answers for every Radzen
        // component, and this grid has to be one of them or an application translating through it ends
        // up with a grid that ignores it.
        [Fact]
        public void ACustomLocalizerInTheContainerAnswersForThisGrid()
        {
            using var ctx = Context();

            ctx.Services.AddRadzenComponents();
            ctx.Services.AddSingleton<ILocalizer>(new ShoutingLocalizer());

            var cut = Render(ctx, p => p.Add(g => g.Template, Detail));

            Assert.Equal("FirstCLEAR ME",
                cut.FindAll("thead input.rz-textbox")[0].GetAttribute("aria-label"));
        }

        // Answers for one key and defers on every other, which is what the interface is for - and what
        // pins that a null answer still falls through to the shipped string.
        sealed class ShoutingLocalizer : ILocalizer
        {
            public string Get(string key, CultureInfo culture) =>
                key == nameof(Blazor.RadzenStrings.DataGrid_FilterValueAriaLabel) ? "CLEAR ME" : null;
        }

        // A grid inside a localized page picks the culture up without being told, which is how every
        // other Radzen component reads it.
        [Fact]
        public void ACascadedDefaultUICultureIsUsedWhenTheGridNamesNone()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.AddCascadingValue(nameof(RadzenFastGrid<Person>.DefaultUICulture), new CultureInfo("fr"));
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, TwoColumns);
                p.Add(g => g.AllowFiltering, true);
            });

            Type(cut, "Ada");

            Assert.Equal("Effacer", cut.Find("button.rz-cell-filter-clear").GetAttribute("aria-label"));
        }

        // UICulture is the grid's own answer, so it wins over the page's.
        [Fact]
        public void TheGridsOwnUICultureBeatsTheCascadedOne()
        {
            using var ctx = Context();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.AddCascadingValue(nameof(RadzenFastGrid<Person>.DefaultUICulture), new CultureInfo("fr"));
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, TwoColumns);
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.UICulture, new CultureInfo("de"));
            });

            Type(cut, "Ada");

            Assert.Equal("Löschen", cut.Find("button.rz-cell-filter-clear").GetAttribute("aria-label"));
        }
    }
}
