# Handoff — the model's side of the export: what a struct-backed `CellStore` would buy

**Goal for this session:** find out, by measurement, what the fill would cost if a cell were not an
object — and what that costs in public API. **Spike and measure first. Do not start a rewrite.**

## Why this, and how we know

An export is `ToWorkbook` (the fill) + `SaveToStream` (the save). The save is done: it went from 442 MB
to 17.7 MB over 550,000 cells and now causes **no garbage collection of any generation**. That work is
in radzenhq/radzen-blazor#2708.

What is left is the fill, and it is the whole of the export's remaining GC:

| | allocated | Gen0 | Gen1 | Gen2 |
| --- | --- | --- | --- | --- |
| fill only (`CellStore.SetValues`) | 93.97 MB | 10667 | 5667 | 1000 |
| fill and save | 111.64 MB | 10000 | 5000 | 1000 |
| **the save's share** | **17.67 MB** | **none** | **none** | **none** |

Filling and then saving collects what filling alone collects. By type, the fill's 94 MB over 550,000
cells — **179 bytes a cell** — is:

| type | est MB | per object | per cell |
| --- | --- | --- | --- |
| `Cell` | 68.5 | 112 B | 131 B |
| `CellData` | 20.5 | 32 B | 39 B |
| boxed `Double` | 4.1 | 24 B | 8 B |

One object per cell, a second for every cell holding anything, and a re-boxed number at
`CellData.cs:150` — `Value = (Type == CellDataType.Number) ? Convert.ToDouble(data, InvariantCulture) :
data` — which re-boxes a value that arrived boxed, deliberately, because a spreadsheet number is a
double.

**These allocations survive.** The workbook is the point of the fill and is kept, so gen-1 and gen-2
counts here are *promotion of a live graph*, not sweeping up after a wasteful one. A save's allocations
were garbage and could go to zero. A fill's are the object graph the caller asked for. **The only way to
collect less is to allocate fewer or smaller objects per cell**, which is the question this session is
for.

§42 reached the same place from the other side and stopped there: *"94.0 MB over 550,000 cells is 179
bytes each, and the `Cell` object is most of that … getting under it means cells that are not objects,
which is a different library."* That is the sentence to test rather than inherit.

## The question, in the order worth answering

1. **What is the ceiling?** Prototype a columnar or struct-backed store — parallel arrays of value,
   type, style id, or a `struct Cell` in a `Dictionary<(int,int), int>` index — in a scratch project, not
   in the library. Fill 550,000 cells. What does it allocate? ClosedXML's `InsertData` fill is **75.1 MB**
   for the same block, so anything above that is not worth a rewrite.
2. **What does it cost the public API?** `Cell` is a **public class** and consumers hold references:
   `sheet.Cells[r, c].Format.Bold = true` works because `Cells[r, c]` returns a live object. A struct
   store either returns a façade (which reintroduces an allocation per access unless it is a `ref struct`
   or the caller uses new by-ref APIs) or breaks that pattern. `CellData` is public too. **Cost this
   honestly before proposing anything**; it may be that the answer is a second storage mode rather than a
   replacement.
3. **Two contained wins exist that need no redesign, and neither removes a collection.** Both are
   unmeasured — measure before claiming:
   - folding `CellData` into `Cell`, saving an object header and a reference per non-empty cell
     (~13 MB by arithmetic, not by experiment);
   - skipping the re-box at `CellData.cs:150` when the value is already a `double` (~4 MB, and **only for
     callers that pass doubles** — a caller passing `int` still boxes, so the 550,000-cell harness would
     show nothing).

## Where to work

