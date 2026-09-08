using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using Radzen.Documents.Spreadsheet;

namespace Radzen.FastGrid.Export
{
    /// <summary>
    /// The export the band menu offers once <c>AddRadzenFastGridExport</c> has registered it.
    /// </summary>
    /// <remarks>
    /// Scoped rather than singleton, because it holds an <see cref="IJSRuntime" />, which belongs to one
    /// circuit on Blazor Server and would be shared across users by a singleton.
    /// </remarks>
    sealed class WorkbookExporter : IFastGridExporter
    {
        // The MIME type for .xlsx. Written out rather than guessed from the extension, because the
        // browser uses it to decide whether it can preview the file rather than only save it.
        const string ContentType =
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

        readonly IJSRuntime? runtime;
        readonly FastGridExportServiceOptions options;

        public WorkbookExporter(FastGridExportServiceOptions options, IJSRuntime? runtime = null)
        {
            this.options = options;
            this.runtime = runtime;
        }

        /// <inheritdoc />
        public async Task ExportAsync<TItem>(RadzenFastGrid<TItem> grid)
        {
            ArgumentNullException.ThrowIfNull(grid);

            // Built here, on the renderer's thread, and that is not incidental: ToWorkbook reads
            // FilteredRows and VisibleColumns, which are the grid's own state and change under a render.
            var workbook = grid.ToWorkbook(new FastGridExportOptions<TItem>
            {
                SheetName = options.SheetName,
                IncludeHeader = options.IncludeHeader,
                FreezeHeader = options.FreezeHeader,
                AddTable = options.AddTable,
                AutoFitColumns = options.AutoFitColumns,
            });

            if (options.OnExport is { } handler)
            {
                await handler(workbook, grid);

                return;
            }

            await SaveAsync(workbook, grid);
        }

        /// <summary>
        /// Writes the workbook and hands it to the browser to save.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>The bytes go to the browser through upstream's <c>Radzen.downloadFile</c></strong>,
        /// which already unwraps a <c>DotNetStreamReference</c> and already handles the difference
        /// between Server and WebAssembly - so this package needs no <c>wwwroot</c>, no module and no
        /// Razor SDK. It revokes the object URL in the same tick as the anchor's click, which this
        /// branch offered upstream as a fault and withdrew: the browser bugs that made an immediate
        /// revoke lose a download were fixed in Firefox 50 and WebKit in 2020, and no download here was
        /// ever seen to fail. The blobs are large - 2.5 MB at 50,000 rows, measured - and that is a
        /// reason to keep an eye on it rather than a reproduction.
        /// </para>
        /// <para>
        /// <strong>The write is off the renderer's thread and the hand-off is back on it</strong>, which
        /// is the split the measurement asks for: <c>SaveToStream</c> is about 1.5 s at 50,000 rows and
        /// would hold a Blazor Server circuit for all of it, and by then the workbook is a detached
        /// object no render can touch, so moving it is safe. The grid's own state was read before this.
        /// </para>
        /// <para>
        /// <strong>No <c>ConfigureAwait(false)</c>, and §39 is why.</strong> The continuation calls into
        /// JavaScript, which on Blazor Server must happen on the renderer's dispatcher; discarding the
        /// synchronization context here resumes off it, and that is the fault §39 spent a session
        /// finding - it passes every test, because a doubled module answers synchronously and the await
        /// never suspends.
        /// </para>
        /// </remarks>
        async Task SaveAsync(Workbook workbook, object grid)
        {
            if (runtime is null)
            {
                // No JavaScript runtime, which is a prerendering pass or a test host. The workbook was
                // built and there is nowhere to put it; throwing here would make a rendering test of a
                // grid carrying the entry fail for a reason that has nothing to do with the grid.
                return;
            }

            // await using, so the stream is released on the throwing paths too. DotNetStreamReference
            // disposes it as well; disposing a MemoryStream twice is a no-op, and one of the two has to
            // be the one that always runs.
            await using var stream = new MemoryStream();

            await Task.Run(() => workbook.SaveToStream(stream));

            stream.Position = 0;

            using var reference = new DotNetStreamReference(stream);

            try
            {
                await runtime.InvokeVoidAsync("Radzen.downloadFile",
                    options.FileName(grid), reference, ContentType);
            }
            catch (JSDisconnectedException)
            {
                // The circuit went away while the file was being written - a second and a half of
                // writing is long enough for that to be ordinary rather than exceptional. Nothing is
                // left to save it to.
            }
        }
    }
}
