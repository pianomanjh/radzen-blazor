using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.JSInterop;

namespace Radzen.FastGrid
{
    /// <summary>
    /// The nine calls this component makes into its own browser module, each named and typed once.
    /// </summary>
    /// <remarks>
    /// Not an abstraction, and deliberately not one. There is no fake behind this and no interface over
    /// it: bUnit's module double already reaches every export - including the ones that answer a value,
    /// which is how a test stages a <see cref="RadzenFastGrid{TItem}.NavigationMetrics" /> - so an
    /// <c>IBrowser</c> would buy reach the suite has and cost a second nine-method implementation to be
    /// kept in step with a script it cannot see. §18 has the probes that settled that. <c>Attachment</c>
    /// earns its two delegates because it has rules of its own to test; this forwards.
    /// <para>
    /// What it is for is the half no test reaches. An export name used to be a string at the call site,
    /// a string in the script and a string in the test that doubled it, so renaming two of the three
    /// left the third passing - a module double in loose mode answers a name it was never set up for.
    /// Each name now exists once on this side. And an argument list written in order by a caller, read
    /// in order by the script and read in order a third time by a test decoder is a list where swapping
    /// two entries is silent in all three: <see cref="AutoFitAsk" /> is why that is no longer true of
    /// the worst of them.
    /// </para>
    /// <para>
    /// A <c>readonly struct</c> over the one module reference. §3: this is a wrapper around a field
    /// rather than an object per call, and every method here runs once per attach, per fit or per focus
    /// - never per row and never per cell.
    /// </para>
    /// </remarks>
    internal readonly struct Browser<TItem>
    {
        readonly IJSObjectReference module;

        internal Browser(IJSObjectReference module) => this.module = module;

        /// <summary>
        /// Binds the delegating pointer listener to the tbody, answering whether the element was there
        /// to bind to. Attaching twice is how the set of events is changed; the script detaches first.
        /// </summary>
        internal ValueTask<bool> AttachAsync(string bodyId,
            DotNetObjectReference<RadzenFastGrid<TItem>> handler, ClickKinds kinds) =>
            module.InvokeAsync<bool>("attach", bodyId, handler, kinds);

        /// <summary>Releases the pointer listener. Silent for a tbody that never had one.</summary>
        internal ValueTask DetachAsync(string bodyId) => module.InvokeVoidAsync("detach", bodyId);

        /// <summary>
        /// Binds the key guard to the view and answers what the view measures, or null when the element
        /// is not there - which is what tells the caller the binding did not happen.
        /// </summary>
        internal ValueTask<RadzenFastGrid<TItem>.NavigationMetrics?> AttachNavigationAsync(
            string viewId, string[] keys) =>
            module.InvokeAsync<RadzenFastGrid<TItem>.NavigationMetrics?>("attachNavigation", viewId,
                keys);

        /// <summary>Releases the key guard.</summary>
        internal ValueTask DetachNavigationAsync(string viewId) =>
            module.InvokeVoidAsync("detachNavigation", viewId);

        /// <summary>
        /// Re-measures the view without touching the binding: the writing direction and how many rows
        /// fit, which are what the arrow keys and the page step are computed from.
        /// </summary>
        internal ValueTask<RadzenFastGrid<TItem>.NavigationMetrics?> MeasureNavigationAsync(
            string viewId) =>
            module.InvokeAsync<RadzenFastGrid<TItem>.NavigationMetrics?>("measureNavigation", viewId);

        /// <summary>
        /// Paints the cursor on a cell and scrolls it into view. The pinned runs and the item size are
        /// what the scroll has to clear: a frozen column and a virtualized row are both cases where the
        /// cell is in the DOM and still not visible.
        /// </summary>
        /// <remarks>
        /// <paramref name="itemSize" /> is a <c>float</c> where every argument beside it is an
        /// <c>int</c>, and that is not an oversight: it is <c>Virtualize</c>'s row height, which is a
        /// float there and is multiplied by the row index to get a scroll offset. Naming these types
        /// was what surfaced it - the untyped call took the float without comment, and a reader
        /// counting six numbers had nothing to tell them one of them was not a count.
        /// </remarks>
        internal ValueTask FocusCellAsync(string viewId, int row, int cell, int pinnedStart,
            int pinnedEnd, float itemSize) =>
            module.InvokeVoidAsync("focusCell", viewId, row, cell, pinnedStart, pinnedEnd, itemSize);

        /// <summary>Takes the cursor paint off whatever currently has it.</summary>
        internal ValueTask BlurCellAsync(string viewId) =>
            module.InvokeVoidAsync("blurCell", viewId);

        /// <summary>
        /// Stops watching a table's container. Held by the script rather than by the circuit, so
        /// nothing else releases it, and it is asked unconditionally - the script answers for a table
        /// it is not watching.
        /// </summary>
        internal ValueTask ReleaseFitAsync(string tableId) =>
            module.InvokeVoidAsync("releaseFit", tableId);

        /// <summary>
        /// Measures and redistributes the columns, answering the width to write into each fitted
        /// column or null for one it did not settle.
        /// </summary>
        internal ValueTask<string?[]?> AutoFitAsync(AutoFitAsk ask) =>
            module.InvokeAsync<string?[]?>("autoFit", ask);
    }

