using System;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// A column is drawn in four sections - header, filter row, body and footer - and each of them asks
    /// the column for a class and a style. These are about the table those four answers make: that every
    /// section pins, that the filter row is stacked with the header rather than with the body, and that
    /// the body's pair - the one asked once per cell - is the same string on every row.
    /// </summary>
    /// <remarks>
    /// §10 records the filter row being missed once, after the title row was fixed, because it is a
    /// second row of the same <c>thead</c> rather than a section of its own and nothing named it. These
    /// name it.
    /// </remarks>
    public class ColumnSectionTests
    {
        static TestContext Context()
        {
            var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            return ctx;
        }

        static RenderFragment FourSections() => Columns.Of(
            Columns.Property<Person, string>(x => x.First, title: "First", width: "90px", frozen: true,
                footerTemplate: _ => b => b.AddContent(0, "total")),
            Columns.Property<Person, string>(x => x.Last, title: "Last",
                footerTemplate: _ => b => b.AddContent(0, "-")));

        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx) =>
            ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.AllowSorting, true);
                p.Add(g => g.ChildContent, FourSections());
            });

        static string ClassOf(IRenderedComponent<RadzenFastGrid<Person>> cut, string selector) =>
            cut.Find(selector).GetAttribute("class") ?? "";

        static string StyleOf(IRenderedComponent<RadzenFastGrid<Person>> cut, string selector) =>
            cut.Find(selector).GetAttribute("style") ?? "";

        // The four selectors, once: the filter row is thead's second tr, which is the whole reason this
        // file exists.
        const string HeaderCell = "thead tr:first-child th:first-child";
        const string FilterCell = "thead tr:nth-child(2) th:first-child";
        const string BodyCell = "tbody tr:first-child td:first-child";
        const string FooterCell = "tfoot tr:first-child td:first-child";

        [Fact]
        public void AFrozenColumnIsPinnedInAllFourSections()
        {
            using var ctx = Context();

            var cut = Render(ctx);

            foreach (var selector in new[] { HeaderCell, FilterCell, BodyCell, FooterCell })
            {
                Assert.Contains("rz-frozen-cell", ClassOf(cut, selector), StringComparison.Ordinal);
                Assert.Contains("inset-inline-start:0", StyleOf(cut, selector), StringComparison.Ordinal);
            }
        }

        [Fact]
        public void EachSectionKeepsTheClassItAlreadyHad()
        {
            // Freezing folds into what the section was already classed with; it does not replace it.
            // The header's is the grid's - it says whether the column sorts and resizes - and the filter
            // row's is a constant, so the two are checked apart.
            using var ctx = Context();

            var cut = Render(ctx);

            Assert.Contains("rz-sortable-column", ClassOf(cut, HeaderCell), StringComparison.Ordinal);
            Assert.Contains("rz-unselectable-text", ClassOf(cut, FilterCell), StringComparison.Ordinal);
        }

        [Fact]
        public void TheFilterRowIsStackedWithTheHeaderAndNotWithTheBody()
        {
            // The one this file is for. The filter row is a second row of the same thead, so its cells
            // sit in the header's stacking and need the header's z-index; a frozen cell there without
            // one ties with the ordinary cell beside it and loses on document order - every position
            // still correct, and the column to its right painting over it.
            using var ctx = Context();

            var cut = Render(ctx);

            Assert.Equal(StyleOf(cut, HeaderCell), StyleOf(cut, FilterCell));
            Assert.Contains("z-index:2", StyleOf(cut, FilterCell), StringComparison.Ordinal);
            Assert.DoesNotContain("z-index", StyleOf(cut, BodyCell), StringComparison.Ordinal);
        }

        [Fact]
        public void TheFooterIsStackedOneAboveTheHeader()
        {
            // The theme makes tfoot td sticky at z-index 2 and thead th at 1, so each section has to
            // clear its own siblings and not the other section's.
            using var ctx = Context();

            var cut = Render(ctx);

            Assert.Contains("z-index:3", StyleOf(cut, FooterCell), StringComparison.Ordinal);
        }

        [Fact]
        public void AnUnfrozenColumnCarriesNoSectionStyleAtAll()
        {
            // The commonest column there is, and the case the four folds exist to keep free: no inset,
            // no z-index, and no style attribute at all in any of the four sections.
            using var ctx = Context();

            var cut = Render(ctx);

            foreach (var selector in new[]
            {
                "thead tr:first-child th:nth-child(2)",
                "thead tr:nth-child(2) th:nth-child(2)",
                "tbody tr:first-child td:nth-child(2)",
                "tfoot tr:first-child td:nth-child(2)",
            })
            {
                Assert.Null(cut.Find(selector).GetAttribute("style"));
            }
        }

        [Fact]
        public void TheBodyPairIsMemoizedAndTheColdSectionsAreNot()
        {
            // Assert.Same rather than Assert.Equal: this is the only assertion that tells a memo that
            // engages from one that recomposes an equal string every time it is asked. The body pair is
            // the one asked once per cell, so a thousand rows share one string of each.
            //
            // Deliberately only the body. The other three sections are asked once per column per render
            // and compose on read; memoizing them cost 8 reference fields per column and gridbench read
            // it as +0.31 KB on a grid with nothing frozen at all.
            var column = new PropertyColumn<Person, string>
            {
                CssClass = "mine",
                TextAlign = TextAlign.Right,
            };

            column.SetFrozen("rz-frozen-cell rz-frozen-cell-left", "inset-inline-start:0");

            Assert.Same(column.BodyCellClass, column.BodyCellClass);
            Assert.Same(column.BodyCellStyle, column.BodyCellStyle);

            // The header and footer styles are the same composition and are memoized with it, since
            // they are built alongside the body's rather than on their own.
            Assert.Same(column.HeaderCellStyle, column.HeaderCellStyle);
            Assert.Same(column.FooterCellStyle, column.FooterCellStyle);
        }

        [Fact]
        public void EverySectionFoldsTheFrozenClassIntoWhatItAlreadyHad()
        {
            var column = new PropertyColumn<Person, string> { CssClass = "mine", FooterCssClass = "footer-mine" };

            column.SetFrozen("rz-frozen-cell rz-frozen-cell-left", "inset-inline-start:0");

            Assert.Equal("mine rz-frozen-cell rz-frozen-cell-left", column.BodyCellClass);
            Assert.Equal("rz-sortable-column rz-frozen-cell rz-frozen-cell-left",
                column.HeaderCellClass("rz-sortable-column"));
            Assert.Equal("rz-unselectable-text rz-frozen-cell rz-frozen-cell-left", column.FilterCellClass);
            Assert.Equal("footer-mine rz-frozen-cell rz-frozen-cell-left", column.FooterCellClass);
        }

        [Fact]
        public void ASectionWithNothingOfItsOwnCarriesTheFrozenClassAlone()
        {
            var column = new PropertyColumn<Person, string>();

            column.SetFrozen("rz-frozen-cell rz-frozen-cell-left", "inset-inline-start:0");

            Assert.Equal("rz-frozen-cell rz-frozen-cell-left", column.BodyCellClass);
            Assert.Equal("rz-frozen-cell rz-frozen-cell-left", column.FooterCellClass);
        }

        [Fact]
        public void AHeaderClassThatChangesIsFoldedAgain()
        {
            // The header's base is the grid's answer and it moves - switching resize on re-composes it -
            // which is half of why this fold is not memoized: a memo keyed on the frozen class alone
            // would hand back a stale one, and keying it on both costs three fields per column.
            var column = new PropertyColumn<Person, string>();

            column.SetFrozen("rz-frozen-cell rz-frozen-cell-left", "inset-inline-start:0");

            Assert.Equal("rz-sortable-column rz-frozen-cell rz-frozen-cell-left",
                column.HeaderCellClass("rz-sortable-column"));
            Assert.Equal("rz-sortable-column rz-resizable-column rz-frozen-cell rz-frozen-cell-left",
                column.HeaderCellClass("rz-sortable-column rz-resizable-column"));
        }

        // A column that shows text has to answer with the same text twice: RenderCell writes it into the
        // cell and CellTextOf answers the truncation tooltip, and a column whose two halves disagree
        // shows one thing on screen and another on hover. Four columns used to spell both out, with
        // nothing checking that the two spellings matched.
        [Theory]
        [InlineData("property")]
        [InlineData("collection")]
        [InlineData("lookup")]
        [InlineData("lookup collection")]
        public void ACellAndItsTooltipAreTheSameText(string kind)
        {
            using var ctx = Context();

            var column = kind switch
            {
                "property" => Columns.Property<Person, string>(x => x.First, title: "First"),
                "collection" => Columns.Collection<Person, Company>(x => x.Accounts, a => a.Name),
                "lookup" => Columns.Lookup<Person, int>(x => x.CategoryId,
                    FastGridLookup.Map(Lookups.Categories())),
                _ => Columns.LookupCollection<Person, int>(x => x.BrandIds,
                    FastGridLookup.Map(Lookups.Brands())),
            };

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ShowCellDataAsTooltip, true);
                p.Add(g => g.ChildContent, Columns.Of(column));
            });

            var cells = cut.FindAll("tbody tr td").ToArray();

            Assert.NotEmpty(cells);

            // Something has to be shown, or this passes over four empty cells and four absent titles.
            Assert.Contains(cells, cell => !string.IsNullOrEmpty(cell.TextContent));

            foreach (var cell in cells)
            {
                var text = cell.TextContent;
                var title = cell.QuerySelector("span[title]")?.GetAttribute("title");

                // A tooltip that is written has to say what the cell says; a cell with text has to have
                // one. Both directions, because a divergence could be either way round.
                if (title is null)
                {
                    Assert.Equal("", text);
                }
                else
                {
                    Assert.Equal(text, title);
                }
            }
        }

        [Fact]
        public void ReleasingTheFreezeTakesTheClassOutOfEverySection()
        {
            var column = new PropertyColumn<Person, string> { CssClass = "mine", FooterCssClass = "footer-mine" };

            column.SetFrozen("rz-frozen-cell rz-frozen-cell-left", "inset-inline-start:0");
            column.SetFrozen(null, null);

            Assert.Equal("mine", column.BodyCellClass);
            Assert.Equal("rz-sortable-column", column.HeaderCellClass("rz-sortable-column"));
            Assert.Equal("rz-unselectable-text", column.FilterCellClass);
            Assert.Equal("footer-mine", column.FooterCellClass);
            Assert.Null(column.BodyCellStyle);
            Assert.Null(column.HeaderCellStyle);
            Assert.Null(column.FooterCellStyle);
        }
    }
}
