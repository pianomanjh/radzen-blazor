using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Radzen.FastGrid.Tests
{
    /// <summary>
    /// Whether a settings restore is answered with a reload, which is the one question
    /// <c>AsyncOwnsData</c> is asked that had no test of its own. §24 measured every site that reads the
    /// predicate; this is the one that survived removal in both directions.
    /// </summary>
    /// <remarks>
    /// Both directions matter and they fail differently. Reading it as "this grid does not load" leaves
    /// a restored sort drawn over rows still in the old order. Reading it as "every grid loads" answers
    /// each restore with a reload, which raises <c>SettingsChanged</c>, which hands the grid new settings
    /// - the loop §10 records as having spun the circuit at several thousand renders a second.
    /// <para>
    /// The first load is deliberately not the subject. §23's deferral runs the owed load after
    /// <c>ApplySettings</c>, so it composes from the restored state whether or not anything asked for a
    /// reload - which is why <c>ARestoredSettingsSortCostsOneQuery</c> cannot see this fault. What the
    /// flag still owns alone is a restore arriving at a grid that has already drawn.
    /// </para>
    /// </remarks>
    public class FastGridSettingsReloadTests
    {
        sealed class RecordingExecutor : IFastGridQueryExecutor
        {
            public List<string> Materialized { get; } = new();

            public bool IsSupported<T>(IQueryable<T> queryable) => true;

            public Task<int> CountAsync<T>(IQueryable<T> q, CancellationToken token = default) =>
                Task.FromResult(q.Count());

            public Task<List<T>> ToListAsync<T>(IQueryable<T> q, CancellationToken token = default)
            {
                Materialized.Add(q.Expression.ToString());

                return Task.FromResult(q.ToList());
            }
        }

        static string[] FirstNames(IRenderedComponent<RadzenFastGrid<Person>> cut) =>
            cut.FindAll("tbody tr")
                .Where(row => row.QuerySelectorAll("td").Length > 0)
                .Select(row => row.QuerySelectorAll("td")[0].TextContent)
                .ToArray();

        [Fact]
        public void SettingsArrivingAtADrawnGridOverAnExecutorSourceReRunTheQuery()
        {
            using var ctx = new TestContext();
            var executor = new RecordingExecutor();

            ctx.Services.AddSingleton<IFastGridQueryExecutor>(executor);
            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = ctx.RenderComponent<RadzenFastGrid<Person>>(p =>
            {
                p.Add(g => g.Data, People.Sample().AsQueryable());
                p.Add(g => g.AllowSorting, true);
                p.Add(g => g.ChildContent, Columns.Of(
                    Columns.Property<Person, string>(x => x.First),
                    Columns.Property<Person, int>(x => x.Id)));
            });

            // Drawn, loaded, and in source order: the state the restore has to change.
            Assert.Single(executor.Materialized);
            Assert.Equal(new[] { "Carol", "Alice", "Dave", "Bob" }, FirstNames(cut));

            cut.SetParametersAndRender(p => p.Add(g => g.Settings, new FastGridSettings
            {
                Columns = new List<FastGridColumnSettings>
                {
                    new() { Property = "First", SortOrder = SortOrder.Descending },
                },
            }));

            // Asked again, and asked for the restored order - not merely re-sorted on the way out.
            Assert.Equal(2, executor.Materialized.Count);
            Assert.Contains("OrderByDescending(", executor.Materialized[1], StringComparison.Ordinal);
            Assert.Equal(new[] { "Dave", "Carol", "Bob", "Alice" }, FirstNames(cut));
        }

        [Fact]
        public void AGridThatDoesNotLoadAnswersASettingsRestoreWithNoReloadAtAll()
        {
            // The other direction, and the one that shipped once. The parent hands back a fresh object
            // each time, which is what round-tripping through storage does and what the recorded loop
            // needed: an object the grid has not already seen is a restore rather than its own echo.
            using var ctx = new TestContext();

            ctx.JSInterop.Mode = JSRuntimeMode.Loose;

            var cut = ctx.RenderComponent<EchoingSettingsHost>(p => p.Add(h => h.Data, People.Sample()));

            // Not "few" - none. An in-memory grid has already drawn the restored state, because the
            // render that applied it composed from it.
            Assert.Equal(0, cut.Instance.Echoes);
        }

        /// <summary>
        /// A parent that stores what the grid gives it and hands back a copy, stopping after a bounded
        /// number of exchanges so a grid that will not settle fails an assertion rather than hanging.
        /// </summary>
        public sealed class EchoingSettingsHost : ComponentBase
        {
            [Parameter] public IEnumerable<Person> Data { get; set; } = default!;

            /// <summary>How many echoes to feed before refusing, which bounds a non-settling grid.</summary>
            [Parameter] public int Limit { get; set; } = 10;

            public int Echoes { get; private set; }

            FastGridSettings settings = new() { Columns = new List<FastGridColumnSettings>() };

            protected override void BuildRenderTree(RenderTreeBuilder builder)
            {
                builder.OpenComponent<RadzenFastGrid<Person>>(0);
                builder.AddAttribute(1, nameof(RadzenFastGrid<Person>.Data), Data);
                builder.AddAttribute(2, nameof(RadzenFastGrid<Person>.Settings), settings);
                builder.AddAttribute(3, nameof(RadzenFastGrid<Person>.SettingsChanged),
                    EventCallback.Factory.Create<FastGridSettings>(this, Store));
                builder.AddAttribute(4, nameof(RadzenFastGrid<Person>.ChildContent), Columns.Of(
                    Columns.Property<Person, string>(x => x.First),
                    Columns.Property<Person, int>(x => x.Id)));
                builder.CloseComponent();
            }

            void Store(FastGridSettings raised)
            {
                Echoes++;

                if (Echoes > Limit)
                {
                    return;
                }

                settings = Copy(raised);

                StateHasChanged();
            }

            static FastGridSettings Copy(FastGridSettings from) => new()
            {
                PageSize = from.PageSize,
                CurrentPage = from.CurrentPage,
                Columns = from.Columns?.Select(c => new FastGridColumnSettings
                {
                    Property = c.Property,
                    SortOrder = c.SortOrder,
                    FilterValue = c.FilterValue,
                    FilterOperator = c.FilterOperator,
                    FilterText = c.FilterText,
                    Visible = c.Visible,
                    Width = c.Width,
                    OrderIndex = c.OrderIndex,
                }).ToList() ?? new List<FastGridColumnSettings>(),
            };
        }
    }
}
