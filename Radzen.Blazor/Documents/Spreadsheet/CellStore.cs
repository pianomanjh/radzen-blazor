using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Radzen.Documents.Spreadsheet;

/// <summary>
/// Represents a store for spreadsheet cells, allowing access and modification of cell values.
/// </summary>
/// <param name="sheet"></param>
[SuppressMessage("Design", "CA1043:Use Integral Or String Argument For Indexers", Justification = "CellRef indexer is intentional for spreadsheet access.")]
public class CellStore(Worksheet sheet)
{
    /// <summary>
    /// Gets the sheet that owns this cell store.
    /// </summary>
    public Worksheet Worksheet { get; } = sheet;

    /// <summary>
    /// Stores what is at each address: the value, or the cell that has taken over from it.
    /// </summary>
    /// <remarks>
    /// A bulk fill writes values and never builds a cell. A cell is built the first time anything
    /// asks for one and is kept, so <see cref="this[int, int]"/> returns the same instance every
    /// time and everything that compares cells by reference is unaffected. A cell value is never a
    /// <see cref="Cell"/>, so one reference answers both questions.
    /// </remarks>
    private readonly Dictionary<(int row, int column), object?> data = [];

    // A stored value's type is its CLR type: the five ways to make a CellData each pair a value with
    // the matching CellDataType, and inference produces the same pairs. CellDataTypeIsTheValuesType
    // holds this to it.
    internal static CellDataType TypeOf(object? value) => value switch
    {
        null => CellDataType.Empty,
        string => CellDataType.String,
        double => CellDataType.Number,
        bool => CellDataType.Boolean,
        DateTime => CellDataType.Date,
        CellError => CellDataType.Error,
        _ => CellDataType.String,
    };

    // Only a cell can be given a quote prefix - every setter of it is on Cell - so a value standing
    // on its own never has one.
    private static Cell Materialise(Worksheet sheet, int row, int column, object? content) =>
        new(sheet, new CellRef(row, column), content, TypeOf(content), quotePrefix: false);

    /// <summary>
    /// Gets a cell at the specified row and column.
    /// If the cell does not exist, it is created and added to the store.
    /// </summary>
    /// <param name="row">The row index of the cell.</param>
    /// <param name="column">The column index of the cell.</param>
    /// <returns>The cell at the specified row and column.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the row or column index is out of bounds.</exception>
    /// <remarks>
    /// The row and column indices are zero-based, so the first cell is at (0, 0).
    /// The method ensures that the specified row and column are within the bounds of the sheet's dimensions.
    /// If the cell does not exist in the store, it creates a new cell with the specified row and column,
    /// initializes it with the sheet reference, and adds it to the store.
    /// </remarks>
    public virtual Cell this[int row, int column]
    {
        get
        {
            EnsureWithinBounds(row, column);

            return GetOrAdd(row, column);
        }

        set
        {
            EnsureWithinBounds(row, column);

            data[(row, column)] = value;
        }
    }

    /// <summary>
    /// Gets or sets a cell at the specified address using a CellRef object.
    /// </summary>

    public Cell this[CellRef address]
    {
        get => this[address.Row, address.Column];
        set => this[address.Row, address.Column] = value;
    }

    /// <summary>
    /// Gets or sets a cell at the specified address using a string in A1 notation.
    /// </summary>
    public Cell this[string address]
    {
        get => this[CellRef.Parse(address)];
        set => this[CellRef.Parse(address)] = value;
    }

    internal List<Cell> GetRow(int row, int start, int end)
    {
        var cells = new List<Cell>(end - start + 1);

        for (var column = start; column <= end; column++)
        {
            cells.Add(this[row, column].Clone());
        }

        return cells;
    }

    private bool InBounds(int row, int column)
    {
        return row >= 0 && row < Worksheet.RowCount && column >= 0 && column < Worksheet.ColumnCount;
    }

    /// <summary>
    /// Attempts to get a cell at the specified row and column.
    /// Returns true only if the cell has been populated. Does not create new cells.
    /// </summary>
    /// <param name="row">The row index of the cell.</param>
    /// <param name="column">The column index of the cell.</param>
    /// <param name="cell">The cell at the specified row and column if it exists; otherwise, null.</param>
    /// <returns>True if the cell exists in the store; otherwise, false.</returns>
    public bool TryGet(int row, int column, out Cell cell)
    {
        cell = (InBounds(row, column) ? Find(row, column) : null)!;

        return cell is not null;
    }

