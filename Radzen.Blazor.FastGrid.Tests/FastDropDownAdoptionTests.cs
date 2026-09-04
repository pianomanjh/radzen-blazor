using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Bunit;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// What a closed drop-down pays to keep saying what it is already saying.
    /// </summary>
    /// <remarks>
    /// The drop-down re-finds the row its bound value names whenever <c>Data</c> arrives as a different
    /// instance, which for a source written in markup - <c>@people.Where(...)</c>, a <c>ToList()</c> in
    /// a property - is every parent render. The find is linear and its comparison goes through a getter
    /// typed to <c>object</c>, so every element it walks boxes: §3's rule 5, on a component nobody has
    /// opened. §19 measured it at 24,171 B a render over a thousand rows.
    /// </remarks>
    public class FastDropDownAdoptionTests
    {
        /// <summary>A source that says how many times it has been walked.</summary>
        /// <remarks>
        /// The allocation test below measures the §3 claim; this measures the mechanism, and does it
        /// without a ratio - a re-find walks the source and a skip does not, so the count is the answer.
        /// </remarks>
        sealed class Counting : IEnumerable<Person>
        {
            readonly List<Person> items;

            internal Counting(List<Person> items) => this.items = items;

            internal int Walks { get; private set; }

            public IEnumerator<Person> GetEnumerator()
            {
                Walks++;

                return items.GetEnumerator();
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        static IRenderedComponent<RadzenFastDropDownDataGrid<Person, object>> Bound(TestContext ctx,
            IEnumerable<Person> data, object? value)
        {
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            return ctx.RenderComponent<RadzenFastDropDownDataGrid<Person, object>>(p =>
            {
                p.Add(g => g.Data, data);
                p.Add(g => g.Value, value);
                p.Add(g => g.TextProperty, (Expression<Func<Person, object>>)(x => x.First));
                p.Add(g => g.ValueProperty, (Expression<Func<Person, object>>)(x => x.Id));
            });
        }

        /// <summary>Bytes allocated per render, over renders that each hand it a fresh source instance.</summary>
        static double PerRender(IRenderedComponent<RadzenFastDropDownDataGrid<Person, object>> cut,
            Func<IEnumerable<Person>> source)
        {
            const int Rounds = 20;

            // Compiled getters, render tree, the lot - measured warm or the first render dominates.
            for (var i = 0; i < 3; i++)
            {
                cut.SetParametersAndRender(p => p.Add(g => g.Data, source()));
            }

            var before = GC.GetAllocatedBytesForCurrentThread();

            for (var i = 0; i < Rounds; i++)
            {
                cut.SetParametersAndRender(p => p.Add(g => g.Data, source()));
            }

            return (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Rounds;
        }

        // The measurement §19 is built on, kept as a test because a number nothing re-checks is a number
        // that drifts. The value is the last row's, so before the change the scan ran to the end - the
        // worst case, and the one a value whose row is not on this page also produces.
        //
        // Asserted as a ratio against the same drop-down over a source it is handed by one instance,
        // which is the case that never re-finds anything: whatever the render costs, re-finding must no
        // longer be most of it. At a thousand rows it used to be seven times the rest.
        [Theory]
        [InlineData(50)]
        [InlineData(1000)]
        public void AFreshSourceInstanceDoesNotCostAScanOfIt(int rows)
        {
            using var ctx = new TestContext();

            var people = People.Many(rows);
            var cut = Bound(ctx, people.Where(_ => true), people[rows - 1].Id);

            var refreshed = PerRender(cut, () => people.Where(_ => true));

            var stable = people.ToList();
            var held = PerRender(cut, () => stable);

            // 1.15, not 1.5. At a thousand rows the scan is 8x and any threshold catches it; at fifty
            // it is 1.4x, so a 1.5 threshold passed with the rule deleted - the test discriminated at
            // one of the two row counts it is run at, which is the half that needed it least.
            Assert.True(refreshed < held * 1.15,
                $"a fresh instance cost {refreshed:0} B a render against {held:0} B for a held one, "
                    + $"which is {refreshed / held:0.00}x - the scan is back");
        }

        // The same claim without a ratio in it. Allocation is what §3 cares about and what §19
        // measured, but it is a number with noise around it; whether the source was walked is not.
        [Fact]
        public void AFreshSourceInstanceIsNotWalkedAtAll()
        {
            using var ctx = new TestContext();

            var people = People.Many(50);
            var first = new Counting(people);
            var cut = Bound(ctx, first, people[49].Id);

            Assert.True(first.Walks > 0, "the first bind has to find the row");

            var second = new Counting(people);

            cut.SetParametersAndRender(p => p.Add(g => g.Data, second));

            Assert.Equal(0, second.Walks);
        }

        // The reason the drop-down re-finds on a data change at all, and the thing the skip must not
        // break: a value bound before its rows exist is not explained by anything, so it has to go on
        // looking on every render until the row turns up.
        [Fact]
        public void AValueBoundBeforeItsRowsArriveIsStillAdoptedWhenTheyDo()
        {
            using var ctx = new TestContext();

            var people = People.Many(8);
            var cut = Bound(ctx, Array.Empty<Person>(), people[3].Id);

            Assert.DoesNotContain(people[3].First!, cut.Markup);

            cut.SetParametersAndRender(p => p.Add(g => g.Data, people));

            Assert.Contains(people[3].First!, cut.Markup);
        }

        // The source going away is not "already explained". Adopt clears what is held before it
        // returns for a null source, and the skip has to leave that alone - otherwise a drop-down whose
        // rows were taken away goes on showing one of them.
        [Fact]
        public void TakingTheSourceAwayLetsGoOfTheRowItHeld()
        {
            using var ctx = new TestContext();

            var people = People.Many(8);
            var cut = Bound(ctx, people, people[3].Id);

            Assert.Contains(people[3].First!, cut.Markup);

            cut.SetParametersAndRender(p => p.Add(g => g.Data, (IEnumerable<Person>?)null));

            Assert.DoesNotContain(people[3].First!, cut.Markup);
        }

        // The trade, asserted rather than left as prose. A source swapped for a genuinely different one
        // goes on showing the row the old one explained the value with, until the value changes or
        // something reloads - which is the lifetime rule §10 chose for the check-box lists and §14 for
        // its lookups, and it is the price of not walking the source on every render to find out.
        [Fact]
        public void ASourceSwappedForADifferentOneKeepsTheRowItAlreadyExplainedTheValueWith()
        {
            using var ctx = new TestContext();

            var people = People.Many(8);
            var cut = Bound(ctx, people, people[3].Id);

            Assert.Contains(people[3].First!, cut.Markup);

            // The same ids, different text, and a different instance - which is what re-running a query
            // against changed rows produces.
            var renamed = People.Many(8);

            foreach (var person in renamed)
            {
                person.First += " (renamed)";
            }

            cut.SetParametersAndRender(p => p.Add(g => g.Data, renamed));

            Assert.Contains(people[3].First!, cut.Markup);
            Assert.DoesNotContain("(renamed)", cut.Markup);
        }

        // A multiple selection is re-found every time, deliberately, and this is what says so rather
        // than leaving it to be discovered. The grid draws its ticks by asking a HashSet<TItem> whether
        // it holds the row it is drawing, and that set compares by reference - so rows carried over
        // from a source that has re-materialised are ticks that do not appear. Skipping would make that
        // state permanent, because a selection that has gone wrong still explains the value.
        //
        // §19 records the underlying fault; what this pins is that the cheap path is not taken here.
        [Fact]
        public void AMultipleSelectionIsFoundAgainRatherThanExplainedFromWhatIsHeld()
        {
            using var ctx = new TestContext();

            var people = People.Many(20);
            var source = new Counting(people);

            var cut = ctx.RenderComponent<RadzenFastDropDownDataGrid<Person, IEnumerable<object>>>(p =>
            {
                p.Add(g => g.Data, source);
                p.Add(g => g.Multiple, true);
                p.Add(g => g.Value, new List<object> { people[2].Id, people[9].Id });
                p.Add(g => g.TextProperty, (Expression<Func<Person, object>>)(x => x.First));
                p.Add(g => g.ValueProperty, (Expression<Func<Person, object>>)(x => x.Id));
            });

            var walked = source.Walks;

            Assert.True(walked > 0, "the first bind has to find the rows");

            cut.SetParametersAndRender(p =>
                p.Add(g => g.Value, new List<object> { people[2].Id, people[9].Id }));

            Assert.True(source.Walks > walked,
                "a multiple selection is re-found, so that rows carried over from an old source cannot "
                    + "become permanent");
        }

        // And one that is genuinely different is answered, so the skip above is not simply "never look
        // again".
        [Fact]
        public void RebindingMultipleToDifferentIdsDoesWalkTheSource()
        {
            using var ctx = new TestContext();

            var people = People.Many(20);
            var source = new Counting(people);

            var cut = ctx.RenderComponent<RadzenFastDropDownDataGrid<Person, IEnumerable<object>>>(p =>
            {
                p.Add(g => g.Data, source);
                p.Add(g => g.Multiple, true);
                p.Add(g => g.Value, new List<object> { people[2].Id });
                p.Add(g => g.TextProperty, (Expression<Func<Person, object>>)(x => x.First));
                p.Add(g => g.ValueProperty, (Expression<Func<Person, object>>)(x => x.Id));
            });

            var walked = source.Walks;

            cut.SetParametersAndRender(p =>
                p.Add(g => g.Value, new List<object> { people[2].Id, people[9].Id }));

            Assert.True(source.Walks > walked, "a value nothing held explains has to be looked for");
            Assert.Contains(people[9].First!, cut.Markup);
        }

        // The skip asks whether what is held is the answer, and "is the answer" has two halves: every
        // value asked for is held, and nothing is held that is not asked for. Dropping the second half
        // leaves a row ticked that the binding no longer names, which is the shape of bug the whole
        // component exists to avoid - a tick the user did not put there.
        [Fact]
        public void RebindingMultipleToFewerIdsLetsGoOfTheRowNoLongerAskedFor()
        {
            using var ctx = new TestContext();

            var people = People.Many(20);

            var cut = ctx.RenderComponent<RadzenFastDropDownDataGrid<Person, IEnumerable<object>>>(p =>
            {
                p.Add(g => g.Data, people);
                p.Add(g => g.Multiple, true);
                p.Add(g => g.Value, new List<object> { people[2].Id, people[9].Id });
                p.Add(g => g.TextProperty, (Expression<Func<Person, object>>)(x => x.First));
                p.Add(g => g.ValueProperty, (Expression<Func<Person, object>>)(x => x.Id));
            });

            Assert.Contains(people[9].First!, cut.Markup);

            cut.SetParametersAndRender(p =>
                p.Add(g => g.Value, new List<object> { people[2].Id }));

            Assert.Contains(people[2].First!, cut.Markup);
            Assert.DoesNotContain(people[9].First!, cut.Markup);
        }

        // Clearing the binding has to clear what is shown. A null value is nothing to explain rather
        // than something already explained, and reading it the other way leaves the drop-down showing a
        // choice after the model stopped holding one.
        [Fact]
        public void ClearingTheValueLetsGoOfTheRowItNamed()
        {
            using var ctx = new TestContext();

            var people = People.Many(8);
            var cut = Bound(ctx, people, people[3].Id);

            Assert.Contains(people[3].First!, cut.Markup);

            cut.SetParametersAndRender(p => p.Add(g => g.Value, (object?)null));

            Assert.DoesNotContain(people[3].First!, cut.Markup);
        }

        // And a value change is answered whatever is held, because that is the half the skip is gated on.
        [Fact]
        public void AChangedValueIsAdoptedFromTheSourceInHand()
        {
            using var ctx = new TestContext();

            var people = People.Many(8);
            var cut = Bound(ctx, people, people[3].Id);

            cut.SetParametersAndRender(p => p.Add(g => g.Value, (object)people[6].Id));

            Assert.Contains(people[6].First!, cut.Markup);
            Assert.DoesNotContain(people[3].First!, cut.Markup);
        }
    }
}
