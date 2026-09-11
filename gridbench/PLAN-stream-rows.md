# Stream Rows Into the Existing Writer, Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Rework #2716 into akorchev's shape: `Workbook.SaveToStreamAsync(Stream, IAsyncEnumerable<CellData?[]>)`, plus a `(CellData?, Format?)` overload, appending rows to the workbook's only sheet through the existing writer.

**Architecture:** The built workbook is the frame. `WriteSheetData` writes the built cells, then loops an optional source through the same cell writers, with a style cached per column and type. The writer chain is async once, awaiting only the source; `SaveToStream` runs it with no source. Tables are written after the sheet so a range can close on the final row.

**Tech Stack:** C#, net8/9/10, `System.IO.Compression`, `System.Xml`, xUnit.

**Spec:** `gridbench/SLIM-GRID-SPEC.md` §70 on `tech/fastgrid-streamed-export`.

## Global Constraints

- Worktree `/Volumes/Josh-Dev/DevCaches/claude-worktrees/radzen-stream-rows`, branch `upstream/xlsx-stream-rows` off master `778839fb3`. **Never push to `upstream/xlsx-streaming-writer`: it is #2716's head, and a push updates the upstream PR.** Push only the new branch, and only to the fork.
- No code comments. XML docs on public members only. American English. Tidy commit messages, no trailer.
- Byte gate after every commit: `SavedParts` pointed at this worktree, `python3 /tmp/compare-parts.py /tmp/parts-before <out>`, expecting **16 parts, 0 differing**.
- New tests are red before they are green, and mutation-checked from `cp` snapshots with a build confirmed for every mutation.
- A two-axis review after each task, with findings fixed before the next.

## Tasks

### Task 1: Branch, and the two commits kept

- [ ] `git worktree add -b upstream/xlsx-stream-rows <path> 778839fb3`, then `git cherry-pick d52ad10a9 52bb1fca4`.
- [ ] Build `Radzen.Blazor` (all TFMs), run the spreadsheet tests, run the byte gate: 16/0.

### Task 2: The writer counts tables and media itself

`SaveSheets` and `SaveSheet` thread `ref int globalTableIndex` and `ref int globalMediaIndex`, and an async method cannot take a `ref`. Move `globalTableIndex`, `globalMediaIndex` and `mediaMap` to fields of the per-save `XlsxWriter`, and have `Write` read the table total from the field.

- [ ] Refactor, then build, test and gate 16/0. No new test: the gate and the suite are the check.
- [ ] Commit: "Count a save's tables and media on the writer, not through ref parameters".

### Task 3: A sheet's tables are written after it

In `SaveSheet`, keep building `tableParts` into `sheetDoc` with the rel ids assigned up front, but move `SaveTable(archive, table, tableId)` to after the sheet entry is closed. Each part's bytes are unchanged; only the order of entries in the zip moves.

- [ ] Refactor, then build, test and gate 16/0.
- [ ] Commit: "Write a sheet's tables after the sheet".

### Task 4: Rows from a source, appended to the only sheet

**Public:** `Workbook.SaveToStreamAsync(Stream stream, IAsyncEnumerable<CellData?[]> rows, CancellationToken cancellationToken = default)`, with XML docs covering the append point, which tables extend, that `dimension` is omitted, the limits, and fault behavior.