    // Every populated cell as the writer sees it, materialising none of them.
    internal IEnumerable<CellView> GetPopulatedViews()
    {
        foreach (var entry in data)
        {
            var address = new CellRef(entry.Key.row, entry.Key.column);

            yield return entry.Value is Cell cell
                ? new CellView(cell)
                : new CellView(address, entry.Value, TypeOf(entry.Value), quotePrefix: false);
        }
    }

    // Whether an address holds anything the writer would write, without building a cell to ask.
    internal bool IsWrittenAt(int row, int column) =>
        data.TryGetValue((row, column), out var content) &&
        (content is Cell cell ? cell.Value is not null || cell.Formula is not null : content is not null);

    internal IEnumerable<Cell> GetPopulatedCells()
    {
        foreach (var key in keysBuffer())
        {
            yield return Adopt(key.row, key.column);
        }

        List<(int row, int column)> keysBuffer() => [.. data.Keys];
    }

    internal int Compact()
    {
        var keysToRemove = new List<(int row, int column)>();

        foreach (var kvp in data)
        {
            if (kvp.Value is Cell cell ? cell.IsEmpty : kvp.Value is null)
            {
                keysToRemove.Add(kvp.Key);
            }
        }

        foreach (var key in keysToRemove)
        {
            data.Remove(key);
        }

        return keysToRemove.Count;
    }

    internal int PopulatedCount => data.Count;

    // Not Find: asking whether a slot is populated must not build the cell that would answer it, which
    // is the whole point of holding a value rather than a cell.
    internal bool HasCell(int row, int column) => data.ContainsKey((row, column));

    // The slot's cell, built from the stored value when the slot holds only a value. Adopting rather
    // than answering null is what keeps every caller written before the value store working unchanged:
    // they asked whether there is a cell here and meant whether there is anything here.
    internal Cell? Find(int row, int column) => data.ContainsKey((row, column)) ? Adopt(row, column) : null;

    // Only a cell that exists has an address to correct; a slot's address is its key.
    private static void UpdateCellAddress((int row, int column) oldKey, (int row, int column) newKey, object? content)
    {
        if (oldKey != newKey && content is Cell cell)
        {
            cell.Address = new CellRef(newKey.row, newKey.column);
        }
    }

    internal void ShiftRowsUp(int deletedRow) =>
        DictionaryShift.Remap<(int row, int column), object?>(data, k =>
            k.row < deletedRow ? k :
            k.row == deletedRow ? null :
            (k.row - 1, k.column),
            UpdateCellAddress);

    internal void ShiftRowsDown(int fromRow, int count) =>
        DictionaryShift.Remap<(int row, int column), object?>(data, k =>
            k.row < fromRow ? k : (k.row + count, k.column),
            UpdateCellAddress);

    internal void ShiftColumnsLeft(int deletedColumn) =>
        DictionaryShift.Remap<(int row, int column), object?>(data, k =>
            k.column < deletedColumn ? k :
            k.column == deletedColumn ? null :
            (k.row, k.column - 1),
            UpdateCellAddress);

    internal void ShiftColumnsRight(int fromColumn, int count) =>
        DictionaryShift.Remap<(int row, int column), object?>(data, k =>
            k.column < fromColumn ? k : (k.row, k.column + count),
            UpdateCellAddress);

    private readonly Dictionary<RangeRef, string> customTypes = [];

    /// <summary>
    /// Sets a custom cell type for the specified cell.
    /// </summary>
    /// <param name="cell">The cell reference.</param>
    /// <param name="type">The custom type name, or null to remove the custom type.</param>
    public void SetCustomType(CellRef cell, string? type)
    {
        SetCustomType(cell.ToRange(), type);
    }

    /// <summary>
    /// Sets a custom cell type for the specified range.
    /// </summary>
    /// <param name="range">The range of cells.</param>
    /// <param name="type">The custom type name, or null to remove the custom type.</param>
    public void SetCustomType(RangeRef range, string? type)
    {
        if (type is null)
        {
            customTypes.Remove(range);
        }
        else
        {
            customTypes[range] = type;
        }
        Worksheet.OnChromeChanged();
    }