| | |
| --- | --- |
| Worktree | `/Volumes/Josh-Dev/DevCaches/claude-worktrees/radzen-spreadsheet-perf` |
| Branch to cut | a **new** one off `upstream/master`, currently `c7d91536c` |
| Do not touch | `upstream/xlsx-writer-perf` (= radzenhq#2708, `84200d156`, awaiting review) and `review/xlsx-writer-perf` (= pianomanjh#12, the same work plus benchmarks) |
| FastGrid worktree | `/Volumes/Josh-Dev/DevCaches/claude-worktrees/radzen-fastgrid` (`tech/radzen-datagrid-slim`) — the spec and the harnesses live here |

**The harnesses are written and kept**, in `gridbench/spreadsheet/` on the FastGrid branch and in
`benchmarks/Spreadsheet/` on `review/xlsx-writer-perf`. Read that README before measuring anything.

| | |
| --- | --- |
| `SaveTypes.cs` | allocation by type, off `GCAllocationTick` — **the tool that found every fault so far** |
| `SaveFloor.cs` | splits a figure into the writer's share and the sink's, and asks the repeated-string question |
| `SaveVersusClosedXml.cs` | each engine's fill, save and export, armed separately |
| `SavedParts.cs` + `compare-parts.py` | sixteen-part byte comparison of a saved workbook between two checkouts |
| `benchmarks/Spreadsheet` | BenchmarkDotNet with `[MemoryDiagnoser]` — allocation *and* collection counts |

A fill histogram is a five-line edit of `SaveTypes.cs`; there is one in this session's scratch if it
survives, but rebuilding it is quicker than finding it.

## Measurement discipline — every one of these cost real time this session

1. **Quote allocation, not wall-clock.** Allocation repeated to a tenth of a megabyte across every run;
   time did not.
2. **For any cross-build timing, put an unchanged arm in both runs and normalise against it.** ClosedXML
   served as that control and **drifted 1.24x between two runs** of the export. Reading the two figures
   against each other directly would have said 2.51x where the truth was 3.1x — close enough to the right
   answer that nobody would have questioned it.
3. **An instrument inside the window it measures adds bias, not noise.** `SaveTypes.cs` armed its
   `GCAllocationTick` listener before opening the allocation window. The listener allocates per tick and
   ticks scale with the arm, so it overstated a 434 MB save by 16 MB and a 9.6 MB one by nothing — an
   error pointing the way the work wanted. It is fixed (unarmed save for the total, armed save for the
   histogram), and the whole published ladder had to be restated. **Nothing looked wrong: the figures
   repeated to a tenth of a megabyte, which is exactly what a systematic error does.** What found it was
   a number that would not reconcile, 458 against 442, and chasing 16 MB rather than rounding it away.
4. **The sink is not the writer.** A `MemoryStream` reaches 2.8 MB through a chain of doublings and the
   save is charged for all of it — a constant 8 MB. Measure into `Stream.Null` when the question is what
   the code allocates.
5. **A gate aimed at the wrong extreme is not a gate.** A test written for `CellRef.MaxLength` being one
   character short passed against the broken bound, because it used a reference nowhere near the limit.
   The real worst case needed all three of both absolute markers, a seven-letter column and the wrapping
   row. **Break the code deliberately and watch the test fail before believing it.**
6. **Prove a comparison can fail before trusting a clean result.** `compare-parts.py` once reported a
   pass over two empty directories because the program behind it had failed to build. It now refuses
   inputs that are not saved workbooks; a run that never happened looks exactly like nothing differing.
7. BenchmarkDotNet takes **space-separated** `--filter` globs and **exits 0 having run nothing** when one
   matches nothing — check `executed benchmarks:`. `[IterationSetup]` pins `InvocationCount` to 1 and
   with it the time column; a save arm reported 707.7 ms ± 979.3 that way and 316.0 ms ± 6.24 without.

## Rules that are not negotiable on an upstream branch

- **No `Claude-Session:` trailer and no reference to Claude of any kind**, in commits or in the PR. The
  FastGrid branch is the opposite — it *does* carry the trailer.
- **Do not post, reply, or edit anything on GitHub without asking Josh first.** Pushing to his fork is
  part of the work; speaking publicly in his name is not.
- **Comments only on public API.** This is a house preference stated directly, and it is visible in
  akorchev's own `f0d742f32`, which stripped narration and even ECMA citations out of #2706. The upstream
  branch adds **two** comment lines, both pre-existing ones that moved with their code.
- **No narrative anywhere** — not in commit messages, not in a PR description. Mechanism, measurement,
  and the caveat if there is one. Tables rather than prose. Data is welcome; the story of finding it is
  not.
- Each commit must **stand alone**: build and pass the suite on its own. Verified per commit, every time.
- **Do not add and then retire something inside one branch.** An earlier version of #2708 added
  `ColumnRef.Append` in commit 2 and deleted it in commit 6; the branch was rebuilt so it never appears.

## What the maintainer has said

- **"We plan to release [a data-grid export] ourselves. Please do not spend any time on this task."**
  That is a *grid* export feature, not the spreadsheet internals. The model is fair game, but a change to
  the public `Cell` is a much bigger ask than a writer change and should be proposed as a question before
  it is built.
- He checks whether a change is **actually needed** and asks what failure it prevents. For this work the
  honest answer will be "no failure; it allocates 94 MB and causes 10,667 gen-0 collections per thousand
  exports" — have the measurement first.
- He objects to changes that **make an unrelated case worse**. A store optimised for dense blocks must
  not punish a sparse sheet; measure both.
- He prefers **existing patterns and existing API** over new ones.

## State of everything else, so you do not re-open it

- **radzenhq/radzen-blazor#2708** — seven commits, `84200d156`, off `upstream/master` `c7d91536c`. 5,169
  tests green and green at each commit alone. Library only, no benchmarks. Awaiting review.
- **pianomanjh/radzen-blazor#12** — the same seven plus one benchmarks commit, `4e8c95b2f`. Two review
  comments from Josh, both fixed in code, **both still open and unanswered** — replying is his call.
- **`tech/radzen-datagrid-slim`** (FastGrid worktree) — carries the record: §46 profiles the writer, §47
  the streaming rewrite, §48 the span formatting and what C# does and does not offer, §49 the floor,
  §50 the instrument correction, §51 runtime and the control, §52 the attribution of the export's
  collections to the fill. **§52 is where this handoff starts.**
- The writer is finished. Do not reopen it to chase the last megabytes; 17.7 MB of a 111 MB export is not
  where the work is.
