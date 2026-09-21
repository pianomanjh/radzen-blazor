using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;

namespace Radzen.FastGrid
{
    /// <summary>
    /// §39: the settings the grid can put somewhere, and the reset that takes them back off.
    /// </summary>
    public partial class RadzenFastGrid<TItem>
    {
        /// <summary>
        /// Where this grid's settings are kept, and what tells it to keep any. A grid with no key
        /// stores nothing and reads nothing.
        /// </summary>
        /// <remarks>
        /// <strong>There is no default, and that is the design.</strong> The wrapper §38 read this
        /// feature off defaulted its key to <c>typeof(T).Name</c>, so a list page and a picker over
        /// one entity shared a blob and restored each other's columns - a fault nobody chose, because
        /// nobody wrote the key down and so nobody checked it. Two grids over one type is the ordinary
        /// case, not the exotic one; a key that has to be written is a key someone read.
        /// </remarks>
        [Parameter] public string? StorageKey { get; set; }

        /// <summary>
        /// Where the bytes go. Null is the viewer's own browser, under <c>localStorage</c>.
        /// </summary>
        [Parameter] public IFastGridSettingsStore? SettingsStore { get; set; }

        /// <summary>
        /// Whether the key has been read yet. One read per grid, on the first render: a second would
        /// be a restore over state the user has since changed.
        /// </summary>
        bool storageRead;

        /// <summary>
        /// Reads the stored settings and applies them, once, on the first render.
        /// </summary>
        /// <remarks>
        /// It lands after a render rather than before one, which is the same shape the README already
        /// describes for a grid on <c>LoadData</c> or the executor: the state arrives, the next render
        /// applies it, and <see cref="ApplySettings" /> decides for itself whether a reload is owed.
        /// Nothing here has to know which source the grid is over.
        /// </remarks>
        async Task RestoreStoredSettingsAsync()
        {
            if (storageRead || StorageKey is not { Length: > 0 } key)
            {
                return;
            }

            storageRead = true;

            // No ConfigureAwait(false) on either, and it is load-bearing rather than habit. Everything
            // below touches component state and ends in StateHasChanged, which asserts it is on the
            // renderer's dispatcher - and dropping the synchronisation context here is what takes it
            // off. The store is the application's code and may genuinely yield; the browser certainly
            // does.
            //
            // **No test could see this and the playground could.** A module double answers
            // synchronously, so the await never suspends, the context is never lost, and fourteen
            // tests pass against a restore that throws in every real browser:
            // "The current thread is not associated with the Dispatcher", from StateHasChanged, with
            // the read already done and the settings already in hand. The exception is raised inside
            // a lifecycle method whose failure the circuit logs rather than shows, so the only symptom
            // on screen is a grid that quietly ignores what it just read.
            var stored = SettingsStore is { } store
                ? await store.ReadAsync(key)
                : await ReadFromBrowserAsync(key);

            // A blob of the wrong vintage is discarded rather than read, and §39 argues why at length:
            // RadzenDataGrid's own settings overlap this shape by name and by JSON type at seven
            // points, its derived UniqueID equals this grid's column identity, and one property name
            // - FilterOperator - is an enum whose numbering disagrees with this one from 7 onwards.
            // Two of the disagreeing values take no filter value, so they rebuild into a live filter
            // the user never wrote. A version nothing else stamps is what stops that being silent.
            if (stored is null || stored.Version != FastGridSettings.CurrentVersion)
            {
                return;
            }

            appliedSettings = stored;
            settingsPending = true;

            StateHasChanged();
        }

