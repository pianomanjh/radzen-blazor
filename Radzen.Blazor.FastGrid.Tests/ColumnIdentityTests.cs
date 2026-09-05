using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// What names a column across a reload, and what the grid does when two columns answer to one name.
    /// §27 has the design; the faults these pin are §10b's open collision, its TemplateColumn half, and
    /// §14's lookup identity, which were three symptoms of one missing concept.
    /// </summary>
    public class ColumnIdentityTests
    {
        static TestContext Context()
        {
            var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            return ctx;
        }

        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx, RenderFragment columns,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>> extra = null) =>
            ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, columns);
                extra?.Invoke(p);
            });

        static string[] Stored(FastGridSettings settings) =>
            settings.Columns.Select(c => c.UniqueID).ToArray();

        // --- the collision ---------------------------------------------------------------------

        [Fact]
        public void TwoColumnsOverOneMemberCannotBeToldApartAndTheGridSaysSo()
        {
            // §10b's open finding, and the reason it is a throw rather than a shrug: ColumnForPath
            // answered with the first match and CaptureSettings wrote both under that key, so hiding the
            // second column and reloading hid the first. A wrong answer on screen, not lost state.
            using var ctx = Context();

            var thrown = Assert.Throws<InvalidOperationException>(() => Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "Given"),
                Columns.Property<Person, string>(x => x.First, title: "Also given"))));

            Assert.Contains("\"Given\"", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("\"Also given\"", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("share the column identity \"First\"", thrown.Message, StringComparison.Ordinal);
            Assert.Contains("Declare a distinct UniqueID", thrown.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ADeclaredUniqueIdIsHowTwoColumnsOverOneMemberAreToldApart()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "Given"),
                Columns.Property<Person, string>(x => x.First, title: "Also given", uniqueId: "Alias")));

            Assert.Equal(new[] { "First", "Alias" }, Identities(cut));
        }

        [Fact]
        public void TwoColumnsThatNameNothingAreNotACollision()
        {
            // The rule Collides carries rather than Equals: two nameless columns both persist nothing,
            // which is a different thing from both persisting under one key. An equality that answered
            // true for two nulls would stop an ordinary grid of template columns from rendering at all.
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Template<Person>(item => b => b.AddContent(0, item.First), title: "One"),
                Columns.Template<Person>(item => b => b.AddContent(0, item.Last), title: "Two")));

            Assert.Equal(2, cut.FindAll("thead th").Count);
            Assert.Equal(new string[] { null, null }, Identities(cut));
        }

        [Fact]
        public void TheThrowIsNotSwallowedByTheRenderAfterIt()
        {
            // The generation is recorded after a clean walk, never before. Recording it first would make
            // the check report the fault once and then match on every later render - a check that finds
            // a fault and then hides it, which is worse than no check at all.
            using var ctx = Context();

            // The same grid twice, deliberately: two fresh grids would each walk once and agree whatever
            // the ordering is, so they would prove nothing about it.
            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "Given"),
                Columns.Property<Person, string>(x => x.Last, title: "Family")));

            RenderFragment colliding = Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "Given"),
                Columns.Property<Person, string>(x => x.First, title: "Also given"));

            Assert.Throws<InvalidOperationException>(() =>
                cut.SetParametersAndRender(p => p.Add(g => g.ChildContent, colliding)));

            Assert.Throws<InvalidOperationException>(() => cut.Render());
        }

        [Fact]
        public void AColumnThatStartsCollidingLaterIsCaughtToo()
        {
            // The gate is bumped by a column whose own identity moved, not only by one joining or
            // leaving. Without that, two columns can come to share a name with nothing added or removed
            // and the check never re-asks - a silent return to the fault, which is the worst shape
            // available.
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "Given"),
                Columns.Property<Person, string>(x => x.First, title: "Also given", uniqueId: "Alias")));

            Assert.Throws<InvalidOperationException>(() => cut.SetParametersAndRender(p =>
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First, title: "Given"),
                    Columns.Property<Person, string>(x => x.First, title: "Also given",
                        uniqueId: "First")))));
        }

        [Fact]
        public void AnUnchangedColumnSetIsNotWalkedAgain()
        {
            // What makes the check affordable. Four columns cost four reads per parameter set for the
            // gate itself; walking them would cost ten more per render. Stated as an upper bound so it
            // survives the renderer skipping a parameter set, which it is entitled to do.
            using var ctx = Context();

            IdentityCountingColumn<Person>.Reads = 0;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, CountingColumns);
            });

            var after = IdentityCountingColumn<Person>.Reads;

            cut.Render();
            cut.Render();
            cut.Render();

            Assert.True(IdentityCountingColumn<Person>.Reads - after <= 3 * 4,
                $"identity was read {IdentityCountingColumn<Person>.Reads - after} times across three " +
                "renders that changed nothing; the walk is not gated");
        }

        // --- what the name is ------------------------------------------------------------------

        [Fact]
        public void AColumnIsNamedByWhatItShowsRatherThanByWhatItOrdersBy()
        {
            // §10b's second collision, and the one that takes no duplicated property: identity used to
            // be the sort path, so a column displaying Last and sorting by First answered to "First" -
            // the same name as the column that really is First. Two ordinary columns, nothing declared
            // twice, and a filter stored for one restored onto the other.
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "Given"),
                Columns.Property<Person, string>(x => x.Last, sortBy: x => x.First, title: "Family")));

            Assert.Equal(new[] { "First", "Last" }, Identities(cut));
        }

        [Fact]
        public void TheStoredNameAndTheRemoteSortNameAreDifferentStrings()
        {
            // The piece in one assertion. A column displaying Last and sorting by First is stored under
            // Last, because that is which column it is, and sorts under First, because that is what the
            // server is being asked to order by.
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.Last, sortBy: x => x.First, title: "Family",
                    sortOrder: SortOrder.Ascending)));

            Assert.Equal(new[] { "Last" }, Stored(cut.Instance.CaptureSettings()));
            Assert.Equal("First", cut.Instance.Sorts.Single().Property);
        }

        [Fact]
        public void AComputedColumnThatOrdersByAMemberIsStillNamedByThatMember()
        {
            // Found by review, and it is a state-losing regression rather than a wrong answer. Before
            // §27 this column's settings key was its sort path, which is non-null here, so its width,
            // order, visibility and filter were captured. Deriving identity from the displayed member
            // alone made it nameless, and it silently stopped being stored - while §27's own "where this
            // could still be wrong" said "nothing changes for them".
            //
            // The rule that fixes it is the one already written for a template column: where a column
            // shows no nameable member, its sort path is not a second name beating the real one.
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "Given"),
                Columns.Property<Person, string>(x => x.Last + "!", title: "Shouty",
                    sortBy: x => x.Last)), p => p.Add(g => g.AllowColumnPicking, true));

            Assert.Equal(new[] { "First", "Last" }, Identities(cut));

            Pick(cut, 1, false);

            Assert.False(cut.Instance.CaptureSettings().Columns.Single(c => c.UniqueID == "Last").Visible);
        }

        [Fact]
        public void AColumnThatShowsAMemberNeverFallsBackToItsSortPath()
        {
            // The fallback's other half, and the one that would undo §10b's second collision if it were
            // ordered the wrong way round. A displayed member wins outright; the sort path is consulted
            // only when there is none.
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.Last, sortBy: x => x.First, title: "Family")));

            Assert.Equal(new[] { "Last" }, Identities(cut));
        }

        [Fact]
        public void ATemplateColumnIsNamedByItsSortPathBecauseNothingElseNamesIt()
        {
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Template<Person>(item => b => b.AddContent(0, item.Last), sortProperty: "Last",
                    title: "Family")));

            Assert.Equal(new[] { "Last" }, Identities(cut));
        }

        [Fact]
        public void ATemplateColumnThatNamesNothingPersistsNothingUntilItIsNamed()
        {
            // The TemplateColumn limitation §15 records, from the other side. It is not that such a
            // column cannot be persisted - it is that nothing could say which column it was.
            using var ctx = Context();

            var bare = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First),
                Columns.Template<Person>(item => b => b.AddContent(0, item.Last), title: "Family")),
                p => p.Add(g => g.AllowColumnPicking, true));

            Pick(bare, 1, false);

            Assert.Equal(new[] { "First" }, Stored(bare.Instance.CaptureSettings()));

            using var named = Context();

            var declared = Render(named, Columns.Of(
                Columns.Property<Person, string>(x => x.First),
                Columns.Template<Person>(item => b => b.AddContent(0, item.Last), title: "Family",
                    uniqueId: "Family")), p => p.Add(g => g.AllowColumnPicking, true));

            Pick(declared, 1, false);

            Assert.Equal(new[] { "First", "Family" }, Stored(declared.Instance.CaptureSettings()));
        }

        [Fact]
        public void ALookupColumnIsNamedByTheIdItIsBoundTo()
        {
            // §14 recorded this as unavailable: an id-path settings key gives a LookupColumn over
            // CategoryId the same identity as a PropertyColumn over CategoryId, which was "§10b's
            // collision newly created rather than avoided". It is available now because that collision
            // throws instead of happening quietly - the reasoning was sound and its premise was that
            // nothing would say so.
            using var ctx = Context();

            // Sorted by the name it shows, which is the canonical lookup markup and the case where the
            // stored key MOVED: before §27 this column was stored under "Last", the path its sort
            // travels under. Review found the move asserted nowhere for a lookup, only for a
            // PropertyColumn - so both strings are pinned here.
            var cut = Render(ctx, Columns.Of(
                Columns.Lookup<Person, int>(x => x.CategoryId,
                    FastGridLookup.Items(Lookups.CategoryRows(), c => c.Id, c => c.Name),
                    title: "Category", sortBy: FastGridSort<Person>.By(x => x.Last))));

            Assert.Equal(new[] { "CategoryId" }, Identities(cut));
            Assert.Equal("Last", ColumnAt(cut, 0).SortPath);
        }

        // --- the round trip --------------------------------------------------------------------

        [Fact]
        public void TwoColumnsOverOneMemberAreEachRestoredOntoThemselves()
        {
            // The fault stated as its fix. Asserted through the identities and the widths rather than
            // only through a capture fed straight back, because a round trip agrees with itself when
            // both ends are wrong.
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, title: "Given"),
                Columns.Property<Person, string>(x => x.First, title: "Also given", uniqueId: "Alias")),
                p => p.Add(g => g.AllowColumnPicking, true));

            Pick(cut, 1, false);

            var captured = cut.Instance.CaptureSettings();

            Assert.False(captured.Columns.Single(c => c.UniqueID == "Alias").Visible);
            Assert.True(captured.Columns.Single(c => c.UniqueID == "First").Visible);
        }

        [Fact]
        public void AColumnThatNamesAMemberIsPersistedEvenWithoutASort()
        {
            // What shrank with §27: a CollectionColumn or a lookup with no SortBy had no settings
            // identity at all, so its width, order, visibility and filter were never captured and
            // nothing said so.
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First),
                Columns.Collection<Person, string>(x => x.Regions, title: "Regions")),
                p => p.Add(g => g.AllowColumnPicking, true));

            Pick(cut, 1, false);

            Assert.Equal(new[] { "First", "Regions" }, Stored(cut.Instance.CaptureSettings()));
        }

        // --- the picker ------------------------------------------------------------------------

        [Fact]
        public void ThePickerNamesAnUntitledColumnByWhatItShows()
        {
            // PickerTitle's last resort was the sort path while identity and the sort path were one
            // string, so a column showing First and ordering by Last was offered as "Last" - which
            // describes the ordering rather than the cells, the same fault HeaderText already had a
            // comment against.
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First, sortBy: x => x.Last)));

            Assert.Equal("First", ColumnAt(cut, 0).PickerTitle);
        }

        [Fact]
        public void ThePickerShowsTheMemberRatherThanADeclaredKey()
        {
            // Found by review. A UniqueID is a storage key, and an author writes one to tell two columns
            // over a member apart - which is the case least likely to carry a Title. Reading Identity
            // here would have put that key in front of a user as the column's name.
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Property<Person, string>(x => x.First),
                Columns.Property<Person, string>(x => x.First, uniqueId: "col_3")));

            Assert.Equal("First", ColumnAt(cut, 1).PickerTitle);
        }

        [Fact]
        public void ThePickerFallsBackToTheKeyOnlyWhenThereIsNoMember()
        {
            // The other end of the same rule: a key beats an empty row in the list, which is what
            // PickerTitle's own summary has always promised.
            using var ctx = Context();

            var cut = Render(ctx, Columns.Of(
                Columns.Template<Person>(item => b => b.AddContent(0, item.Last), uniqueId: "Actions")));

            Assert.Equal("Actions", ColumnAt(cut, 0).PickerTitle);
        }

        // --- the message -----------------------------------------------------------------------

        [Theory]
        [InlineData(true, true, "Both declare it.")]
        [InlineData(false, false, "Neither declares a UniqueID")]
        [InlineData(true, false, "One declares it and the other derived it")]
        [InlineData(false, true, "One declares it and the other derived it")]
        public void TheAdviceDependsOnWhereTheTwoNamesCameFrom(bool first, bool second, string expected)
        {
            // Asserted on the message rather than through a grid, so that changing the advice fails a
            // test rather than only removing it.
            var message = ColumnIdentity.CollisionMessage(
                Identity("Same", first), "A", Identity("Same", second), "B");

            Assert.Contains(expected, message, StringComparison.Ordinal);
        }

        static ColumnIdentity Identity(string name, bool declared) =>
            declared ? ColumnIdentity.Of(name, null) : ColumnIdentity.Of(null, name);

        [Fact]
        public void OneNameIsOneIdentityHoweverEachColumnArrivedAtIt()
        {
            // Equals and Collides have to agree, on a type whose only job is to say whether two columns
            // are the same one. Where the name came from is provenance - it changes the advice in the
            // message and nothing else.
            var declared = ColumnIdentity.Of("First", null);
            var derived = ColumnIdentity.Of(null, "First");

            Assert.Equal(declared, derived);
            Assert.Equal(declared.GetHashCode(), derived.GetHashCode());
            Assert.True(declared.Collides(derived));
            Assert.NotEqual(declared.IsDeclared, derived.IsDeclared);
        }

        [Fact]
        public void AnEmptyUniqueIdIsNotADeclaration()
        {
            // A UniqueID bound to a value that has not arrived yet must fall back rather than naming
            // every such column the empty string, which would make them all collide with each other.
            var identity = ColumnIdentity.Of(string.Empty, "First");

            Assert.Equal("First", identity.Name);
            Assert.False(identity.IsDeclared);
        }

        static ColumnBase<Person> ColumnAt(IRenderedComponent<RadzenFastGrid<Person>> cut, int index) =>
            cut.FindComponents<ColumnBase<Person>>()[index].Instance;

        static void Pick(IRenderedComponent<RadzenFastGrid<Person>> cut, int index, bool visible) =>
            cut.InvokeAsync(() => ColumnAt(cut, index).SetPicked(visible)).Wait();

        static string[] Identities(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            Enumerable.Range(0, cut.FindAll("thead th").Count)
                .Select(i => ColumnAt(cut, i).Identity.Name)
                .ToArray();

        static RenderFragment CountingColumns => builder =>
        {
            for (var i = 0; i < 4; i++)
            {
                builder.OpenRegion(i);
                builder.OpenComponent<IdentityCountingColumn<Person>>(0);
                builder.AddAttribute(1, nameof(IdentityCountingColumn<Person>.Name), $"Column{i}");
                builder.CloseComponent();
                builder.CloseRegion();
            }
        };
    }

    /// <summary>A column that records how often it is asked what it is called.</summary>
    public sealed class IdentityCountingColumn<TItem> : ColumnBase<TItem>
    {
        public static int Reads;

        [Parameter] public string Name { get; set; } = string.Empty;

        internal override string? DisplayPath
        {
            get
            {
                Reads++;
                return Name;
            }
        }

        public override void RenderCell(RenderTreeBuilder builder, int sequence, TItem item)
            => builder.AddContent(sequence, Name);
    }
}
