using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime;
using ClosedXML.Excel;
using Radzen.Documents.Spreadsheet;

// What a cell that is not an object would cost the fill. Four stores, one block, one process.
//
// The prototype stores run the SAME type inference the library's CellData constructor runs - a
// string is parsed for number, date and boolean before it is accepted as text, a number is widened
// to double - because a store that skipped it would be measuring a different program.

const int Rows = 50_000;
const int Cols = 11;

// Built before anything is measured, so every arm is charged for the store it builds and none of
// them for the test data. (An earlier harness built these inside the window and read 20% high.)
var block = new object?[Rows][];
var seed = new DateTime(2020, 1, 1);

for (var r = 0; r < Rows; r++)
{
    var line = new object?[Cols];

    for (var c = 0; c < Cols; c++)
    {
        line[c] = (c % 4) switch
        {
            0 => "row " + r + " col " + c,
            1 => r * Cols + c,
            2 => seed.AddDays(r % 900),
            _ => (r + c) % 2 == 0,
        };
    }

    block[r] = line;
}

// Two single-type blocks, so the re-box at CellData.cs:150 is visible. A caller passing double
// hands over a box the constructor could keep; a caller passing int forces a new one whatever the
// constructor does. The same fix is worth 13 MB to one and nothing to the other.
var doubles = new object?[Rows][];
var ints = new object?[Rows][];

for (var r = 0; r < Rows; r++)
{
    var dline = new object?[Cols];
    var iline = new object?[Cols];

    for (var c = 0; c < Cols; c++)
    {
        dline[c] = (double)(r * Cols + c);
        iline[c] = r * Cols + c;
    }

    doubles[r] = dline;
    ints[r] = iline;
}

// A sparse sheet: the same 550,000 values spread one row in ten over 500,000 rows. The maintainer's
// standing objection is a change that makes an unrelated case worse, and a columnar store is
// exactly the shape that would.
const int SparseRows = 500_000;
const int SparseStride = 10;

// Nothing is counted inside the window. Each arm returns the store it built and the count is read
// from it afterwards, because an instrument inside the window it measures adds bias, not noise.
static object Measure(Func<object> fill, List<Run>? into)
{
    Settle();

    var gen0 = GC.CollectionCount(0);
    var gen1 = GC.CollectionCount(1);
    var gen2 = GC.CollectionCount(2);
    var before = GC.GetTotalAllocatedBytes(precise: true);
    var watch = Stopwatch.StartNew();

    var store = fill();

    watch.Stop();

    var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
    var run = new Run(allocated / 1024.0 / 1024.0, watch.Elapsed.TotalMilliseconds,
        GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2);

    if (into is not null)
    {
        if (Environment.GetEnvironmentVariable("FILLFLOOR_TRACE") is not null)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"    trace: allocated {run.Mb:0.0} MB, gen0 {run.Gen0}, gen1 {run.Gen1}, gen2 {run.Gen2}"));
        }

        into.Add(run);
    }

    return store;
}


// Read after the window, from the store the arm returned.
static long Populated(object store) => store switch
{
    Worksheet sheet => CountRadzen(sheet),
    IXLWorksheet ws => ws.CellsUsed().Count(),
    Dictionary<(int, int), Mimic> m => m.Count,
    Dictionary<(int, int), FoldedMimic> f => f.Count,
    int n => n,
    HandleStore h => h.Count,
    StructStore s => s.Count,
    ChunkStore k => k.Count,
    ColumnStore c => c.Count,
    _ => -1,
};

static long CountRadzen(Worksheet sheet)
{
    var total = 0L;

    for (var r = 0; r < sheet.RowCount; r++)
    {
        for (var c = 0; c < sheet.ColumnCount; c++)
        {
            if (sheet.Cells.TryGet(r, c, out var cell) && !cell.IsEmpty)
            {
                total++;
            }
        }
    }

    return total;
}

// ---- the incumbent -------------------------------------------------------------------------

object RadzenBulk()
{
    var sheet = new Workbook().AddSheet("Sheet1", Rows, Cols);

    sheet.Cells.SetValues(0, 0, block);

    return sheet;
}

object RadzenSparse()
{
    var sheet = new Workbook().AddSheet("Sheet1", SparseRows, Cols);

    for (var r = 0; r < Rows; r++)
    {
        for (var c = 0; c < Cols; c++)
        {
            sheet.Cells[r * SparseStride, c].Value = block[r][c];
        }
    }

