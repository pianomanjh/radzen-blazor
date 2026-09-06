using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Bunit;
using Microsoft.AspNetCore.Components;
using Radzen.Blazor;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// What comes back when a settings blob has been through a serializer. §32's subject.
    /// </summary>
    /// <remarks>
    /// <c>FastGridColumnSettings.FilterValue</c> is <c>object?</c>, so every one of these went out as a
    /// <c>DateTime</c> or an <c>int</c> and comes back as a <c>JsonElement</c>. Before §32 that made the
    /// typed builders decline, and a decline is absorbed by the reflective one, where
    /// <c>Expression.Constant(value, type)</c> throws <em>inside the render</em> - so the assertion these
    /// tests would have failed on was not a wrong row count, it was an exception.
    /// <para>
    /// The round trip is a real <c>JsonSerializer</c> rather than a hand-built <c>JsonElement</c>,
    /// deliberately: the fault is about what a serializer does to <c>object</c>, and a fixture that
    /// constructs the wrapper itself is a test agreeing with the diagnosis rather than with the
    /// consumer.
    /// </para>
    /// </remarks>
    public class FastGridSettingsRestoreTests
    {
        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx, RenderFragment columns,
            FastGridSettings? settings = null, IEnumerable<Person>? data = null,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>>? extra = null)
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

                extra?.Invoke(p);
            });
        }

        static string[] Names(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindAll("tbody tr")
                .Where(row => row.QuerySelectorAll("td").Length > 0)
                .Select(row => row.QuerySelectorAll("td")[0].TextContent)
                .ToArray();

        /// <summary>Through a real serializer, which is what turns every value into a JsonElement.</summary>
        static FastGridSettings Stored(params FastGridColumnSettings[] columns) =>
            JsonSerializer.Deserialize<FastGridSettings>(
                JsonSerializer.Serialize(new FastGridSettings { Columns = columns.ToList() }))!;

        static RenderFragment AllTypes() => Columns.Of(
            Columns.Property<Person, string>(x => x.First),
            Columns.Property<Person, DateTime>(x => x.Hired),
            Columns.Property<Person, int>(x => x.Id),
            Columns.Property<Person, Grade>(x => x.Grade),
            Columns.Property<Person, Guid>(x => x.Reference),
            Columns.Property<Person, decimal?>(x => x.Bonus));

        [Fact]
        public void ARoundTrippedDateFilterStillFilters()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes(), Stored(new FastGridColumnSettings
            {
                UniqueID = "Hired",
                FilterValue = new DateTime(2019, 5, 4),
                FilterOperator = FilterOperator.Equals,
                FilterText = "2019-05-04",
            }));

            Assert.Equal(new[] { "Carol" }, Names(cut));
        }

        [Fact]
        public void ARoundTrippedDateFilterWithNoTextStillFilters()
        {
            // The half §31's fallback could not have reached: there is nothing to re-parse, and this is
            // the shape every programmatic filter and the whole Filters path arrives in.
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes(), Stored(new FastGridColumnSettings
            {
                UniqueID = "Hired",
                FilterValue = new DateTime(2019, 5, 4),
                FilterOperator = FilterOperator.Equals,
            }));

            Assert.Equal(new[] { "Carol" }, Names(cut));
        }

        [Fact]
        public void ARoundTrippedNumberFilterStillFilters()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes(), Stored(new FastGridColumnSettings
            {
                UniqueID = "Id", FilterValue = 3, FilterOperator = FilterOperator.Equals,
            }));

            Assert.Equal(new[] { "Carol" }, Names(cut));
        }

        [Fact]
        public void ARoundTrippedEnumFilterStillFilters()
        {
            // An enum converts through neither IConvertible nor ConvertType's nullable-enum branch, so
            // this one reaches Enum.Parse over the value's string form - which is "1", the number
            // System.Text.Json wrote.
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes(), Stored(new FastGridColumnSettings
            {
                UniqueID = "Grade", FilterValue = Grade.Senior, FilterOperator = FilterOperator.Equals,
            }));

            Assert.Equal(new[] { "Carol", "Bob" }, Names(cut));
        }

        [Fact]
        public void ARoundTrippedGuidFilterStillFilters()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes(), Stored(new FastGridColumnSettings
            {
                UniqueID = "Reference",
                FilterValue = People.Sample()[0].Reference,
                FilterOperator = FilterOperator.Equals,
            }));

            Assert.Equal(new[] { "Carol" }, Names(cut));
        }

        [Fact]
        public void ARoundTrippedStringFilterStillFilters()
        {
            // The one case that worked before §32, and by accident: FilterExpression.Text reads its
            // value as `value as string ?? value?.ToString()`. Pinned so the accident cannot be lost
            // while the deliberate path is being built beside it.
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes(), Stored(new FastGridColumnSettings
            {
                UniqueID = "First", FilterValue = "Car", FilterOperator = FilterOperator.Contains,
            }));

            Assert.Equal(new[] { "Carol" }, Names(cut));
        }

        [Theory]
        [InlineData(FilterOperator.IsNull, new[] { "Alice" })]
        [InlineData(FilterOperator.IsNotNull, new[] { "Carol", "Dave", "Bob" })]
        public void AFilterThatNeedsNoValueIsRestored(FilterOperator filterOperator, string[] expected)
        {
            // Not a serializer fault at all: the guard on the restore read `FilterValue is not null` as
            // "something was stored", and an IsNull's null is the value it means. Written on every
            // capture, restored on none.
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes(), Stored(new FastGridColumnSettings
            {
                UniqueID = "Bonus", FilterValue = null, FilterOperator = filterOperator,
            }));

            Assert.Equal(expected, Names(cut));
        }

        [Theory]
        [InlineData(FilterOperator.IsEmpty, new[] { "" })]
        [InlineData(FilterOperator.IsNotEmpty, new[] { "Carol", "Alice", "Dave" })]
        public void TheOtherTwoValuelessOperatorsAreRestoredToo(FilterOperator filterOperator,
            string[] expected)
        {
            // Over data with one blank name rather than the shared sample, and the mutation loop is why:
            // against Sample() an IsNotEmpty matches all four rows, so that row of the theory passed
            // whether or not the filter had been restored at all. A test that agrees with the data is
            // not a test either.
            using var ctx = new TestContext();

            var data = People.Sample();
            data[3].First = "";

            var cut = Render(ctx, AllTypes(), Stored(new FastGridColumnSettings
            {
                UniqueID = "First", FilterValue = null, FilterOperator = filterOperator,
            }), data);

            Assert.Equal(expected, Names(cut));
        }

        [Fact]
        public void ARoundTrippedInFilterIsDroppedRatherThanLeftMatchingEveryRow()
        {
            // §32's stated hole, pinned as a hole. A JSON array comes back as one opaque value, so it is
            // not a sequence and nothing here can rebuild the list - that waits for ②'s format change.
            //
            // The rows alone cannot tell the two outcomes apart: a dropped filter and an In that matches
            // everything both show four names. What separates them is whether the grid believes it is
            // filtered, so the assertion is on Filters, not on the body. Before §32 the column was
            // filtered and composing Constant(true).
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes(), Stored(new FastGridColumnSettings
            {
                UniqueID = "Id",
                FilterValue = new List<int> { 3, 1 },
                FilterOperator = FilterOperator.In,
            }));

            Assert.Equal(new[] { "Carol", "Alice", "Dave", "Bob" }, Names(cut));
            Assert.Empty(cut.Instance.Filters);
        }

        [Fact]
        public void AnInFilterThatCameBackAsASequenceIsRestored()
        {
            // The other side of the same rule, and what makes the drop above a statement about JSON
            // arrays rather than about In. A serializer whose arrays stay IEnumerable - Newtonsoft's
            // JArray is one - lands here, and the elements are converted by the predicate builders.
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes(), new FastGridSettings
            {
                Columns = new List<FastGridColumnSettings>
                {
                    new()
                    {
                        UniqueID = "Id",
                        FilterValue = new List<object> { "3", "1" },
                        FilterOperator = FilterOperator.In,
                    },
                },
            });

            Assert.Equal(new[] { "Carol", "Alice" }, Names(cut));
        }

        [Fact]
        public void AStoredFilterThatCannotBeRebuiltAtAllLeavesTheColumnUnfiltered()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes(), new FastGridSettings
            {
                Columns = new List<FastGridColumnSettings>
                {
                    new()
                    {
                        UniqueID = "Hired",
                        FilterValue = new Company { Name = "not a date" },
                        FilterOperator = FilterOperator.Equals,
                    },
                },
            });

            Assert.Equal(new[] { "Carol", "Alice", "Dave", "Bob" }, Names(cut));
            Assert.Empty(cut.Instance.Filters);
        }

        [Fact]
        public void TheValueIsPreferredToTheText()
        {
            // Order, asserted where the two disagree. The value is culture-free and the text is one
            // person's typing, so a value that still converts wins.
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes(), Stored(new FastGridColumnSettings
            {
                UniqueID = "Id",
                FilterValue = 3,
                FilterOperator = FilterOperator.Equals,
                FilterText = "1",
            }));

            Assert.Equal(new[] { "Carol" }, Names(cut));
        }

        [Fact]
        public void TheTextIsReachedOnlyWhenNoFormOfTheValueConverts()
        {
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes(), new FastGridSettings
            {
                Columns = new List<FastGridColumnSettings>
                {
                    new()
                    {
                        UniqueID = "Id",
                        FilterValue = new Company { Name = "not a number" },
                        FilterOperator = FilterOperator.Equals,
                        FilterText = "1",
                    },
                },
            });

            Assert.Equal(new[] { "Alice" }, Names(cut));
        }

        [Fact]
        public void ALookupFilterWhoseIdsCannotBeRebuiltFallsBackToItsNames()
        {
            // A lookup column stores ids and the name they were picked by. The ids came back as a JSON
            // array, which is not a sequence and cannot be rebuilt, so the text is what restores it -
            // and on this column alone the text means something the value never could.
            using var ctx = new TestContext();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First),
                Columns.Lookup<Person, int>(x => x.CategoryId, FastGridLookup.Map(Lookups.Categories()),
                    uniqueId: "Category")),
                Stored(new FastGridColumnSettings
                {
                    UniqueID = "Category",
                    FilterValue = new List<int> { 10 },
                    FilterOperator = FilterOperator.In,
                    FilterText = "Toys",
                }));

            Assert.Equal(new[] { "Carol", "Dave" }, Names(cut));
        }

        [Fact]
        public void TheValuesStringFormIsConvertedInvariantly()
        {
            // Why attempt 3 is a conversion to a Type rather than a call to the column's own text
            // parser: the string form it reads came out of a serializer, so it is invariant, while
            // FilterValueFromText reads CurrentCulture because it reads what somebody typed. Under
            // de-DE the parser takes "250.5" for two and a half thousand.
            //
            // This test exists because the mutation that swapped the two survived the suite. The
            // argument recorded for it in §32 was the lookup name matcher, which turned out to be
            // unreachable - a lookup column filters by In, so it never reaches this attempt at all.
            var original = CultureInfo.CurrentCulture;

            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");

                using var ctx = new TestContext();

                var cut = Render(ctx, AllTypes(), Stored(new FastGridColumnSettings
                {
                    UniqueID = "Bonus", FilterValue = 250.5m, FilterOperator = FilterOperator.Equals,
                }));

                Assert.Equal(new[] { "Carol" }, Names(cut));
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [Fact]
        public void TheTextIsReParsedInTheCurrentCulture()
        {
            var original = CultureInfo.CurrentCulture;

            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");

                using var ctx = new TestContext();

                // 250,5 is the de-DE spelling of Carol's bonus, and 2505 under any culture that reads
                // the comma as a group separator.
                var cut = Render(ctx, AllTypes(), new FastGridSettings
                {
                    Columns = new List<FastGridColumnSettings>
                    {
                        new()
                        {
                            UniqueID = "Bonus",
                            FilterValue = new Company { Name = "not a number" },
                            FilterOperator = FilterOperator.Equals,
                            FilterText = "250,5",
                        },
                    },
                });

                Assert.Equal(new[] { "Carol" }, Names(cut));
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }
        }

        [Fact]
        public void TheFiltersSetterReconstructsTheSameWay()
        {
            // The second restore path, and the one that has never had a text to fall back to. A
            // RadzenDataFilter or a remote filter store hands descriptors straight in.
            using var ctx = new TestContext();

            var cut = Render(ctx, AllTypes());

            var value = JsonSerializer.Deserialize<object>(
                JsonSerializer.Serialize(new DateTime(2019, 5, 4)));

            cut.InvokeAsync(() => cut.Instance.ApplyFilters(new[]
            {
                new FilterDescriptor
                {
                    Property = "Hired",
                    FilterValue = value,
                    FilterOperator = FilterOperator.Equals,
                },
            })).Wait();

            Assert.Equal(new[] { "Carol" }, Names(cut));
        }

        [Fact]
        public void ARestoredFilterIsCapturedBackAsSomethingThatRestoresAgain()
        {
            // The round trip is not one-way: what a restore rebuilt has to be storable, or a consumer
            // that saves after every change writes a blob that degrades a little each time.
            using var ctx = new TestContext();

            var first = Render(ctx, AllTypes(), Stored(new FastGridColumnSettings
            {
                UniqueID = "Hired",
                FilterValue = new DateTime(2019, 5, 4),
                FilterOperator = FilterOperator.Equals,
            }));

            var captured = JsonSerializer.Deserialize<FastGridSettings>(
                JsonSerializer.Serialize(first.Instance.CaptureSettings()))!;

            var second = Render(ctx, AllTypes(), captured);

            Assert.Equal(new[] { "Carol" }, Names(second));
        }
    }
}
