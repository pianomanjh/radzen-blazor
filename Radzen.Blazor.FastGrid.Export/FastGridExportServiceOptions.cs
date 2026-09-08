using System;
using System.Threading.Tasks;
using Radzen.Documents.Spreadsheet;

namespace Radzen.FastGrid.Export
{
    /// <summary>
    /// What the registered export does, for every grid that shows the band menu.
    /// </summary>
    /// <remarks>
    /// Registration-wide, because the point of registering is that every grid gets the entry without
    /// being told. Anything one grid wants differently it gets by calling
    /// <see cref="FastGridExport.ToWorkbook{TItem}" /> itself, which is still the whole feature - or, for
    /// the two that genuinely vary per grid, from the grid this hands to <see cref="FileName" /> and
    /// <see cref="OnExport" />.
    /// </remarks>
    public sealed class FastGridExportServiceOptions
    {
        /// <summary>The name of the sheet inside the file.</summary>
        public string SheetName { get; set; } = "Sheet1";

        /// <summary>Whether the header row is written.</summary>
        public bool IncludeHeader { get; set; } = true;

        /// <summary>Whether the header row is frozen.</summary>
        public bool FreezeHeader { get; set; } = true;

        /// <summary>Whether the written range becomes a table, which draws Excel's filter buttons.</summary>
        public bool AddTable { get; set; } = true;

        /// <summary>Whether columns are sized to what is in them.</summary>
        public bool AutoFitColumns { get; set; } = true;

        /// <summary>
        /// The name of the file the browser saves, given the grid being exported.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <strong>It is handed the grid because two grids on one page would otherwise download two
        /// files with the same name</strong> - the review found that, and found the first draft's own
        /// remark claiming the per-grid case was what <see cref="OnExport" /> was for, when
        /// <c>OnExport</c> could not see the grid either.
        /// </para>
        /// <para>
        /// Typed as <c>object</c>, which is not pretty and is honest: these options are shared by every
        /// grid in an application and those grids have different row types, so there is no one
        /// <c>RadzenFastGrid&lt;TItem&gt;</c> to name here. Cast it if the name depends on which grid,
        /// and ignore it otherwise - which is what the default does.
        /// </para>
        /// </remarks>
        public Func<object, string> FileName { get; set; } = _ => "export.xlsx";

        /// <summary>
        /// What to do with the workbook, in place of saving it to the user's machine.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Null by default, which means the download. §40 refused a download outright - <em>"bytes to a
        /// browser is <c>IJSRuntime</c> and a data URL or a stream reference, it differs between Server
        /// and WebAssembly, and it is the application's to own"</em> - and that refusal is reversed
        /// here, deliberately, because a menu entry that produces nothing a user can open is not a
        /// feature. What the section got right is that some applications want the bytes to go somewhere
        /// else, and this is where they say so.
        /// </para>
        /// <para>
        /// The two halves of §40's reason survive: the difference between Server and WebAssembly is
        /// handled once, by <c>Radzen.downloadFile</c>, rather than by every application; and an
        /// application that wants to own it still can. The second argument is the grid, for
        /// <see cref="FileName" />'s reason.
        /// </para>
        /// </remarks>
        public Func<Workbook, object, Task>? OnExport { get; set; }
    }
}