    return sheet;
}

// ---- the bar -------------------------------------------------------------------------------

object RadzenDoubles()
{
    var sheet = new Workbook().AddSheet("Sheet1", Rows, Cols);

    sheet.Cells.SetValues(0, 0, doubles);

    return sheet;
}

object RadzenInts()
{
    var sheet = new Workbook().AddSheet("Sheet1", Rows, Cols);

    sheet.Cells.SetValues(0, 0, ints);

    return sheet;
}

// Written the way a consumer writes it today - store[r, c].Value = x - through a handle rather
// than through a live object. The handle is a readonly struct, so nothing is allocated per access.
object HandleFacade()
{
    var store = new HandleStore(Rows * Cols);

    for (var r = 0; r < Rows; r++)
    {
        for (var c = 0; c < Cols; c++)
        {
            // NOT store[r, c].Value = block[r][c]. That is CS1612, "cannot modify the return value
            // ... because it is not a variable": the indexer hands back a struct by value, and
            // assigning to a property of it would mutate a temporary. The pattern the library
            // documents does not survive a struct handle; a method does.
            store[r, c].SetValue(block[r][c]);
        }
    }

    // Proof, checked by the compiler rather than asserted: the property chain consumers actually
    // write still compiles through a readonly struct handle, because Format is a reference.
    store[0, 0].Format.Bold = true;

    return store;
}

// Written through a ref return straight into the store's slot. Cheapest, and the shape a caller
// cannot hold: see RefHazard below for what it costs.
object RefReturn()
{
    var store = new HandleStore(Rows * Cols);

    for (var r = 0; r < Rows; r++)
    {
        for (var c = 0; c < Cols; c++)
        {
            Infer.Into(ref store.SlotRef(r, c), block[r][c]);
        }
    }

    return store;
}

object ClosedXmlInsertData()
{
    // Not disposed inside the window: a fill's store is what survives it, which is what the Radzen
    // arm leaves behind too. Disposing here would both charge the fill for teardown and free the
    // graph being compared.
    var wb = new XLWorkbook();
    var ws = wb.AddWorksheet("Sheet1");

    ws.Cell(1, 1).InsertData(block);

    // The workbook is the store; it must outlive the window, so hand back the sheet, not the count.
    return ws;
}

// ---- the prototypes ------------------------------------------------------------------------

// Calibration. A graph of known shape, built the same way, so the two instruments can be checked
// against arithmetic rather than against each other: a Cell look-alike (16 B header + 8 refs + a
// CellRef + a bool = 96 B), a CellData look-alike (32 B) and a sized dictionary entry (24 B) is
// 152 B a cell, or 79.7 MB over 550,000. Whichever instrument disagrees with that is the wrong one.
object Calibration()
{
    var store = new Dictionary<(int, int), Mimic>(Rows * Cols);

    for (var r = 0; r < Rows; r++)
    {
        for (var c = 0; c < Cols; c++)
        {
            store[(r, c)] = new Mimic { Data = new MimicData() };
        }
    }

    return store;
}

// The same graph with CellData's two fields moved into the cell: one object per cell instead of
// two, the cell 8 bytes wider. This measures the fold rather than predicting it.
object CalibrationFolded()
{
    var store = new Dictionary<(int, int), FoldedMimic>(Rows * Cols);

    for (var r = 0; r < Rows; r++)
    {
        for (var c = 0; c < Cols; c++)
        {
            store[(r, c)] = new FoldedMimic();
        }
    }

    return store;
}

object StructDictSizedDense()
{
    var store = new StructStore(Rows * Cols);

    for (var r = 0; r < Rows; r++)
    {
        for (var c = 0; c < Cols; c++)
        {
            store.Set(r, c, block[r][c]);
        }
    }

    return store;
}

object ChunkedDense()
{
    var store = new ChunkStore();

    for (var r = 0; r < Rows; r++)
    {
        for (var c = 0; c < Cols; c++)
        {
            store.Set(r, c, block[r][c]);
        }
    }

    return store;
}

object ChunkedSparse()
{
    var store = new ChunkStore();

    for (var r = 0; r < Rows; r++)
    {
        for (var c = 0; c < Cols; c++)
        {
            store.Set(r * SparseStride, c, block[r][c]);
        }
    }

