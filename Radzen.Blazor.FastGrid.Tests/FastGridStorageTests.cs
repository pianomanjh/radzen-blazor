using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// §39's storage half: a key, somewhere to put it, and a reset that takes it back off. The store
    /// here is a dictionary rather than the browser - the browser path is the same three calls through
    /// <c>Browser</c>, and what is worth testing is what the grid does with what comes back.
    /// </summary>
    public class FastGridStorageTests
    {
        sealed class Store : IFastGridSettingsStore
        {
            readonly Dictionary<string, FastGridSettings> kept = new();

            public int Writes { get; private set; }

            public int Removes { get; private set; }

            public FastGridSettings? Held(string key) => kept.TryGetValue(key, out var s) ? s : null;

            public void Seed(string key, FastGridSettings settings) => kept[key] = settings;

            public ValueTask<FastGridSettings?> ReadAsync(string key) =>
                new(kept.TryGetValue(key, out var settings) ? settings : null);

            public ValueTask WriteAsync(string key, FastGridSettings settings)
            {
                Writes++;
                kept[key] = settings;

                return default;
            }

            public ValueTask RemoveAsync(string key)
            {
                Removes++;
                kept.Remove(key);

                return default;
            }
        }

        static TestContext Context()
        {
            var ctx = new TestContext();
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;
            ctx.JSInterop.SetupModule("_content/Radzen.Blazor/Radzen.Blazor.js");
            return ctx;
        }

        static IRenderedComponent<RadzenFastGrid<Person>> Render(TestContext ctx, Store store,
            string key = "people", System.Action<ComponentParameterCollectionBuilder<RadzenFastGrid<Person>>> extra = null) =>
            ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowSorting, true);
                p.Add(g => g.AllowFiltering, true);
                p.Add(g => g.AllowColumnPicking, true);
                p.Add(g => g.StorageKey, key);
                p.Add(g => g.SettingsStore, store);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(c => c.First, title: "First"),
                    Columns.Property<Person, string>(c => c.Last, title: "Last"),
                    Columns.Property<Person, decimal>(c => c.Salary, title: "Salary")));
                extra?.Invoke(p);
            });

        static string[] Body(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindAll("tbody tr td:first-child").Select(c => c.TextContent.Trim()).ToArray();

        // --- the round trip --------------------------------------------------------------------

        [Fact]
        public async Task AUserChangeIsStoredUnderTheKey()
        {
            using var ctx = Context();
            var store = new Store();

            var cut = Render(ctx, store);

            Assert.Equal(0, store.Writes);

            await cut.InvokeAsync(() => cut.Instance.SortBy(cut.Instance.VisibleColumns[0]));

            Assert.True(store.Writes > 0);
            Assert.NotNull(store.Held("people"));
            Assert.Equal(SortOrder.Ascending, store.Held("people")!.Columns!
                .Single(c => c.UniqueID == "First").SortOrder);
        }

        [Fact]
        public async Task AStoredSortComesBackOnASecondGrid()
        {
            using var ctx = Context();
            var store = new Store();

            var first = Render(ctx, store);
            await first.InvokeAsync(() => first.Instance.SortBy(first.Instance.VisibleColumns[0]));

            var second = Render(ctx, store);

            // Ascending by first name: Alice, Bob, Carol, Dave. The sample's own order starts Carol.
            Assert.Equal("Alice", Body(second)[0]);
        }

        [Fact]
        public void AGridWithNoKeyStoresNothingAndReadsNothing()
        {
            using var ctx = Context();
            var store = new Store();

            store.Seed("people", new FastGridSettings
            {
                Version = FastGridSettings.CurrentVersion,
                Columns = new List<FastGridColumnSettings>
                {
                    new() { UniqueID = "First", SortOrder = SortOrder.Ascending }
                }
            });

            var cut = Render(ctx, store, key: null);

            Assert.Equal("Carol", Body(cut)[0]);
            Assert.Equal(0, store.Writes);
        }

        [Fact]
        public void TwoGridsUnderDifferentKeysDoNotSeeEachOther()
        {
            using var ctx = Context();
            var store = new Store();

            store.Seed("people", new FastGridSettings
            {
                Version = FastGridSettings.CurrentVersion,
                Columns = new List<FastGridColumnSettings>
                {
                    new() { UniqueID = "First", SortOrder = SortOrder.Ascending }
                }
            });

            var mine = Render(ctx, store, key: "people");
            var theirs = Render(ctx, store, key: "people-picker");

            Assert.Equal("Alice", Body(mine)[0]);
            Assert.Equal("Carol", Body(theirs)[0]);
        }

        // --- the version stamp -----------------------------------------------------------------

        [Fact]
        public void ABlobWithNoVersionIsDiscardedRatherThanRead()
        {
            using var ctx = Context();
            var store = new Store();

            // What System.Text.Json makes of a RadzenDataGrid blob: the names it shares with this
            // shape land, and Version - which upstream has never written - stays null.
            store.Seed("people", new FastGridSettings
            {
                Version = null,
                Columns = new List<FastGridColumnSettings>
                {
                    new() { UniqueID = "First", SortOrder = SortOrder.Ascending, Visible = false }
                }
            });

            var cut = Render(ctx, store);

            // Neither half of it applied: not the sort, and not the hidden column.
            Assert.Equal("Carol", Body(cut)[0]);
            Assert.Equal(new[] { "First", "Last", "Salary" },
                cut.Instance.VisibleColumns.Select(c => c.Title).ToArray());
        }

        [Fact]
        public void ABlobOfAnotherVersionIsDiscardedToo()
        {
            using var ctx = Context();
            var store = new Store();

            store.Seed("people", new FastGridSettings
            {
                Version = FastGridSettings.CurrentVersion + 1,
                Columns = new List<FastGridColumnSettings>
                {
                    new() { UniqueID = "First", SortOrder = SortOrder.Ascending }
                }
            });

            Assert.Equal("Carol", Body(Render(ctx, store))[0]);
        }

        [Fact]
        public async Task WhatTheGridWritesCarriesTheCurrentVersion()
        {
            using var ctx = Context();
            var store = new Store();

            var cut = Render(ctx, store);
            await cut.InvokeAsync(() => cut.Instance.SortBy(cut.Instance.VisibleColumns[0]));

            Assert.Equal(FastGridSettings.CurrentVersion, store.Held("people")!.Version);
        }

        // --- the browser, which is what a grid gets when it names no store ---------------------

        const string ModulePath = "./_content/Radzen.Blazor.FastGrid/fastgrid.js";

        /// <summary>
        /// §18's hazard, for the three names this piece added: a name is a string at the call site, a
        /// string in the script, and a string in whatever doubles it, so renaming two of the three
        /// leaves the third passing. This reads the script.
        /// </summary>
        [Fact]
        public void TheScriptExportsTheThreeNamesTheGridInvokes()
        {
            var script = File.ReadAllText(Path.Combine(System.AppContext.BaseDirectory, "fastgrid.js"));

            foreach (var name in new[] { "readSetting", "writeSetting", "removeSetting" })
            {
                Assert.Contains($"export function {name}(", script, System.StringComparison.Ordinal);
            }
        }

        [Fact]
        public void WithNoStoreTheGridRestoresFromTheJsonTheScriptAnswers()
        {
            using var ctx = Context();

            // The stored shape, written out by hand rather than round-tripped, so this pins the wire
            // format: enums by name, and nothing for what nobody chose.
            ctx.JSInterop.SetupModule(ModulePath)
                .Setup<string>("readSetting", "people")
                .SetResult("{\"Version\":1,\"Columns\":[{\"UniqueID\":\"First\",\"SortOrder\":\"Ascending\"}]}");

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowSorting, true);
                p.Add(g => g.StorageKey, "people");
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(c => c.First, title: "First"),
                    Columns.Property<Person, decimal>(c => c.Salary, title: "Salary")));
            });

            Assert.Equal("Alice", Body(cut)[0]);
        }

        [Fact]
        public async Task WithNoStoreAUserChangeGoesToTheScriptUnderTheKey()
        {
            using var ctx = Context();
            ctx.JSInterop.SetupModule(ModulePath);

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowSorting, true);
                p.Add(g => g.StorageKey, "people");
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(c => c.First, title: "First")));
            });

            await cut.InvokeAsync(() => cut.Instance.SortBy(cut.Instance.VisibleColumns[0]));

            var write = ctx.JSInterop.Invocations["writeSetting"].Single();

            Assert.Equal("people", write.Arguments[0]);

            // Round-trippable, and carrying the sort the user just made: what crosses is the whole
            // stored shape as text, not an object the serializer will make its own decisions about
            // on the way back.
            var json = Assert.IsType<string>(write.Arguments[1]);

            Assert.Contains("\"Version\":1", json, System.StringComparison.Ordinal);
            Assert.Contains("\"SortOrder\":\"Ascending\"", json, System.StringComparison.Ordinal);
        }

        [Fact]
        public async Task ClearSettingsTellsTheScriptToForgetTheKey()
        {
            using var ctx = Context();
            ctx.JSInterop.SetupModule(ModulePath);

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowSorting, true);
                p.Add(g => g.StorageKey, "people");
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(c => c.First, title: "First")));
            });

            await cut.InvokeAsync(() => cut.Instance.ClearSettings());

            Assert.Equal("people", ctx.JSInterop.Invocations["removeSetting"].Single().Arguments[0]);
        }

        /// <summary>
        /// The read that actually suspends, which is the only kind a browser performs.
        /// </summary>
        /// <remarks>
        /// Every other test here answers the module synchronously, so the <c>await</c> never yields and
        /// the synchronisation context is never lost. That is what let a restore ship green while
        /// throwing in every real browser: with <c>ConfigureAwait(false)</c> on the read, the
        /// continuation resumed off the renderer's dispatcher and <c>StateHasChanged</c> threw *"the
        /// current thread is not associated with the Dispatcher"* - inside a lifecycle method, so the
        /// circuit logged it and the only symptom on screen was a grid ignoring what it had just read.
        /// Leaving the result unset until after the first render is how a test reaches that at all.
        /// </remarks>
        [Fact]
        public void ARestoreThatSuspendsStillReachesTheGrid()
        {
            using var ctx = Context();

            var planned = ctx.JSInterop.SetupModule(ModulePath).Setup<string>("readSetting", "people");

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowSorting, true);
                p.Add(g => g.StorageKey, "people");
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(c => c.First, title: "First"),
                    Columns.Property<Person, decimal>(c => c.Salary, title: "Salary")));
            });

            // The grid has drawn and is waiting on the read.
            Assert.Equal("Carol", Body(cut)[0]);

            planned.SetResult(
                "{\"Version\":1,\"Columns\":[{\"UniqueID\":\"First\",\"SortOrder\":\"Ascending\"}]}");

            cut.WaitForAssertion(() => Assert.Equal("Alice", Body(cut)[0]));
        }

        // --- the reset -------------------------------------------------------------------------

        [Fact]
        public async Task ClearSettingsForgetsTheKeyAndPutsTheGridBack()
        {
            using var ctx = Context();
            var store = new Store();

            var cut = Render(ctx, store);
            await cut.InvokeAsync(() => cut.Instance.SortBy(cut.Instance.VisibleColumns[0]));

            Assert.Equal("Alice", Body(cut)[0]);

            await cut.InvokeAsync(() => cut.Instance.ClearSettings());

            Assert.Equal(1, store.Removes);
            Assert.Null(store.Held("people"));
            Assert.Equal("Carol", Body(cut)[0]);
        }

        [Fact]
        public async Task ClearSettingsPutsBackASortTheMarkupDeclared()
        {
            using var ctx = Context();
            var store = new Store();

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample());
                p.Add(g => g.AllowSorting, true);
                p.Add(g => g.StorageKey, "declared");
                p.Add(g => g.SettingsStore, store);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(c => c.First, title: "First"),
                    Columns.Property<Person, decimal>(c => c.Salary, title: "Salary",
                        sortOrder: SortOrder.Ascending)));
            });

            // Ascending by salary puts Dave at 1000 in front. Descending would put Carol there - and
            // so does no sort at all, since Carol leads the sample's own order, so a descending
            // declaration could not tell "the declared sort came back" from "nothing sorts this".
            Assert.Equal("Dave", Body(cut)[0]);

            await cut.InvokeAsync(() => cut.Instance.SortBy(cut.Instance.VisibleColumns[0]));

            Assert.Equal("Alice", Body(cut)[0]);

            await cut.InvokeAsync(() => cut.Instance.ClearSettings());

            // Back to what the markup says - not to unsorted, which is what clearing the sort list
            // and stopping there would give: a declared sort is seeded once and never re-seeded.
            Assert.Equal("Dave", Body(cut)[0]);
        }

        /// <summary>
        /// §39: restored state is not a change its user made. On a grid the handler loads, the restore
        /// owes a reload - and that reload must not announce, or the grid writes the key straight back
        /// having done nothing at all.
        /// </summary>
        [Fact]
        public void ARestoreOnALoadDataGridDoesNotWriteItselfBack()
        {
            using var ctx = Context();
            var store = new Store();

            store.Seed("loaded", new FastGridSettings
            {
                Version = FastGridSettings.CurrentVersion,
                Columns = new List<FastGridColumnSettings>
                {
                    new() { UniqueID = "First", SortOrder = SortOrder.Ascending }
                }
            });

            var rows = People.Sample();

            ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, rows);
                p.Add(g => g.Count, rows.Count);
                p.Add(g => g.AllowSorting, true);
                p.Add(g => g.StorageKey, "loaded");
                p.Add(g => g.SettingsStore, store);
                p.Add(g => g.LoadData, EventCallback.Factory.Create<LoadDataArgs>(ctx.Renderer, _ => { }));
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(c => c.First, title: "First")));
            });

            Assert.Equal(0, store.Writes);
        }

        /// <summary>
        /// §39's third weight-carrying case, through the store rather than through the parameter: a
        /// stored filter the column's type cannot parse takes that filter with it, and the column comes
        /// back unfiltered rather than throwing or showing a narrower answer as the user's own.
        /// </summary>
        [Fact]
        public void AStoredFilterTheColumnCannotParseRestoresUnfiltered()
        {
            using var ctx = Context();
            var store = new Store();

            store.Seed("people", new FastGridSettings
            {
                Version = FastGridSettings.CurrentVersion,
                Columns = new List<FastGridColumnSettings>
                {
                    // Salary is a decimal. Nothing here parses as one.
                    new()
                    {
                        UniqueID = "Salary",
                        FilterOperator = FastGridFilterOperator.Equals,
                        FilterValues = new List<string?> { "not-a-number" }
                    }
                }
            });

            var cut = Render(ctx, store);

            Assert.Equal(4, cut.FindAll("tbody tr").Count);
        }

        /// <summary>
        /// The positive control for the test above. Without it, "four rows" could mean the filter was
        /// dropped for the right reason or that stored filters are never applied at all.
        /// </summary>
        [Fact]
        public void AStoredFilterTheColumnCanParseIsApplied()
        {
            using var ctx = Context();
            var store = new Store();

            store.Seed("people", new FastGridSettings
            {
                Version = FastGridSettings.CurrentVersion,
                Columns = new List<FastGridColumnSettings>
                {
                    new()
                    {
                        UniqueID = "Salary",
                        FilterOperator = FastGridFilterOperator.Equals,
                        FilterValues = new List<string?> { "4000" }
                    }
                }
            });

            var cut = Render(ctx, store);

            Assert.Single(cut.FindAll("tbody tr"));
        }

        [Fact]
        public async Task ClearSettingsPutsBackAColumnTheUserHid()
        {
            using var ctx = Context();
            var store = new Store();

            var cut = Render(ctx, store);

            await cut.InvokeAsync(() => cut.Instance.VisibleColumns[1].SetPicked(false));
            cut.Render();

            Assert.Equal(new[] { "First", "Salary" },
                cut.Instance.VisibleColumns.Select(c => c.Title).ToArray());

            await cut.InvokeAsync(() => cut.Instance.ClearSettings());
            cut.Render();

            Assert.Equal(new[] { "First", "Last", "Salary" },
                cut.Instance.VisibleColumns.Select(c => c.Title).ToArray());
        }

        // --- the two changes that announced without storing ------------------------------------

        // Both of these passed with the fault in place for as long as a handler was wired, which is why
        // neither grid below wires one: a StorageKey and no SettingsChanged is what §39 calls the
        // ordinary way to use the storage, and it is the only arrangement the fault could be seen in.
        // Found in the playground rather than here - a drag from 260px to 540px stored nothing while the
        // pager beside it stored CurrentPage - and the suite was 1204 green while it was true.

        [Fact]
        public async Task ADraggedWidthIsStoredUnderTheKey()
        {
            using var ctx = Context();
            var store = new Store();

            var cut = Render(ctx, store);

            await cut.InvokeAsync(() => cut.Instance.OnColumnResized(0, 321));

            Assert.True(store.Writes > 0);
            Assert.Equal("321px", store.Held("people")!.Columns!.Single(c => c.UniqueID == "First").Width);
        }

        [Fact]
        public async Task AMovedColumnIsStoredUnderTheKey()
        {
            using var ctx = Context();
            var store = new Store();

            var cut = Render(ctx, store);

            await cut.InvokeAsync(() => cut.Instance.ReorderColumn(0, 2));

            Assert.True(store.Writes > 0);

            // As OrderIndex per column, not as the order of the entries: the capture walks the columns
            // in declaration order and records where each one was dragged to. Moving First to the end
            // is what these three numbers say.
            Assert.Equal(new[] { ("First", 2), ("Last", 0), ("Salary", 1) },
                store.Held("people")!.Columns!.Select(c => (c.UniqueID!, c.OrderIndex!.Value)).ToArray());
        }

        /// <summary>
        /// The two drag settles that do not reload: their names for what a user changed.
        /// </summary>
        /// <remarks>
        /// A theory rather than two tests because the property is one property and the symmetry is the
        /// point - the sweep caught the reorder half surviving while the resize half was covered, which
        /// is what two hand-written siblings drift into.
        /// </remarks>
        public static TheoryData<string, Func<RadzenFastGrid<Person>, Task>> DragSettles => new()
        {
            { "a resize", grid => grid.OnColumnResized(0, 321) },
            { "a reorder", grid => grid.ReorderColumn(0, 2) },
        };

        /// <summary>
        /// A drag settle still reaches the application's handler, and still reaches it awaited.
        /// </summary>
        /// <remarks>
        /// Both halves are here because the review found the second one going. Routing the three
        /// announce sites through one method took <c>RefreshAsync</c>'s answer to a question the sites
        /// disagreed about: it does not await the callback, on the argument in its own comment, and the
        /// resize and the reorder always had. Awaited is what makes a consumer's handler throwing
        /// something the caller sees rather than an unobserved task, which is what this asserts - a
        /// fire-and-forget invoke would let the call return cleanly.
        /// </remarks>
        [Theory]
        [MemberData(nameof(DragSettles))]
        public async Task ADragSettleReachesTheHandlerAndIsAwaited(string _, Func<RadzenFastGrid<Person>, Task> settle)
        {
            using var ctx = Context();
            var store = new Store();

            FastGridSettings handed = null;

            var cut = Render(ctx, store, extra: p => p.Add(g => g.SettingsChanged,
                EventCallback.Factory.Create<FastGridSettings>(new object(), s => handed = s)));

            await cut.InvokeAsync(() => settle(cut.Instance));

            Assert.NotNull(handed);
            Assert.NotNull(handed.Columns);

            // Asynchronously, and that is the whole point: a handler that throws on the way in throws at
            // the call site whether the task is awaited or discarded, so it cannot tell the two apart.
            // One that yields first faults the task instead, and only an await sees that. The first
            // draft of this threw synchronously and passed with the fault reinstated.
            var throwing = Render(ctx, store, key: "throwing", extra: p => p.Add(g => g.SettingsChanged,
                EventCallback.Factory.Create<FastGridSettings>(new object(),
                    async (FastGridSettings _) =>
                    {
                        await Task.Yield();

                        throw new InvalidOperationException("from the handler");
                    })));

            var caught = await Assert.ThrowsAsync<InvalidOperationException>(
                () => throwing.InvokeAsync(() => settle(throwing.Instance)));

            Assert.Equal("from the handler", caught.Message);
        }

        [Fact]
        public async Task ADraggedWidthComesBackOnASecondGrid()
        {
            using var ctx = Context();
            var store = new Store();

            var first = Render(ctx, store);
            await first.InvokeAsync(() => first.Instance.OnColumnResized(0, 321));

            var second = Render(ctx, store);

            Assert.Equal("321px", second.Instance.VisibleColumns[0].EffectiveWidth);
        }
    }
}
