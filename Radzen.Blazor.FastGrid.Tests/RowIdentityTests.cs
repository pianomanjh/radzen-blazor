using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// What the grid does when it is asked twice about the same row and handed a different object each
    /// time - which is what a source read again per render gives it. §21 has the four places that used
    /// to answer by comparing instances, and these are the faults each of them had.
    /// </summary>
    public class RowIdentityTests
    {
        static TestContext Context()
        {
            var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");
            return ctx;
        }

        // The whole point: same ids, new objects, exactly as AsNoTracking() or a LoadData handler gives.
        static List<Person> Reread(IEnumerable<Person> people) =>
            people.Select(p => new Person { Id = p.Id, First = p.First, Last = p.Last }).ToList();

        static RenderFragment Columns2 => Columns.Of(
            Columns.Property<Person, string>(p => p.First, title: "First"),
            Columns.Property<Person, int>(p => p.Id, title: "Id"));

        static RenderFragment<Person> Detail => person => b => b.AddContent(0, "detail " + person.Id);

        static IRenderedComponent<RadzenFastGrid<Person>> Expandable(TestContext ctx,
            IEnumerable<Person> data, bool keyed) =>
            ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, data);
                p.Add(g => g.ChildContent, Columns2);
                p.Add(g => g.Template, Detail);

                if (keyed)
                {
                    p.Add(g => g.ItemKey, (Func<Person, object>)(x => x.Id));
                }
            });

        [Fact]
        public void AnExpandedRowIsStillExpandedWhenItsSourceIsReadAgain()
        {
            // §10's fault, as an assertion. The row draws collapsed and the old instance is held.
            using var ctx = Context();

            var people = People.Sample();
            var cut = Expandable(ctx, people, keyed: true);

            cut.InvokeAsync(() => cut.Instance.ToggleRow(people[0])).Wait();

            Assert.Contains("detail " + people[0].Id, cut.Markup, StringComparison.Ordinal);

            cut.SetParametersAndRender(p => p.Add(g => g.Data, Reread(people)));

            Assert.Contains("detail " + people[0].Id, cut.Markup, StringComparison.Ordinal);
        }

        [Fact]
        public void WithoutAnItemKeyExpansionCompareTheRowItselfAsItAlwaysHas()
        {
            // The other half, stated rather than left to be discovered: with no key there is nothing to
            // identify a row by, so a re-read row is a different row and draws collapsed. Unchanged
            // behaviour, and the reason the key is what fixes it.
            using var ctx = Context();

            var people = People.Sample();
            var cut = Expandable(ctx, people, keyed: false);

            cut.InvokeAsync(() => cut.Instance.ToggleRow(people[0])).Wait();

            Assert.Contains("detail " + people[0].Id, cut.Markup, StringComparison.Ordinal);

            cut.SetParametersAndRender(p => p.Add(g => g.Data, Reread(people)));

            Assert.DoesNotContain("detail ", cut.Markup, StringComparison.Ordinal);
        }

        [Fact]
        public void ExpandingOneRowOverManyReadsHoldsOneOfIt()
        {
            // The leak half. With instances compared, each re-read row expanded is a new entry that can
            // never be matched again, so the set grows without bound and one collapse cannot empty it.
            // One collapse emptying it is what says only one entry was ever there.
            using var ctx = Context();

            var people = People.Sample();
            var cut = Expandable(ctx, people, keyed: true);

            var current = people;

            for (var i = 0; i < 5; i++)
            {
                cut.InvokeAsync(() => cut.Instance.ToggleRow(current[0])).Wait();
                Assert.True(cut.Instance.IsRowExpanded(current[0]), "expanded on read " + i);

                current = Reread(current);
                cut.SetParametersAndRender(p => p.Add(g => g.Data, current));

                // Re-expanding a row that is already expanded would collapse it, so it is toggled back
                // open only through the loop's next pass - what matters is that the entry is one.
                cut.InvokeAsync(() => cut.Instance.ToggleRow(current[0])).Wait();
            }

            Assert.False(cut.Instance.IsRowExpanded(current[0]));
            Assert.DoesNotContain("detail ", cut.Markup, StringComparison.Ordinal);

            // And the direct statement of it: the instance from the very first read is not still held.
            // Comparing instances, every re-read that was expanded again left its own entry behind, and
            // this one - the first - could never be matched or removed by anything afterwards.
            Assert.False(cut.Instance.IsRowExpanded(people[0]));
        }

        [Fact]
        public void TheGridsOwnSelectionDoesNotDoubleOverARereadSource()
        {
            // Found while designing §21 and never asserted before: SelectRow asked the caller's
            // collection whether it held the row, and a miss took the not-selected branch and added the
            // row beside the equal one already there.
            using var ctx = Context();

            var people = People.Sample();
            ICollection<Person> selection = new List<Person> { people[0], people[1] };

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, Reread(people));
                p.Add(g => g.ChildContent, Columns2);
                p.Add(g => g.SelectionMode, DataGridSelectionMode.Multiple);
                p.Add(g => g.ItemKey, (Func<Person, object>)(x => x.Id));
                p.Add(g => g.Selection, selection);
                p.Add(g => g.SelectionChanged,
                    EventCallback.Factory.Create<ICollection<Person>>(new object(), next => selection = next));
            });

            // The row on screen is a different object with the same id as the one already selected.
            cut.FindAll("tbody tr")[0].Click();

            // Clicking a selected row in Multiple mode removes it, so finding it gone is what says the
            // grid recognised it. Comparing instances, the lookup missed and the click took the
            // not-selected branch instead: the count here was 2, the same row twice.
            Assert.Equal(0, selection.Count(p => p.Id == people[0].Id));

            // And only that row went. With one row selected, "deselected the one clicked" and "dropped
            // the lot" are the same assertion, so the second row is here to tell them apart.
            Assert.Equal(new[] { people[1].Id }, selection.Select(p => p.Id));
        }

        [Fact]
        public void ASelectionTheGridDidNotBuildStillTicksTheRowOnScreen()
        {
            // The last hole, and the only part of §21 that is not free: the tick reads the collection
            // the caller supplied, and a List compares by instance however the grid compares. Over a
            // re-read source the selected row is a different object and drew unselected.
            using var ctx = Context();

            var people = People.Sample();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, Reread(people));
                p.Add(g => g.ChildContent, Columns2);
                p.Add(g => g.ItemKey, (Func<Person, object>)(x => x.Id));
                p.Add(g => g.Selection, new List<Person> { people[0] });
            });

            Assert.Equal("true", cut.FindAll("tbody tr")[0].GetAttribute("aria-selected"));
        }

        [Fact]
        public void AGridWithNoItemKeyLeavesTheCallersCollectionAlone()
        {
            // The control for the row above: with no key there is nothing to copy the selection into
            // and nothing is copied, so behaviour and cost are exactly what they were.
            using var ctx = Context();

            var people = People.Sample();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, Reread(people));
                p.Add(g => g.ChildContent, Columns2);
                p.Add(g => g.Selection, new List<Person> { people[0] });
            });

            Assert.Null(cut.FindAll("tbody tr")[0].GetAttribute("aria-selected"));
        }

        [Fact]
        public void ARowWhoseKeyIsNullIsStillTickedWhenItIsSelected()
        {
            // A keyed grid used to answer a flat "not selected" for any row the key had no name for,
            // because the tick committed to the key set with no way back. A lookup row's id is null
            // often enough that the fallback is the whole reason the null-key rule exists.
            using var ctx = Context();

            var people = People.Sample();
            var unnamed = people.Single(p => p.RegionId is null);

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, people);
                p.Add(g => g.ChildContent, Columns2);
                p.Add(g => g.ItemKey, (Func<Person, object>)(x => x.RegionId!));
                p.Add(g => g.Selection, new List<Person> { unnamed });
            });

            var row = cut.FindAll("tbody tr")[people.IndexOf(unnamed)];

            Assert.Equal("true", row.GetAttribute("aria-selected"));
        }

        [Fact]
        public void ARowFiledUnderAKeyIsReleasedWhenTheKeyIsTakenAway()
        {
            // ItemKey is a parameter and a caller may stop supplying one. A row expanded while it had a
            // key was filed under that key, and the collapse afterwards could only remove by the key it
            // has now - so the entry stayed for good, and Single mode would collapse a row nobody had
            // expanded.
            using var ctx = Context();

            var people = People.Sample();
            var keyed = (Func<Person, object>)(x => x.Id);

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, people);
                p.Add(g => g.ChildContent, Columns2);
                p.Add(g => g.Template, Detail);
                p.Add(g => g.ItemKey, keyed);
            });

            cut.InvokeAsync(() => cut.Instance.ToggleRow(people[0])).Wait();

            cut.SetParametersAndRender(p => p.Add(g => g.ItemKey, (Func<Person, object>)null!));

            // Unkeyed it is no longer held, so this expands it again, and this collapses it.
            cut.InvokeAsync(() => cut.Instance.ToggleRow(people[0])).Wait();
            cut.InvokeAsync(() => cut.Instance.ToggleRow(people[0])).Wait();

            Assert.False(cut.Instance.IsRowExpanded(people[0]));

            // And the entry filed under the old key is gone rather than waiting to come back with it.
            cut.SetParametersAndRender(p => p.Add(g => g.ItemKey, keyed));

            Assert.False(cut.Instance.IsRowExpanded(people[0]));
            Assert.DoesNotContain("detail ", cut.Markup, StringComparison.Ordinal);
        }

        [Fact]
        public void AKeyThatCannotTellTwoRowsApartIsRefusedByTheRenderer()
        {
            // Written to check that the published selection keeps two rows sharing a key, and it found
            // something better: it cannot happen. SetKey is given the same key the identity uses, and
            // Blazor's diff refuses duplicate sibling keys outright - so a non-unique ItemKey throws
            // long before a selection could hold two rows the grid cannot tell apart. The uniqueness
            // ItemKey needs is not this library's to enforce, and worrying about de-duplication in the
            // selection is worrying about a state no keyed grid can reach.
            using var ctx = Context();

            var people = People.Sample();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, people);
                p.Add(g => g.ChildContent, Columns2);
                p.Add(g => g.ItemKey, (Func<Person, object>)(x => x.Grade));
            });

            // On the diff rather than the first build: the first render has nothing to match against.
            var thrown = Assert.Throws<InvalidOperationException>(() =>
                cut.SetParametersAndRender(p => p.Add(g => g.AllowSorting, true)));

            Assert.Contains("same key value", thrown.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AMultipleSelectDropDownTicksTheRowsItHolds()
        {
            // §19 measured this at two ticks before a re-read and none after.
            using var ctx = Context();

            var people = People.Many(6);

            var cut = ctx.RenderComponent<RadzenFastDropDownDataGrid<Person, IEnumerable<object>>>(p =>
            {
                p.Add(g => g.Data, people);
                p.Add(g => g.Multiple, true);
                p.Add(g => g.Value, new List<object> { people[1].Id, people[3].Id });
                p.Add(g => g.TextProperty, (Expression<Func<Person, object>>)(x => x.First));
                p.Add(g => g.ValueProperty, (Expression<Func<Person, object>>)(x => x.Id));
            });

            cut.InvokeAsync(() => cut.Instance.OpenPopup()).Wait();

            var before = cut.FindAll("tr[aria-selected='true']").Count;

            Assert.Equal(2, before);

            cut.SetParametersAndRender(p => p.Add(g => g.Data, Reread(people)));

            Assert.Equal(2, cut.FindAll("tr[aria-selected='true']").Count);
        }

        [Fact]
        public void ClickingAnAlreadyChosenRowPublishesItsIdOnce()
        {
            // §19 measured three ids published for two chosen rows: Remove missed the carried instance,
            // so the click added the new one beside it - two objects, one id, published twice.
            using var ctx = Context();

            var people = People.Many(6);
            IEnumerable<object> published = null;

            var cut = ctx.RenderComponent<RadzenFastDropDownDataGrid<Person, IEnumerable<object>>>(p =>
            {
                p.Add(g => g.Data, people);
                p.Add(g => g.Multiple, true);
                p.Add(g => g.Value, new List<object> { people[1].Id, people[3].Id });
                p.Add(g => g.TextProperty, (Expression<Func<Person, object>>)(x => x.First));
                p.Add(g => g.ValueProperty, (Expression<Func<Person, object>>)(x => x.Id));
                p.Add(g => g.ValueChanged,
                    EventCallback.Factory.Create<IEnumerable<object>>(new object(), v => published = v));
            });

            cut.SetParametersAndRender(p => p.Add(g => g.Data, Reread(people)));
            cut.InvokeAsync(() => cut.Instance.OpenPopup()).Wait();

            // The second row of the popup is the row already chosen, drawn from the re-read source.
            cut.FindAll("tbody tr")[1].Click();

            // The number §19 measured, inverted: three ids for two chosen rows becomes the one id that
            // is left. Asserting only distinctness would pass for an empty publication, and for one
            // that had silently dropped the row nobody clicked.
            Assert.NotNull(published);
            Assert.Equal(new object[] { people[3].Id }, published);
        }
    }
}
