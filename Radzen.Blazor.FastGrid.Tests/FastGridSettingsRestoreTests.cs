using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// What comes back when a settings blob has been through a serializer. §32's subject, answered
    /// differently by §33.
    /// </summary>
    /// <remarks>
    /// §32 stored values as <c>object?</c> and spent four heuristic attempts guessing the type back out
    /// of whatever the serializer had left - and still could not rebuild a check-box list. §33 stores
    /// canonical text instead, so the round trip is lossless by construction and the way back is one
    /// parse against the column's own type. These tests were written for the first design and are kept
    /// pointed at the same outcomes, because the outcomes are what a consumer sees; where §33 changed an
    /// answer rather than a mechanism, the test says so.
    /// <para>
    /// The round trip is a real <c>JsonSerializer</c> rather than a hand-built blob, deliberately: the
    /// fault this guards against is about what a serializer does, and a fixture that constructs the
    /// stored form itself is a test agreeing with the diagnosis rather than with the consumer.
    /// </para>
    /// </remarks>
    public class FastGridSettingsRestoreTests
    {
        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx, RenderFragment columns,
            FastGridSettings? settings = null, IEnumerable<Person>? data = null)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data ?? People.Sample());
                p.Add(g => g.ChildContent, columns);
                p.Add(g => g.AllowFiltering, true);

                if (settings is not null)
                {
                    p.Add(g => g.Settings, settings);
                }
            });
        }

        static string[] Names(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindAll("tbody tr")
                .Where(row => row.QuerySelectorAll("td").Length > 0)
                .Select(row => row.QuerySelectorAll("td")[0].TextContent)
                .ToArray();

        /// <summary>Through a real serializer, which is the whole point of the format being text.</summary>
        static FastGridSettings Stored(params FastGridColumnSettings[] columns) =>
            JsonSerializer.Deserialize<FastGridSettings>(
                JsonSerializer.Serialize(new FastGridSettings { Columns = columns.ToList() }))!;

        static FastGridColumnSettings Filter(string id, FastGridFilterOperator op, params string?[] values) =>
            new() { UniqueID = id, FilterOperator = op, FilterValues = values };

        static RenderFragment AllTypes() => Columns.Of(
            Columns.Property<Person, string>(x => x.First),
            Columns.Property<Person, DateTime>(x => x.Hired),
            Columns.Property<Person, int>(x => x.Id),
            Columns.Property<Person, Grade>(x => x.Grade),
            Columns.Property<Person, Guid>(x => x.Reference),
            Columns.Property<Person, decimal?>(x => x.Bonus));

        static RenderFragment WithLookup() => Columns.Of(
            Columns.Property<Person, string>(x => x.First),
            Columns.Lookup<Person, int>(x => x.CategoryId, FastGridLookup.Map(Lookups.Categories()),
                uniqueId: "Category"));

        [Theory]
        [InlineData("Hired", FastGridFilterOperator.Equals, "2019-05-04T00:00:00.0000000", new[] { "Carol" })]
        [InlineData("Id", FastGridFilterOperator.Equals, "3", new[] { "Carol" })]
        [InlineData("Grade", FastGridFilterOperator.Equals, "Senior", new[] { "Carol", "Bob" })]
        [InlineData("First", FastGridFilterOperator.Contains, "Car", new[] { "Carol" })]
        [InlineData("Bonus", FastGridFilterOperator.GreaterThan, "100", new[] { "Carol" })]
        public void ARoundTrippedFilterStillFilters(string id, FastGridFilterOperator op, string value,
            string[] expected)
        {
            // Every one of these terminated the circuit before §32 and needed a heuristic after it.
            // Through text they are a parse.
            using var ctx = new TestContext();

            Assert.Equal(expected, Names(Render(ctx, AllTypes(), Stored(Filter(id, op, value)))));
        }

        [Fact]
        public void AGuidFilterSurvivesTheRoundTrip()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes(), Stored(Filter("Reference", FastGridFilterOperator.Equals,
                People.Sample()[0].Reference.ToString())));

            Assert.Equal(new[] { "Carol" }, Names(cut));
        }

        [Fact]
        public void ACheckBoxListFilterSurvivesTheRoundTrip()
        {
            // §32's stated hole, closed. Stored as object? an In list came back from System.Text.Json as
            // one opaque JsonElement that nothing could rebuild, so the filter was dropped and the grid
            // showed every row. One text per ticked box survives any serializer there is.
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes(),
                Stored(Filter("Id", FastGridFilterOperator.In, "3", "1")));

            Assert.Equal(new[] { "Carol", "Alice" }, Names(cut));
        }

        [Fact]
        public void ABetweenSurvivesTheRoundTrip()
        {
            // The operator §31 could not have, because upstream has no value for it - so a range had to
            // be spent as the model's second condition. Here it is one condition of arity two.
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes(),
                Stored(Filter("Id", FastGridFilterOperator.Between, "2", "3")));

            Assert.Equal(new[] { "Carol", "Bob" }, Names(cut));
        }

        [Fact]
        public void ACompoundSurvivesTheRoundTrip()
        {
            // §31's worked case, and the only reason the model is two conditions wide: "these values or
            // blank" on a nullable column, which no single condition says.
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes(), Stored(new FastGridColumnSettings
            {
                UniqueID = "Bonus",
                FilterOperator = FastGridFilterOperator.In,
                FilterValues = new string?[] { "250.5" },
                SecondFilterOperator = FastGridFilterOperator.IsNull,
                SecondFilterValues = Array.Empty<string?>(),
                LogicalFilterOperator = LogicalFilterOperator.Or,
            }));

            Assert.Equal(new[] { "Carol", "Alice" }, Names(cut));
        }

        [Theory]
        [InlineData(FastGridFilterOperator.IsNull, new[] { "Alice" })]
        [InlineData(FastGridFilterOperator.IsNotNull, new[] { "Carol", "Dave", "Bob" })]
        public void AFilterThatNeedsNoValueIsRestored(FastGridFilterOperator op, string[] expected)
        {
            // §32's third fault: the restore was guarded on the value being non-null, and an IsNull's
            // null is the value it means - so it was written on every capture and restored on none.
            using var ctx = new TestContext();

            Assert.Equal(expected, Names(Render(ctx, AllTypes(), Stored(Filter("Bonus", op)))));
        }

        [Theory]
        [InlineData(FastGridFilterOperator.IsEmpty, new[] { "" })]
        [InlineData(FastGridFilterOperator.IsNotEmpty, new[] { "Carol", "Alice", "Dave" })]
        public void TheOtherTwoValuelessOperatorsAreRestoredToo(FastGridFilterOperator op, string[] expected)
        {
            // Over data with one blank name rather than the shared sample: against Sample() an
            // IsNotEmpty matches all four rows, so that row of the theory passed whether or not the
            // filter had been restored. A test that agrees with the data is not a test.
            using var ctx = new TestContext();

            var data = People.Sample();
            data[3].First = "";

            Assert.Equal(expected, Names(Render(ctx, AllTypes(), Stored(Filter("First", op)), data)));
        }

        [Fact]
        public void ACompoundLosingOneConditionIsDroppedWhole()
        {
            // §33's rule, and the trap it avoids. Degrading to the surviving condition would leave
            // "In [250.5] OR IsNull" showing only the blank rows once the In half is gone - a narrower
            // and entirely different answer, presented as the user's. Dropping shows everything.
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes(), Stored(new FastGridColumnSettings
            {
                UniqueID = "Bonus",
                FilterOperator = FastGridFilterOperator.In,
                FilterValues = new string?[] { "not a number" },
                SecondFilterOperator = FastGridFilterOperator.IsNull,
                SecondFilterValues = Array.Empty<string?>(),
                LogicalFilterOperator = LogicalFilterOperator.Or,
            }));

            Assert.Equal(new[] { "Carol", "Alice", "Dave", "Bob" }, Names(cut));
            Assert.Empty(cut.Instance.Filters);
        }

        [Fact]
        public void AStoredFilterThatCannotBeParsedLeavesTheColumnUnfiltered()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes(),
                Stored(Filter("Hired", FastGridFilterOperator.Equals, "not a date")));

            Assert.Equal(new[] { "Carol", "Alice", "Dave", "Bob" }, Names(cut));
            Assert.Empty(cut.Instance.Filters);
        }

        [Fact]
        public void TheStoredValueIsPreferredToTheText()
        {
            // Order, asserted where the two disagree. The stored value is canonical and culture-free;
            // the text is one person's typing.
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes(), Stored(new FastGridColumnSettings
            {
                UniqueID = "Id",
                FilterOperator = FastGridFilterOperator.Equals,
                FilterValues = new string?[] { "3" },
                FilterText = "1",
            }));

            Assert.Equal(new[] { "Carol" }, Names(cut));
        }

        [Fact]
        public void TheTextIsReachedOnlyWhenTheStoredValueWillNotParse()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes(), Stored(new FastGridColumnSettings
            {
                UniqueID = "Id",
                FilterOperator = FastGridFilterOperator.Equals,
                FilterValues = new string?[] { "not a number" },
                FilterText = "1",
            }));

            Assert.Equal(new[] { "Alice" }, Names(cut));
        }

        [Fact]
        public void ALookupFilterWhoseIdsCannotBeParsedFallsBackToItsNames()
        {
            // The one column where the text says something the value cannot: it stores ids and the text
            // is the name they were picked by.
            using var ctx = new TestContext();

            var cut = Render(ctx, WithLookup(), Stored(new FastGridColumnSettings
            {
                UniqueID = "Category",
                FilterOperator = FastGridFilterOperator.In,
                FilterValues = new string?[] { "not an id" },
                FilterText = "Toys",
            }));

            Assert.Equal(new[] { "Carol", "Dave" }, Names(cut));
        }

        [Fact]
        public void ALookupFilterRoundTripsThroughItsIds()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, WithLookup(),
                Stored(Filter("Category", FastGridFilterOperator.In, "10")));

            Assert.Equal(new[] { "Carol", "Dave" }, Names(cut));
        }

        [Fact]
        public void StoredValuesAreParsedInvariantlyWhateverTheCurrentCultureIs()
        {
            // The canonical form is invariant, so a blob written under one culture restores under any
            // other. Under de-DE a culture-sensitive parse would read "250.5" as two and a half thousand.
            var original = CultureInfo.CurrentCulture;

            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");

                using var ctx = new TestContext();

                var cut = Render(ctx, AllTypes(),
                    Stored(Filter("Bonus", FastGridFilterOperator.Equals, "250.5")));

                Assert.Equal(new[] { "Carol" }, Names(cut));
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [Fact]
        public void TheTextFallbackIsStillReadInTheCurrentCulture()
        {
            // The other half of the same rule: the text is what somebody typed, so it is read the way
            // they typed it. 250,5 is the de-DE spelling of Carol's bonus.
            var original = CultureInfo.CurrentCulture;

            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");

                using var ctx = new TestContext();

                var cut = Render(ctx, AllTypes(), Stored(new FastGridColumnSettings
                {
                    UniqueID = "Bonus",
                    FilterOperator = FastGridFilterOperator.Equals,
                    FilterValues = new string?[] { "not a number" },
                    FilterText = "250,5",
                }));

                Assert.Equal(new[] { "Carol" }, Names(cut));
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [Fact]
        public void AColumnThatCannotNameItsFilterTypeKeepsTheTextItWasGiven()
        {
            // EffectiveFilterType answers object for a column it cannot resolve, so there is nothing to
            // parse towards and the text stands. §32 measured what trusting a serializer's wrapper did
            // here instead: the grid hid every row while the blob looked intact.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First),
                Columns.Property<Person, object>(x => x.Mixed)),
                Stored(Filter("Mixed", FastGridFilterOperator.Equals, "n/a")));

            Assert.Equal("n/a", Assert.Single(cut.Instance.Filters).FilterValue);
        }

        [Fact]
        public void ABetweenInsideACompoundReachesEveryRouteTheSameWay()
        {
            // The review's case, and the one the suite had carefully avoided: every compound it tested
            // was two simple conditions, which is the shape that happened to work. A Between's two
            // bounds were flattened into the parent's child list beside the other condition, so the
            // And that makes a range a range was replaced by the compound's Or - and only the routes
            // that never see a descriptor were right. An in-memory grid showed two rows while the
            // string sent to a server asked for nearly the table.
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes(), Stored(new FastGridColumnSettings
            {
                UniqueID = "Bonus",
                FilterOperator = FastGridFilterOperator.Between,
                FilterValues = new string?[] { "200", "300" },
                SecondFilterOperator = FastGridFilterOperator.IsNull,
                SecondFilterValues = Array.Empty<string?>(),
                LogicalFilterOperator = LogicalFilterOperator.Or,
            }));

            // The typed route, which was always right.
            Assert.Equal(new[] { "Carol", "Alice" }, Names(cut));

            // The reflective route, over the descriptors the grid reports - which is what a declining
            // column composes through, and what ApplyFilters is handed.
            var reflective = People.Sample().AsQueryable()
                .Where(cut.Instance.Filters, LogicalFilterOperator.And, FilterCaseSensitivity.Default)
                .Select(p => p.First)
                .ToArray();

            Assert.Equal(new[] { "Carol", "Alice" }, reflective);
        }

        [Fact]
        public void ABetweenSurvivesBeingHandedBackThroughApplyFilters()
        {
            // Shape, not just rows. Read back as two conditions a range consumed the compound's second
            // slot, so the IsNull half was pushed out - and a capture after that stored the range as
            // GreaterThanOrEquals plus LessThanOrEquals, which is a blob degrading on every round trip.
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes(), Stored(new FastGridColumnSettings
            {
                UniqueID = "Bonus",
                FilterOperator = FastGridFilterOperator.Between,
                FilterValues = new string?[] { "200", "300" },
                SecondFilterOperator = FastGridFilterOperator.IsNull,
                SecondFilterValues = Array.Empty<string?>(),
                LogicalFilterOperator = LogicalFilterOperator.Or,
            }));

            var reported = cut.Instance.Filters;

            cut.InvokeAsync(() => cut.Instance.ApplyFilters(reported)).Wait();

            Assert.Equal(new[] { "Carol", "Alice" }, Names(cut));

            var captured = cut.Instance.CaptureSettings().Columns!.Single(c => c.UniqueID == "Bonus");

            Assert.Equal(FastGridFilterOperator.Between, captured.FilterOperator);
            Assert.Equal(FastGridFilterOperator.IsNull, captured.SecondFilterOperator);
        }

        [Fact]
        public void AUtcDateSurvivesTheRoundTripAsTheSameInstant()
        {
            // Convert.ChangeType can see a DateTime and reads it wrongly: no RoundtripKind, so a stored
            // UTC instant came back shifted into local time with its Kind lost, and the same blob
            // restored differently in every time zone - the exact thing invariant text was meant to
            // stop. Asserted on the value rather than on rows, because in one time zone the wrong
            // answer is the right one.
            using var ctx = new TestContext();

            var utc = new DateTime(2019, 5, 4, 0, 0, 0, DateTimeKind.Utc);

            var cut = Render(ctx, AllTypes(), Stored(Filter("Hired", FastGridFilterOperator.Equals,
                utc.ToString("O", CultureInfo.InvariantCulture))));

            var restored = Assert.IsType<DateTime>(Assert.Single(cut.Instance.Filters).FilterValue);

            Assert.Equal(utc, restored);
            Assert.Equal(DateTimeKind.Utc, restored.Kind);
        }

        [Fact]
        public void ADateCarryingSubSecondPrecisionIsCapturedWithoutLosingIt()
        {
            // The capture side of the same rule, and the mutation that found it: every round-trip test
            // wrote its own stored text, and the one test that captured used a date on a whole second -
            // where the culture-sensitive ToString happens to round-trip.
            using var ctx = new TestContext();

            var precise = new DateTime(2019, 5, 4, 1, 2, 3, 456, DateTimeKind.Unspecified).AddTicks(789);
            var data = People.Sample();

            data[0].Hired = precise;

            var cut = Render(ctx, AllTypes(),
                Stored(Filter("Hired", FastGridFilterOperator.Equals,
                    precise.ToString("O", CultureInfo.InvariantCulture))), data);

            var captured = JsonSerializer.Deserialize<FastGridSettings>(
                JsonSerializer.Serialize(cut.Instance.CaptureSettings()))!;

            Assert.Equal(new[] { "Carol" }, Names(Render(ctx, AllTypes(), captured, data)));
        }

        [Fact]
        public void AHalfSpecifiedBetweenFiltersNothingRatherThanSomething()
        {
            // Arity is the rule, so a range with one bound is not a range. Nothing had tested it, and
            // reading Between's arity as one would have let a single bound through as a filter of its
            // own - which is a different answer wearing the user's filter.
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes(),
                Stored(Filter("Id", FastGridFilterOperator.Between, "2")));

            Assert.Equal(new[] { "Carol", "Alice", "Dave", "Bob" }, Names(cut));
            Assert.Empty(cut.Instance.Filters);
        }

        [Fact]
        public void ASecondConditionWithNoValueIsIgnoredRatherThanJoined()
        {
            // EffectiveSecond, not Second. A declared-but-empty second condition must not narrow
            // anything - joined with And it would empty the grid, and nothing covered it.
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes(), Stored(new FastGridColumnSettings
            {
                UniqueID = "Id",
                FilterOperator = FastGridFilterOperator.Equals,
                FilterValues = new string?[] { "3" },
                SecondFilterOperator = FastGridFilterOperator.Equals,
                SecondFilterValues = Array.Empty<string?>(),
                LogicalFilterOperator = LogicalFilterOperator.And,
            }));

            Assert.Equal(new[] { "Carol" }, Names(cut));
        }

        [Fact]
        public void AnOrderedOperatorOnAStringColumnDeclinesRatherThanThrowing()
        {
            // §32's review finding 1a, which §33 closed on the typed path: Expression.GreaterThan has no
            // overload for two strings, and building one threw inside the render.
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes(),
                Stored(Filter("First", FastGridFilterOperator.GreaterThan, "B")));

            Assert.Equal(new[] { "Carol", "Alice", "Dave", "Bob" }, Names(cut));
        }

        [Fact]
        public void ALookupsBlankEntrySurvivesTheRoundTrip()
        {
            // A ticked "(none)" is a real choice on a lookup column - SelectedKeys has always said so -
            // and restore dropped it twice over: a stored null read as unparseable text, and the rebuild
            // kept only values that were already keys. The rows with no id came back unhidden and the
            // list came back with one fewer tick than the user left.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First),
                Columns.Lookup<Person, int?>(x => x.RegionId, FastGridLookup.Map(Lookups.Regions()),
                    uniqueId: "Region")),
                Stored(new FastGridColumnSettings
                {
                    UniqueID = "Region",
                    FilterOperator = FastGridFilterOperator.In,
                    FilterValues = new string?[] { null },
                }));

            // Alice is the row with no region.
            Assert.Equal(new[] { "Alice" }, Names(cut));
        }

        [Fact]
        public void TheFiltersSetterRoundTripsACompound()
        {
            // The second restore path. §33 made composites the currency in both directions, so a
            // compound this grid reports is one it can be handed back.
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes());

            cut.InvokeAsync(() => cut.Instance.Filter(
                cut.Instance.Filters is null ? null! : ColumnOf(cut, "Bonus"),
                new FastGridFilter(
                    new FastGridFilterCondition(FastGridFilterOperator.GreaterThan, 100m),
                    new FastGridFilterCondition(FastGridFilterOperator.IsNull),
                    LogicalFilterOperator.Or))).Wait();

            Assert.Equal(new[] { "Carol", "Alice" }, Names(cut));

            var reported = cut.Instance.Filters;

            cut.InvokeAsync(() => cut.Instance.ApplyFilters(reported)).Wait();

            Assert.Equal(new[] { "Carol", "Alice" }, Names(cut));
        }

        static ColumnBase<Person> ColumnOf(IRenderedComponent<RadzenFastGrid<Person>> cut, string id) =>
            cut.FindComponents<PropertyColumn<Person, decimal?>>()
                .Select(c => (ColumnBase<Person>)c.Instance)
                .Single(c => c.Identity.Name == id);

        [Fact]
        public void ARestoredFilterIsCapturedBackAsSomethingThatRestoresAgain()
        {
            // The round trip is not one-way: what a restore rebuilt has to be storable, or a consumer
            // that saves after every change writes a blob that degrades a little each time.
            using var ctx = new TestContext();

            var first = Render(ctx, AllTypes(),
                Stored(Filter("Hired", FastGridFilterOperator.Equals, "2019-05-04T00:00:00.0000000")));

            var captured = JsonSerializer.Deserialize<FastGridSettings>(
                JsonSerializer.Serialize(first.Instance.CaptureSettings()))!;

            Assert.Equal(new[] { "Carol" }, Names(Render(ctx, AllTypes(), captured)));
        }

        [Fact]
        public void ABetweenIsCapturedBackAsSomethingThatRestoresAgain()
        {
            using var ctx = new TestContext();

            var first = Render(ctx, AllTypes(),
                Stored(Filter("Id", FastGridFilterOperator.Between, "2", "3")));

            var captured = JsonSerializer.Deserialize<FastGridSettings>(
                JsonSerializer.Serialize(first.Instance.CaptureSettings()))!;

            Assert.Equal(new[] { "Carol", "Bob" }, Names(Render(ctx, AllTypes(), captured)));
        }

        [Fact]
        public void ACheckBoxListIsCapturedBackAsSomethingThatRestoresAgain()
        {
            using var ctx = new TestContext();

            var first = Render(ctx, AllTypes(), Stored(Filter("Id", FastGridFilterOperator.In, "3", "1")));

            var captured = JsonSerializer.Deserialize<FastGridSettings>(
                JsonSerializer.Serialize(first.Instance.CaptureSettings()))!;

            Assert.Equal(new[] { "Carol", "Alice" }, Names(Render(ctx, AllTypes(), captured)));
        }
    }
}
