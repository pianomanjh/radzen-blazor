using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Radzen.Blazor;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// §36: the checklist is offered where something already knows the column's values - a lookup's map,
    /// an enum's type, or the author declaring a check-box list - and nowhere else.
    /// </summary>
    /// <remarks>
    /// The gate is <em>queries per open</em>, which is the number §10 measured going wrong at three
    /// scans for one render. Everything below the operator rules is about that count.
    /// </remarks>
    public class FastGridChecklistTests
    {
        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx,
            RenderFragment columns,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>>? extra = null,
            IEnumerable<Person>? data = null, FilterUI ui = FilterUI.Menu)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data ?? People.Sample());
                p.Add(g => g.ChildContent, columns);
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.FilterUI, ui);
                extra?.Invoke(p);
            });
        }

        static void OpenMenu(IRenderedComponent<RadzenFastGrid<Person>> cut, int column) =>
            cut.FindAll("thead button.rz-grid-filter-icon")[column].Click();

        static void PickItem(IRenderedComponent<RadzenFastGrid<Person>> cut, string label) =>
            cut.FindAll("button.rz-filter-menu-item").Single(item => item.TextContent == label).Click();

        static RadzenListBox<IEnumerable>[] Lists(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindComponents<RadzenListBox<IEnumerable>>().Select(c => c.Instance).ToArray();

        /// <summary>The blank is the entry standing for no value, wherever it sits in the list.</summary>
        static FastGridFilterEntry Blank(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            Lists(cut).Single().Data.Cast<object>().OfType<FastGridFilterEntry>()
                .Single(e => e.Value is null);

        // ---- the operator rule, where it is a rule about nothing but a flag ----

        [Fact]
        public void ASetOffersTheTwoSequenceOperatorsAndNothingElse()
        {
            Assert.Equal(
                new[] { FastGridFilterOperator.In, FastGridFilterOperator.NotIn },
                FastGridFilterOperators.Set(nullable: false));
        }

        [Fact]
        public void ANullableSetGetsTheSameAbsenceTailEveryOtherTypeGets()
        {
            // The append is factored rather than copied, so this is the same two operators in the same
            // place as on a string or a date - which is the thing a second copy would let drift.
            var set = FastGridFilterOperators.Set(nullable: true);
            var text = FastGridFilterOperators.Menu(typeof(string), nullable: true);

            Assert.Equal(
                new[]
                {
                    FastGridFilterOperator.In,
                    FastGridFilterOperator.NotIn,
                    FastGridFilterOperator.IsNull,
                    FastGridFilterOperator.IsNotNull,
                },
                set);

            Assert.Equal(text.TakeLast(2), set.TakeLast(2));
        }

        // ---- the operator rule, where it needs a column to have declared something ----

        [Fact]
        public void ADeclaredCheckBoxListIsOfferedTheSetOperators()
        {
            // Without this the declaration is inert under FilterUI.Menu: a string column would be handed
            // Contains and StartsWith and no way to reach the list its author asked for.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, string>(x => x.First,
                filterMode: FilterMode.CheckBoxList)));

            var column = cut.FindComponent<PropertyColumn<Person, string>>().Instance;

            Assert.Contains(FastGridFilterOperator.In, column.MenuOperators);
            Assert.DoesNotContain(FastGridFilterOperator.Contains, column.MenuOperators);
        }

        [Fact]
        public void TheSameColumnWithoutTheDeclarationIsOfferedItsTypesOperators()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, string>(x => x.First)));

            var column = cut.FindComponent<PropertyColumn<Person, string>>().Instance;

            Assert.Contains(FastGridFilterOperator.Contains, column.MenuOperators);
            Assert.DoesNotContain(FastGridFilterOperator.In, column.MenuOperators);
        }

        [Fact]
        public void ALookupIsASetWhateverTheGridsModeIs()
        {
            // Unconditional, where the base asks the declaration: a lookup filters by ids whatever
            // editor is drawing, and has since §14.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Lookup<Person, int>(x => x.CategoryId,
                FastGridLookup.Items(Lookups.CategoryRows(), c => c.Id, c => c.Name))), ui: FilterUI.Menu);

            var column = cut.FindComponent<LookupColumn<Person, int>>().Instance;

            Assert.Equal(
                new[] { FastGridFilterOperator.In, FastGridFilterOperator.NotIn },
                column.MenuOperators);
        }

        // ---- the enum arm: values from the type, and no query to get them ----

        [Fact]
        public void AnEnumOffersEveryMemberOfItsTypeRatherThanTheOnesTheRowsHold()
        {
            // Offering only what the data holds is the defect §14 rejected for lookups by name - a
            // filter control whose options move as the data does moves under the reader. Every sample
            // row is Junior; Senior is still offered.
            using var ctx = new TestContext();
            var data = People.Sample();

            foreach (var person in data)
            {
                person.Grade = Grade.Junior;
            }

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, Grade>(x => x.Grade)), data: data);

            OpenMenu(cut, 0);
            PickItem(cut, "In");

            Assert.Equal(
                new object[] { Grade.Junior, Grade.Senior },
                Lists(cut).Single().Data.Cast<object>());
        }

        [Fact]
        public void AWrappedEnumDrawsTheSameWordAnUnwrappedOneDoes()
        {
            // A nullable enum is wrapped in an entry and a non-nullable one is not, so upstream stops
            // recognising it as an Enum on one of the two paths - and GetDisplayDescription is what it
            // does when it does recognise one. An enum that reads one way when the column is nullable
            // and another when it is not would be one rule with two spellings.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, Grade?>(x => x.Rank),
                Columns.Property<Person, Grade>(x => x.Grade)));

            OpenMenu(cut, 0);
            PickItem(cut, "In");

            var nullable = Lists(cut).Single().Data.Cast<object>()
                .OfType<FastGridFilterEntry>().Where(e => e.Value is not null)
                .Select(e => e.ToString()).ToArray();

            cut.Find("div.rz-filter-menu-buttons button.rz-light").Click();

            OpenMenu(cut, 1);
            PickItem(cut, "In");

            var plain = Lists(cut).Single().Data.Cast<object>()
                .Select(v => Radzen.Blazor.EnumExtensions.GetDisplayDescription((Enum)v)).ToArray();

            Assert.Equal(plain, nullable);
        }

        // ---- the blank ----

        [Fact]
        public void ANullableColumnLeadsWithTheBlankEntry()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, int?>(x => x.RegionId,
                filterMode: FilterMode.CheckBoxList)));

            OpenMenu(cut, 0);
            PickItem(cut, "In");

            var offered = Lists(cut).Single().Data.Cast<object>().ToArray();

            // First, and standing for no value - §14's own placement and §14's own meaning.
            Assert.Equal(cut.Instance.BlankFilterText, offered[0].ToString());
            Assert.Null(Assert.IsType<FastGridFilterEntry>(offered[0]).Value);
        }

        [Fact]
        public void ANonNullableColumnOffersNoBlank()
        {
            // Read as default it would filter to the rows whose value happens to be zero while the list
            // showed nothing ticked, which is §14's own trap.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, int>(x => x.Id,
                filterMode: FilterMode.CheckBoxList)));

            OpenMenu(cut, 0);
            PickItem(cut, "In");

            // Not wrapped at all: a column that cannot hold a blank offers the raw values it always did.
            Assert.DoesNotContain(Lists(cut).Single().Data.Cast<object>(), v => v is FastGridFilterEntry);
        }

        [Fact]
        public void TickingTheBlankFiltersToTheRowsWithNothingInThem()
        {
            // The whole point of the entry, and the round trip it needs: the blank becomes a null in the
            // In list, and List<int?>.Contains is what matches the rows carrying no id.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, int?>(x => x.RegionId, filterMode: FilterMode.CheckBoxList),
                Columns.Property<Person, string>(x => x.First)));

            OpenMenu(cut, 0);
            PickItem(cut, "In");

            var blank = Blank(cut);

            cut.InvokeAsync(() => Lists(cut).Single().Change
                .InvokeAsync(new List<object> { blank }));

            cut.Find("div.rz-filter-menu-buttons button.rz-primary").Click();

            var column = cut.FindComponent<PropertyColumn<Person, int?>>().Instance;
            var values = ((IEnumerable)column.CurrentFilter!.First.Value!).Cast<object?>().ToArray();

            Assert.Equal(new object?[] { null }, values);

            // And it narrows to exactly the rows that have no region, rather than to none at all.
            var shown = cut.FindAll("tbody tr")
                .Select(row => row.QuerySelectorAll("td")[1].TextContent).ToArray();

            Assert.Equal(
                People.Sample().Where(p => p.RegionId is null).Select(p => p.First).ToArray(),
                shown);
        }

        [Fact]
        public void TheBlankSurvivesReopeningTheMenuAsATickedBox()
        {
            // SelectionOf's half of the round trip: the committed null has to come back as the entry,
            // because a raw null is not one of the values the list is bound to.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, int?>(x => x.RegionId,
                filterMode: FilterMode.CheckBoxList)));

            OpenMenu(cut, 0);
            PickItem(cut, "In");

            var blank = Blank(cut);

            cut.InvokeAsync(() => Lists(cut).Single().Change.InvokeAsync(new List<object> { blank }));
            cut.Find("div.rz-filter-menu-buttons button.rz-primary").Click();

            OpenMenu(cut, 0);

            Assert.Same(blank, Lists(cut).Single().Value!.Cast<object>().Single());
        }

        [Fact]
        public void AStringColumnsBlankStandsForTheEmptyStringToo()
        {
            // In coalesces a null string to the empty one, so leaving the scanned "" beside the blank
            // would put two entries in the list that filter identically - one of them drawn as an empty
            // row.
            using var ctx = new TestContext();
            var data = People.Sample();

            data[0].First = string.Empty;

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, string>(x => x.First,
                filterMode: FilterMode.CheckBoxList)), data: data);

            OpenMenu(cut, 0);
            PickItem(cut, "In");

            // One entry standing for nothing, and no separate entry whose value is the empty string.
            var offered = Lists(cut).Single().Data.Cast<object>().OfType<FastGridFilterEntry>().ToArray();

            Assert.Single(offered.Where(e => e.Value is null));
            Assert.DoesNotContain(offered, e => e.Value as string == string.Empty);
        }

        [Fact]
        public void AColumnThatCannotCarryTheNullIsNotOfferedABlank()
        {
            // Nullable is necessary and not sufficient. A column declared as object hands its filter to
            // the reflective route, which drops the null out of the In list - so an entry offered here
            // would tick, commit, and narrow to no rows at all rather than to the rows with nothing.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, object>(x => x.Mixed, filterMode: FilterMode.CheckBoxList)));

            var column = cut.FindComponent<PropertyColumn<Person, object>>().Instance;

            // Still nullable, and still offered the operator that asks the question directly.
            Assert.Contains(FastGridFilterOperator.IsNull, column.MenuOperators);

            OpenMenu(cut, 0);
            PickItem(cut, "In");

            Assert.DoesNotContain(Lists(cut).Single().Data.Cast<object>(), v => v is FastGridFilterEntry);
        }

        [Theory]
        [InlineData("property")]
        [InlineData("collection")]
        public void ACollectionOfAnyKindIsNotOfferedABlank(string kind)
        {
            // LookupCollectionColumn has refused one since §14, and the reason is about meaning rather
            // than mechanism: "has no regions at all" is a different question from "has a region that is
            // null", and In over the elements does not ask it. §36 asked FilterNullable, which reads the
            // element type, and handed a collection of strings a blank no element could ever be.
            //
            // Both kinds, because they refuse it in different places and a test of one is not a test of
            // the other: a PropertyColumn over a List<string> refuses through ComposesItsOwnFilter's
            // !IsCollection, and CollectionColumn - which derives from ColumnBase, not from
            // PropertyColumn - has to say so itself. The first version of this test named "collection"
            // and rendered the first, so the second's override was never executed.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(kind == "property"
                ? Columns.Property<Person, List<string>>(x => x.Regions,
                    filterMode: FilterMode.CheckBoxList)
                : Columns.Collection<Person, string>(x => x.Regions,
                    filterMode: FilterMode.CheckBoxList)));

            OpenMenu(cut, 0);
            PickItem(cut, "In");

            Assert.DoesNotContain(Lists(cut).Single().Data.Cast<object>(), v => v is FastGridFilterEntry);
        }

        [Fact]
        public void AClassTypedColumnCarriesTheNullTheSameWay()
        {
            // The case the object column cannot do and this one can: a reference type the column still
            // composes its own predicate for. This is what FilterExpression.Listed's `default(TProp) is
            // null` arm exists for - written as a Nullable.GetUnderlyingType test it dropped the null
            // here too.
            using var ctx = new TestContext();
            var data = People.Sample();

            data[0].Customer = null!;
            data[1].Customer = null!;

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, Company>(x => x.Customer, filterMode: FilterMode.CheckBoxList),
                Columns.Property<Person, string>(x => x.First)), data: data);

            OpenMenu(cut, 0);
            PickItem(cut, "In");

            cut.InvokeAsync(() => Lists(cut).Single().Change
                .InvokeAsync(new List<object> { Blank(cut) }));

            cut.Find("div.rz-filter-menu-buttons button.rz-primary").Click();

            Assert.Equal(
                data.Where(p => p.Customer is null).Select(p => p.First).ToArray(),
                cut.FindAll("tbody tr").Select(r => r.QuerySelectorAll("td")[1].TextContent).ToArray());
        }

        [Fact]
        public void EveryEntryIsWrappedWhereAnyIs()
        {
            // The invariant the circuit crash was about, which no test pinned: DropDownBase infers a
            // multiple selection's element type from the first item in Data and casts the whole
            // selection to it, so a list holding a blank beside raw values makes upstream cast a value
            // to the blank's type. Re-introducing exactly that - wrap the blank, leave the rest raw -
            // passed the whole suite. Asserted positively here because the browser cannot be.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, int?>(x => x.RegionId,
                filterMode: FilterMode.CheckBoxList)));

            OpenMenu(cut, 0);
            PickItem(cut, "In");

            var offered = Lists(cut).Single().Data.Cast<object>().ToArray();

            Assert.True(offered.Length > 1);
            Assert.All(offered, v => Assert.IsType<FastGridFilterEntry>(v));
        }

        [Fact]
        public void ADeclaredFilterIsTickedOnTheFirstRenderOfTheList()
        {
            // A filter that was already committed when the panel opens - from markup, a settings
            // restore or ApplyFilters - shows as ticked, which every other test here reaches only by
            // ticking first.
            //
            // It does NOT pin the Data-before-Value ordering in RenderFilterMenuList, which was the
            // reason it was written: §35's panel arms on click and opens after the render, so the list
            // draws twice and the second draw heals a first one that had no entries to map onto. Tried
            // and confirmed - swapping the two AddAttribute calls leaves this and the whole suite
            // green. The ordering is a claim about one intermediate frame, which is the same shape as
            // §35's unobservable draft clear.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(Columns.Property<Person, int?>(x => x.RegionId,
                filterMode: FilterMode.CheckBoxList,
                filterOperator: FilterOperator.In,
                filterValue: new List<int?> { null })));

            OpenMenu(cut, 0);

            var ticked = Lists(cut).Single().Value;

            Assert.NotNull(ticked);
            Assert.Null(Assert.IsType<FastGridFilterEntry>(Assert.Single(ticked.Cast<object>())).Value);
        }

        // ---- the gate: queries per open ----

        /// <summary>
        /// §10's own control, and the instrument this section's gate needs: it counts the queries that
        /// were <em>issued</em>, and the failure mode here is N of them where one is right.
        /// </summary>
        sealed class ScanCountingExecutor : IFastGridQueryExecutor
        {
            public int Scans { get; private set; }

            public bool IsSupported<T>(IQueryable<T> queryable) => true;

            public Task<int> CountAsync<T>(IQueryable<T> queryable, CancellationToken token = default) =>
                Task.FromResult(queryable.Count());

            public Task<List<T>> ToListAsync<T>(IQueryable<T> queryable, CancellationToken token = default)
            {
                // The page load and the check-box list's scan both come through here; only one of them
                // composes a Distinct.
                if (queryable.Expression.ToString().Contains("Distinct", StringComparison.Ordinal))
                {
                    Scans++;
                }

                return Task.FromResult(queryable.ToList());
            }
        }

        static (IRenderedComponent<RadzenFastGrid<Person>> Cut, ScanCountingExecutor Executor) Counted(
            TestContext ctx, RenderFragment columns)
        {
            var executor = new ScanCountingExecutor();

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(executor);

            return (Render(ctx, columns, data: People.Sample().AsQueryable()), executor);
        }

        [Fact]
        public void ALookupsOpenCostsNoQuery()
        {
            // §14 holds the map, so there is nothing to scan for. The lookup's own resolve is once at
            // startup and is not this gate's business.
            using var ctx = new TestContext();
            var (cut, executor) = Counted(ctx, Columns.Of(
                Columns.Lookup<Person, int>(x => x.CategoryId,
                    FastGridLookup.Items(Lookups.CategoryRows(), c => c.Id, c => c.Name))));

            OpenMenu(cut, 0);
            PickItem(cut, "In");

            Assert.Equal(0, executor.Scans);
        }

        [Fact]
        public void AnEnumsOpenCostsNoQuery()
        {
            using var ctx = new TestContext();
            var (cut, executor) = Counted(ctx, Columns.Of(
                Columns.Property<Person, Grade>(x => x.Grade)));

            OpenMenu(cut, 0);
            PickItem(cut, "In");

            Assert.Equal(0, executor.Scans);
        }

        [Fact]
        public void OneOpenOfOneColumnIsOneScanAndNotOnePerColumn()
        {
            // The gate, in the shape §10 measured it failing: three scans for one render. Two declared
            // columns and one open - a panel that warmed every column's list, which is what leading with
            // the checklist means, would read two here.
            using var ctx = new TestContext();
            var (cut, executor) = Counted(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, filterMode: FilterMode.CheckBoxList),
                Columns.Property<Person, string>(x => x.Last, filterMode: FilterMode.CheckBoxList)));

            OpenMenu(cut, 0);
            PickItem(cut, "In");

            cut.WaitForAssertion(() => Assert.Equal(1, executor.Scans));

            // And the second column's own open is its own one, rather than free or double.
            OpenMenu(cut, 1);
            PickItem(cut, "In");

            cut.WaitForAssertion(() => Assert.Equal(2, executor.Scans));
        }

        [Fact]
        public void ReopeningTheSameColumnScansNothingFurther()
        {
            using var ctx = new TestContext();
            var (cut, executor) = Counted(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, filterMode: FilterMode.CheckBoxList)));

            OpenMenu(cut, 0);
            PickItem(cut, "In");

            cut.WaitForAssertion(() => Assert.Equal(1, executor.Scans));

            OpenMenu(cut, 0);
            PickItem(cut, "In");
            OpenMenu(cut, 0);
            PickItem(cut, "In");

            Assert.Equal(1, executor.Scans);
        }

        [Fact]
        public void ReloadIsWhatMakesTheNextOpenScanAgain()
        {
            using var ctx = new TestContext();
            var (cut, executor) = Counted(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, filterMode: FilterMode.CheckBoxList)));

            OpenMenu(cut, 0);
            PickItem(cut, "In");

            cut.WaitForAssertion(() => Assert.Equal(1, executor.Scans));

            cut.InvokeAsync(() => cut.Instance.Reload());

            OpenMenu(cut, 0);
            PickItem(cut, "In");

            cut.WaitForAssertion(() => Assert.Equal(2, executor.Scans));
        }

        [Fact]
        public void AColumnThatNothingKnowsTheValuesOfDrawsNoListAndCostsNoQuery()
        {
            // The refutation of §31's "Filter by value...", asked as a number: an undeclared column
            // cannot reach a scan at all, whatever its reader picks.
            using var ctx = new TestContext();
            var (cut, executor) = Counted(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First)));

            OpenMenu(cut, 0);

            Assert.DoesNotContain(FastGridFilterOperator.In,
                cut.FindComponent<PropertyColumn<Person, string>>().Instance.MenuOperators);

            PickItem(cut, "Contains");

            Assert.Empty(Lists(cut));
            Assert.Equal(0, executor.Scans);
        }
    }
}
