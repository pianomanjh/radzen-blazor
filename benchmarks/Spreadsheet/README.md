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

## The streaming sweep

`dotnet run -c Release -- --filter "*StreamBenchmarks*" --buildTimeout 900`

`StreamBenchmarks` asks the one question `SaveBenchmarks` cannot: does the streamed write allocate the
same whatever the row count is? A single arm cannot answer that, so the row count is the parameter and
the built save is the arm beside it - the one known to grow, because it holds a value per cell. **If the
sweep does not move the built arm the instrument is not resolving anything**, and neither arm's flatness
means a thing.

Three arms, 10,000 / 50,000 / 200,000 rows by eleven columns, on this machine:

| Arm | 10k | 50k | 200k | Gen1/Gen2 at 200k |
| --- | ---: | ---: | ---: | --- |
| Streamed, nothing boxed | 152 KB | 160 KB | 182 KB | none, and no Gen0 either |
| Streamed, no workbook | 2.7 MB | 12.8 MB | 50.5 MB | none |
| Built, then saved | 23.6 MB | 103.3 MB | 441.0 MB | 3000 Gen1, 1000 Gen2 |

**The flat arm is the claim.** 30 KB of drift across a 20x sweep is the shared string table filling to
its hundred entries and the zip's buffers, not a cost per row. The same run writes a real file - 200,001
rows and 2,200,011 cells, 133 MB of sheet XML compressed to 5.7 MB - which is worth checking before
believing any figure taken against `Stream.Null`.

**The middle arm is not the writer.** A column is a `Func<T, object?>`, so every non-string cell arrives
in a box: about 0.26 KB per row here, allocated and dead in gen0 before the row after it is read. That
is the accessor's cost and it is the caller's to remove, which is why the arm above it exists - what is
left when nothing boxes is the writer alone.

**The built arm is what was replaced.** It is the only one of the three that promotes anything out of
gen0, and it is 2,400x the flat arm at 200,000 rows.

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