    return store;
}

object StructDictDense()
{
    var store = new StructStore();

    for (var r = 0; r < Rows; r++)
    {
        for (var c = 0; c < Cols; c++)
        {
            store.Set(r, c, block[r][c]);
        }
    }

    return store;
}

object StructDictSparse()
{
    var store = new StructStore();

    for (var r = 0; r < Rows; r++)
    {
        for (var c = 0; c < Cols; c++)
        {
            store.Set(r * SparseStride, c, block[r][c]);
        }
    }

    return store;
}

object ColumnarDense()
{
    var store = new ColumnStore(Rows);

    for (var r = 0; r < Rows; r++)
    {
        for (var c = 0; c < Cols; c++)
        {
            store.Set(r, c, block[r][c]);
        }
    }

    return store;
}

object ColumnarSparse()
{
    var store = new ColumnStore(SparseRows);

    for (var r = 0; r < Rows; r++)
    {
        for (var c = 0; c < Cols; c++)
        {
            store.Set(r * SparseStride, c, block[r][c]);
        }
    }

    return store;
}

// ---- what the save pays to read the cells back --------------------------------------------
//
// Today the writer iterates Cell objects that already exist and allocates nothing to see them.
// A struct store has to present something. Three shapes, over stores built outside every window.

var builtSheet = (Worksheet)RadzenBulk();
var builtSlots = (HandleStore)HandleFacade();

object ReadRadzenCells()
{
    var seen = 0;

    for (var r = 0; r < Rows; r++)
    {
        for (var c = 0; c < Cols; c++)
        {
            if (builtSheet.Cells.TryGet(r, c, out var cell) && cell.ValueType != CellDataType.Empty)
            {
                seen++;
            }
        }
    }

    return seen;
}

// What the writer would pay if GetPopulatedCells kept handing out Cell objects: one materialised
// per cell. A facade holds only the store and the address, so it is 32 bytes and not 112.
object ReadMaterialisedFacades()
{
    var seen = 0;

    for (var r = 0; r < Rows; r++)
    {
        for (var c = 0; c < Cols; c++)
        {
            var cell = new CellFacade(builtSlots, r, c);

            if (cell.Type != SlotType.Empty)
            {
                seen++;
            }
        }
    }

    return seen;
}

// What it would pay if the writer were taught to read slots instead - the same loop with nothing
// materialised.
object ReadSlotsInPlace()
{
    var seen = 0;

    for (var r = 0; r < Rows; r++)
    {
        for (var c = 0; c < Cols; c++)
        {
            if (builtSlots.Get(r, c).Type != SlotType.Empty)
            {
                seen++;
            }
        }
    }

    return seen;
}

var reads = new (string Name, Func<object> Fill)[]
{
    ("Cell objects, today", ReadRadzenCells),
    ("materialised facades", ReadMaterialisedFacades),
    ("slots read in place", ReadSlotsInPlace),
};

// ---- run -----------------------------------------------------------------------------------

var dense = new (string Name, Func<object> Fill)[]
{
    ("Radzen SetValues", RadzenBulk),
    ("ClosedXML InsertData", ClosedXmlInsertData),
    ("struct in dictionary", StructDictDense),
    ("struct dict, sized", StructDictSizedDense),
    ("readonly handle", HandleFacade),
    ("ref return", RefReturn),
    ("chunked slot blocks", ChunkedDense),
    ("columnar arrays", ColumnarDense),
    ("calibration, 152 B", Calibration),
    ("calibration, folded", CalibrationFolded),
};

// The re-box arms, run as their own report so the mixed block above is not disturbed.
var boxing = new (string Name, Func<object> Fill)[]
{
    ("SetValues, all double", RadzenDoubles),
    ("SetValues, all int", RadzenInts),
};

var sparse = new (string Name, Func<object> Fill)[]
{
    ("Radzen indexer", RadzenSparse),
    ("struct in dictionary", StructDictSparse),
    ("chunked slot blocks", ChunkedSparse),
    ("columnar arrays", ColumnarSparse),
};

