using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// §34 through a grid: the three routes reading one clock, the wire carrying no token, and a stored
    /// filter that still means what it says a week later.
    /// </summary>
    /// <remarks>
    /// <see cref="RelativeDateTests" /> covers the rules themselves, at the layer they live at. What is
    /// here is what only a grid can answer - that the delegate route, the expression route and the
    /// descriptors all agree, which is the class of fault §33's review found and measured, and the two
    /// answers a relative date can produce are both plausible dates.
    /// </remarks>
    public class RelativeDateGridTests
    {
        // Sunday afternoon, seven hours behind UTC.
        static readonly DateTimeOffset Now =
            new DateTimeOffset(2026, 9, 6, 14, 30, 45, TimeSpan.FromHours(-7));

        /// <summary>
        /// Rows either side of the boundaries a naive range gets wrong: two on the day the range ends,
        /// one on the day it starts, and one the day before that.
        /// </summary>
        static List<Person> Rows() => new()
        {
            new Person { Id = 1, First = "Today", Hired = new DateTime(2026, 9, 6, 9, 0, 0) },
            new Person { Id = 2, First = "Tonight", Hired = new DateTime(2026, 9, 6, 23, 15, 0) },
            new Person { Id = 3, First = "FiveAgo", Hired = new DateTime(2026, 9, 1, 12, 0, 0) },
            new Person { Id = 4, First = "SixAgo", Hired = new DateTime(2026, 8, 31, 0, 0, 0) },
            new Person { Id = 5, First = "SevenAgo", Hired = new DateTime(2026, 8, 30, 23, 59, 0) },
        };

        static RenderFragment TwoColumns() => Columns.Of(
            Columns.Property<Person, string>(x => x.First),
            Columns.Property<Person, DateTime>(x => x.Hired, uniqueId: "Hired"));

        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx, TimeProvider clock,
            IEnumerable<Person>? data = null, FastGridSettings? settings = null)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data ?? Rows());
                p.Add(g => g.ChildContent, TwoColumns());
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.FilterUI, FilterUI.Row);
                p.Add(g => g.Clock, clock);

                if (settings is not null)
                {
                    p.Add(g => g.Settings, settings);
                }
            });
        }

        static ColumnBase<Person> HiredColumn(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindComponents<PropertyColumn<Person, DateTime>>().Single().Instance;

        static string[] Names(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindAll("tbody tr.rz-data-row")
                .Where(row => row.QuerySelectorAll("td").Length > 0)
                .Select(row => row.QuerySelectorAll("td")[0].TextContent)
                .ToArray();

        static FastGridFilter Range(string lower, string upper)
        {
            Assert.True(FastGridRelativeDate.TryParse(lower, out var from));
            Assert.True(FastGridRelativeDate.TryParse(upper, out var to));

            return new FastGridFilter(new FastGridFilterCondition(FastGridFilterOperator.Between,
                new object?[] { from, to }));
        }

        static void Apply(IRenderedComponent<RadzenFastGrid<Person>> cut, FastGridFilter filter) =>
            cut.InvokeAsync(() => cut.Instance.Filter(HiredColumn(cut), filter)).Wait();

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TheLastSevenDaysIsWhatTheClockSaysItIs(bool queryable)
        {
            // Both routes, because they are built by different code and only one of them is exercised
            // by a grid over a List. §33's review is the reason this is a Theory: two routes disagreed
            // about a range and the flat one was on the wire.
            using var ctx = new TestContext();

            var data = queryable ? Rows().AsQueryable() : (IEnumerable<Person>)Rows();
            var cut = Render(ctx, new FixedClock(Now), data);

            Apply(cut, Range("today-6d", "today@end"));

            Assert.Equal(new[] { "Today", "Tonight", "FiveAgo", "SixAgo" }, Names(cut));
        }

        [Fact]
        public void AMidnightUpperBoundWouldHaveDroppedTheWholeOfToday()
        {
            // Why the day part is in the grammar. Between is inclusive at both ends, so the same range
            // written without @end silently answers about six days rather than seven - a wrong answer
            // that reads like a right one.
            using var ctx = new TestContext();

            var cut = Render(ctx, new FixedClock(Now));

            Apply(cut, Range("today-6d", "today"));

            Assert.Equal(new[] { "FiveAgo", "SixAgo" }, Names(cut));
        }

        [Fact]
        public void MovingTheClockMovesTheRows()
        {
            // The whole point of the feature: the filter is not re-authored and the answer changes.
            using var ctx = new TestContext();

            var clock = new FixedClock(Now);
            var cut = Render(ctx, clock);

            Apply(cut, Range("today-6d", "today@end"));

            Assert.Equal(new[] { "Today", "Tonight", "FiveAgo", "SixAgo" }, Names(cut));

            clock.Advance(TimeSpan.FromDays(1));

            cut.InvokeAsync(() => cut.Instance.Reload()).Wait();

            // A day later the window has slid off SixAgo and taken nothing new on.
            Assert.Equal(new[] { "Today", "Tonight", "FiveAgo" }, Names(cut));
        }

        [Fact]
        public void TheDescriptorsCarryDatesAndNotTokens()
        {
            // What a provider, a LoadData handler and the OData string are all built from. None of them
            // can read `today-6d`.
            using var ctx = new TestContext();

            var cut = Render(ctx, new FixedClock(Now));

            Apply(cut, Range("today-6d", "today@end"));

            var bounds = cut.Instance.Filters.Single().Filters!.ToList();

            Assert.Equal(new DateTime(2026, 8, 31), bounds[0].FilterValue);
            Assert.Equal(new DateTime(2026, 9, 7).AddTicks(-1), bounds[1].FilterValue);
            Assert.Equal(FilterOperator.GreaterThanOrEquals, bounds[0].FilterOperator);
            Assert.Equal(FilterOperator.LessThanOrEquals, bounds[1].FilterOperator);
        }

        [Fact]
        public void TheAuthoredFilterKeepsItsTokens()
        {
            // The two names are meant to differ, and this is the difference: the query reads dates while
            // what the user asked for - and what a pill will have to render - is still relative.
            using var ctx = new TestContext();

            var cut = Render(ctx, new FixedClock(Now));

            Apply(cut, Range("today-6d", "today@end"));

            var column = HiredColumn(cut);

            Assert.IsType<FastGridRelativeDate>(column.CurrentFilter!.First.Value);
            Assert.IsType<DateTime>(column.ActiveFilter!.First.Value);
        }

        [Fact]
        public void AFilterWithNoTokensIsTheSameObjectOnBothSides()
        {
            // The allocation gate where a consumer meets it: a grid with no relative filter anywhere
            // must cost what it cost before §34 existed.
            using var ctx = new TestContext();

            var cut = Render(ctx, new FixedClock(Now));

            Apply(cut, new FastGridFilter(new FastGridFilterCondition(
                FastGridFilterOperator.GreaterThanOrEquals, new DateTime(2026, 9, 1))));

            var column = HiredColumn(cut);

            Assert.Same(column.CurrentFilter, column.ActiveFilter);

            // The second half is what stops the first being vacuous, which the review pointed out:
            // ActiveFilter falls back to CurrentFilter, so `Same` passes just as well against a grid
            // that never resolves anything at all. A token in the same place must not be Same.
            Apply(cut, Range("today-6d", "today@end"));

            column = HiredColumn(cut);

            Assert.NotSame(column.CurrentFilter, column.ActiveFilter);
        }

        [Fact]
        public void ATokenIsStoredAsItselfAndRestoredAgainstTheReadingDay()
        {
            // Through a real serializer, for the reason FastGridSettingsRestoreTests gives: the fault
            // this guards against is about what a serializer does to a value.
            using var ctx = new TestContext();

            var written = Render(ctx, new FixedClock(Now));

            Apply(written, Range("today-6d", "today@end"));

            var captured = written.Instance.CaptureSettings();
            var hired = captured.Columns!.Single(c => c.UniqueID == "Hired");

            // Stored as what it says, not as what it currently means.
            Assert.Equal(new[] { "today-6d", "today@end" }, hired.FilterValues!.ToArray());
            Assert.Equal(FastGridFilterOperator.Between, hired.FilterOperator);

            var blob = JsonSerializer.Deserialize<FastGridSettings>(JsonSerializer.Serialize(captured))!;

            using var later = new TestContext();

            // One day on, and the same blob asks a different question - which is the entire reason a
            // preset is not flattened at pick time.
            var restored = Render(later, new FixedClock(Now.AddDays(1)), settings: blob);

            Assert.Equal(new[] { "Today", "Tonight", "FiveAgo" }, Names(restored));

            var column = HiredColumn(restored);

            Assert.IsType<FastGridRelativeDate>(column.CurrentFilter!.First.Value);
        }

        static IElement FilterBox(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindAll("thead tr")[1].QuerySelectorAll("input")[1];

        [Fact]
        public void LeavingTheFilterBoxUntouchedDoesNotDestroyTheFilter()
        {
            // The review measured this one. The box renders the first condition's value, so a relative
            // filter showed `today-6d`; the commit path compared what it was handed against
            // AppliedFilterText, which is null for every filter that did not come from a box. So a blur
            // - or, with FilterAsYouType on, a keystroke and a backspace - re-applied the box's own text
            // as a new filter, and `today-6d` did not parse as a date, so the filter was cleared.
            using var ctx = new TestContext();

            var cut = Render(ctx, new FixedClock(Now));

            Apply(cut, Range("today-6d", "today@end"));

            var box = FilterBox(cut);

            Assert.Equal("today-6d", box.GetAttribute("value"));

            box.Change("today-6d");

            Assert.Equal(new[] { "Today", "Tonight", "FiveAgo", "SixAgo" }, Names(cut));
            Assert.NotNull(HiredColumn(cut).CurrentFilter);
        }

        [Fact]
        public void LeavingAnUntouchedBoxDoesNotNarrowARangeEither()
        {
            // The same fault one step back, and it predates §34: an absolute range showed its lower
            // bound, and the blur committed that as an Equals. §34 only made the same path destroy the
            // filter outright instead of narrowing it, which is how it was found.
            using var ctx = new TestContext();

            var cut = Render(ctx, new FixedClock(Now));

            Apply(cut, new FastGridFilter(new FastGridFilterCondition(FastGridFilterOperator.Between,
                new object?[] { new DateTime(2026, 8, 31), new DateTime(2026, 9, 7).AddTicks(-1) })));

            Assert.Equal(new[] { "Today", "Tonight", "FiveAgo", "SixAgo" }, Names(cut));

            FilterBox(cut).Change(FilterBox(cut).GetAttribute("value"));

            Assert.Equal(FastGridFilterOperator.Between, HiredColumn(cut).CurrentFilter!.First.Operator);
            Assert.Equal(new[] { "Today", "Tonight", "FiveAgo", "SixAgo" }, Names(cut));
        }

        [Fact]
        public void ADateBoxReadsATokenTypedIntoIt()
        {
            // The third reader §34 did not consider. Storage learned `today-6d` and the box did not, so
            // the same six characters meant a date out of a settings blob and nothing at all out of the
            // box beside it - which is a collision between the two vocabularies after all.
            using var ctx = new TestContext();

            var cut = Render(ctx, new FixedClock(Now));

            FilterBox(cut).Change("today-1d");

            var filter = HiredColumn(cut).CurrentFilter;

            Assert.IsType<FastGridRelativeDate>(filter!.First.Value);
            Assert.Equal("today-1d", filter.First.Value!.ToString());

            // And the documented cost, pinned where a user meets it: the box's default operator is
            // Equals, and Equals on a token is midnight exactly - so this matches nothing rather than
            // yesterday's rows. §31's table calls date Equals "whole day"; that is true of a date the
            // user picked and false of a token, and the menu in ④ is what resolves it by writing the
            // Between instead.
            Assert.Empty(Names(cut));
        }

        [Fact]
        public void ANewFilterDoesNotInheritTheOldOnesResolution()
        {
            // The mutation loop found this one: dropping the resolution in SetFilter changed nothing,
            // because every route resolves before it reads. That makes the rule real and untested rather
            // than unnecessary - read ActiveFilter between the two, which is what a FilterTemplate author
            // or a pill would do, and a stale resolution is what answers.
            using var ctx = new TestContext();

            var cut = Render(ctx, new FixedClock(Now));

            Apply(cut, Range("today-6d", "today@end"));

            var column = HiredColumn(cut);
            var absolute = new FastGridFilter(new FastGridFilterCondition(
                FastGridFilterOperator.GreaterThanOrEquals, new DateTime(2026, 9, 1)));

            cut.InvokeAsync(() => column.SetFilter(absolute, text: null)).Wait();

            Assert.Same(absolute, column.ActiveFilter);
        }

        [Fact]
        public void ARerenderWithNoReloadReadsTheClockToo()
        {
            // The draw pass's stamp. An in-memory grid composes as it draws, so a render that follows a
            // clock change - a parent re-rendering, a checkbox toggling - has to see the new day without
            // anything reloading.
            using var ctx = new TestContext();

            var clock = new FixedClock(Now);
            var cut = Render(ctx, clock);

            Apply(cut, Range("today-6d", "today@end"));

            Assert.Equal(new[] { "Today", "Tonight", "FiveAgo", "SixAgo" }, Names(cut));

            clock.Advance(TimeSpan.FromDays(1));

            cut.Render();

            Assert.Equal(new[] { "Today", "Tonight", "FiveAgo" }, Names(cut));
        }

        [Fact]
        public void AStringColumnFilteredToTheLiteralTextSurvivesTheRoundTrip()
        {
            // The collision that cannot happen, asked of a whole grid rather than of the parser.
            using var ctx = new TestContext();

            var rows = Rows();

            rows[0].First = "today-6d";

            var cut = Render(ctx, new FixedClock(Now), rows);
            var first = cut.FindComponents<PropertyColumn<Person, string>>().Single().Instance;

            cut.InvokeAsync(() => cut.Instance.Filter(first, new FastGridFilter(
                new FastGridFilterCondition(FastGridFilterOperator.Equals, "today-6d")))).Wait();

            Assert.Equal(new[] { "today-6d" }, Names(cut));

            var blob = JsonSerializer.Deserialize<FastGridSettings>(
                JsonSerializer.Serialize(cut.Instance.CaptureSettings()))!;

            using var later = new TestContext();

            Assert.Equal(new[] { "today-6d" }, Names(Render(later, new FixedClock(Now), rows, blob)));
        }
    }
}
