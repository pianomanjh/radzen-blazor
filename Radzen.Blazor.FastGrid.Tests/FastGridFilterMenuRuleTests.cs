using System;
using System.Linq;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// §35's two pure rules: which operators a column of a given type offers, and what picking one
    /// writes. Asked of the types directly.
    /// </summary>
    /// <remarks>
    /// §33's review finding is the reason these are not grid tests. "A nullable date offers IsNull" and
    /// "Equals on a date writes a range" are claims about a <see cref="Type" /> and an operator; reaching
    /// them through a rendered panel would test the panel, and would keep passing if the rule moved.
    /// </remarks>
    public class FastGridFilterMenuRuleTests
    {
        static FastGridFilterOperator[] Menu<T>(bool nullable = false) =>
            FastGridFilterOperators.Menu(typeof(T), nullable);

        [Fact]
        public void AStringOffersTheTextOperatorsAndNoRange()
        {
            var offered = Menu<string>();

            Assert.Contains(FastGridFilterOperator.Contains, offered);
            Assert.Contains(FastGridFilterOperator.StartsWith, offered);
            Assert.Contains(FastGridFilterOperator.IsEmpty, offered);
            Assert.DoesNotContain(FastGridFilterOperator.Between, offered);
            Assert.DoesNotContain(FastGridFilterOperator.GreaterThan, offered);
        }

        [Fact]
        public void ANumberOffersOrderingAndARange()
        {
            var offered = Menu<int>();

            Assert.Contains(FastGridFilterOperator.Between, offered);
            Assert.Contains(FastGridFilterOperator.LessThanOrEquals, offered);
            Assert.DoesNotContain(FastGridFilterOperator.Contains, offered);
        }

        [Theory]
        [InlineData(typeof(DateTime))]
        [InlineData(typeof(DateTimeOffset))]
        [InlineData(typeof(DateOnly))]
        public void EveryDateTypeOffersTheSameFour(Type type)
        {
            var offered = FastGridFilterOperators.Menu(type, nullable: false);

            Assert.Equal(
                new[]
                {
                    FastGridFilterOperator.Equals,
                    FastGridFilterOperator.LessThan,
                    FastGridFilterOperator.GreaterThan,
                    FastGridFilterOperator.Between,
                },
                offered);
        }

        [Fact]
        public void ABoolOffersOnlyEquality()
        {
            Assert.Equal(new[] { FastGridFilterOperator.Equals }, Menu<bool>());
        }

        [Fact]
        public void AnEnumIsEditedAsASet()
        {
            Assert.Equal(
                new[] { FastGridFilterOperator.In, FastGridFilterOperator.NotIn },
                Menu<DayOfWeek>());
        }

        [Fact]
        public void ATypeThatSaysNothingGetsOnlyTheTwoThatMeanSomethingForAnything()
        {
            // PropertyColumn<T, object>, or a template column whose path does not resolve. Offering it
            // Contains would put a substring match on whatever the rows actually hold.
            Assert.Equal(
                new[] { FastGridFilterOperator.Equals, FastGridFilterOperator.NotEquals },
                Menu<object>());
        }

        [Fact]
        public void ANullableColumnGetsTheTwoAbsenceOperatorsLast()
        {
            var offered = Menu<int?>();

            Assert.Equal(FastGridFilterOperator.IsNull, offered[^2]);
            Assert.Equal(FastGridFilterOperator.IsNotNull, offered[^1]);

            // The underlying type still decides everything before them.
            Assert.Contains(FastGridFilterOperator.Between, offered);
        }

        [Fact]
        public void AReferenceTypeIsNullableOnlyWhenTheColumnSaysSo()
        {
            Assert.DoesNotContain(FastGridFilterOperator.IsNull, Menu<string>());
            Assert.Contains(FastGridFilterOperator.IsNull, Menu<string>(nullable: true));
        }

        [Fact]
        public void NoOfferedOperatorIsTheOneTheMenuCannotEdit()
        {
            // Custom is carried to a LoadData handler and composes no predicate here - §33. A menu that
            // offered it would be offering the user a filter the grid cannot apply.
            foreach (var type in new[]
                     {
                         typeof(string), typeof(int), typeof(DateTime), typeof(bool), typeof(DayOfWeek),
                         typeof(object), typeof(Guid), typeof(TimeSpan),
                     })
            {
                Assert.DoesNotContain(FastGridFilterOperator.Custom,
                    FastGridFilterOperators.Menu(type, nullable: true));
            }
        }

        // ---- what picking one writes ----

        static FastGridFilterCondition Written(FastGridFilterOperator filterOperator, object? value,
            object? second = null, Type? type = null)
        {
            var filter = FilterMenu.Compose(filterOperator, value, second, type ?? typeof(DateTime));

            Assert.NotNull(filter);

            return filter!.First;
        }

        [Fact]
        public void EqualsOnADateIsWrittenAsTheWholeDay()
        {
            // §31's table says whole day; §34 made a single instant mean midnight exactly. The menu is
            // where the two are reconciled, and this is the reconciliation.
            var written = Written(FastGridFilterOperator.Equals, new DateTime(2026, 3, 5, 14, 30, 0));

            Assert.Equal(FastGridFilterOperator.Between, written.Operator);
            Assert.Equal(new DateTime(2026, 3, 5), written.Value);
            Assert.Equal(new DateTime(2026, 3, 5, 23, 59, 59, 999).AddTicks(9999), written.SecondValue);
        }

        [Fact]
        public void ARangeReachesTheEndOfItsLastDay()
        {
            // The failure §34 built `@end` to prevent, one layer up: a range ending at midnight drops
            // everything that happened on its own last day.
            var written = Written(FastGridFilterOperator.Between,
                new DateTime(2026, 1, 1), new DateTime(2026, 3, 31));

            Assert.Equal(new DateTime(2026, 1, 1), written.Value);
            Assert.Equal(new DateTime(2026, 3, 31, 23, 59, 59, 999).AddTicks(9999), written.SecondValue);
        }

        [Fact]
        public void AfterADateMeansAfterThatDayEnds()
        {
            var written = Written(FastGridFilterOperator.GreaterThan, new DateTime(2026, 3, 5));

            Assert.Equal(FastGridFilterOperator.GreaterThan, written.Operator);
            Assert.Equal(new DateTime(2026, 3, 5, 23, 59, 59, 999).AddTicks(9999), written.Value);
        }

        [Fact]
        public void BeforeADateMeansBeforeThatDayStarts()
        {
            var written = Written(FastGridFilterOperator.LessThan, new DateTime(2026, 3, 5, 14, 0, 0));

            Assert.Equal(FastGridFilterOperator.LessThan, written.Operator);
            Assert.Equal(new DateTime(2026, 3, 5), written.Value);
        }

        [Fact]
        public void AWholeDayRuleOverATokenSetsItsDayPartRatherThanItsInstant()
        {
            // The token stays a token: resolution is still §34's job, and the menu only says which end
            // of the day it means.
            var written = Written(FastGridFilterOperator.Equals,
                new FastGridRelativeDate(FastGridRelativeDateAnchor.Today));

            var lower = Assert.IsType<FastGridRelativeDate>(written.Value);
            var upper = Assert.IsType<FastGridRelativeDate>(written.SecondValue);

            Assert.False(lower.EndOfDay);
            Assert.True(upper.EndOfDay);
            Assert.Equal(FastGridRelativeDateAnchor.Today, upper.Anchor);
        }

        [Fact]
        public void ADateOnlyHasNoEndOfDayToReach()
        {
            // §34's rule, and it has to be the same no-op here or a DateOnly column would have two
            // vocabularies that disagree about what @end does.
            var written = Written(FastGridFilterOperator.Equals, new DateOnly(2026, 3, 5),
                type: typeof(DateOnly));

            Assert.Equal(new DateOnly(2026, 3, 5), written.Value);
            Assert.Equal(new DateOnly(2026, 3, 5), written.SecondValue);
        }

        [Fact]
        public void ANonDateColumnIsWrittenLiterally()
        {
            var written = Written(FastGridFilterOperator.Equals, 5, type: typeof(int));

            Assert.Equal(FastGridFilterOperator.Equals, written.Operator);
            Assert.Equal(5, written.Value);
        }

        [Fact]
        public void AHalfTypedRangeClearsRatherThanFiltersToNothing()
        {
            Assert.Null(FilterMenu.Compose(FastGridFilterOperator.Between, 5, null, typeof(int)));
            Assert.Null(FilterMenu.Compose(FastGridFilterOperator.Equals, null, null, typeof(int)));
        }

        [Fact]
        public void AnOperatorThatTakesNoValueIsAFilterOnItsOwn()
        {
            var filter = FilterMenu.Compose(FastGridFilterOperator.IsNull, null, null, typeof(int?));

            Assert.NotNull(filter);
            Assert.Equal(FastGridFilterOperator.IsNull, filter!.First.Operator);
            Assert.Empty(filter.First.Values);
        }

        // ---- presets ----

        [Fact]
        public void EveryPresetIsARangeOfTwoTokensAndIsRecognisedBack()
        {
            foreach (var preset in FastGridFilterPresets.All)
            {
                var filter = preset.Filter();

                Assert.Equal(FastGridFilterOperator.Between, filter.First.Operator);
                Assert.IsType<FastGridRelativeDate>(filter.First.Value);
                Assert.IsType<FastGridRelativeDate>(filter.First.SecondValue);

                Assert.Equal(preset, FastGridFilterPresets.Recognize(filter));
            }
        }

        [Fact]
        public void ThePresetsAreTheSixTokenPairsTheDesignTabulated()
        {
            Assert.Equal("today", Text(FastGridFilterPreset.Today).Lower);
            Assert.Equal("today@end", Text(FastGridFilterPreset.Today).Upper);
            Assert.Equal(("today-1d", "today-1d@end"), Text(FastGridFilterPreset.Yesterday));
            Assert.Equal(("today-6d", "today@end"), Text(FastGridFilterPreset.Last7Days));
            Assert.Equal(("today-29d", "today@end"), Text(FastGridFilterPreset.Last30Days));
            Assert.Equal(("month-start", "today@end"), Text(FastGridFilterPreset.ThisMonth));
            Assert.Equal(("year-start", "today@end"), Text(FastGridFilterPreset.ThisYear));

            static (string Lower, string Upper) Text(FastGridFilterPreset preset)
            {
                var (lower, upper) = preset.Range();

                return (lower.ToString(), upper.ToString());
            }
        }

        [Fact]
        public void NoTwoPresetsAreTheSameRange()
        {
            var ranges = FastGridFilterPresets.All
                .Select(preset => preset.Range())
                .Select(range => range.Lower + ".." + range.Upper)
                .ToArray();

            Assert.Equal(ranges.Length, ranges.Distinct().Count());
        }

        [Fact]
        public void ARangeOfAbsoluteDatesIsNotAPreset()
        {
            // The recognition is on the tokens, not on what they resolve to. Two dates that happen to
            // span the last seven days today will not mean that tomorrow, and calling them the preset
            // is the silent staleness §34 exists to prevent.
            var absolute = new FastGridFilter(new FastGridFilterCondition(
                FastGridFilterOperator.Between,
                new object?[] { DateTime.Today.AddDays(-6), DateTime.Today }));

            Assert.Null(FastGridFilterPresets.Recognize(absolute));
        }

        [Fact]
        public void ATokenRangeThatIsNotOneOfTheSixIsNotAPreset()
        {
            var ninety = new FastGridFilter(new FastGridFilterCondition(
                FastGridFilterOperator.Between,
                new object?[]
                {
                    new FastGridRelativeDate(FastGridRelativeDateAnchor.Today, -89,
                        FastGridRelativeDateUnit.Days, endOfDay: false),
                    new FastGridRelativeDate(FastGridRelativeDateAnchor.Today, 0,
                        FastGridRelativeDateUnit.Days, endOfDay: true),
                }));

            Assert.Null(FastGridFilterPresets.Recognize(ninety));
        }

        [Fact]
        public void ACompoundIsNotAPresetEvenWhenItsFirstHalfIsOne()
        {
            var compound = new FastGridFilter(
                FastGridFilterPreset.Last7Days.Filter().First,
                new FastGridFilterCondition(FastGridFilterOperator.IsNull),
                LogicalFilterOperator.Or);

            Assert.Null(FastGridFilterPresets.Recognize(compound));
        }

        [Fact]
        public void NothingRecognisesAsAPreset()
        {
            Assert.Null(FastGridFilterPresets.Recognize(null));
            Assert.Null(FastGridFilterPresets.Recognize(new FastGridFilter(
                new FastGridFilterCondition(FastGridFilterOperator.Equals, DateTime.Today))));
        }
    }
}