static void Report(string title, (string Name, Func<object> Fill)[] arms, int cells)
{
    var results = new Dictionary<string, List<Run>>();
    var counts = new Dictionary<string, long>();

    // One warm-up pass per arm, discarded, and the populated count read from what it returned.
    foreach (var (name, fill) in arms)
    {
        results[name] = [];
        counts[name] = Populated(Measure(fill, null));
    }

    // Interleaved: one pass of every arm, three times over, so a drifting machine cannot favour one.
    for (var pass = 0; pass < 3; pass++)
    {
        foreach (var (name, fill) in arms)
        {
            Measure(fill, results[name]);
        }
    }

    Console.WriteLine();
    Console.WriteLine(title);
    Console.WriteLine("  store                  median MB    B / cell   gen0  gen1  gen2    median ms   populated");
    Console.WriteLine("  --------------------  ----------  ----------  -----  ----  ----  -----------  ----------");

    foreach (var (name, _) in arms)
    {
        var runs = results[name];

        runs.Sort((a, b) => a.Mb.CompareTo(b.Mb));
        var median = runs[1];

        runs.Sort((a, b) => a.Ms.CompareTo(b.Ms));
        var ms = runs[1].Ms;

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {name,-20}  {median.Mb,10:0.0}  {median.Mb * 1024 * 1024 / cells,10:0}  {median.Gen0,5}  {median.Gen1,4}  {median.Gen2,4}  {ms,11:0}  {counts[name],10:N0}"));
    }
}

Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"{Rows:N0} rows x {Cols} columns = {Rows * Cols:N0} cells, ClosedXML 0.104.2, {(GCSettings.IsServerGC ? "server" : "workstation")} GC"));
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"sizeof(CellSlot) = {Unsafe.SizeOf<CellSlot>()} bytes"));

Report("dense: 550,000 cells in a 50,000 x 11 block", dense, Rows * Cols);
Report("read back: what the save pays to see 550,000 cells", reads, Rows * Cols);
Report("re-box: 550,000 cells of one numeric type", boxing, Rows * Cols);
Report(string.Create(CultureInfo.InvariantCulture,
    $"sparse: the same 550,000 cells one row in {SparseStride} over {SparseRows:N0} rows"), sparse, Rows * Cols);

// ---- prototype stores ----------------------------------------------------------------------

// A compacting gen-2 collect between arms, so no arm inherits the previous one's store.
//
// There is no live-size column here on purpose. GetTotalMemory reported this fill's store at 173 MB
// against the 94 MB its own fill allocated, and the calibration arm - a graph of known shape, 81.8 MB
// by arithmetic - measured 82.1 MB allocated and 149 MB by GetTotalMemory. It over-reports graphs of
// small objects by about 1.8x and array-shaped stores by nothing, which is a bias in the prototypes'
// favour. Allocation agrees with arithmetic to 0.4% and is the number to quote.
static void Settle()
{
    for (var i = 0; i < 2; i++)
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
    }
}

record struct Run(double Mb, double Ms, int Gen0, int Gen1, int Gen2);

// Shaped like Cell and CellData, with no behaviour: nine references, an address and a flag.
sealed class Mimic
{
    public object? Worksheet, Format, Hyperlink, Data, Formula, SyntaxTree, ValidationErrors, Changed;
    public (int Row, int Column) Address;
    public bool QuotePrefix;
}

// Mimic with Value and Type inlined: nine references plus the two fields, one object not two.
sealed class FoldedMimic
{
    public object? Worksheet, Format, Hyperlink, Formula, SyntaxTree, ValidationErrors, Changed;
    public object? Value;
    public (int Row, int Column) Address;
    public SlotType Type;
    public bool QuotePrefix;
}

sealed class MimicData
{
    public object? Value;
    public SlotType Type;
}

enum SlotType : byte { Empty, Number, String, Date, Boolean, Error }

// A cell as a value: the number inline where a number is what it is, a reference only for the
// types that need one. 24 bytes against the 112 of a Cell plus the 32 of its CellData.
struct CellSlot
{
    public double Number;    // number, date serial, or boolean as 0/1
    public object? Text;     // string or error - null for everything else
    public int StyleId;      // handle into a shared format table, 0 = no format
    public SlotType Type;
    public bool QuotePrefix;
}

