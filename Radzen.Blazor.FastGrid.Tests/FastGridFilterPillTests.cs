using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Radzen.Blazor;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// §37: what a pill says, asked of the rule rather than of the bar that draws it.
    /// </summary>
    /// <remarks>
    /// §33's review finding is why these are not markup assertions. "An <c>In</c> over four lookup ids
    /// reads as three names and a count" is a claim about a column and a filter; reaching it through
    /// rendered HTML would test the render tree and would keep passing if the rule moved.
    /// </remarks>
    public class FastGridFilterPillTests
    {
        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx,
            RenderFragment columns,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>> extra = null,
            bool pills = true, bool filtering = true, FilterUI ui = FilterUI.Row)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, columns);
                p.Add(g => g.AllowFiltering, filtering);
                p.Add(g => g.ShowFilterPills, pills);
                p.Add(g => g.FilterUI, ui);
                extra?.Invoke(p);
            });
        }

        /// <summary>The phrase for a filter a column is holding, through the rule and not the markup.</summary>
        static string Phrase(IRenderedComponent<RadzenFastGrid<Person>> cut, ColumnBase<Person> column,
            FastGridFilter filter) =>
            FilterPill.Phrase(cut.Instance, column, filter);

        static FastGridFilter One(FastGridFilterOperator op, params object[] values) =>
            new(new FastGridFilterCondition(op, values));

        /// <summary>
        /// Counts what was <em>issued</em>: data loads and check-box-list scans, apart.
        /// </summary>
        /// <remarks>
        /// §36's instrument, in the shape §37's two gates need. A counter that reads zero looks
        /// identical whether the query was avoided or never attempted, which is why every use of this
        /// below asserts a number it has watched move.
        /// </remarks>
        sealed class CountingExecutor : IFastGridQueryExecutor
        {
            public int Loads { get; private set; }

            public int Scans { get; private set; }

            public bool IsSupported<T>(IQueryable<T> queryable) => true;

            public Task<int> CountAsync<T>(IQueryable<T> queryable, CancellationToken token = default) =>
                Task.FromResult(queryable.Count());

            public Task<List<T>> ToListAsync<T>(IQueryable<T> queryable, CancellationToken token = default)
            {
                if (queryable.Expression.ToString().Contains("Distinct", StringComparison.Ordinal))
                {
                    Scans++;
                }
                else
                {
                    Loads++;
                }

                return Task.FromResult(queryable.ToList());
            }
        }

        static CountingExecutor Counting(TestContext ctx)
        {
            var executor = new CountingExecutor();

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(executor);

            return executor;
        }

        static IRenderedComponent<RadzenFastGrid<Person>> Queryable(TestContext ctx,
            RenderFragment columns, FilterUI ui = FilterUI.Row)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample().AsQueryable());
                p.Add(g => g.ChildContent, columns);
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.ShowFilterPills, true);
                p.Add(g => g.FilterUI, ui);
            });
        }

        static IElement[] Pills(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindAll(".rz-filter-pills .rz-chip").ToArray();

        static string[] PillText(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindAll(".rz-filter-pills .rz-chip-text").Select(e => e.TextContent).ToArray();

        /// <summary>The column a title names, whatever kind of column it is.</summary>
        /// <remarks>
        /// By title rather than by generic type: several of these grids hold columns of four different
        /// closed types, and a test that names each one would say more about the helper than about the
        /// pill. §36's finding is the reason it goes by what the reader sees - a test naming the wrong
        /// class is one no reading of the test would show.
        /// </remarks>
        static ColumnBase<Person> Column(IRenderedComponent<RadzenFastGrid<Person>> cut, string title) =>
            cut.FindComponents<ColumnBase<Person>>()
                .Select(c => c.Instance)
                .Single(c => c.Title == title);

        static void Apply(IRenderedComponent<RadzenFastGrid<Person>> cut, string title, object value) =>
            cut.InvokeAsync(() => cut.Instance.Filter(Column(cut, title), value)).Wait();

        // ---- the phrase, as a rule about an operator's arity ----

        [Fact]
        public void AnOperatorThatTakesNoValueIsTheTitleAndTheWord()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, decimal?>(x => x.Bonus, title: "Bonus")));

            var column = cut.FindComponent<PropertyColumn<Person, decimal?>>().Instance;

            Assert.Equal("Bonus Is null",
                Phrase(cut, column, One(FastGridFilterOperator.IsNull)));
        }

        [Fact]
        public void AScalarOperatorIsTheWordAndOneValue()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First")));

            var column = cut.FindComponent<PropertyColumn<Person, string>>().Instance;

            Assert.Equal("First Contains an",
                Phrase(cut, column, One(FastGridFilterOperator.Contains, "an")));
        }

        [Fact]
        public void ARangeIsBothBoundsJoinedByItsOwnWord()
        {
            // Not the conjunction that joins two conditions: one separates two bounds of one condition
            // and the languages that spell those differently would have no way to say so.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, decimal>(x => x.Salary, title: "Salary")));

            var column = cut.FindComponent<PropertyColumn<Person, decimal>>().Instance;

            Assert.Equal("Salary Between 10 and 20",
                Phrase(cut, column, One(FastGridFilterOperator.Between, 10m, 20m)));
        }

        [Fact]
        public void ASecondConditionIsJoinedByTheWordTheModelJoinedItWith()
        {
            // §31's worked case - "these values or blank" - is one column's filter and gets one pill,
            // because the x clears a column and nothing removes half a filter.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, decimal?>(x => x.Bonus, title: "Bonus")));

            var column = cut.FindComponent<PropertyColumn<Person, decimal?>>().Instance;

            var filter = new FastGridFilter(
                new FastGridFilterCondition(FastGridFilterOperator.GreaterThan, 5m),
                new FastGridFilterCondition(FastGridFilterOperator.IsNull),
                LogicalFilterOperator.Or);

            Assert.Equal("Bonus Greater than 5 Or Is null", Phrase(cut, column, filter));
        }

        // ---- naming values, which must cost no query ----

        [Fact]
        public void AFormattedColumnReadsAsItsCellsDo()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, decimal>(x => x.Salary, title: "Salary", format: "C0")));

            var column = cut.FindComponent<PropertyColumn<Person, decimal>>().Instance;

            Assert.Equal(
                "Salary Equals " + 1234m.ToString("C0", CultureInfo.CurrentCulture),
                Phrase(cut, column, One(FastGridFilterOperator.Equals, 1234m)));
        }

        [Fact]
        public void AnEnumReadsTheWordItsCheckBoxListDraws()
        {
            // GetDisplayDescription, so a [Display] name reaches the pill - and so an enum reads one
            // way here and in the list rather than one rule with two spellings.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, Grade>(x => x.Grade, title: "Grade")));

            var column = cut.FindComponent<PropertyColumn<Person, Grade>>().Instance;

            Assert.Equal("Grade Equals Senior engineer",
                Phrase(cut, column, One(FastGridFilterOperator.Equals, Grade.Senior)));
        }

        [Fact]
        public void ALookupIdReadsAsItsName()
        {
            // §31: a pill reading "In 10, 20" answers "why is data hidden" worse than showing nothing.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Lookup<Person, int>(x => x.CategoryId,
                FastGridLookup.Items(Lookups.CategoryRows(), c => c.Id, c => c.Name),
                title: "Category")));

            var column = cut.FindComponent<LookupColumn<Person, int>>().Instance;

            Assert.Equal("Category In Toys, Games",
                Phrase(cut, column, One(FastGridFilterOperator.In, new List<int> { 10, 20 })));
        }

        [Fact]
        public void AnIdTheLookupDoesNotHoldPrintsAsItself()
        {
            // The honest answer for a filter naming a row that has left the lookup: the base's
            // ToString rather than a blank or a throw.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Lookup<Person, int>(x => x.CategoryId,
                FastGridLookup.Items(Lookups.CategoryRows(), c => c.Id, c => c.Name),
                title: "Category")));

            var column = cut.FindComponent<LookupColumn<Person, int>>().Instance;

            Assert.Equal("Category In 99",
                Phrase(cut, column, One(FastGridFilterOperator.In, new List<int> { 99 })));
        }

        [Fact]
        public void ANullValueReadsAsTheBlanksOwnWord()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, decimal?>(x => x.Bonus, title: "Bonus")),
                p => p.Add(g => g.BlankFilterText, "(none)"));

            var column = cut.FindComponent<PropertyColumn<Person, decimal?>>().Instance;

            Assert.Equal("Bonus Equals (none)",
                Phrase(cut, column, One(FastGridFilterOperator.Equals, new object[] { null })));
        }

        [Fact]
        public void ANameIsNeverResolvedByRunningAScan()
        {
            // §36's gate, restated where §37 could break it: a pill draws on the render after a filter
            // is applied, so resolving an id by scanning would spend queries-per-open somewhere §36
            // never looks. The column below is a declared check-box list whose menu has never been
            // opened, so nothing has ever asked for its values.
            using var ctx = new TestContext();
            var executor = Counting(ctx);

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, string>(
                x => x.First, title: "First", filterMode: FilterMode.CheckBoxList)));

            var column = cut.FindComponent<PropertyColumn<Person, string>>().Instance;

            Assert.Equal(0, executor.Scans);

            Assert.Equal("First In Alice, Bob",
                Phrase(cut, column, One(FastGridFilterOperator.In,
                    new List<string> { "Alice", "Bob" })));

            Assert.Equal(0, executor.Scans);
        }

        // ---- the count, past which a list stops being a label ----

        [Fact]
        public void ASetOfThreeIsNamedInFull()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Lookup<Person, int>(x => x.CategoryId,
                FastGridLookup.Items(Lookups.CategoryRows(), c => c.Id, c => c.Name),
                title: "Category")));

            var column = cut.FindComponent<LookupColumn<Person, int>>().Instance;

            Assert.Equal("Category In Toys, Games, Puzzles",
                Phrase(cut, column, One(FastGridFilterOperator.In, new List<int> { 10, 20, 30 })));
        }

        [Fact]
        public void ASetPastThreeNamesThreeAndCountsTheRest()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Lookup<Person, int>(x => x.CategoryId,
                FastGridLookup.Items(Lookups.CategoryRows(), c => c.Id, c => c.Name),
                title: "Category")));

            var column = cut.FindComponent<LookupColumn<Person, int>>().Instance;

            Assert.Equal("Category In Toys, Games, Puzzles or 1 more",
                Phrase(cut, column, One(FastGridFilterOperator.In, new List<int> { 10, 20, 30, 40 })));
        }

        [Fact]
        public void AStringUnderASetOperatorIsOneValueRatherThanItsCharacters()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First")));

            var column = cut.FindComponent<PropertyColumn<Person, string>>().Instance;

            Assert.Equal("First In Ada",
                Phrase(cut, column, One(FastGridFilterOperator.In, "Ada")));
        }

        // ---- the preset, which replaces the clause rather than sitting beside it ----

        [Fact]
        public void ARelativeRangeThatIsOneOfTheSixReadsAsItsName()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, DateTime>(x => x.Hired, title: "Hired")));

            var column = cut.FindComponent<PropertyColumn<Person, DateTime>>().Instance;

            Assert.Equal("Hired Last 7 days",
                Phrase(cut, column, FastGridFilterPreset.Last7Days.Filter()));
        }

        [Fact]
        public void ARelativeRangeThatIsNotOneOfTheSixReadsAsItsTokens()
        {
            // §34 named a pill as one of the two places a token "sits alone with no position to be
            // judged by", and today-2d is the canonical text it settled on for exactly that.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, DateTime>(x => x.Hired, title: "Hired")));

            var column = cut.FindComponent<PropertyColumn<Person, DateTime>>().Instance;

            var filter = One(FastGridFilterOperator.Between,
                new FastGridRelativeDate(FastGridRelativeDateAnchor.Today, -2,
                    FastGridRelativeDateUnit.Days, endOfDay: false),
                new FastGridRelativeDate(FastGridRelativeDateAnchor.Today, 0,
                    FastGridRelativeDateUnit.Days, endOfDay: true));

            Assert.Equal("Hired Between today-2d and today@end", Phrase(cut, column, filter));
        }

        [Fact]
        public void APresetIsAShapeOfTheWholeFilterAndNotOfHalfOfOne()
        {
            // Asked of Recognize, which is where the rule lives. FilterPill used to pass null for the
            // second clause to enforce this, and the mutation loop showed that write unobservable -
            // Recognize matches only a filter whose Second is null, so a two-condition filter is
            // declined whichever clause asks. §33's finding: the guard was one layer below the rule.
            var preset = FastGridFilterPreset.Last7Days.Filter();

            Assert.Equal(FastGridFilterPreset.Last7Days, FastGridFilterPresets.Recognize(preset));

            var paired = new FastGridFilter(preset.First,
                new FastGridFilterCondition(FastGridFilterOperator.IsNull), LogicalFilterOperator.Or);

            Assert.Null(FastGridFilterPresets.Recognize(paired));
        }

        [Fact]
        public void ATwoConditionFilterSpellsBothHalvesOut()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, DateTime>(x => x.Hired, title: "Hired")));

            var column = cut.FindComponent<PropertyColumn<Person, DateTime>>().Instance;
            var preset = FastGridFilterPreset.Last7Days.Filter();

            var filter = new FastGridFilter(preset.First,
                new FastGridFilterCondition(FastGridFilterOperator.IsNull), LogicalFilterOperator.Or);

            Assert.StartsWith("Hired Between today-6d and today@end Or Is null",
                Phrase(cut, column, filter), StringComparison.Ordinal);
        }

        [Fact]
        public void AColumnWithNoHeaderTextIsItsClauseAlone()
        {
            // The header is prepended only when there is one, and no other test reached the branch.
            // A PropertyColumn falls back to its property path, so an empty Title is what an unnamed
            // column actually looks like - the fallback is the reason the branch is nearly dead rather
            // than a reason it can go.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "")));

            var column = cut.FindComponent<PropertyColumn<Person, string>>().Instance;

            Assert.Equal(string.Empty, column.HeaderText);

            Assert.Equal("Contains an",
                Phrase(cut, column, One(FastGridFilterOperator.Contains, "an")));
        }

        // ---- the bar, which only a render answers ----

        [Fact]
        public void NothingIsDrawnWhileNothingIsFiltered()
        {
            // A permanently empty band costs a row of chrome forever to avoid one layout shift.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First")));

            Assert.Empty(cut.FindAll(".rz-filter-pills"));
        }

        [Fact]
        public void OnePillIsDrawnPerFilteredColumn()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First"),
                Columns.Property<Person, string>(x => x.Last, title: "Last")));

            Apply(cut, "First", "A");

            Assert.Single(Pills(cut));

            Apply(cut, "Last", "B");

            Assert.Equal(2, Pills(cut).Length);
        }

        [Fact]
        public void NoPillIsDrawnWhileTheFeatureIsOff()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First")), pills: false);

            Apply(cut, "First", "A");

            Assert.Empty(cut.FindAll(".rz-filter-pills"));
        }

        [Fact]
        public void ThePillSaysWhatTheRuleSays()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First")));

            Apply(cut, "First", "A");

            Assert.Equal("First Contains A", Assert.Single(PillText(cut)));
        }

        [Fact]
        public void TheRemoveButtonClearsThatColumnAndLeavesTheOther()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First"),
                Columns.Property<Person, string>(x => x.Last, title: "Last")));

            Apply(cut, "First", "A");
            Apply(cut, "Last", "B");

            cut.FindAll(".rz-filter-pills .rz-chip button")[0].Click();

            Assert.False(Column(cut, "First").HasFilter);
            Assert.True(Column(cut, "Last").HasFilter);
            Assert.Single(Pills(cut));
        }

        [Fact]
        public void TheBodyIsNotAButtonWhereThereIsNowhereForItToGo()
        {
            // AllowFiltering off: the filter arrived from outside and there is no editor to open. A
            // control that looks clickable and does nothing is the fault §27's review caught.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First")), filtering: false);

            Apply(cut, "First", "A");

            var pill = Pills(cut).Single();

            Assert.Null(pill.GetAttribute("role"));
            Assert.Null(pill.GetAttribute("tabindex"));
        }

        [Fact]
        public void TheBodyOpensTheMenuUnderTheMenuUI()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First")), ui: FilterUI.Menu);

            Apply(cut, "First", "A");

            Assert.Empty(cut.FindAll(".rz-overlaypanel .rz-filter-menu-item"));

            Pills(cut).Single().Click();

            // The panel is built and seeded from what the column is filtering by, which is the whole of
            // §31's "reopens that column's menu with the filter loaded".
            Assert.NotEmpty(cut.FindAll(".rz-overlaypanel .rz-filter-menu-item"));
            Assert.Contains(cut.FindAll(".rz-overlaypanel .rz-filter-menu-item"),
                item => item.ClassList.Contains("rz-state-highlight") && item.TextContent == "Contains");
        }

        [Fact]
        public void OnlyAColumnWithAPillNamesItsFilterCell()
        {
            // The id is what the pill's body sends the cursor to under FilterUI.Row. Written for the
            // feature alone it cost about 1 KB a render on a grid with nothing filtered - ids no pill
            // existed to use - which is what the benchmark caught and this pins.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First"),
                Columns.Property<Person, string>(x => x.Last, title: "Last")));

            Assert.All(cut.FindAll("thead .rz-cell-filter"), cell => Assert.Null(cell.GetAttribute("id")));

            Apply(cut, "First", "A");

            var cells = cut.FindAll("thead .rz-cell-filter");

            Assert.NotNull(cells[0].GetAttribute("id"));
            Assert.Null(cells[1].GetAttribute("id"));
        }

        [Fact]
        public void NoFilterCellIsNamedWhileTheBarIsOff()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First")), pills: false);

            Apply(cut, "First", "A");

            Assert.All(cut.FindAll("thead .rz-cell-filter"), cell => Assert.Null(cell.GetAttribute("id")));
        }

        // ---- the gates ----

        [Fact]
        public void ClearAllOverSixFilteredColumnsIsOneQuery()
        {
            // §31 left this open, fearing the naive loop that calls the public clear per column.
            // ClearFilters was never that loop - and a sentence saying so is not a gate.
            using var ctx = new TestContext();
            var executor = Counting(ctx);

            var cut = Queryable(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First"),
                Columns.Property<Person, string>(x => x.Last, title: "Last"),
                Columns.Property<Person, decimal>(x => x.Salary, title: "Salary"),
                Columns.Property<Person, decimal?>(x => x.Bonus, title: "Bonus"),
                Columns.Property<Person, Grade>(x => x.Grade, title: "Grade"),
                Columns.Property<Person, int>(x => x.Id, title: "Id")));

            Apply(cut, "First", "A");
            Apply(cut, "Last", "B");
            Apply(cut, "Salary", 1m);
            Apply(cut, "Bonus", 2m);
            Apply(cut, "Grade", Grade.Junior);
            Apply(cut, "Id", 1);

            Assert.Equal(6, Pills(cut).Length);

            var before = executor.Loads;

            cut.FindAll(".rz-filter-pills > button").Last().Click();

            Assert.Empty(Pills(cut));
            Assert.Equal(before + 1, executor.Loads);
        }

        [Fact]
        public void RemovingOnePillIsOneQuery()
        {
            using var ctx = new TestContext();
            var executor = Counting(ctx);

            var cut = Queryable(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First")));

            Apply(cut, "First", "A");

            var before = executor.Loads;

            cut.FindAll(".rz-filter-pills .rz-chip button")[0].Click();

            Assert.Equal(before + 1, executor.Loads);
        }

        [Fact]
        public void TheBarRunsNoQueryOfItsOwn()
        {
            // The gate's other half: drawing the bar is a read of what the columns already hold.
            using var ctx = new TestContext();
            var executor = Counting(ctx);

            // Under the menu, because a filter row draws the check-box list itself and that scan is
            // §36's, not §37's. The menu is never opened here, so nothing but the bar has asked.
            var cut = Queryable(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First",
                    filterMode: FilterMode.CheckBoxList)), ui: FilterUI.Menu);

            cut.InvokeAsync(() => cut.Instance.Filter(Column(cut, "First"),
                new List<string> { "Alice" }, FastGridFilterOperator.In)).Wait();

            Assert.Single(Pills(cut));
            Assert.Equal(0, executor.Scans);
        }

        // ---- what the mutation loop found unpinned ----

        [Fact]
        public void AColumnThatCannotBeFilteredGetsNoPillEvenHoldingAFilter()
        {
            // A declared FilterValue is written to CurrentFilter whether or not the column can be
            // filtered, so HasFilter - not CurrentFilter - is what the bar has to ask. Without it the
            // bar would draw an x that Filter refuses, which is the affordance the design rules out.
            //
            // **Two columns, and the first draft had one.** The band itself is drawn only when some
            // column HasFilter, so with a single unfilterable column the band never opens and the
            // per-column guard is unreachable - the mutation that dropped it stayed green. A second,
            // genuinely filtered column is what opens the band so the guard has something to refuse.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First",
                    filterValue: "A", filterable: false),
                Columns.Property<Person, string>(x => x.Last, title: "Last")));

            var refused = cut.FindComponents<PropertyColumn<Person, string>>()
                .Select(c => c.Instance).Single(c => c.Title == "First");

            Assert.NotNull(refused.CurrentFilter);
            Assert.False(refused.HasFilter);

            Apply(cut, "Last", "B");

            Assert.Equal(new[] { "Last Contains B" }, PillText(cut));
        }

        [Fact]
        public void AnEditablePillIsAButtonAndATabStop()
        {
            // §12's one tab stop is a rule about the grid's cells; this band is chrome outside
            // role="grid", so a filter only a mouse can remove would be the mouse-only control §31
            // refused for the header icon.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First")));

            Apply(cut, "First", "A");

            var pill = Pills(cut).Single();

            Assert.Equal("button", pill.GetAttribute("role"));
            Assert.Equal("0", pill.GetAttribute("tabindex"));
        }

        [Theory]
        [InlineData("Enter")]
        [InlineData(" ")]
        public void TheKeysThatComeWithTheButtonRoleOpenThePill(string key)
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First")), ui: FilterUI.Menu);

            Apply(cut, "First", "A");

            Assert.Empty(cut.FindAll(".rz-overlaypanel .rz-filter-menu-item"));

            Pills(cut).Single().KeyDown(key);

            Assert.NotEmpty(cut.FindAll(".rz-overlaypanel .rz-filter-menu-item"));
        }

        [Fact]
        public void AKeyThatIsNotOneOfThoseTwoDoesNothing()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First")), ui: FilterUI.Menu);

            Apply(cut, "First", "A");

            Pills(cut).Single().KeyDown("a");

            Assert.Empty(cut.FindAll(".rz-overlaypanel .rz-filter-menu-item"));
        }

        [Fact]
        public void UnderTheRowTheBodyPutsTheCursorInThatColumnsFilterCell()
        {
            // The other half of the two-destination rule. Asserted through the interop rather than
            // through a rendered result, because focus and the scroll it causes both happen in a
            // browser - which is where the behaviour itself was confirmed.
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule("./_content/Radzen.Blazor.FastGrid/fastgrid.js");

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "First"),
                Columns.Property<Person, string>(x => x.Last, title: "Last")));

            Apply(cut, "Last", "B");

            var cell = cut.FindAll("thead .rz-cell-filter")[1].GetAttribute("id");

            Assert.NotNull(cell);

            Pills(cut).Single().Click();

            // The second column's cell, not the first: the id carries the drawn index, and a pill for
            // a column that is not the leftmost is what catches this going by position instead.
            Assert.Equal(cell,
                Assert.Single(module.Invocations["focusFilter"].Single().Arguments));
        }

        [Fact]
        public void AFormattedEnumReadsAsItsCellsDoRatherThanAsItsName()
        {
            // The pill's whole promise is that it reads what the cells read. An earlier draft excepted
            // enums from the format, and a column declaring Format="D" then drew 1 in every cell and
            // "Senior engineer" in its pill. The mutation loop found the exception unobservable; this
            // is the assertion that says which way it should have gone.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, Grade>(x => x.Grade, title: "Grade", format: "D")));

            var column = cut.FindComponent<PropertyColumn<Person, Grade>>().Instance;

            var cell = column.CellTextOf(People.Sample().First(p => p.Grade == Grade.Senior));

            Assert.Equal("1", cell);
            Assert.Equal("Grade Equals " + cell,
                Phrase(cut, column, One(FastGridFilterOperator.Equals, Grade.Senior)));
        }

        // ---- §44: a filter on a column nobody can see ----

        static IElement[] HiddenPills(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindAll(".rz-filter-pills .rz-filter-pill-hidden").ToArray();

        /// <summary>What the notice says, which is empty while it is saying nothing.</summary>
        /// <remarks>
        /// The element itself is always written once the bar is, because a hidden pill's
        /// <c>aria-controls</c> names it and an id that is not in the document is the §35 fault. So the
        /// assertion has to be about its text rather than about its presence.
        /// </remarks>
        static string Notice(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.Find(".rz-filter-pill-notice").TextContent;

        static RenderFragment TwoColumnsSecondHidden() => Columns.Of(
            Columns.Property<Person, string>(x => x.First, title: "First"),
            Columns.Property<Person, string>(x => x.Last, title: "Last", visible: false));

        [Fact]
        public void TheBandIsDrawnForAFilterOnAColumnThatIsNotDrawn()
        {
            // The fault §44 found. AnyColumnFiltered asked the drawn columns, so a grid filtered by
            // nothing but a hidden column drew no band - and Clear all filters, which does reach every
            // column, was behind the button the band was not drawing.
            using var ctx = new TestContext();

            var cut = Render(ctx, TwoColumnsSecondHidden());

            Assert.Empty(cut.FindAll(".rz-filter-pills"));

            Apply(cut, "Last", "B");

            Assert.NotEmpty(cut.FindAll(".rz-filter-pills"));
            Assert.Equal("Last Contains B", Assert.Single(PillText(cut)));
            Assert.Single(HiddenPills(cut));
        }

        [Fact]
        public void ADrawnColumnsPillIsNotMarkedHidden()
        {
            // The counterweight: without it, marking every pill hidden would pass the test above.
            using var ctx = new TestContext();

            var cut = Render(ctx, TwoColumnsSecondHidden());

            Apply(cut, "First", "A");

            Assert.Single(Pills(cut));
            Assert.Empty(HiddenPills(cut));
        }

        [Fact]
        public void AHiddenColumnsPillSaysWhyWhenItIsClicked()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, TwoColumnsSecondHidden());

            Apply(cut, "Last", "B");

            Assert.Equal("", Notice(cut));
            Assert.Equal("false", HiddenPills(cut).Single().GetAttribute("aria-expanded"));

            HiddenPills(cut).Single().Click();

            Assert.Equal(cut.Instance.HiddenColumnFilterText, Notice(cut));
            Assert.Equal("true", HiddenPills(cut).Single().GetAttribute("aria-expanded"));

            // Clicking it again takes the notice away, which is what a toggle promises.
            HiddenPills(cut).Single().Click();

            Assert.Equal("", Notice(cut));
        }

        [Theory]
        [InlineData("Enter")]
        [InlineData(" ")]
        public void TheHiddenPillTakesTheKeysItsRolePromises(string key)
        {
            // §31 refused the mouse-only control, and this is a role="button" on a span - which takes
            // neither key from the browser, so both are spelled out or neither works.
            using var ctx = new TestContext();

            var cut = Render(ctx, TwoColumnsSecondHidden());

            Apply(cut, "Last", "B");

            HiddenPills(cut).Single().KeyDown(key);

            Assert.Equal(cut.Instance.HiddenColumnFilterText, Notice(cut));
        }

        [Fact]
        public void AnotherKeyLeavesTheHiddenPillAlone()
        {
            // The counterweight: without it, toggling on every key would pass the theory above and would
            // fire on Tab out of the pill.
            using var ctx = new TestContext();

            var cut = Render(ctx, TwoColumnsSecondHidden());

            Apply(cut, "Last", "B");

            HiddenPills(cut).Single().KeyDown("Tab");

            Assert.Equal("", Notice(cut));
        }

        [Fact]
        public void TheHiddenPillSaysItIsHiddenWithoutBeingOpened()
        {
            // The glyph is aria-hidden and the phrase is the same one a drawn pill carries, so without
            // this the two are indistinguishable to a screen reader.
            using var ctx = new TestContext();

            var cut = Render(ctx, TwoColumnsSecondHidden());

            Apply(cut, "Last", "B");

            var pill = HiddenPills(cut).Single();

            Assert.Equal("Last Contains B " + cut.Instance.HiddenColumnFilterText,
                pill.GetAttribute("aria-label"));

            // The disclosure names what it opens, and the id is in the document before it is opened.
            Assert.Equal(cut.Instance.HiddenColumnNoticeElementId, pill.GetAttribute("aria-controls"));
            Assert.NotNull(cut.Find("#" + cut.Instance.HiddenColumnNoticeElementId));
        }

        [Fact]
        public void AHiddenColumnsPillDoesNotSendAnyoneToAFilterCell()
        {
            // The drawn pill's click focuses the filter cell through JS. There is no cell for a column
            // that is not drawn, so the click has to do something else entirely rather than aim at one.
            using var ctx = new TestContext();

            var module = ctx.JSInterop.SetupModule("./_content/Radzen.Blazor.FastGrid/fastgrid.js");

            module.SetupVoid("focusFilter", _ => true).SetVoidResult();

            var cut = Render(ctx, TwoColumnsSecondHidden());

            Apply(cut, "Last", "B");

            HiddenPills(cut).Single().Click();

            Assert.Empty(module.Invocations["focusFilter"]);
        }

        [Fact]
        public void AHiddenColumnsFilterIsStillRemovableFromItsPill()
        {
            // The escape hatch, and the reason a pill is the answer rather than clearing the filter when
            // the column goes: one click either way, and the reader is the one who chooses.
            using var ctx = new TestContext();

            var cut = Render(ctx, TwoColumnsSecondHidden());

            Apply(cut, "Last", "B");

            cut.FindAll(".rz-filter-pills .rz-filter-pill-hidden button").Single().Click();

            Assert.False(Column(cut, "Last").HasFilter);
            Assert.Empty(cut.FindAll(".rz-filter-pills"));
        }

        [Fact]
        public void ClearAllFiltersReachesTheColumnNobodyCanSee()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, TwoColumnsSecondHidden());

            Apply(cut, "First", "A");
            Apply(cut, "Last", "B");

            cut.FindAll(".rz-filter-pills > button").Last().Click();

            Assert.False(Column(cut, "First").HasFilter);
            Assert.False(Column(cut, "Last").HasFilter);
        }

        [Fact]
        public void TheNoticeGoesWhenTheFilterItExplainsDoes()
        {
            // Read back off the column rather than cleared by hand, so removing the filter, showing the
            // column and Clear all each take it away without knowing the notice exists.
            using var ctx = new TestContext();

            var cut = Render(ctx, TwoColumnsSecondHidden());

            Apply(cut, "Last", "B");

            HiddenPills(cut).Single().Click();

            Assert.NotEqual("", Notice(cut));

            cut.InvokeAsync(() => cut.Instance.Filter(Column(cut, "Last"), null)).Wait();

            Assert.Empty(cut.FindAll(".rz-filter-pills"));
        }

        /// <summary>The same two columns, with the second's visibility always written.</summary>
        /// <remarks>
        /// Not <c>Columns.Property(visible: ...)</c>, which omits the attribute when it is true - and an
        /// omitted parameter is not a reset, so a column re-rendered that way keeps the <c>false</c> it
        /// was given. The test below turns the column back on, which needs the attribute to be there
        /// both times.
        /// </remarks>
        static RenderFragment SecondColumnVisible(bool visible) => SecondColumn(visible);

        /// <summary>The first column always, and the second present only when it is asked for.</summary>
        /// <remarks>
        /// Written out rather than composed from <c>Columns.Of</c> for two reasons the tests below
        /// need. The visibility is always written, and an omitted parameter is not a reset - so a column
        /// re-rendered without it keeps the <c>false</c> it was given. And the first column keeps its
        /// sequence numbers whether or not the second is there, so removing the second removes only the
        /// second: a fragment of a different shape disposes both, which is enough to hide a fault in
        /// what happens to the one that went.
        /// </remarks>
        static RenderFragment SecondColumn(bool? visible) => builder =>
        {
            builder.OpenComponent<PropertyColumn<Person, string>>(0);
            builder.AddAttribute(1, nameof(PropertyColumn<Person, string>.Property),
                (Expression<Func<Person, string>>)(x => x.First));
            builder.AddAttribute(2, nameof(PropertyColumn<Person, string>.Title), "First");
            builder.CloseComponent();

            if (visible is not { } declared)
            {
                return;
            }

            builder.OpenComponent<PropertyColumn<Person, string>>(3);
            builder.AddAttribute(4, nameof(PropertyColumn<Person, string>.Property),
                (Expression<Func<Person, string>>)(x => x.Last));
            builder.AddAttribute(5, nameof(PropertyColumn<Person, string>.Title), "Last");
            builder.AddAttribute(6, nameof(ColumnBase<Person>.Visible), declared);
            builder.CloseComponent();
        };

        [Fact]
        public void TheNoticeGoesWhenTheColumnLeavesTheMarkup()
        {
            // The one case the read-back cannot heal: a removed column keeps its filter and its
            // invisibility, so the notice would go on answering for a pill that is not there. Same rule
            // RemoveColumn already applies to the sort and to the column's check-box-list values.
            using var ctx = new TestContext();

            var cut = Render(ctx, SecondColumnVisible(false));

            Apply(cut, "Last", "B");

            HiddenPills(cut).Single().Click();

            Assert.NotEqual("", Notice(cut));

            cut.SetParametersAndRender(p => p.Add(g => g.ChildContent, SecondColumn(null)));

            Assert.Empty(cut.FindAll(".rz-filter-pills"));

            // And it stays gone once something else puts the bar back.
            Apply(cut, "First", "A");

            Assert.Equal("", Notice(cut));
        }

        [Fact]
        public void TheNoticeGoesWhenTheColumnComesBack()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, SecondColumnVisible(false));

            Apply(cut, "Last", "B");

            HiddenPills(cut).Single().Click();

            Assert.NotEqual("", Notice(cut));

            cut.SetParametersAndRender(p =>
                p.Add(g => g.ChildContent, SecondColumnVisible(true)));

            Assert.Equal("", Notice(cut));
            Assert.Empty(HiddenPills(cut));
            Assert.Single(Pills(cut));
        }
    }
}