    /// <summary>
    /// Gets the custom cell type for the specified cell, or null if no custom type is set.
    /// </summary>
    /// <param name="row">The row index of the cell.</param>
    /// <param name="column">The column index of the cell.</param>
    /// <returns>The custom type name, or null if no custom type is set.</returns>
    public string? GetCustomType(int row, int column)
    {
        foreach (var kvp in customTypes)
        {
            if (kvp.Key.Contains(row, column))
            {
                return kvp.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Fills a rectangular block of cells from a jagged array, in one pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Filling a sheet a cell at a time through <see cref="this[int, int]"/> pays, per cell, two
    /// bounds checks and two dictionary operations - the getter looks the cell up, misses, and the
    /// setter it delegates to looks it up again to insert - plus whatever rehashing the dictionary
    /// does on the way up to its final size. This does the bounds check once for the block, sizes the
    /// dictionary once and looks each cell up once.
    /// </para>
    /// <para>
    /// Not wrapped in an update batch. Each write notifies its own dependents, exactly as an
    /// assignment through the indexer does; suspending evaluation would make the sheet re-run every
    /// formula it has, including the ones that do not read the block.
    /// </para>
    /// <para>
    /// Rows may be ragged; a short row leaves the cells past its end untouched, and a null row is
    /// skipped. A value replaces whatever the cell held, a formula included. Values are assigned
    /// through <see cref="Cell.Value"/>, so the same type inference applies as when they are written
    /// one at a time.
    /// </para>
    /// </remarks>
    /// <param name="row">The zero-based row the block starts at.</param>
    /// <param name="column">The zero-based column the block starts at.</param>
    /// <param name="values">The values, one array per row.</param>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The block does not fit in the sheet.</exception>
    public void SetValues(int row, int column, IReadOnlyList<IReadOnlyList<object?>?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count == 0)
        {
            return;
        }

        var widest = 0;
        var total = 0;

        for (var r = 0; r < values.Count; r++)
        {
            if (values[r] is { } line)
            {
                widest = Math.Max(widest, line.Count);
                total += line.Count;
            }
        }

        if (total == 0)
        {
            return;
        }

        EnsureWithinBounds(row, column);
        EnsureWithinBounds(row + values.Count - 1, column + widest - 1);

        data.EnsureCapacity(data.Count + total);

        // The fast path below writes values without building a cell, which a derived store's
        // overridden indexer would never see. Only the base store takes it.
        var derived = GetType() != typeof(CellStore);

        for (var r = 0; r < values.Count; r++)
        {
            if (values[r] is not { } line)
            {
                continue;
            }

            for (var c = 0; c < line.Count; c++)
            {
                if (derived)
                {
                    // GetOrAdd, not the indexer: it returns the stored cell and announces a new one
                    // through the setter, where an overridden getter may hand back a snapshot that a
                    // write would go into instead of the store.
                    var dispatched = GetOrAdd(row + r, column + c);

                    dispatched.Formula = null;
                    dispatched.Value = line[c];
                    continue;
                }

                ref var content = ref CollectionsMarshal.GetValueRefOrAddDefault(data, (row + r, column + c), out _);

                if (content is Cell cell)
                {
                    cell.Formula = null;
                    cell.Value = line[c];
                    continue;
                }

                // Nothing has asked for a cell here, so nothing holds a reference to one and no
                // formula names it - a formula reference materialises the cell it names. There is
                // no dependent to recalculate and no subscriber to notify.
                CellData.Infer(line[c], Worksheet.Culture, out content, out _);
            }
        }
    }

    // The indexer's path. A cell built here is assigned through the indexer, so a derived store that
    // overrides the setter sees it, which is what reading a missing cell has always done.
    private Cell GetOrAdd(int row, int column)
    {
        data.TryGetValue((row, column), out var content);

        if (content is Cell existing)
        {
            return existing;
        }

        var cell = Materialise(Worksheet, row, column, content);

        this[row, column] = cell;

        return cell;
    }

    // The store's own path, for an address already in it. It takes no bounds check, because the
    // store can outlive a shrunk sheet, and announces nothing, because nothing is being created.
    private Cell Adopt(int row, int column)
    {
        ref var content = ref CollectionsMarshal.GetValueRefOrAddDefault(data, (row, column), out _);

        if (content is Cell existing)
        {
            return existing;
        }

        var cell = Materialise(Worksheet, row, column, content);

        content = cell;

        return cell;
    }

    /// <summary>
    /// Ensures that the specified row and column indices are within the bounds of the sheet.
    /// </summary>
    /// <param name="row">The row index to check.</param>
    /// <param name="column">The column index to check.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the row or column index is out of range.</exception>
    protected void EnsureWithinBounds(int row, int column)
    {
        if (row < 0 || row >= Worksheet.RowCount)
        {
            throw new ArgumentOutOfRangeException(nameof(row), "Row index is out of range.");
        }

        if (column < 0 || column >= Worksheet.ColumnCount)
        {
            throw new ArgumentOutOfRangeException(nameof(column), "Column index is out of range.");
        }
    }
}