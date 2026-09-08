# Spreadsheet allocation harnesses

The three programs behind the figures in radzenhq/radzen-blazor#2706. They are not part of
`Radzen.Blazor.GridBench` and are not in any solution: each is a `dotnet run` console program whose
csproj carries **one `ProjectReference` to whichever `Radzen.Blazor.csproj` you want to measure**, which
is the whole point - the ladder is produced by checking out each commit of that branch in turn and
running the same program against it.

| File | What it answers |
| --- | --- |
| `Ladder.cs` | What filling 550,000 cells a cell at a time allocates. Run at each commit for the ladder. |
| `BulkVsIndexer.cs` | The same fill against `CellStore.SetValues`, both arms interleaved in one process. |
| `BatchOnAFormulaSheet.cs` | What wrapping a small `SetValues` in `Worksheet.Batch` costs on a sheet carrying formulas that do not read the block. |

## Method

50,000 rows by eleven columns of mixed strings, numbers, dates and booleans - 550,000 cells.

**The values are built before the measured region**, so what is reported is the sheet's cost and not the
test data's. This is the one thing to keep if the harness is rebuilt: an earlier version of it built the
strings inside the measured region and reported figures about 20% higher, and those figures went into a
PR description where nobody could reproduce them.

Allocation is read with `GC.GetTotalAllocatedBytes(precise: true)` around a forced collection, three
passes, arms interleaved. **Allocation is the number to quote.** Wall-clock on this machine drifts far
enough between runs to reverse a ranking - the same benchmark arm has come back at 1.36x, 1.40x and
1.79x of its baseline across three runs of one binary - so times are recorded here and not published as
results.

## Rebuilding one

    dotnet new console -o <name>
    cp Ladder.cs <name>/Program.cs
    # add to <name>/<name>.csproj, pointing at the checkout you want to measure:
    #   <ProjectReference Include=".../Radzen.Blazor/Radzen.Blazor.csproj" />
    # and, because Radzen.Blazor's analyzers are strict:
    #   <NoWarn>$(NoWarn);CA1515;CA1002;CA1062;CA1024;CS1591</NoWarn>
    cd <name> && dotnet run -c Release

`SpreadsheetAlloc.csproj.template` is one that worked; the `ProjectReference` path in it is absolute and
needs changing.
