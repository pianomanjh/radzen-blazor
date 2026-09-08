# Spreadsheet save benchmarks

Everything behind the allocation figures in this branch's commit messages.

## The BenchmarkDotNet project

`dotnet run -c Release -- --filter "*SaveBenchmarks*" --buildTimeout 900`

Four arms: this package's save and ClosedXML's, and each engine's whole export. `[MemoryDiagnoser]`
reports allocated bytes per operation and the GC collections each arm causes.

This is the comparison BenchmarkDotNet is right for - two libraries, one build, one process, and a claim
about time as well as allocation. Two things it is **not** right for here, which is why `harnesses/`
exists beside it:

- **The ladder across commits.** Every rung compares two builds of `Radzen.Blazor`, and one process
  cannot hold both. The harnesses are console programs whose `ProjectReference` is repointed at the
  checkout being measured, which is how a figure is attributed to one commit.
- **Naming the object.** BenchmarkDotNet reports a total. Every finding in this branch came from a
  histogram of `GCAllocationTick` by *type* - a `Format` per cell, an `Action` per cell, a tree node per
  cell, an `XElement` per cell - which a total cannot show.

**Read the run before believing the table.** BenchmarkDotNet takes space-separated `--filter` globs and
**exits 0 having run nothing** when a filter matches none; check the `executed benchmarks:` line.

## Reading the numbers

**Allocation is the figure to quote.** It repeats to a tenth of a megabyte across runs and machines.
Wall-clock on this hardware does not: the run recorded below reports the save at 707.7 ms with an error
of 979.3 ms, which is the instrument saying it cannot resolve the question at this size. Where a time
ratio is quoted anywhere in this branch it is because the arms were interleaved and the ratio was stable
across passes, never from a single run.

## The harnesses

Each is a `dotnet run` console program, excluded from this project's compilation because each has its
own entry point. Copy one to a scratch console project whose csproj carries a single `ProjectReference`
to the `Radzen.Blazor.csproj` being measured - `SpreadsheetAlloc.csproj.template` is one that worked.

| File | What it answers |
| --- | --- |
| `SaveTypes.cs` | What `SaveToStream` allocates and, by `GCAllocationTick`, which types it goes to. The fill is outside the armed window, so nothing is subtracted. |
| `SaveVersusClosedXml.cs` | The save alone against ClosedXML's, both filled outside the window, plus each engine's fill and whole export. |
| `SavedParts.cs` | Every part of a workbook using every worksheet feature, unpacked for byte comparison between two checkouts. Pair with `compare-parts.py`. |
| `SpanFormatAgreement.cs` | Whether `TryFormat` spells a number the way `ToString(InvariantCulture)` and `XmlConvert.ToString` do, over the values a fixture would not carry. |

### Proving the output did not change

    dotnet run -c Release -- ./before      # csproj pointed at the base commit
    dotnet run -c Release -- ./after       # and then at the branch
    python3 compare-parts.py ./before ./after

`compare-parts.py` normalises the two things meant to differ between any two runs - the random revision
uid and the `docProps` timestamp - and nothing else.

**Mutate a byte in one of the outputs before believing a clean result.** A comparison that cannot fail is
not evidence, and an earlier version of this one reported a clean pass over two empty directories
because the program behind it had failed to build.
