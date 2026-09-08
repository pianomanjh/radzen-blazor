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
        /// <strong>One thing about upstream's is worth knowing and is not this package's to fix here.</strong>
        /// It calls <c>URL.revokeObjectURL</c> in the same tick as the anchor's click. A browser that has
        /// not finished reading the blob by then loses the file, and this is the one caller that
        /// routinely produces megabytes - 2.5 MB at 50,000 rows, measured. It is offered upstream on its
        /// own branch, which is what this branch did with #2696, #2702 and #2705.
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
                // The circuit went away while the file was being written - four seconds is long enough
                // for that to be ordinary rather than exceptional. Nothing is left to save it to.
            }
        }
    }
}
