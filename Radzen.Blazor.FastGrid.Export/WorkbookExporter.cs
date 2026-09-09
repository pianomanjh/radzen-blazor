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

            var exported = new FastGridExportOptions<TItem>
            {
                SheetName = options.SheetName,
                IncludeHeader = options.IncludeHeader,
                FreezeHeader = options.FreezeHeader,
                AddTable = options.AddTable,
                AutoFitColumns = options.AutoFitColumns,
            };

            // A handler that asked for the workbook asked for the model, and building it is the only way
            // to answer that. Built here, on the renderer's thread, and that is not incidental:
            // ToWorkbook reads FilteredRows and VisibleColumns, which are the grid's own state and
            // change under a render.
            if (options.OnExport is { } handler)
            {
                await handler(grid.ToWorkbook(exported), grid);

                return;
            }

            // §65: no workbook, so nothing holds a cell per value. The grid's state is read here, before
            // the first row is asked for, for the same reason the workbook was built here.
            if (options.Destination is { } sink)
            {
                var destination = await sink(grid);

                await using (destination)
                {
                    await grid.SaveToStreamAsync(destination, exported);
                }

                return;
            }

            await SaveAsync(grid, exported);
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
        /// <strong>The write is on the renderer's thread, and the <c>Task.Run</c> that used to move it
        /// went with the workbook.</strong> That split was safe because a built workbook is a detached
        /// object no render can touch; a streamed write is not detached - it calls
        /// <c>CellTextOf</c> per cell and pulls rows from a composition over the grid's own source, so
        /// running it on a pool thread would read grid state while a render writes it. Correctness over
        /// latency, and the latency is better anyway: what a circuit used to hold for one 1.5 s block it
        /// now holds in slices, and every await in an asynchronous row source is a point the circuit
        /// gets back. An application that wants it off the circuit entirely sets <c>Destination</c> and
        /// owns where the bytes go.
        /// </para>
        /// <para>
        /// <strong>No <c>ConfigureAwait(false)</c>, and §39 is why.</strong> The continuation calls into
        /// JavaScript, which on Blazor Server must happen on the renderer's dispatcher; discarding the
        /// synchronization context here resumes off it, and that is the fault §39 spent a session
        /// finding - it passes every test, because a doubled module answers synchronously and the await
        /// never suspends.
        /// </para>
        /// </remarks>
        async Task SaveAsync<TItem>(RadzenFastGrid<TItem> grid, FastGridExportOptions<TItem> exported)
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

            // The buffer the browser needs is the last O(rows) thing here, and §65 says so rather than
            // hiding it: streaming into a MemoryStream caps the win at the buffer. What it removes is
            // the workbook in front of it, which was the larger half. An application that wants the
            // buffer gone too sets Destination.
            await grid.SaveToStreamAsync(stream, exported);

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
