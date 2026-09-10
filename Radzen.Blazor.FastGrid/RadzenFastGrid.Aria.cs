using System.Collections.Generic;

namespace Radzen.FastGrid
{
    // Where a cell sits in the whole table, for the cases where the DOM cannot say.
    //
    // A grid that renders every row and every column needs none of this: the browser can count what it
    // has, and the ARIA specification says so outright - "if all of the columns are present in the DOM,
    // including aria-colindex is not necessary as user agents can calculate the column index". So the
    // rule here is that rule read literally. Nothing is emitted until the DOM stops being the whole
    // table, which is exactly two things: paging or virtualization windowing the rows, and the column
    // picker hiding a column.
    //
    // That is not tidiness, it is the budget - though not the part of it the design expected. Measured,
    // both attributes are free in bytes: a row index costs nothing and a cell index +0.09 KB at 1000
    // rows. What a cell index costs is *time*, about 1.1x, which is the same shape frozen columns have
    // at 1.10x for two frames on the cells of one column. So the 153 KB baseline never sees either, the
    // grid that pays is the one that would otherwise be lying, and the per-cell one is written in three
    // tiers rather than always - see HowMuchNumbering.
    public partial class RadzenFastGrid<TItem>
    {
        /// <summary>
        /// Each visible column's position in the whole set, 1-based and counting the toggle. Empty
        /// when nothing is hidden, which is also the answer to whether any of this is emitted.
        /// </summary>
        readonly List<int> columnIndexes = new();

        /// <summary>
        /// Whether the DOM holds a window of the rows rather than all of them, which is the condition
        /// the specification puts on emitting a row index at all.
        /// </summary>
        bool RowsAreCounted => Paging || AllowVirtualization;

        /// <summary>
        /// Rows of the header, which are rows of the grid and are numbered with the rest of them. The
        /// filter row is a second row of the same header rather than a thing of its own - the same
        /// fact that made it get missed when frozen columns were first pinned.
        /// </summary>
        int HeaderRows => ShowHeader ? (AllowFiltering ? 2 : 1) : 0;

        /// <summary>How much of the column numbering has to be written for it to be readable.</summary>
        enum ColumnNumbering
        {
            /// <summary>None: the drawn columns are the first n of the set, so counting them is right.</summary>
            None,

            /// <summary>One per row, on the first cell: an unbroken run that starts somewhere else.</summary>
            FirstCell,

            /// <summary>All of them: the run has a hole in it, so every position has to be given.</summary>
            EveryCell,
        }

        ColumnNumbering numbering;

        /// <summary>
        /// Whether the grid is drawing fewer columns than it has, which is when it has to say how many.
        /// </summary>
        bool ColumnsAreCounted => columnIndexes.Count > 0;

        /// <summary>Whether the cell drawn at <paramref name="index" /> carries its position.</summary>
        bool NumbersCell(int index) =>
            numbering == ColumnNumbering.EveryCell ||
            (numbering == ColumnNumbering.FirstCell && index == 0);

        /// <summary>
        /// The toggle is the first cell of the row and always column one, so it only ever needs a
        /// number where every cell gets one - the run that starts at it cannot be the one that has
        /// drifted.
        /// </summary>
        bool NumbersToggle => numbering == ColumnNumbering.EveryCell;

        /// <summary>
        /// The number of rows the grid has, header included, or <c>-1</c> where it is not yet known -
        /// which is what a virtualized grid has before its first count comes back, and what an
        /// asynchronous source has before it has loaded. Saying zero there would be a claim; -1 is the
        /// value the attribute defines for "unknown".
        /// </summary>
        string AriaRowCount()
        {
            var total = AllowVirtualization ? virtualTotal ?? -1 : TotalCount();

            return total < 0
                ? "-1"
                : IndexString(total + HeaderRows);
        }

        /// <summary>
        /// A data row's place among all of them: its position in the data set, after the header rows,
        /// counting from one. Paging offsets it by the page; virtualization already hands over an
        /// absolute index.
        /// </summary>
        string AriaRowIndex(int rowIndex) =>
            IndexString((AllowVirtualization ? rowIndex : skip + rowIndex) + HeaderRows + 1);

        /// <summary>The columns the grid has, counting the toggle and the ones the picker took away.</summary>
        string AriaColCount() =>
            IndexString(columns.Count + (ExpandColumn ? 1 : 0));

        /// <summary>
        /// Works out each visible column's position in the whole set, once per render pass, and only
        /// for a grid that is hiding one.
        /// </summary>
        /// <remarks>
        /// The frame is the declared order, because that is the only ordering a hidden column has a
        /// place in: a reorder index is a position among the columns that are *visible*, and a column
        /// nobody can see was never given one. On a grid that both hides and reorders, the cells
        /// therefore carry their declared positions rather than their drawn ones. That is the case the
        /// specification already has a rule for - non-contiguous columns need the index on every cell,
        /// which is what this emits - and it is the honest answer rather than a drawn position invented
        /// for a column that has none.
        /// </remarks>
        void RefreshColumnIndexes()
        {
            columnIndexes.Clear();
            numbering = ColumnNumbering.None;

            if (visibleColumns.Count == columns.Count)
            {
                return;
            }

            var toggle = ExpandColumn ? 1 : 0;

            for (var i = 0; i < visibleColumns.Count; i++)
            {
                columnIndexes.Add(columns.IndexOf(visibleColumns[i]) + toggle + 1);
            }

            numbering = HowMuchNumbering(toggle);
        }

        /// <summary>
        /// How much of it has to be written, which is the specification's own three cases rather than
        /// a simplification of them: nothing while a browser can reach the right answer by counting,
        /// one per row while the drawn columns are an unbroken run that starts late, and every cell
        /// once that run has a hole in it.
        /// </summary>
        /// <remarks>
        /// Worth the three cases rather than always writing every cell, and the reason is measured:
        /// one attribute on every cell of a thousand-row grid allocates nothing and costs about 1.1x
        /// the render time - the same shape frozen columns have, where two frames per cell buy 1.10x
        /// and no bytes. So hiding the last column of a grid, which needs none of this, should not pay
        /// for hiding the middle one.
        /// </remarks>
        ColumnNumbering HowMuchNumbering(int toggle)
        {
            var counted = true;
            var unbroken = true;

            for (var i = 0; i < columnIndexes.Count; i++)
            {
                // What a browser would work out for itself: the toggle, then one column after another.
                if (columnIndexes[i] != i + 1 + toggle)
                {
                    counted = false;
                }

                if (columnIndexes[i] != columnIndexes[0] + i)
                {
                    unbroken = false;
                }
            }

            if (counted)
            {
                return ColumnNumbering.None;
            }

            // A toggle pins the first cell to column one, so a run that starts anywhere else already
            // has a hole between the two - there is no unbroken case left for it to be.
            return unbroken && toggle == 0 ? ColumnNumbering.FirstCell : ColumnNumbering.EveryCell;
        }

        /// <summary>The 1-based position of the visible column at <paramref name="index" />.</summary>
        string AriaColIndex(int index) => IndexString(columnIndexes[index]);

        /// <summary>The toggle column, which is always the first of them.</summary>
        static string AriaToggleColIndex => "1";
    }
}