        /// <summary>Writes what the grid just raised, if anything is keeping it.</summary>
        /// <remarks>
        /// Off <see cref="CaptureSettings" />'s own object rather than capturing a second time: the
        /// two would have to agree, and one of them is what the application was handed.
        /// </remarks>
        async Task StoreSettingsAsync(FastGridSettings settings)
        {
            if (StorageKey is not { Length: > 0 } key)
            {
                return;
            }

            // Stamped on the way out, so an application implementing the store round-trips it without
            // having to know it exists.
            settings.Version = FastGridSettings.CurrentVersion;

            if (SettingsStore is { } store)
            {
                await store.WriteAsync(key, settings).ConfigureAwait(false);

                return;
            }

            await WriteToBrowserAsync(key, settings).ConfigureAwait(false);
        }

        /// <summary>
        /// Puts the grid back to what its markup declares, and forgets what was stored.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>It clears the overrides and nothing else.</strong> It does not touch the column
        /// list and it does not compute an order - §39 declined to port the consuming application's
        /// reset for exactly that reason, and doing either here would reintroduce the upstream fault
        /// that workaround exists for. Width, visibility and position come back on their own, because
        /// each is read as "what a drag said, or what the markup did" on every render.
        /// </para>
        /// <para>
        /// A declared sort is the one thing that does not come back on its own. <c>OnParametersSet</c>
        /// seeds it once and calls it a starting state rather than a live binding, so clearing the
        /// sort list would leave a grid whose markup declares <c>SortOrder</c> unsorted - which is not
        /// what its markup says and not what a reset means. It is re-seeded here, in declaration
        /// order, which is the only order markup expresses.
        /// </para>
        /// </remarks>
        public async Task ClearSettings()
        {
            if (StorageKey is { Length: > 0 } key)
            {
                // The same rule as the restore above: what follows mutates columns and reloads.
                if (SettingsStore is { } store)
                {
                    await store.RemoveAsync(key);
                }
                else
                {
                    await RemoveFromBrowserAsync(key);
                }
            }

            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];

                column.SetFilter(null, null, null);
                column.SetPicked(null);
                column.SetResizedWidth(null);
                column.SetReorderedIndex(null);
            }

            sorts.Clear();

            for (var i = 0; i < columns.Count; i++)
            {
                if (columns[i].SortOrder is { } order && columns[i].CanSort)
                {
                    ApplyDeclaredSort(columns[i], order);
                }
            }

            skip = 0;
            pageSize = declaredPageSize > 0 ? declaredPageSize : pageSize;

            // Not announced, and this is the half of the reset that a test found rather than the
            // design: RefreshAsync raises and stores, so a reset that announced removed the key and
            // then wrote it straight back - with the declared sort in it, now recorded as a choice the
            // user made. The grid's declared state is not a setting anyone chose, which is the same
            // reason the first page is handed over unannounced.
            await RefreshAsync(announce: false);
        }

        async ValueTask<FastGridSettings?> ReadFromBrowserAsync(string key)
        {
            if (await BrowserAsync().ConfigureAwait(false) is not { } browser)
            {
                return null;
            }

            var json = await browser.ReadSettingAsync(key).ConfigureAwait(false);

            if (json is not { Length: > 0 })
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize(json, FastGridSettingsJson.Default.FastGridSettings);
            }
            catch (JsonException)
            {
                // Something else's key, or ours from before a shape change that the version stamp
                // cannot catch because the blob does not parse far enough to carry one. Either way the
                // grid draws what its markup declares, which is the same answer the version check gives.
                return null;
            }
        }

        async ValueTask WriteToBrowserAsync(string key, FastGridSettings settings)
        {
            if (await BrowserAsync().ConfigureAwait(false) is not { } browser)
            {
                return;
            }

            await browser.WriteSettingAsync(key,
                JsonSerializer.Serialize(settings, FastGridSettingsJson.Default.FastGridSettings))
                .ConfigureAwait(false);
        }

        async ValueTask RemoveFromBrowserAsync(string key)
        {
            if (await BrowserAsync().ConfigureAwait(false) is { } browser)
            {
                await browser.RemoveSettingAsync(key).ConfigureAwait(false);
            }
        }
    }
}