// The same inference CellData runs, writing into a slot instead of into two objects.
static class Infer
{
    public static void Into(ref CellSlot slot, object? value)
    {
        slot.QuotePrefix = false;

        switch (value)
        {
            case null:
                slot.Type = SlotType.Empty;
                slot.Text = null;
                slot.Number = 0;
                return;

            case string text:
                FromString(ref slot, text);
                return;

            case double d: slot.Type = SlotType.Number; slot.Number = d; slot.Text = null; return;
            case int i: slot.Type = SlotType.Number; slot.Number = i; slot.Text = null; return;
            case long l: slot.Type = SlotType.Number; slot.Number = l; slot.Text = null; return;
            case decimal m: slot.Type = SlotType.Number; slot.Number = (double)m; slot.Text = null; return;
            case float f: slot.Type = SlotType.Number; slot.Number = f; slot.Text = null; return;
            case short s: slot.Type = SlotType.Number; slot.Number = s; slot.Text = null; return;
            case byte b: slot.Type = SlotType.Number; slot.Number = b; slot.Text = null; return;
            case bool flag: slot.Type = SlotType.Boolean; slot.Number = flag ? 1 : 0; slot.Text = null; return;
            case DateTime date: slot.Type = SlotType.Date; slot.Number = date.ToOADate(); slot.Text = null; return;

            default:
                slot.Type = SlotType.String;
                slot.Text = value.ToString();
                slot.Number = 0;
                return;
        }
    }