**Writer:**
- `WriteAsync(Stream, IAsyncEnumerable<CellData?[]>? rows, CancellationToken)` holds today's `Write` body, made async through `SaveSheets`, `SaveSheet`, `WriteSheetXml` and `WriteSheetData`. `Write(Stream)` becomes `WriteAsync(stream, null, default).GetAwaiter().GetResult()`, which completes synchronously because nothing is awaited without a source.
- Archive and fault handling follow #2716: create the archive without `using`; on a throw, `Discard` a seekable destination (`SetLength(start)`) and rethrow, so no central directory is written.
- Refuse a source when `Sheets.Count != 1`, with `InvalidOperationException`, before anything is written.
- `WriteRows` returns the last row it emitted. `WriteSheetData` then appends source rows starting at `last + 1`, through `WriteRowStart` and a cell writer shared with #2716's `WriteStreamedCell`: the reference, the style, and a shared string or typed value. Style is cached per `(column, type)`: `null` when the type is not a date, and a date style from `GetOrCreateCellStyle(DefaultFormat, CellDataType.Date, …)` when it is. A null element writes nothing.
- The limits: `MaxRows` 1,048,576 and `MaxCellCharacters` 32,767 for streamed strings, each throwing `InvalidOperationException` naming the row or the cell.
- When there is a source, `CreateDimension` is not added.
- Tables: the table ids are known up front. The range written for a table without totals whose `Range.End.Row` equals the frame's last row becomes `End.Row = lastWritten`. `SaveTable` gains an optional `RangeRef? range` override so the model's `Table` is not mutated.

**Tests** (`Radzen.Blazor.Tests/Spreadsheet/WorkbookStreamedRowsTests.cs`):
- Appends after a built header: 3 rows read back cell for cell.
- Appends after built rows: the first streamed row lands directly below the last built one.
- `CellData.FromString("123")` reads back as the string "123".
- A number, a date and a bool round-trip, and every date cell in a column shares one style id.
- A null element writes no `<c>`.
- A table over the header and one built row, with 3 streamed rows, reads back with range `A1:C5`; the workbook's own table is still `A1:C2` after the save.
- A table that does not reach the frame's bottom is not widened.
- `dimension` is absent with a source, and present in a `SaveToStream` of the same workbook.
- A two-sheet workbook is refused, and the destination is left empty.
- A faulting source leaves a seekable destination at length 0.
- Cancellation throws `OperationCanceledException`, and the destination is left empty.
- A 32,768-character string is refused, naming its cell.

- [ ] Write the tests and see them fail to build, then implement; build, run the spreadsheet tests, gate 16/0, mutation-check, review, commit: "Append rows from a source to a workbook's only sheet as it saves".

### Task 5: A streamed cell can carry its own format

- Overload `SaveToStreamAsync(Stream, IAsyncEnumerable<(CellData? Data, Format? Format)[]>, CancellationToken)`. The loop reads tuples. The `CellData?[]` overload adapts through one reused tuple array per enumeration, which is safe because the writer does not keep a row.
- A cell with a `Format` resolves its style through `GetOrCreateCellStyle(format, type, …)`, cached by `(Format reference, type)`. A cell without one uses the column and type cache.

**Tests:** alternating backgrounds over two reused `Format`s give exactly two style ids for data cells and read back with the two backgrounds; a number format on one cell reads back on that cell only; the `CellData?[]` overload writes the same parts as the tuple overload with null formats.

- [ ] Red, green, gate, mutation-check, review, commit: "Let a streamed cell carry its own format".

### Task 6: The EF demo, on the new API

Port `c6348a2a4`'s `SpreadsheetStreamedExport` to build the header and first rows in a `Workbook` and stream the rest. Build the demos in **Debug**, since Release compiles against the NuGet package.

- [ ] Port, build in Debug, commit: "Add a demo streaming an EF query to Excel".

### Task 7: Verification and record

- [ ] Full `Radzen.Blazor.Tests` in Debug, and the library for all TFMs.
- [ ] Gate 16/0 at the tip, and prove the gate can fail by editing one byte.
- [ ] Allocation: `celldata/` rig arm on the new API with typed `CellData` rows, B/row slope, beside #2716's 104 and the built path, all interleaved, calibration arm included.
- [ ] Open a streamed file in Numbers (PDF export), with a built control beside it.
- [ ] Record in §70, commit the spec, and push `upstream/xlsx-stream-rows` to the fork. No PR, and not #2716's branch.
