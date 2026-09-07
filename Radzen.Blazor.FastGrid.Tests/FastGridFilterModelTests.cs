using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// The filter model itself, asked directly rather than through a grid.
    /// </summary>
    /// <remarks>
    /// These exist because the mutation loop kept surviving. Every one of the behaviours below was
    /// covered end to end and by a route that could not see it: a range over the expression builder
    /// while every test rendered over a list, an unjoined second condition while every path that could
    /// produce one dropped it first, an arity rule while the restore rejected the case before it was
    /// reached. A test one layer above the rule is not a test of the rule.
    /// </remarks>
    public class FastGridFilterModelTests
    {
        static readonly System.Linq.Expressions.Expression<Func<Person, int>> Id = p => p.Id;

        static int[] Matching(FastGridFilter filter, bool queryable)
        {
            var people = People.Sample();

            if (queryable)
            {
                // The expression route, which only a queryable source reaches - every grid test in this
                // suite renders over a List and therefore composes delegates instead.
                var predicate = FilterExpression<Person, int>.For(Id, filter,
                    FilterCaseSensitivity.Default, inMemory: false);

                return predicate is null
                    ? people.Select(p => p.Id).OrderBy(id => id).ToArray()
                    : people.AsQueryable().Where(predicate).Select(p => p.Id).OrderBy(id => id).ToArray();
            }

            var composed = FilterExpression<Person, int>.PredicateFor(p => p.Id, filter,
                FilterCaseSensitivity.Default);

            return composed is null
                ? people.Select(p => p.Id).OrderBy(id => id).ToArray()
                : people.Where(composed).Select(p => p.Id).OrderBy(id => id).ToArray();
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ARangeIsBoundedAtBothEnds(bool queryable)
        {
            // Ids are 1..4. A range that only applied its lower bound would keep 3 and 4.
            var between = new FastGridFilter(
                new FastGridFilterCondition(FastGridFilterOperator.Between, new object?[] { 2, 3 }));

            Assert.Equal(new[] { 2, 3 }, Matching(between, queryable));
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ASecondConditionThatNarrowsNothingIsNotJoined(bool queryable)
        {
            // Second is set and IsPresent is false, which is the only state separating Second from
            // EffectiveSecond. An empty string is the case that discriminates: it *composes* - an
            // Equals against "" is a perfectly good predicate - while not being a filter anybody
            // asked for. The first draft used an empty int condition, which fails to compose as well,
            // so the mutation swapping EffectiveSecond for Second survived it.
            var people = People.Sample();

            var filter = new FastGridFilter(
                new FastGridFilterCondition(FastGridFilterOperator.Contains, "a"),
                new FastGridFilterCondition(FastGridFilterOperator.Equals, string.Empty),
                LogicalFilterOperator.And);

            System.Linq.Expressions.Expression<Func<Person, string>> first = p => p.First;

            string[] names;

            if (queryable)
            {
                var predicate = FilterExpression<Person, string>.For(first, filter,
                    FilterCaseSensitivity.Default, inMemory: false);

                names = people.AsQueryable().Where(predicate!).Select(p => p.First).OrderBy(n => n)
                    .ToArray();
            }
            else
            {
                var composed = FilterExpression<Person, string>.PredicateFor(p => p.First, filter,
                    FilterCaseSensitivity.Default);

                names = people.Where(composed!).Select(p => p.First).OrderBy(n => n).ToArray();
            }

            // Carol and Dave contain a lower-case "a"; joined to an Equals "" the answer would be none.
            Assert.Equal(new[] { "Carol", "Dave" }, names);
        }

        [Fact]
        public void ARangeMissingABoundIsNotAFilter()
        {
            // Arity is the rule: two values or it is not a range. Read as arity one, a half-typed range
            // would filter by its lower bound alone - an answer the user never asked for, while they
            // were still typing the other end.
            var half = new FastGridFilterCondition(FastGridFilterOperator.Between, new object?[] { 2 });

            Assert.False(half.IsPresent);
            Assert.False(new FastGridFilter(half).IsPresent);

            var whole = new FastGridFilterCondition(FastGridFilterOperator.Between, new object?[] { 2, 3 });

            Assert.True(whole.IsPresent);
        }

        [Fact]
        public void AColumnDeclaringNoFilterCarriesNone()
        {
            // §3's third rule, asserted rather than assumed: a column that declares nothing must not
            // build a filter to hold nothing in. It did, and the review measured it as ~104 bytes per
            // column on every grid's first parameter set, whether or not filtering was even allowed.
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First),
                    Columns.Property<Person, int>(x => x.Id)));
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.FilterUI, FilterUI.Row);
            });

            Assert.All(cut.FindComponents<PropertyColumn<Person, int>>(),
                column => Assert.Null(column.Instance.CurrentFilter));

            Assert.Empty(cut.Instance.Filters);
        }
    }
}