    /// <summary>Which pointer events the delegating listener has to answer.</summary>
    /// <remarks>
    /// Already an object across the seam rather than three arguments - this only gives it a name on
    /// this side. The script destructures <c>click</c>, <c>doubleClick</c> and <c>contextMenu</c>,
    /// which is what the serializer's camel casing makes of these.
    /// </remarks>
    internal readonly record struct ClickKinds(bool Click, bool DoubleClick, bool ContextMenu);

    /// <summary>Everything a fit is asked for, as one value rather than ten positional arguments.</summary>
    /// <remarks>
    /// This type was written in the test suite first, as <c>FastGridAutoFitTests.Ask</c>, beside a
    /// hand-rolled decoder that read <c>invocation.Arguments</c> by index - which is the caller's own
    /// bug copied into the thing that was supposed to catch it. Swapping <see cref="Min" /> and
    /// <see cref="Max" /> was silent in the caller, in the script and in the test at once. It is one
    /// object now in all three, and the test reads the object.
    /// <para>
    /// A record struct: one of these is built per fit, and a fit is a user gesture or a first render.
    /// </para>
    /// <para>
    /// One collection kind across all four sequences, and that is not tidiness either: they arrived as
    /// a <c>List</c>, two arrays and a third array, so a caller reading them had to know which was
    /// which - the test that reads this had to say <c>Length</c> for three of them and <c>Count</c> for
    /// the fourth. Nothing here indexes them; the serializer walks them.
    /// </para>
    /// </remarks>
    internal readonly record struct AutoFitAsk(
        string Table,
        IReadOnlyList<int> Indices,
        IReadOnlyList<string?> Min,
        IReadOnlyList<string?> Max,
        int ToggleOffset,
        int Bare,
        bool Wait,
        bool Animate,
        string Overflow,
        IReadOnlyList<bool> Required);

    /// <summary>
    /// The half of the browser seam that appears in no signature: what the script selects the grid's
    /// own markup by.
    /// </summary>
    /// <remarks>
    /// Names written as string literals in <c>RadzenFastGrid.cs</c> and <c>ColumnBase.cs</c>, hundreds
    /// of lines from each other, and read as selectors in <c>fastgrid.js</c>, with none of the three
    /// files mentioning the others. A rename on either side is silent: the script stops finding rows,
    /// or finds them and the C# carries on emitting something nothing looks for.
    /// <para>
    /// Every name here is one the grid <em>emits</em> and the script <em>selects</em>. Names the script
    /// writes rather than reads are not in this list and should not be added to it - the cursor's
    /// <c>rz-state-focused</c> is the theme's, put on by the script, and a constant for it here would
    /// be one nothing on this side could assert. It was in this list for a day and asserted by nothing,
    /// which is what that mistake looks like.
    /// </para>
    /// <para>
    /// These constants do not remove that - the script cannot import them, and there is no build step
    /// here to generate one side from the other. What they do is make the list exist, in one place, so
    /// that <c>FastGridBrowserContractTests</c> can assert a rendered grid still carries every one of
    /// them. A rename in the markup then fails a C# test instead of a browser.
    /// </para>
    /// </remarks>
    internal static class BrowserContract
    {
        /// <summary>The attribute carrying a row's index, which is how a delegated click resolves one.</summary>
        internal const string RowIndexAttribute = "data-r";

        /// <summary>Marks the row-detail toggle, whose clicks the delegating listener leaves alone.</summary>
        internal const string ToggleAttribute = "data-toggle";

        /// <summary>A drawn data row. What the cursor counts when rows carry no index of their own.</summary>
        internal const string DataRowClass = "rz-data-row";

        /// <summary>The span a cell's text lives in, which is what a fit measures.</summary>
        internal const string CellDataClass = "rz-cell-data";

        /// <summary>A header's title, measured to give a column its heading-width floor.</summary>
        internal const string ColumnTitleClass = "rz-column-title";

        /// <summary>The table a fit measures, reached as a direct child of the view.</summary>
        internal const string TablePath = ":scope > table";

        /// <summary>Where a fit writes its answer. A table without one cannot be fitted at all.</summary>
        internal const string ColgroupPath = ":scope > colgroup";

        /// <summary>
        /// The header row a fit measures the headings in, and the body it counts rows in. Both are
        /// direct children: an element in between breaks the script and breaks nothing a looser
        /// selector would notice.
        /// </summary>
        internal const string HeadRowPath = ":scope > thead > tr";

        /// <summary>The body a fit counts rows in, as a direct child of the table.</summary>
        internal const string BodyPath = ":scope > tbody";
    }
}
