using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// The lifetime of the two listeners this grid binds in the browser: bound when the feature is on,
    /// let go when it is switched off, and recorded only once the browser has confirmed it.
    /// </summary>
    /// <remarks>
    /// The grid's own tests drive the real interop path, because what they are about is which calls it
    /// makes and which ids the markup leaves behind for those calls to name - and bUnit records both.
    /// The module's tests drive <see cref="Attachment{TPayload}" /> against a fake, because what they
    /// are about is what happens when a call fails, and a failure cannot be staged through the module
    /// path at all.
    /// </remarks>
    public class FastGridAttachmentTests
    {
        const string ModulePath = "./_content/Radzen.Blazor.FastGrid/fastgrid.js";

        static RenderFragment TwoColumns() => Columns.Of(
            Columns.Property<Person, string>(x => x.First, title: "First"),
            Columns.Property<Person, string>(x => x.Last, title: "Last"));

        /// <summary>
        /// A browser that answers the way a browser does. The stub's own answer is null, which is the
        /// script's way of saying it could not find the element - so a grid measured against it never
        /// believes it holds a guard, and every rule below about letting one go is unreachable.
        /// </summary>
        static BunitJSModuleInterop Navigating(TestContext ctx)
        {
            var module = ctx.JSInterop.SetupModule(ModulePath);

            module.Setup<RadzenFastGrid<Person>.NavigationMetrics>("attachNavigation", _ => true)
                .SetResult(new RadzenFastGrid<Person>.NavigationMetrics { Rtl = false, Rows = 12 });

            return module;
        }

        static IRenderedComponent<RadzenFastGrid<Person>> Grid(TestContext ctx,
            Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>> extra = null) =>
            ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.ChildContent, TwoColumns());
                extra?.Invoke(p);
            });

        // ---- the grid's own lifetime ----

        [Fact]
        public void TheKeyGuardIsLetGoWhenTheGridStopsNavigating()
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = Navigating(ctx);
            var cut = Grid(ctx, p => p.Add(g => g.AllowKeyboardNavigation, true));

            Assert.Single(module.Invocations["attachNavigation"]);
            Assert.Empty(module.Invocations["detachNavigation"]);

            cut.SetParametersAndRender(p => p.Add(g => g.AllowKeyboardNavigation, false));

            Assert.Single(module.Invocations["detachNavigation"]);
        }

        [Fact]
        public void TheViewKeepsTheNameItsGuardIsLookedUpByAfterNavigationIsSwitchedOff()
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            Navigating(ctx);

            var cut = Grid(ctx, p => p.Add(g => g.AllowKeyboardNavigation, true));
            var id = cut.Find(".rz-data-grid-data").Id;

            Assert.False(string.IsNullOrEmpty(id));

            cut.SetParametersAndRender(p => p.Add(g => g.AllowKeyboardNavigation, false));

            // The name outlasts the feature, because releasing the guard means naming the element it is
            // bound to and that happens after this render.
            Assert.Equal(id, cut.Find(".rz-data-grid-data").Id);

            // The name is not the feature. The tab stop and the key handler do go, or the grid would
            // still take focus and still swallow keys nothing acts on.
            Assert.Null(cut.Find(".rz-data-grid-data").GetAttribute("tabindex"));
            Assert.False(cut.Markup.Contains("onkeydown", StringComparison.Ordinal));
        }

        [Fact]
        public void AGridThatNeverNavigatesAndNeverDelegatesIsNeverNamed()
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.JSInterop.SetupModule(ModulePath);

            var cut = Grid(ctx);

            // The latch is what keeps this true: an id emitted whenever the element exists would be a
            // frame per grid paid for two features that are off, which is the rule in §3 this grid is
            // built around.
            Assert.Null(cut.Find(".rz-data-grid-data").GetAttribute("id"));
            Assert.Null(cut.Find("tbody").GetAttribute("id"));
        }

        [Fact]
        public void TheListenerIsLetGoWhenVirtualizationTakesTheDelegationAway()
        {
            using var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var module = ctx.JSInterop.SetupModule(ModulePath);
            module.Setup<bool>("attach", _ => true).SetResult(true);

            var cut = Grid(ctx, p => p.Add(g => g.RowClick,
                EventCallback.Factory.Create<Person>(this, _ => { })));

            var id = cut.Find("tbody").Id;

            Assert.False(string.IsNullOrEmpty(id));
            Assert.Single(module.Invocations["attach"]);

            // Virtualization stops the grid delegating, so the markup goes back to per-cell handlers and
            // the listener has to go - otherwise every click is raised twice, once by the listener still
            // bound and once by the handler that replaced it. The detach names the tbody by the id it
            // still carries; a grid that dropped that id would be asking the script to remove a listener
            // from an element it can no longer find.
            cut.SetParametersAndRender(p => p.Add(g => g.AllowVirtualization, true));

            Assert.Equal(id, Assert.Single(module.Invocations["detach"]).Arguments[0]);
            Assert.Equal(id, cut.Find("tbody").Id);
        }

        // ---- the module ----

        /// <summary>An adapter that records what it was asked to do, and can be told to fail.</summary>
        sealed class Fake<TPayload>
        {
            internal List<TPayload> Attached { get; } = new List<TPayload>();

            internal int Detached { get; private set; }

            internal bool Answer { get; set; } = true;

            internal Exception Throws { get; set; }

            internal Attachment<TPayload> Attachment() => new Attachment<TPayload>(
                payload =>
                {
                    Attached.Add(payload);

                    return Throws is not null ? throw Throws : Task.FromResult(Answer);
                },
                () =>
                {
                    Detached++;

                    return Task.CompletedTask;
                });
        }

        [Fact]
        public async Task ABindingIsRecordedOnlyOnceTheBrowserHasConfirmedIt()
        {
            var fake = new Fake<string> { Throws = new InvalidOperationException("the circuit went away") };
            var attachment = fake.Attachment();

            Assert.Equal(AttachResult.Failed, await attachment.SyncAsync(true, "keys"));

            // Recorded before the call instead, the grid would believe it holds a listener it does not -
            // and would then never ask for the one thing that could put it right, because there is
            // nothing to release.
            Assert.False(attachment.Attached);
            Assert.Equal(0, fake.Detached);
        }

        [Fact]
        public async Task ADeclinedBindingIsNotRetried()
        {
            var fake = new Fake<string> { Answer = false };
            var attachment = fake.Attachment();

            Assert.Equal(AttachResult.Declined, await attachment.SyncAsync(true, "keys"));
            Assert.Equal(AttachResult.Unchanged, await attachment.SyncAsync(true, "keys"));

            Assert.Single(fake.Attached);
            Assert.False(attachment.Attached);
        }

        [Fact]
        public async Task AChangedPayloadIsBoundAgainAndAnUnchangedOneIsNot()
        {
            var fake = new Fake<string>();
            var attachment = fake.Attachment();

            Assert.Equal(AttachResult.Attached, await attachment.SyncAsync(true, "click"));
            Assert.Equal(AttachResult.Unchanged, await attachment.SyncAsync(true, "click"));
            Assert.Equal(AttachResult.Attached, await attachment.SyncAsync(true, "click+contextmenu"));

            Assert.Equal(new[] { "click", "click+contextmenu" }, fake.Attached);

            // Not detached in between: the scripts both detach before they bind, which is what lets this
            // spend one round trip on a re-attach rather than two.
            Assert.Equal(0, fake.Detached);
        }

        [Fact]
        public async Task TheAttemptIsForgottenWithTheListener()
        {
            // The payload is its type's default on purpose, and that is the whole test. Letting go also
            // clears the remembered payload, so for any other value the guard below would see a change
            // and attach again whether or not the attempt had been forgotten - which is what the first
            // version of this test did, and it passed with the rule deleted.
            var fake = new Fake<int>();
            var attachment = fake.Attachment();

            await attachment.SyncAsync(true, 0);

            Assert.Equal(AttachResult.Detached, await attachment.SyncAsync(false, 0));
            Assert.False(attachment.Attached);
            Assert.Equal(1, fake.Detached);

            // Left set, a feature switched off and on again reaches the unchanged-payload guard and
            // returns, having neither a listener nor whatever the caller renders instead of one.
            Assert.Equal(AttachResult.Attached, await attachment.SyncAsync(true, 0));
            Assert.Equal(2, fake.Attached.Count);
        }

        [Fact]
        public async Task LettingGoOfNothingIsNotACall()
        {
            var fake = new Fake<string>();
            var attachment = fake.Attachment();

            Assert.Equal(AttachResult.Unchanged, await attachment.SyncAsync(false, "keys"));

            Assert.Empty(fake.Attached);
            Assert.Equal(0, fake.Detached);
        }
    }
}
