# Decouple the streaming writer from #2711 — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended)
> or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`)
> syntax for tracking.

**Goal:** Make the streaming XLSX writer a self-contained upstream PR on top of #2710, then measure
whether the fork's slot store still earns its keep.

**Architecture:** #2711 is declined and closing. The streaming writer's entire dependency on it is
`CellView` plus a mechanical `Cell` → `in CellView` conversion of the writer's per-cell methods, so
those travel in the streaming commit itself and the two-state store is left behind. The writer's
observable output must not change: `SavedParts.cs` + `compare-parts.py` compare every part of a saved
workbook byte for byte between two checkouts, and that is the gate.

**Tech Stack:** .NET 10, xunit, BenchmarkDotNet-free console harnesses under `gridbench/spreadsheet/`.

**Spec:** `gridbench/SLIM-GRID-SPEC.md` §67 (and §65/§66 for the streaming design itself).

## Global Constraints

- **Upstream branches carry no comments except XML docs on public API.** Internal types get no doc
  blocks. Fork branches keep the narrative comments and the `Claude-Session:` trailer.
- **No Claude/AI attribution** in commits or PR bodies on `upstream/*` branches.
- **American English** in upstream submissions. The spec itself stays British.
- **No `benchmarks/` directory upstream** — quote numbers in the PR body instead.
- **No public API changes.** `CellStore.data` stays `protected` and typed `Dictionary<(int, int), Cell>`;
  `Cell.Data` stays a stored `CellData` returning the same instance on every read.
- **Take a backup branch before any rebase.** `git switch` refuses a branch checked out in another
  worktree — **check the exit code**, or a following `git rebase` runs against the wrong branch.
- Run every gate **both filtered and in the full suite**; they disagree.
- Restore mutations **from a file snapshot**, never `git checkout --`.

---

### Task 1: Extract `Cell.FormatValue` and add `CellView` on top of #2710

`CellView` is the writer's read shape: it can describe a cell that has a `Cell` object behind it *or*
one that exists only as an `(address, value, type)` triple, which is what a streamed row is. It needs a
static formatter, because the raw case has no `Cell` to call.

**Files:**
- Create: `Radzen.Blazor/Documents/Spreadsheet/CellView.cs`
- Modify: `Radzen.Blazor/Documents/Spreadsheet/Cell.cs` (the private `FormatValue`)
- Modify: `Radzen.Blazor/Documents/Spreadsheet/CellStore.cs` (add two internal helpers)
- Test: `Radzen.Blazor.Tests/Spreadsheet/XlsxWriterContractTests.cs` (existing, must stay green)

**Interfaces:**
- Produces: `internal readonly struct CellView` with `CellRef Address`; `object? Value`;
  `CellDataType ValueType`; `bool QuotePrefix`; `string? Formula`; `Format? FormatOrNull`;
  `Hyperlink? Hyperlink`; `string? FormatDisplayText(CultureInfo culture)`. Two constructors:
  `CellView(Cell cell)` and `CellView(CellRef address, object? value, CellDataType type, bool quotePrefix)`.
- Produces: `internal static string? Cell.FormatValue(object? value, CultureInfo culture)`.
- Produces: `internal IEnumerable<CellView> CellStore.GetPopulatedViews()` and
  `internal bool CellStore.IsWrittenAt(int row, int column)`.

- [ ] **Step 1: Create the branch off #2710**

```bash
cd /Volumes/Josh-Dev/DevCaches/claude-worktrees/radzen-fastgrid
git branch backup/xlsx-streaming-writer-predecouple upstream/xlsx-streaming-writer
git worktree add -b upstream/xlsx-streaming-writer-v2 ../radzen-decouple upstream/xlsx-reader-inline-strings
```

- [ ] **Step 2: Extract the static formatter in `Cell.cs`**

Replace the private instance method with a delegating pair. Master's body moves verbatim; only the
receiver changes.

```csharp
    private string? FormatValue(CultureInfo culture) => FormatValue(Value, culture);

    internal static string? FormatValue(object? value, CultureInfo culture)
    {
        return value switch
        {
            null => null,
            CellError error => error.ToString(),
            string str => str,
            IFormattable formattable => formattable.ToString(null, culture),
            _ => value.ToString()
        };
    }
```

- [ ] **Step 3: Create `CellView.cs`**

No doc comments — it is an internal type on an upstream branch.

```csharp
using System.Globalization;

namespace Radzen.Documents.Spreadsheet;

#nullable enable

internal readonly struct CellView
{
    private readonly Cell? cell;
    private readonly object? value;
    private readonly CellDataType type;
    private readonly bool quotePrefix;

    public readonly CellRef Address;

    public CellView(Cell cell)
    {
        this.cell = cell;
        Address = cell.Address;
        value = null;
        type = CellDataType.Empty;
        quotePrefix = false;
    }

    public CellView(CellRef address, object? value, CellDataType type, bool quotePrefix)
    {
        cell = null;
        Address = address;
        this.value = value;
        this.type = type;
        this.quotePrefix = quotePrefix;
    }

    public object? Value => cell is not null ? cell.Value : value;

    public CellDataType ValueType => cell is not null ? cell.ValueType : type;

    public bool QuotePrefix => cell is not null ? cell.QuotePrefix : quotePrefix;

    public string? Formula => cell?.Formula;

    public Format? FormatOrNull => cell?.FormatOrNull;

    public Hyperlink? Hyperlink => cell?.Hyperlink;

    public string? FormatDisplayText(CultureInfo culture) => cell is not null
        ? cell.FormatDisplayText(culture)
        : NumberFormat.Apply(null, value, type, culture) ?? Cell.FormatValue(value, culture);
}
```

- [ ] **Step 4: Add the two store helpers to `CellStore.cs`**

Every entry in this store is a `Cell`, so a view is always cell-backed here. The raw constructor exists
for the streamed writer, which has no store at all.

```csharp
    internal IEnumerable<CellView> GetPopulatedViews()
    {
        foreach (var cell in data.Values)
        {
            yield return new CellView(cell);
        }
    }

    internal bool IsWrittenAt(int row, int column) =>
        data.TryGetValue((row, column), out var cell) &&
        (cell.Value is not null || cell.Formula is not null);
```

- [ ] **Step 5: Build and run the full suite**

Run: `dotnet test Radzen.Blazor.Tests/Radzen.Blazor.Tests.csproj -c Release --nologo`
Expected: PASS, same count as on `upstream/xlsx-reader-inline-strings`. Nothing calls the new members
yet, so any failure here is an extraction error in `FormatValue`.

- [ ] **Step 6: Commit**

```bash
git add Radzen.Blazor/Documents/Spreadsheet/CellView.cs \
        Radzen.Blazor/Documents/Spreadsheet/Cell.cs \
        Radzen.Blazor/Documents/Spreadsheet/CellStore.cs
git commit -m "Add a read shape a writer can use for a cell it does not have"
```

---

### Task 2: Convert the writer to `CellView`, proving the output is byte-identical

**Files:**
- Modify: `Radzen.Blazor/Documents/Spreadsheet/XlsxWriter.cs`
- Test: `gridbench/spreadsheet/SavedParts.cs` + `gridbench/spreadsheet/compare-parts.py` (from
  `gridbench/spreadsheet-perf`)

**Interfaces:**
- Consumes: `CellView`, `CellStore.GetPopulatedViews()`, `CellStore.IsWrittenAt` from Task 1.
- Produces: `XlsxWriter` per-cell methods taking `in CellView` — `WriteCell`, `CellTypeAttribute`,
  `WriteFormula`, `WriteTypedValue`, `GetOrCreateCellStyle`, `GetOrCreateNumberFormat`,
  `CreateCellStyleElement`, `HasCellFormatting`, and `CellOrder : IComparer<CellView>`.

- [ ] **Step 1: Capture the reference output BEFORE changing the writer**

```bash
cd /Volumes/Josh-Dev/DevCaches/claude-worktrees/radzen-fastgrid
git show gridbench/spreadsheet-perf:gridbench/spreadsheet/SavedParts.cs > /tmp/SavedParts.cs
git show gridbench/spreadsheet-perf:gridbench/spreadsheet/compare-parts.py > /tmp/compare-parts.py
```

Build a console project referencing `../radzen-decouple/Radzen.Blazor/Radzen.Blazor.csproj` using the
csproj shape in `gridbench/spreadsheet/README.md` ("Rebuilding one"), run it, and keep the unpacked
parts as `/tmp/parts-before/`.

- [ ] **Step 2: Convert the enumeration sites**

Three `GetPopulatedCells()` calls become `GetPopulatedViews()`: the hyperlink pass (~`:1012`),
`BuildSharedFormulaGroups` (~`:1243`), and `WriteRows` (~`:1361`). `ComputeAutoFitWidths` also moves to
`GetPopulatedViews()`, **without** the address list and indexer re-read that #2711 introduced — read
straight from the view:

```csharp
        foreach (var view in sheet.Cells.GetPopulatedViews())
        {
            var col = view.Address.Column;

            if (!sheet.Columns.IsAutoFit(col) || sheet.MergedCells.Contains(view.Address))
            {
                continue;
            }

            var text = view.FormatDisplayText(CultureInfo.InvariantCulture);
            var format = view.FormatOrNull;
```

Note the `try`/`catch (ArgumentOutOfRangeException)` goes away with the re-read that could throw, and
`GetEffectiveFormat()` becomes `FormatOrNull` — conditional formats are not consulted for autofit
width, which matches what a view can see. **If any autofit test fails on this, restore
`GetEffectiveFormat` by adding a delegating member to `CellView` rather than reverting the loop.**

- [ ] **Step 3: Convert the pooled array and the comparer**

```csharp
        var cells = ArrayPool<CellView>.Shared.Rent(sheet.Cells.PopulatedCount);
```
with the matching `ArrayPool<CellView>.Shared.Return(cells, clearArray: true)`, `WriteRows`'s parameter
as `CellView[] cells`, and `private sealed class CellOrder : IComparer<CellView>`.

- [ ] **Step 4: Convert the per-cell methods to `in CellView`**

Change the parameter type on `WriteCell`, `CellTypeAttribute`, `WriteFormula`, `WriteTypedValue`,
`GetOrCreateCellStyle`, `GetOrCreateNumberFormat`, `CreateCellStyleElement` and `HasCellFormatting`.
The two `Cell`-holding call sites wrap:

```csharp
            var style = anchor is not null && HasCellFormatting(new CellView(anchor)) ? new MergeAnchor(anchor) : null;
```
```csharp
        return anchor.StyleId ??= GetOrCreateCellStyle(new CellView(anchor.Cell), styleTracker);
```

and the merged-cell skip uses the store directly:

```csharp
                    if (sheet.Cells.IsWrittenAt(r, c))
                    {
                        continue;
                    }
```

- [ ] **Step 5: Run the full suite**

Run: `dotnet test Radzen.Blazor.Tests/Radzen.Blazor.Tests.csproj -c Release --nologo`
Expected: PASS, same count as Task 1 Step 5.

- [ ] **Step 6: Prove the saved file did not change**

Re-run the `SavedParts` program against the converted checkout into `/tmp/parts-after/`, then:

Run: `python3 /tmp/compare-parts.py /tmp/parts-before /tmp/parts-after`
Expected: every part identical. **This is the gate for the whole task** — a passing test suite does not
prove the XML is unchanged, and this does.

- [ ] **Step 7: Mutation-check the gate**

Change one written attribute (for example make `CellTypeAttribute` return `null` for
`CellDataType.Boolean`), re-run Step 6, and confirm `compare-parts.py` reports a difference. Restore
from a file snapshot taken before the mutation — **not** `git checkout --` — and re-run Step 6 to
confirm it is clean again. A comparison that cannot fail is not a gate.

- [ ] **Step 8: Commit**

```bash
git add Radzen.Blazor/Documents/Spreadsheet/XlsxWriter.cs
git commit -m "Write a cell through a read shape instead of the cell itself"
```

---

### Task 3: Replay the streaming commit on the new base

**Files:**
- Create: `Radzen.Blazor/Documents/Spreadsheet/StreamedSheet.cs`,
  `Radzen.Blazor/Documents/Spreadsheet/XlsxWriter.Streaming.cs`
- Modify: `Radzen.Blazor/Documents/Spreadsheet/Workbook.cs`,
  `Radzen.Blazor/Documents/Spreadsheet/XlsxReader.cs`
- Test: `Radzen.Blazor.Tests/Spreadsheet/StreamedSheetTests.cs`,
  `Radzen.Blazor.Tests/Spreadsheet/StreamedSheetPipelineTests.cs`,
  `Radzen.Blazor.Tests/Spreadsheet/InlineStringXlsxTests.cs`

**Interfaces:**
- Consumes: `CellView`'s raw constructor from Task 1; the `in CellView` writer methods from Task 2.
- Produces: `Workbook.SaveToStreamAsync(Stream, StreamedSheet, CancellationToken)`.

- [ ] **Step 1: Cherry-pick the streaming commit**

```bash
git cherry-pick --no-commit d0f0e2165
```

Expected: conflicts only where `XlsxWriter.cs` was already converted in Task 2. Resolve by keeping
Task 2's converted signatures and taking the streaming commit's *additions*.

**Note on the six review findings.** All six are artifacts of #2711 — the two-state store, the
`CellView`/`CellStore` members it added, and the `CellData.Equals` it introduced — so they close with the
PR and none needs a task here. Step 2 is what proves that claim rather than assuming it.

- [ ] **Step 2: Confirm nothing from #2711 is still referenced**

Run: `grep -rn 'GetType()\|StoredValue\|StoredType\|CellStore.TypeOf\|Adopt' Radzen.Blazor/Documents/Spreadsheet/`
Expected: no hits. Any hit is a leftover of the two-state store and must be removed, not ported.

- [ ] **Step 3: Run the full suite**

Run: `dotnet test Radzen.Blazor.Tests/Radzen.Blazor.Tests.csproj -c Release --nologo`
Expected: PASS, including all `StreamedSheet*` tests.

- [ ] **Step 4: Re-run the byte-comparison from Task 2 Step 6**

Expected: still identical to `/tmp/parts-before`. The streamed path is additive; the ordinary save must
be untouched by it.

- [ ] **Step 5: Commit**

```bash
git commit -m "Write a sheet from a row source without building its cells"
```

---

### Task 4: Fix the two bugs found reviewing #14 before it goes upstream

Both were found reviewing fork PR #14 and recorded in §67. Task 3 replays that commit unchanged, so
without this task they ship to upstream in the submission.

**Files:**
- Modify: `Radzen.Blazor/Documents/Spreadsheet/XlsxWriter.Streaming.cs`
- Test: `Radzen.Blazor.Tests/Spreadsheet/StreamedSheetTests.cs`

**Interfaces:**
- Consumes: `Workbook.SaveToStreamAsync(Stream, StreamedSheet, CancellationToken)` from Task 3.

- [ ] **Step 1: Write the failing test for cancellation at the end of enumeration**

A token cancelled after the last row must still fault the write, not be ignored because the row loop has
already exited.

```csharp
    [Fact]
    public async Task Cancellation_AfterLastRow_StillFaults()
    {
        using var source = new CancellationTokenSource();

        var rows = new[] { new Person("Ada", 1), new Person("Bob", 2) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await using var stream = new MemoryStream();

            await Workbook.SaveToStreamAsync(stream, new StreamedSheet<Person>
            {
                Name = "Sheet1",
                Rows = Walk(rows, source),
                Columns = Columns(),
            }, source.Token);
        });

        static async IAsyncEnumerable<Person> Walk(Person[] rows, CancellationTokenSource source)
        {
            foreach (var row in rows)
            {
                yield return row;
            }

            await source.CancelAsync();
        }
    }
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test Radzen.Blazor.Tests/Radzen.Blazor.Tests.csproj -c Release --filter 'FullyQualifiedName~Cancellation_AfterLastRow' --nologo`
Expected: FAIL — the write completes and no exception is thrown.

- [ ] **Step 3: Check the token once more after the row loop**

Add a `cancellationToken.ThrowIfCancellationRequested()` after the enumeration completes and before the
sheet is finalized, matching the existing check between the last row and the hand-off.

- [ ] **Step 4: Run it and watch it pass**

Run: the same filter as Step 2. Expected: PASS.

- [ ] **Step 5: Write the failing test for a numeric table heading**

A heading that looks like a number must stay text, or the table's `<tableColumn name>` falls back to
`Column1` and the header row disagrees with the table definition.

```csharp
    [Fact]
    public async Task NumericHeading_StaysTextInTheTableDefinition()
    {
        var sheet = await RoundTrip(new StreamedSheet<Person>
        {
            Name = "Sheet1",
            AddTable = true,
            Rows = Rows(),
            Columns = [new StreamedColumn<Person> { Title = "2024", Value = p => p.Name }],
        });

        Assert.Equal("2024", sheet.Tables[0].Columns[0].Name);
    }
```

- [ ] **Step 6: Run it and watch it fail**

Run: `dotnet test Radzen.Blazor.Tests/Radzen.Blazor.Tests.csproj -c Release --filter 'FullyQualifiedName~NumericHeading' --nologo`
Expected: FAIL with `Column1` where `2024` was expected.

- [ ] **Step 7: Write the heading with `SetText` semantics**

The heading is a caption, never a value, so it takes the literal-text path rather than type inference —
which is what the streamed path already does for the header row and the table definition does not.

- [ ] **Step 8: Run both new tests and the full suite**

Run: `dotnet test Radzen.Blazor.Tests/Radzen.Blazor.Tests.csproj -c Release --nologo`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add Radzen.Blazor/Documents/Spreadsheet/XlsxWriter.Streaming.cs \
        Radzen.Blazor.Tests/Spreadsheet/StreamedSheetTests.cs
git commit -m "Honor a cancelled token after the last row, and keep a numeric heading text"
```

---

### Task 5: Measure whether the fork's slot store still earns its keep

This is the arm §67 names as the one that decides. It is the reason Task 3 comes before any fork
rebuild — both sides must exist to be compared.

**Files:**
- Create: a console harness from `gridbench/spreadsheet/README.md`'s "Rebuilding one" recipe, measuring
  a streamed export rather than a fill.

- [ ] **Step 1: Write the harness**

50,000 rows × 11 columns as in `BulkVsIndexer.cs`, values built **outside** the measured region, three
passes, arms interleaved, `GC.GetTotalAllocatedBytes(precise: true)` around a forced collect. Include
the same known-shape calibration arm §67 used — 550,000 × 96 B + 550,000 × 32 B + two reference arrays
+ a dictionary sized once, 90.3 MB by arithmetic. Write to a `Stream.Null`-backed sink so the download
is not measured.

- [ ] **Step 2: Run it against the fork tip (slot store)**

Point the `ProjectReference` at `/Volumes/Josh-Dev/DevCaches/claude-worktrees/radzen-fastgrid`.
Record allocated MB.

- [ ] **Step 3: Run it against the decoupled branch (master storage)**

Point the `ProjectReference` at `../radzen-decouple`. Record allocated MB.

- [ ] **Step 4: Check the calibration arms agree**

Expected: both report the same figure to the tenth of a megabyte. If they do not, the rigs are not
comparable and neither export number may be quoted.

- [ ] **Step 5: Record the result in §67**

Append the measured pair under "What this asks of the fork's slot store", replacing the sentence
"**This is read from the code and not yet measured.**" with the numbers. **If the two agree, the slot
store earns nothing on the streamed path and retiring it is evidenced; if they differ, say by how much
and stop — that is a finding that changes the plan.**

- [ ] **Step 6: Commit the spec update**

```bash
git add gridbench/SLIM-GRID-SPEC.md
git commit -m "Measure the streamed export against both storage shapes"
```

---

### Task 6: Rebuild the fork branches on the decoupled base

Only after Task 5 reports. If the slot store is retired, `spreadsheet-cell-storage` leaves the stack
entirely and this is a shorter rebuild than the handoff's three-branch dance.

**Files:**
- Modify: branches `spreadsheet-streamed-sheet`, `tech/fastgrid-streamed-export`

- [ ] **Step 1: Back up both branches**

```bash
git branch backup/spreadsheet-streamed-sheet-predecouple spreadsheet-streamed-sheet
git branch backup/fastgrid-streamed-export-predecouple tech/fastgrid-streamed-export
```

- [ ] **Step 2: Rebuild `spreadsheet-streamed-sheet`**

The fork copy is the commented, `Claude-Session`-trailered twin of the upstream branch. Replay its
writer commits onto the new base, keeping the house-style comments:

```bash
git rebase --onto upstream/xlsx-streaming-writer-v2 upstream/spreadsheet-cell-storage spreadsheet-streamed-sheet
```

Check the exit code before doing anything else. `rerere` remembers the two recurring conflicts.

- [ ] **Step 3: Rebuild `tech/fastgrid-streamed-export`**

Detach at `tech/radzen-datagrid-slim`, merge `origin/master`, merge `spreadsheet-streamed-sheet`, then
replay the ten grid commits with `git rebase --onto <new base> <old base> tech/fastgrid-streamed-export`.

- [ ] **Step 4: Run the FastGrid suites**

Run: `dotnet test Radzen.Blazor.FastGrid.Tests/Radzen.Blazor.FastGrid.Tests.csproj -c Release --nologo`
then the full `Radzen.Blazor.Tests` suite.
Expected: PASS. `FastGridExportCostTests` uses the per-thread allocation counter and a single-threaded
`Pump`, so run it **both** filtered and in the full suite — §66 records that they disagree.

- [ ] **Step 5: Commit nothing extra; the rebases are the change**

---

### Task 7: Draft the #2711 close and the #14 retarget — do not post

**Do not post anything to GitHub without explicit approval.** Public comments on Josh's behalf are
asked for first, every time, even inside a task that authorised other pushes.

- [ ] **Step 1: Draft the #2711 closing comment**

Short, non-defensive, measurement-led. It should say: the smaller shape you described measures 8.4 MB of
the 75.6 the branch won (94.0 → 85.6 against 94.0 → 18.4, 550,000 cells, rig calibrated by reproducing
the previous branch's figures byte for byte), so it is not worth the churn you were right to question;
closing. Thank him for the specifics — two of the six findings were the branch's own bugs and were
confirmed.

- [ ] **Step 2: Draft the #14 description**

`Component: result` title form, leading with the measurement. No `benchmarks/` directory; quote numbers
in the body. No Claude attribution.

- [ ] **Step 3: Present both drafts to Josh and stop**

Expected: explicit approval before any `gh pr` or `gh issue` command runs.

---

## Appendix: the measurement project

Every harness in `gridbench/spreadsheet/` is a standalone `dotnet run` console program, in no solution,
whose csproj carries exactly one `ProjectReference` to the checkout being measured. That is the whole
mechanism: the same program, run against different checkouts.

```bash
dotnet new console -o <name>
cp <harness>.cs <name>/Program.cs
```

Then replace `<name>/<name>.csproj` with this, changing only the `ProjectReference` path:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <NoWarn>$(NoWarn);CA1515;CA1002;CA1062;CA1024;CS1591</NoWarn>
    <EnforceCodeStyleInBuild>false</EnforceCodeStyleInBuild>
    <RunAnalyzers>false</RunAnalyzers>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="/absolute/path/to/checkout/Radzen.Blazor/Radzen.Blazor.csproj" />
  </ItemGroup>
</Project>
```

```bash
cd <name> && dotnet run -c Release
```

The `NoWarn` list is not optional — `Radzen.Blazor`'s analyzers are strict enough to fail the build of a
console program without it.

**Two rules from `gridbench/spreadsheet/README.md` that this plan depends on:**

1. **Build the values outside the measured region.** An earlier version of the fill harness built its
   strings inside the window and reported figures about 20% high, and those figures reached a PR body
   where nobody could reproduce them.
2. **Quote allocation, never wall-clock.** The same arm on this machine has come back at 1.36x, 1.40x
   and 1.79x of its baseline across three runs of one binary, while allocation stayed identical to the
   tenth of a megabyte. Record times; do not publish them as results.
