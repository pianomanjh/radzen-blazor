using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Radzen.FastGrid
{
    /// <summary>When a grid sizes its columns to the content they hold.</summary>
    public enum AutoFitMode
    {
        /// <summary>Never. Columns are as wide as the markup and the browser make them.</summary>
        None,

        /// <summary>Once, when rows first reach the page. A user is free to resize afterwards.</summary>
        Once,

        /// <summary>
        /// Only when asked - by double-clicking a resize handle, or through <c>AutoFitAsync</c>.
        /// </summary>
        OnDemand
    }

    /// <summary>What a fit does when the columns it sized do not fit the space there is.</summary>
    public enum AutoFitOverflow
    {
        /// <summary>
        /// Let the table be as wide as its columns need and the grid scroll sideways. Every column is
        /// readable and some of them are off-screen.
        /// </summary>
        Scroll,

        /// <summary>
        /// Keep the table inside its container by taking the difference out of the columns that can
        /// spare it, down to each one's <c>MinWidth</c>. Columns marked
        /// <see cref="AutoFitPriority.Required" /> keep their measured width and give up nothing, so a
        /// grid with more required width than container still scrolls - there is no arrangement that
        /// would not.
        /// </summary>
        Fit
    }

    /// <summary>How hard a column argues for its measured width when there is not enough room.</summary>
    public enum AutoFitPriority
    {
        /// <summary>
        /// Gives up width, proportionally to how much it has to spare, down to its <c>MinWidth</c>.
        /// Its content truncates the way it did before being fitted at all.
        /// </summary>
        BestEffort,

        /// <summary>
        /// Keeps the width its content needs. Use it for the columns a row is identified by - the ones
        /// that make a scrollbar worth having rather than the ones a scrollbar is hiding.
        /// </summary>
        Required
    }

    // Sizing a column to what is in it. The measurement has to happen in the browser: the table is
    // table-layout:fixed, so nothing sizes itself to its content and there is no layout to read back -
    // the width has to be worked out and written. The script does both in one pass, for the same reason
    // the resize drag does: a feature whose whole job is to set a handful of strings should not cost a
    // render of every row to deliver them.
    public partial class RadzenFastGrid<TItem>
    {
        /// <summary>Whether, and when, columns are sized to the content they hold.</summary>
        /// <remarks>
        /// A column that declares its own <c>Width</c> is left alone, and one that sets
        /// <c>AutoFit="false"</c> opts out. Nothing is emitted and nothing is imported while this is
        /// <see cref="AutoFitMode.None" />.
        /// </remarks>
        [Parameter] public AutoFitMode AutoFitColumns { get; set; }

        /// <summary>What a fit does when its columns do not fit the space there is.</summary>
        /// <remarks>
        /// <see cref="AutoFitOverflow.Scroll" /> is the default and is what a grid did before this
        /// existed. <see cref="AutoFitOverflow.Fit" /> keeps the table inside its container and follows
        /// it as that container changes size, which is the case for the same grid on a laptop and on a
        /// desktop.
        /// </remarks>
        [Parameter] public AutoFitOverflow AutoFitOverflow { get; set; }

        /// <summary>
        /// The overflow the last fit was run under. Changing the mode has to re-arm a
        /// <see cref="AutoFitMode.Once" /> grid: the two answers are different widths, and the fit
        /// that already happened produced the other one.
        /// </summary>
        AutoFitOverflow lastOverflow;

        bool AutoFitEnabled => AutoFitColumns != AutoFitMode.None;

        /// <summary>The id the script resolves the table by. Only emitted for a grid that fits.</summary>
        internal string TableElementId => ElementId + "-table";

        /// <summary>
        /// The column left with no width, so the browser hands it whatever is left over. Skipped by the
        /// colgroup, which is the only place a width would otherwise come back.
        /// </summary>
        ColumnBase<TItem>? bareColumn;

        /// <summary>
        /// Whether a <see cref="AutoFitMode.Once" /> fit is still owed. Cleared by the fit that lands,
        /// not by the render that arms it: under virtualization the rows arrive after the render, so
        /// the script is what decides there is something to measure.
        /// </summary>
        bool autoFitPending = true;

        /// <summary>
        /// Sizes every eligible column to the content it currently holds.
        /// </summary>
        /// <remarks>
        /// What it measures is what is rendered - the current page, or the current virtualized window.
        /// A grid that has scrolled past the widest value in a column has not seen it, and neither has
        /// this.
        /// </remarks>
        public Task AutoFitAsync() => AutoFitAsync(null);

        /// <summary>Sizes one column, or every eligible column when <paramref name="column" /> is null.</summary>
        public async Task AutoFitAsync(ColumnBase<TItem>? column)
        {
            if (!AutoFitEnabled)
            {
                return;
            }

            await RunAutoFitAsync(column, wait: false, automatic: false);
        }

        /// <summary>
        /// Arms the one automatic fit, if this grid owes one. Called from the render loop; the script
        /// waits for rows rather than this deciding whether there are any, because
        /// <c>Virtualize</c> re-renders itself and its window arrives without a render of the grid.
        /// </summary>
        async Task AutoFitOnFirstRenderAsync()
        {
            if (AutoFitColumns == AutoFitMode.Once && lastOverflow != AutoFitOverflow)
            {
                lastOverflow = AutoFitOverflow;
                autoFitPending = true;
            }

            if (AutoFitColumns != AutoFitMode.Once || !autoFitPending)
            {
                return;
            }

            // A lookup column whose names have not arrived is drawing blank cells, and the script waits
            // for rows rather than for anything in them - so a fit taken now settles that column at its
            // header width and the names arrive into a column too narrow for them, permanently, because
            // nothing invalidates a fit. Deferred rather than re-armed when they land: a column that
            // jumps after the grid looked settled is what deciding Once stays instant already refused.
            //
            // This gives back, temporarily, the property that disarming on the attempt exists to
            // provide - so every way out of that fetch has to hand it over again, or a lookup that
            // never resolves is a fit that never fires.
            if (LookupsOutstanding)
            {
                return;
            }

            // Disarmed by the attempt rather than by the answer. A fit that comes back with nothing -
            // no script, no table, or a view that moved while it was in flight - has still had its one
            // go, and re-arming on the answer means a grid whose script never loads asks again on every
            // render it ever does.
            autoFitPending = false;

            await RunAutoFitAsync(null, wait: true, automatic: true);
        }

        async Task RunAutoFitAsync(ColumnBase<TItem>? column, bool wait, bool automatic)
        {
            var script = await ModuleAsync();

            if (script is null || visibleColumns.Count == 0)
            {
                return;
            }

            var targets = new List<int>();

            for (var i = 0; i < visibleColumns.Count; i++)
            {
                if (visibleColumns[i].CanAutoFit(automatic)
                    && (column is null || ReferenceEquals(visibleColumns[i], column)))
                {
                    targets.Add(i);
                }
            }

            if (targets.Count == 0)
            {
                return;
            }

            var minimums = new string?[targets.Count];
            var maximums = new string?[targets.Count];
            var required = new bool[targets.Count];

            for (var i = 0; i < targets.Count; i++)
            {
                minimums[i] = visibleColumns[targets[i]].MinWidth;
                maximums[i] = visibleColumns[targets[i]].MaxWidth;
                required[i] = visibleColumns[targets[i]].AutoFitPriority == AutoFitPriority.Required;
            }

            // Only a fit of the whole grid places the bare column: fitting one column in isolation must
            // not take the stretch away from wherever it currently sits.
            var bare = column is null ? BareColumnIndex(targets) : -1;

            // The view the measurement is about to be taken in. A sort, a filter or a page turn that
            // lands while it is in flight moves every row it measured, and the widths that come back
            // are then about rows the grid is no longer showing.
            var generation = viewGeneration;

            string?[]? widths;

            try
            {
                // Animated only for a fit somebody asked for. The one Once runs is the grid settling
                // into its first layout, and animating that reads as a page still loading rather than
                // as an answer to anything - where a re-fit is exactly the case where showing what
                // moved is the point.
                // Fitting to the container is only ever a whole-grid answer: one column cannot be
                // redistributed against, and a single-column fit is a user pointing at that column
                // rather than at the layout. But it is not the same as leaving the mode - "keep" is
                // what says so, and a plain false said the other thing and took the fit down.
                var overflow = AutoFitOverflow != AutoFitOverflow.Fit
                    ? "scroll"
                    : column is null ? "fit" : "keep";

                widths = await script.InvokeAsync<string?[]?>("autoFit", TableElementId, targets,
                    minimums, maximums, ExpandColumn ? 1 : 0, bare, wait, !automatic, overflow,
                    required);
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                // The circuit going away mid-measurement, which is an ordinary way for this to end and
                // has no caller to report to. Same reasoning as the click attach; JSDisconnectedException
                // does not derive from JSException, so the narrow catch misses the case this is for.
                return;
            }

            if (widths is null || generation != viewGeneration || disposed)
            {
                return;
            }

            for (var i = 0; i < widths.Length && i < targets.Count; i++)
            {
                visibleColumns[targets[i]].SetAutoFitWidth(widths[i], replacingUserWidth: !automatic);
            }

            // Only a fit of the whole grid places it. A one-column fit sends -1, and assigning that
            // unconditionally would clear the bare column the last full fit chose - taking the stretch
            // away from a column nobody touched, on some later unrelated render.
            if (column is null)
            {
                bareColumn = bare >= 0 && bare < visibleColumns.Count ? visibleColumns[bare] : null;
            }

            // The widths are already on the page - the script wrote them. What is not is the frozen
            // inset: it is a calc() sum composed here from those same widths and emitted on every
            // frozen cell, so leaving it alone pins them to what the columns used to be. A grid with
            // nothing frozen has nothing stale and skips the render, which is the whole point of
            // letting the script write.
            if (hasFrozenColumns)
            {
                StateHasChanged();
            }
        }

        /// <summary>
        /// The column left bare, so the browser gives it the space the fitted ones did not take: the
        /// last visible column that is not frozen and is being fitted.
        /// </summary>
        /// <remarks>
        /// The last rather than the widest, though the widest is what a distribution pass would pick.
        /// Which column is widest is a property of the data, so a filter would change which one
        /// stretches and the table would rearrange itself for no reason a reader can see.
        /// <para>
        /// Never a frozen one: a frozen run ends at the first column of it that declares no width, so
        /// leaving a frozen column bare unpins every column after it.
        /// </para>
        /// </remarks>
        int BareColumnIndex(List<int> targets)
        {
            for (var i = targets.Count - 1; i >= 0; i--)
            {
                if (!visibleColumns[targets[i]].Frozen)
                {
                    return targets[i];
                }
            }

            return -1;
        }
    }
}
