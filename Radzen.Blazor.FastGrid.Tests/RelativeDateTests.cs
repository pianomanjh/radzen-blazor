using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// §34's token: its grammar, what it resolves to, and the one rule that keeps it a value.
    /// </summary>
    /// <remarks>
    /// Asked directly, against a fixed instant. Everything here is a pure rule, and §33's review finding
    /// is why it is tested as one - <em>a test one layer above a rule is not a test of the rule</em> - so
    /// a test that reaches `today-6d` through a rendered grid is a test of the grid.
    /// <see cref="RelativeDateGridTests" /> is what covers the layer above.
    /// </remarks>
    public class RelativeDateTests
    {
        // A Sunday afternoon, seven hours behind UTC. Afternoon because a token that resolved to `now`
        // rather than to the start of its day would pass every midnight-stamped assertion below.
        static readonly DateTimeOffset Now =
            new DateTimeOffset(2026, 9, 6, 14, 30, 45, TimeSpan.FromHours(-7));

        static FastGridRelativeDate Parse(string text)
        {
            Assert.True(FastGridRelativeDate.TryParse(text, out var token), text);

            return token!;
        }

        static DateTime Resolved(string text) =>
            (DateTime)Parse(text).Resolve(typeof(DateTime), Now);

        [Theory]
        [InlineData("today")]
        [InlineData("today@end")]
        [InlineData("today-6d")]
        [InlineData("today+1d")]
        [InlineData("today-29d@end")]
        [InlineData("month-start")]
        [InlineData("month-start-1m")]
        [InlineData("year-start+1y")]
        [InlineData("year-start@end")]
        public void TheCanonicalTextRoundTrips(string text) =>
            Assert.Equal(text, Parse(text).ToString());

        [Theory]
        // A missing unit, a digit-less offset, an unsigned one, a doubled sign.
        [InlineData("today-6")]
        [InlineData("today6d")]
        [InlineData("today-d")]
        [InlineData("today+-6d")]
        // A unit that is not one. §34 refuses `w` because it is `7d` spelled twice.
        [InlineData("today-1w")]
        // An anchor that is not one, and a bare offset with none.
        [InlineData("tomorrow")]
        [InlineData("yesterday")]
        [InlineData("-6d")]
        [InlineData("month")]
        // Trailing rubbish after an otherwise valid token, and a day part with nothing before it.
        [InlineData("today-6dx")]
        [InlineData("@end")]
        [InlineData("todayend")]
        // The other vocabulary. A stored date must never read as a token, which is the whole of why the
        // two cannot collide inside one date column.
        [InlineData("2019-05-04T00:00:00.0000000")]
        [InlineData("2019-05-04")]
        [InlineData("")]
        [InlineData(null)]
        public void WhatIsNotAToken(string? text)
        {
            Assert.False(FastGridRelativeDate.TryParse(text, out var token));
            Assert.Null(token);
        }

        [Fact]
        public void CaseIsAcceptedOnTheWayInAndNotProducedOnTheWayOut()
        {
            // Lenient at the boundary so `Today` written by hand in markup is understood; one spelling
            // in a stored blob because ToString is the only thing that writes one.
            Assert.Equal("today-6d@end", Parse("TODAY-6D@END").ToString());
            Assert.Equal(Parse("today-6d@end"), Parse("Today-6d@End"));
        }

        [Fact]
        public void AZeroOffsetHasNoUnitToDisagreeAbout()
        {
            // Otherwise today+0d and today+0m would be two values meaning one instant, which is the
            // second-spelling problem §34 refuses a `w` unit over.
            var days = new FastGridRelativeDate(FastGridRelativeDateAnchor.Today, 0,
                FastGridRelativeDateUnit.Days, endOfDay: false);
            var months = new FastGridRelativeDate(FastGridRelativeDateAnchor.Today, 0,
                FastGridRelativeDateUnit.Months, endOfDay: false);

            Assert.Equal(days, months);
            Assert.Equal("today", months.ToString());
        }

        [Fact]
        public void AnAnchorIsTheStartOfItsDayAndNotTheMomentItIsRead()
        {
            Assert.Equal(new DateTime(2026, 9, 6), Resolved("today"));
            Assert.Equal(new DateTime(2026, 9, 1), Resolved("month-start"));
            Assert.Equal(new DateTime(2026, 1, 1), Resolved("year-start"));
        }

        [Fact]
        public void TheDayPartIsTheLastTickOfTheDay()
        {
            // Between is inclusive at both ends, so a range ending at midnight drops the whole of its
            // last day. A tick rather than 23:59:59.999, because a DateTime is ticks and the rounder
            // form leaves a window rows can fall into.
            Assert.Equal(new DateTime(2026, 9, 6, 23, 59, 59).AddTicks(9_999_999), Resolved("today@end"));
            Assert.Equal(new DateTime(2026, 9, 7).AddTicks(-1), Resolved("today@end"));
        }

        [Fact]
        public void TheOffsetCountsFromTheAnchorAndNotFromNow()
        {
            Assert.Equal(new DateTime(2026, 8, 31), Resolved("today-6d"));
            Assert.Equal(new DateTime(2026, 8, 8), Resolved("today-29d"));
            Assert.Equal(new DateTime(2026, 8, 1), Resolved("month-start-1m"));
            Assert.Equal(new DateTime(2025, 1, 1), Resolved("year-start-1y"));
            Assert.Equal(new DateTime(2026, 9, 7), Resolved("today+1d"));
        }

        [Fact]
        public void AMonthOffsetClamps()
        {
            // 31 March less one month is 28 February, which is AddMonths' answer. The alternatives are
            // throwing on one day in twelve and skipping silently, and both are worse.
            var march = new DateTimeOffset(2026, 3, 31, 9, 0, 0, TimeSpan.Zero);

            Assert.Equal(new DateTime(2026, 2, 28), Parse("today-1m").Resolve(typeof(DateTime), march));
        }

        [Fact]
        public void AnOffsetOffTheCalendarClampsRatherThanThrows()
        {
            // Throwing would put an exception inside BuildRenderTree, which §32 established is a dead
            // circuit on Blazor Server that the application never sees.
            var far = new FastGridRelativeDate(FastGridRelativeDateAnchor.Today, int.MinValue,
                FastGridRelativeDateUnit.Days, endOfDay: false);
            var further = new FastGridRelativeDate(FastGridRelativeDateAnchor.Today, int.MaxValue,
                FastGridRelativeDateUnit.Years, endOfDay: true);

            Assert.Equal(DateTime.MinValue, far.Resolve(typeof(DateTime), Now));
            Assert.Equal(DateTime.MaxValue, further.Resolve(typeof(DateTime), Now));
        }

        [Theory]
        [InlineData(9)]
        [InlineData(-7)]
        [InlineData(0)]
        public void TheClampSurvivesTheOffsetColumnToo(int hours)
        {
            // The review found the clamp undone one line later: DateTimeOffset(DateTime, TimeSpan)
            // throws when the instant less the offset leaves the calendar, so a clamped MinValue threw
            // for every clock east of UTC and a clamped MaxValue for every clock west of it. The old
            // test picked the two arms that cannot throw - a DateTime column, and one negative offset.
            var stamp = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.FromHours(hours));

            var far = new FastGridRelativeDate(FastGridRelativeDateAnchor.Today, int.MinValue,
                FastGridRelativeDateUnit.Days, endOfDay: false);
            var further = new FastGridRelativeDate(FastGridRelativeDateAnchor.Today, int.MaxValue,
                FastGridRelativeDateUnit.Years, endOfDay: true);

            // The assertion is that neither throws; the instants themselves are whatever the calendar's
            // edge is at this offset, and pinning those would be pinning the clamp's arithmetic twice.
            Assert.IsType<DateTimeOffset>(far.Resolve(typeof(DateTimeOffset), stamp));
            Assert.IsType<DateTimeOffset>(further.Resolve(typeof(DateTimeOffset), stamp));
        }

        [Fact]
        public void ADateTimeCarriesNoKindOntoTheWire()
        {
            // Comparison ignores Kind, but "O" does not: a Local instant renders an offset into a string
            // a server then has to parse, and §33's review measured what a misread offset costs.
            Assert.Equal(DateTimeKind.Unspecified, Resolved("today").Kind);
            Assert.Equal(DateTimeKind.Unspecified, Resolved("month-start-1m").Kind);
        }

        [Fact]
        public void ItResolvesToTheColumnsOwnType()
        {
            // So that what reaches FilterExpression and the descriptors cannot be told from a value
            // somebody typed.
            Assert.Equal(new DateOnly(2026, 9, 6), Parse("today").Resolve(typeof(DateOnly), Now));
            Assert.Equal(new DateTime(2026, 8, 31), Parse("today-6d").Resolve(typeof(DateTime?), Now));

            Assert.Equal(new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.FromHours(-7)),
                Parse("today").Resolve(typeof(DateTimeOffset), Now));
        }

        [Fact]
        public void ADateOnlyHasNoEndOfDayToReach()
        {
            // A no-op rather than an error: the menu writes the same pair for every date column, and a
            // DateOnly range wants both bounds to be days.
            Assert.Equal(new DateOnly(2026, 9, 6), Parse("today@end").Resolve(typeof(DateOnly), Now));
        }

        [Fact]
        public void ADateTimeOffsetTakesTheStampsZoneAndNotTheMachines()
        {
            // The clock the grid was given is what decides which zone "today" is in. Asking
            // TimeZoneInfo.Local here would put a second answer beside it.
            var tokyo = new DateTimeOffset(2026, 9, 6, 9, 0, 0, TimeSpan.FromHours(9));

            Assert.Equal(new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.FromHours(9)),
                Parse("today").Resolve(typeof(DateTimeOffset), tokyo));
        }

        [Fact]
        public void OnlyADateColumnCanHoldOne()
        {
            Assert.True(FastGridRelativeDate.AppliesTo(typeof(DateTime)));
            Assert.True(FastGridRelativeDate.AppliesTo(typeof(DateTime?)));
            Assert.True(FastGridRelativeDate.AppliesTo(typeof(DateTimeOffset)));
            Assert.True(FastGridRelativeDate.AppliesTo(typeof(DateOnly)));

            Assert.False(FastGridRelativeDate.AppliesTo(typeof(string)));
            Assert.False(FastGridRelativeDate.AppliesTo(typeof(int)));
            Assert.False(FastGridRelativeDate.AppliesTo(typeof(object)));

            // TimeOnly and TimeSpan are times of day rather than days, so an anchor says nothing here.
            Assert.False(FastGridRelativeDate.AppliesTo(typeof(TimeOnly)));
            Assert.False(FastGridRelativeDate.AppliesTo(typeof(TimeSpan)));
        }

        // ---- the filter-level rule -------------------------------------------------------------

        static FastGridFilter Range(string lower, string upper) =>
            new FastGridFilter(new FastGridFilterCondition(FastGridFilterOperator.Between,
                new object?[] { Parse(lower), Parse(upper) }));

        static FastGridFilter Between(object lower, object upper) =>
            new FastGridFilter(new FastGridFilterCondition(FastGridFilterOperator.Between,
                new[] { lower, upper }));

        [Fact]
        public void AFilterHoldingNoTokensResolvesToItself()
        {
            // The allocation gate as a test, and the one §33's review paid for: the common case is a
            // grid with no relative filter anywhere and it must cost what it costs today. Reference
            // equality is the assertion because "the same filter" is the claim.
            var absolute = Between(new DateTime(2019, 1, 1), new DateTime(2020, 1, 1));

            Assert.Same(absolute, FilterResolution.Resolve(absolute, typeof(DateTime), Now));
        }

        [Fact]
        public void ANonDateColumnIsNotEvenWalked()
        {
            // A token on an int column stays a token rather than becoming a DateTime in an int's slot.
            // The first assertion alone could not tell the guard from walking and finding nothing, which
            // the review pointed out - and the guard is what the allocation gate rests on.
            var numbers = Between(1, 5);

            Assert.Same(numbers, FilterResolution.Resolve(numbers, typeof(int), Now));

            var confused = Between(Parse("today"), 5);

            Assert.Same(confused, FilterResolution.Resolve(confused, typeof(int), Now));
            Assert.IsType<FastGridRelativeDate>(
                FilterResolution.Resolve(confused, typeof(int), Now).First.Value);
        }

        [Fact]
        public void ARangeOfTwoTokensResolvesBothAndStaysARange()
        {
            // The operator and its arity are what §34 promises not to disturb - if either moved, the
            // token would have stopped being a value.
            var relative = Between(Parse("today-6d"), Parse("today@end"));
            var resolved = FilterResolution.Resolve(relative, typeof(DateTime), Now);

            Assert.NotSame(relative, resolved);
            Assert.Equal(FastGridFilterOperator.Between, resolved.First.Operator);
            Assert.Equal(2, resolved.First.Values.Count);
            Assert.Equal(new DateTime(2026, 8, 31), resolved.First.Value);
            Assert.Equal(new DateTime(2026, 9, 7).AddTicks(-1), resolved.First.SecondValue);
        }

        [Fact]
        public void OneTokenBesideOneAbsoluteBoundResolvesOnlyTheToken()
        {
            var mixed = Between(new DateTime(2019, 1, 1), Parse("today@end"));
            var resolved = FilterResolution.Resolve(mixed, typeof(DateTime), Now);

            Assert.Equal(new DateTime(2019, 1, 1), resolved.First.Value);
            Assert.Equal(new DateTime(2026, 9, 7).AddTicks(-1), resolved.First.SecondValue);
        }

        [Fact]
        public void TheSecondConditionResolvesToo()
        {
            // Otherwise "before this month, or blank" would be half read - and §33's rule is that a
            // compound is right in both halves or not carried at all.
            var compound = new FastGridFilter(
                new FastGridFilterCondition(FastGridFilterOperator.IsNull),
                new FastGridFilterCondition(FastGridFilterOperator.GreaterThanOrEquals,
                    Parse("month-start")),
                LogicalFilterOperator.Or);

            var resolved = FilterResolution.Resolve(compound, typeof(DateTime), Now);

            Assert.Equal(FastGridFilterOperator.IsNull, resolved.First.Operator);
            Assert.Equal(new DateTime(2026, 9, 1), resolved.Second!.Value);
            Assert.Equal(LogicalFilterOperator.Or, resolved.LogicalFilterOperator);
        }

        [Fact]
        public void AnInIsLeftAlone()
        {
            // A list of dates has no token in it to find, so this half passes whether or not the arity
            // guard exists - which the review pointed out. The second half is the guard: a token sitting
            // directly in an In's values is left as one.
            var picked = new FastGridFilter(new FastGridFilterCondition(FastGridFilterOperator.In,
                new object?[] { new List<DateTime> { new DateTime(2019, 5, 4) } }));

            Assert.Same(picked, FilterResolution.Resolve(picked, typeof(DateTime), Now));

            var declared = new FastGridFilter(
                new FastGridFilterCondition(FastGridFilterOperator.In, Parse("today")));

            Assert.Same(declared, FilterResolution.Resolve(declared, typeof(DateTime), Now));
            Assert.IsType<FastGridRelativeDate>(declared.First.Value);
        }

        [Fact]
        public void WhetherAFilterWouldReadDifferentlyLater()
        {
            Assert.False(FilterResolution.IsRelative(null));
            Assert.False(FilterResolution.IsRelative(
                Between(new DateTime(2019, 1, 1), new DateTime(2020, 1, 1))));
            Assert.True(FilterResolution.IsRelative(Between(Parse("today-6d"), Parse("today@end"))));
        }

        [Fact]
        public void ACachedTotalOutlivesTheDayOnlyWhenSomethingIsRelative()
        {
            // The rule behind DropStaleTotal, pulled out of the virtualized provider so a test can reach
            // it at all. The mutation loop is why it is out here: with the comparison inline, changing it
            // left 990 tests passing, because bUnit never runs Virtualize's items provider.
            using var bench = new FilterBench();

            var absolute = bench.Hired(new FastGridFilter(new FastGridFilterCondition(
                FastGridFilterOperator.GreaterThanOrEquals, new DateTime(2026, 9, 1))));
            var relative = bench.Hired(Range("today-6d", "today@end"));

            var yesterday = Now.Date.AddDays(-1);

            // The day has not turned: nothing can have been outlived.
            Assert.False(Composition.OutlivedTheDay(new[] { relative }, Now.Date, Now));

            // It has turned, but nothing on the grid reads a clock.
            Assert.False(Composition.OutlivedTheDay(new[] { absolute }, yesterday, Now));

            // It has turned and something does.
            Assert.True(Composition.OutlivedTheDay(new[] { relative }, yesterday, Now));
            Assert.True(Composition.OutlivedTheDay(new[] { absolute, relative }, yesterday, Now));
        }

        [Fact]
        public void TheQueryableRouteResolvesWithoutTheDescriptorsBeingBuilt()
        {
            // Composition.Filter is reached directly by an async source's page query and by the count
            // beside it - neither of which builds a descriptor first. The mutation loop found that the
            // resolve call there was covered only through the render path, which does build them.
            using var bench = new FilterBench();

            var column = bench.Hired(Range("today-6d", "today@end"));

            var people = new[]
            {
                new Person { Id = 1, Hired = new DateTime(2026, 9, 6, 9, 0, 0) },
                new Person { Id = 2, Hired = new DateTime(2026, 8, 31) },
                new Person { Id = 3, Hired = new DateTime(2026, 8, 30) },
            };

            var options = new CompositionOptions(true, FilterCaseSensitivity.Default,
                LogicalFilterOperator.And, Now);

            var kept = Composition.Filter(new[] { column }, people.AsQueryable(), options)
                .Select(p => p.Id).ToArray();

            Assert.Equal(new[] { 1, 2 }, kept);
        }

        /// <summary>A renderer and a grid, so a column can be born and given a filter.</summary>
        sealed class FilterBench : IDisposable
        {
            readonly Bunit.TestContext ctx = new Bunit.TestContext();
            readonly RadzenFastGrid<Person> grid = new RadzenFastGrid<Person>();

            internal ColumnBase<Person> Hired(FastGridFilter filter)
            {
                var column = ctx.RenderComponent<PropertyColumn<Person, DateTime>>(p =>
                {
                    p.AddCascadingValue(grid);
                    p.Add(c => c.Property, (System.Linq.Expressions.Expression<Func<Person, DateTime>>)
                        (x => x.Hired));
                }).Instance;

                column.SetFilter(filter, text: null);

                return column;
            }

            public void Dispose() => ctx.Dispose();
        }

        // ---- storage ---------------------------------------------------------------------------

        [Fact]
        public void ATokenIsStoredAsWhatItSaysRatherThanWhatItMeans()
        {
            // The whole point of it being read at query time. A blob written in March that stored two
            // dates would come back in September still asking about March.
            Assert.Equal("today-6d", FilterValueText.From(Parse("today-6d")));
            Assert.Equal("month-start@end", FilterValueText.From(Parse("month-start@end")));
        }

        [Fact]
        public void ATokenComesBackATokenOnADateColumn()
        {
            Assert.Equal(Parse("today-6d"), FilterValueText.To("today-6d", typeof(DateTime)));
            Assert.Equal(Parse("today"), FilterValueText.To("today", typeof(DateTime?)));
            Assert.Equal(Parse("year-start"), FilterValueText.To("year-start", typeof(DateOnly)));
        }

        [Fact]
        public void AStringColumnFilteredToTheLiteralTextIsUntouched()
        {
            // Why the two vocabularies cannot collide: a token is only ever looked for on a date column.
            Assert.Equal("today-6d", FilterValueText.To("today-6d", typeof(string)));
            Assert.Equal("today", FilterValueText.To("today", typeof(object)));
        }

        [Fact]
        public void AStoredDateStillReadsAsADate()
        {
            Assert.Equal(new DateTime(2019, 5, 4),
                FilterValueText.To("2019-05-04T00:00:00.0000000", typeof(DateTime)));
        }

        [Fact]
        public void AnInListRefusesTokens()
        {
            // Its values are collected into a list typed to the column so a provider can translate
            // Contains, and a token does not fit one. Refused rather than added and thrown on.
            Assert.Null(FilterValueText.To("today", typeof(DateTime), relative: false));
            Assert.Equal(new DateTime(2019, 5, 4),
                FilterValueText.To("2019-05-04T00:00:00.0000000", typeof(DateTime), relative: false));
        }
    }
}