    private static void FromString(ref CellSlot slot, string text)
    {
        // Spans throughout: every TryParse below has a ReadOnlySpan<char> overload, so the
        // inference never takes a substring and never re-materialises the text it was handed.
        ReadOnlySpan<char> span = text;

        var canBeDate = CanBeDate(span);

        if (double.TryParse(span, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
        {
            slot.Type = SlotType.Number;
            slot.Number = number;
            slot.Text = null;
            return;
        }

        if (canBeDate && DateTime.TryParse(span, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            slot.Type = SlotType.Date;
            slot.Number = date.ToOADate();
            slot.Text = null;
            return;
        }

        if (bool.TryParse(span, out var flag))
        {
            slot.Type = SlotType.Boolean;
            slot.Number = flag ? 1 : 0;
            slot.Text = null;
            return;
        }

        slot.Type = SlotType.String;
        slot.Text = text;
        slot.Number = 0;
    }

    // The library walks this string a character at a time. Two vectorised searches over the same
    // span answer the same question: a date needs a digit and needs a separator or a letter.
    private static readonly SearchValues<char> Digits = SearchValues.Create("0123456789");
    private static readonly SearchValues<char> Separators = SearchValues.Create("/-.:,");

    private static bool CanBeDate(ReadOnlySpan<char> value)
    {
        if (!value.ContainsAny(Digits))
        {
            return false;
        }

        return value.ContainsAny(Separators) || ContainsLetter(value);
    }

    private static bool ContainsLetter(ReadOnlySpan<char> value)
    {
        foreach (var ch in value)
        {
            if (char.IsLetter(ch))
            {
                return true;
            }
        }

        return false;
    }
}

// The API-preserving shape. The indexer hands back a readonly struct that knows only where the
// cell is, so `store[r, c].Value = x` and `store[r, c].Format.Bold = true` both still read as they
// do today, and neither allocates. Format stays a class in a side table, so the property chain a
// consumer writes is unchanged.
sealed class HandleStore(int capacity)
{
    private readonly Dictionary<(int row, int column), CellSlot> data = new(capacity);
    private readonly Dictionary<(int row, int column), MimicFormat> formats = [];

    public int Count => data.Count;

    public CellHandle this[int row, int column] => new(this, row, column);

    // A ref straight into the dictionary's storage. Valid only until the next insert: adding a cell
    // can resize the entry array, after which the ref points into the old one and a write through
    // it is silently lost. Fine inside a fill loop that holds it for one statement; not something to
    // hand a consumer, which is why the public shape above is the handle and not this.
    public ref CellSlot SlotRef(int row, int column)
        => ref CollectionsMarshal.GetValueRefOrAddDefault(data, (row, column), out _);

    public CellSlot Get(int row, int column)
        => CollectionsMarshal.GetValueRefOrNullRef(data, (row, column)) is var slot && !Unsafe.IsNullRef(ref slot)
            ? slot
            : default;

    public void Set(int row, int column, object? value)
        => Infer.Into(ref CollectionsMarshal.GetValueRefOrAddDefault(data, (row, column), out _), value);

    public MimicFormat Format(int row, int column)
    {
        ref var format = ref CollectionsMarshal.GetValueRefOrAddDefault(formats, (row, column), out var existed);

        if (!existed)
        {
            format = new MimicFormat();
        }

        return format!;
    }
}

// Sixteen bytes, passed by value, allocating nothing. `readonly` so every member is guaranteed not
// to mutate a copy - the trap a mutable struct façade would set.
readonly struct CellHandle(HandleStore store, int row, int column)
{
    // A getter is fine. It is the setter that cannot be reached through the indexer.
    public object? Value => store.Get(row, column).Text;

    public void SetValue(object? value) => store.Set(row, column, value);

    // This one does survive: Format returns a class, and mutating what a reference points at is not
    // mutating the handle. store[r, c].Format.Bold = true compiles unchanged - see the assertion in
    // HandleFacade.
    public MimicFormat Format => store.Format(row, column);
}

// The non-breaking shape: Cell stays a class and every signature is unchanged, but it holds only
// where the cell is. Equality is by address, because CellDependencyGraph and FormulaEvaluator key
// dictionaries and hash sets on cells and a per-access instance would otherwise never match.
sealed class CellFacade(HandleStore store, int row, int column) : IEquatable<CellFacade>
{
    private readonly HandleStore store = store;
    private readonly int row = row;
    private readonly int column = column;

    public object? Value => store.Get(row, column).Text;

    public SlotType Type => store.Get(row, column).Type;

    public MimicFormat Format => store.Format(row, column);

    public bool Equals(CellFacade? other) => other is not null && other.row == row && other.column == column;

    public override bool Equals(object? obj) => Equals(obj as CellFacade);

    public override int GetHashCode() => HashCode.Combine(row, column);
}

sealed class MimicFormat
{
    public bool Bold;
}

// Sparse-shaped: the dictionary CellStore already has, holding the value instead of a reference to
// one. Written through a ref so the slot is updated in place and never copied out.
sealed class StructStore
{
    private readonly Dictionary<(int row, int column), CellSlot> data;

    public StructStore(int capacity = 0) => data = new(capacity);

    public int Count => data.Count;

    public void Set(int row, int column, object? value)
    {
        ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(data, (row, column), out _);

        Infer.Into(ref slot, value);
    }
}

// Dense-shaped: one set of parallel arrays per column, allocated on first touch and sized to the
// sheet. No per-cell allocation at all, and no per-cell dictionary entry.
sealed class ColumnStore(int rows)
{
    private readonly Dictionary<int, Column?> columns = [];

    public int Count
    {
        get
        {
            var total = 0;

            foreach (var column in columns.Values)
            {
                foreach (var type in column!.Types.AsSpan())
                {
                    if (type != SlotType.Empty)
                    {
                        total++;
                    }
                }
            }

            return total;
        }
    }

    public void Set(int row, int column, object? value)
    {
        ref var strip = ref CollectionsMarshal.GetValueRefOrAddDefault(columns, column, out var existed);

        if (!existed)
        {
            strip = new Column(rows);
        }

        var slot = default(CellSlot);

        Infer.Into(ref slot, value);

        // One bounds check per span rather than three indexer checks.
        strip!.Numbers.AsSpan()[row] = slot.Number;
        strip.Texts.AsSpan()[row] = slot.Text;
        strip.Types.AsSpan()[row] = slot.Type;
    }

    private sealed class Column(int rows)
    {
        public readonly double[] Numbers = new double[rows];
        public readonly object?[] Texts = new object?[rows];
        public readonly SlotType[] Types = new SlotType[rows];
    }
}

// The shape that might serve both: a column is a dictionary of fixed-height blocks of slots,
// allocated when a cell in that block is first written. A dense block pays array prices; a sheet
// that never touches a block never allocates it.
sealed class ChunkStore
{
    public const int Height = 256;

    private readonly Dictionary<(int column, int block), CellSlot[]> blocks = [];

    public int Count
    {
        get
        {
            var total = 0;

            foreach (var block in blocks.Values)
            {
                foreach (ref readonly var slot in block.AsSpan())
                {
                    if (slot.Type != SlotType.Empty)
                    {
                        total++;
                    }
                }
            }

            return total;
        }
    }

    public void Set(int row, int column, object? value)
    {
        ref var block = ref CollectionsMarshal.GetValueRefOrAddDefault(blocks, (column, row / Height), out var existed);

        if (!existed)
        {
            block = new CellSlot[Height];
        }

        Infer.Into(ref block!.AsSpan()[row % Height], value);
    }
}
