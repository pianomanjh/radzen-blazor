using System.Text;

namespace Radzen.FastGrid
{
    // Frozen columns. The theme makes a .rz-frozen-cell sticky and gives it a background, a z-index and
    // the seam shadow - but not an inset, and sticky without one does nothing at all. Something has to
    // say where each frozen column is pinned.
    //
    // RadzenDataGrid has the browser do it: updateFrozenColumnPositions measures the header, then writes
    // an inline style to every frozen cell in every row. That is a DOM write per frozen cell per row, and
    // it would have to run again after every render - on scroll, under virtualization, on every page.
    //
    // Here the inset is worked out on the server instead, because it is a property of the column rather
    // than of the cell: the table is table-layout:fixed with a colgroup, so a column's distance from the
    // edge is the sum of the declared widths between it and that edge. Composed once per column, folded
    // into the cell style that was already memoized and already emitted, and correct on first paint with
    // no script and no interop.
    //
    // The widths are added with calc() rather than parsed, so a column may be sized in any unit - px,
    // rem, a percentage, or a mixture - and the browser resolves it.
    public partial class RadzenFastGrid<TItem>
    {
        // Set while any drawn column is frozen. Everything below is skipped when it is false, which is
        // every grid that does not freeze anything.
        bool hasFrozenColumns;

        /// <summary>
        /// Works out which columns are pinned, to which edge, and how far in - once per render, over the
        /// columns as they are currently drawn, so reordering or hiding one is accounted for.
        /// </summary>
        /// <remarks>
        /// A run is the unbroken sequence of frozen columns at an edge. A column marked frozen that is
        /// not part of a run is stranded in the middle of the table, which is what RadzenDataGrid's
        /// "inner" classes are for; here it is simply drawn unfrozen.
        /// </remarks>
        void RefreshFrozenColumns()
        {
            hasFrozenColumns = false;

            for (var i = 0; i < visibleColumns.Count; i++)
            {
                if (visibleColumns[i].Frozen)
                {
                    hasFrozenColumns = true;

                    break;
                }
            }

            if (!hasFrozenColumns)
            {
                // Only worth clearing when something might be stale, which is a column that was frozen
                // on a previous render and is not now.
                for (var i = 0; i < visibleColumns.Count; i++)
                {
                    if (visibleColumns[i].IsFrozen)
                    {
                        visibleColumns[i].SetFrozen(null, null);
                    }
                }

                return;
            }

            for (var i = 0; i < visibleColumns.Count; i++)
            {
                visibleColumns[i].SetFrozen(null, null);
            }

            var left = 0;

            while (left < visibleColumns.Count
                && visibleColumns[left].Frozen
                && visibleColumns[left].FrozenPosition == FrozenColumnPosition.Left)
            {
                left++;
            }

            var right = visibleColumns.Count - 1;

            while (right >= left
                && visibleColumns[right].Frozen
                && visibleColumns[right].FrozenPosition == FrozenColumnPosition.Right)
            {
                right--;
            }

            PinLeftRun(left);
            PinRightRun(right + 1);
        }

        void PinLeftRun(int count)
        {
            if (count == 0)
            {
                return;
            }

            // The toggle column is a cell in every row and sits before the first data column, so a left
            // inset has to clear it. Its width is a theme variable rather than a number the server can
            // know, which is exactly what calc() is for.
            var offset = ExpandColumn ? new StringBuilder("var(--rz-grid-column-icon-width)") : new StringBuilder();

            for (var i = 0; i < count; i++)
            {
                var column = visibleColumns[i];
                var last = i == count - 1;

                column.SetFrozen(
                    last ? "rz-frozen-cell rz-frozen-cell-left rz-frozen-cell-left-end"
                         : "rz-frozen-cell rz-frozen-cell-left",
                    Inset("left", offset));

                if (last)
                {
                    break;
                }

                // A column with no width leaves every column after it unplaceable, so the run stops
                // being frozen here rather than pinning the rest to a position that is a guess.
                if (Append(offset, column) is false)
                {
                    for (var j = i + 1; j < count; j++)
                    {
                        visibleColumns[j].SetFrozen(null, null);
                    }

                    // The column that was going to be the last of the run is now the end of it, so it
                    // is the one that carries the seam.
                    column.SetFrozen("rz-frozen-cell rz-frozen-cell-left rz-frozen-cell-left-end",
                        Inset("left", offset));

                    return;
                }
            }
        }

        void PinRightRun(int start)
        {
            if (start >= visibleColumns.Count)
            {
                return;
            }

            var offset = new StringBuilder();

            for (var i = visibleColumns.Count - 1; i >= start; i--)
            {
                var column = visibleColumns[i];
                var first = i == start;

                column.SetFrozen(
                    first ? "rz-frozen-cell rz-frozen-cell-right rz-frozen-cell-right-end"
                          : "rz-frozen-cell rz-frozen-cell-right",
                    Inset("right", offset));

                if (first)
                {
                    break;
                }

                if (Append(offset, column) is false)
                {
                    for (var j = start; j < i; j++)
                    {
                        visibleColumns[j].SetFrozen(null, null);
                    }

                    column.SetFrozen("rz-frozen-cell rz-frozen-cell-right rz-frozen-cell-right-end",
                        Inset("right", offset));

                    return;
                }
            }
        }

        /// <summary>Adds a column's width to the running offset, or false when it declares none.</summary>
        bool Append(StringBuilder offset, ColumnBase<TItem> column)
        {
            var width = column.EffectiveWidth ?? ColumnWidth;

            if (string.IsNullOrEmpty(width))
            {
                return false;
            }

            if (offset.Length > 0)
            {
                offset.Append(" + ");
            }

            offset.Append(width);

            return true;
        }

        /// <summary>
        /// The inset declaration for an offset built so far. The first column of a run is at zero, and a
        /// single term needs no calc() around it.
        /// </summary>
        static string Inset(string edge, StringBuilder offset) => offset.Length switch
        {
            0 => edge + ":0",
            _ => offset.ToString().Contains('+', System.StringComparison.Ordinal)
                ? edge + ":calc(" + offset + ")"
                : edge + ":" + offset,
        };
    }
}
