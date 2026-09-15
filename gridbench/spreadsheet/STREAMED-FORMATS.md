# Streamed row formatting benchmark

`StreamedFormats.cs` adapts `StreamedExportFloor.cs` to the merged workbook-frame API and the tuple-format overload. The `.csproj` accepts `RadzenProject` (an absolute path to the library project) and `FormattedRows=true` for checkouts with the tuple API. Use canonical filesystem paths; on macOS, use `/private/tmp` rather than `/tmp` for temporary worktrees to avoid Razor namespace generation errors.

## Reproduce

Build each checkout separately, then run the resulting binaries sequentially with no builds in progress:

```sh
dotnet build gridbench/spreadsheet/StreamedFormats.csproj -c Release \
  -p:RadzenProject=/absolute/baseline/Radzen.Blazor/Radzen.Blazor.csproj \
  -o /absolute/output/baseline

dotnet build gridbench/spreadsheet/StreamedFormats.csproj -c Release \
  -p:RadzenProject=/absolute/candidate/Radzen.Blazor/Radzen.Blazor.csproj \
  -p:FormattedRows=true -o /absolute/output/candidate

dotnet /absolute/output/baseline/StreamedFormats.dll
dotnet /absolute/output/candidate/StreamedFormats.dll
```

## Method

- 50,000 rows × 11 columns: 550,000 mixed string, numeric, date and boolean values. There are 150,000 distinct input strings.
- All `CellData` objects and their underlying values are prepared before the measured region. Workbook/header creation, source enumeration, tuple-row projection, format creation and XLSX serialization are measured. Each arm writes to the same non-buffering sink.
- Formatted arms use one tuple row buffer. The reused arm creates one `Format` per column; the fresh arm deliberately creates a new equivalent `Format` for every cell. Numeric columns use `$#,##0.0000`; dates use `yyyy-mm-dd`; other formats are default.
- One warm-up per arm, then three passes, interleaved with rotating arm order. Process-wide `GC.GetTotalAllocatedBytes(precise: true)` and generation collection counts are sampled after a forced GC. Report median allocation in MiB (1,048,576 bytes).
- The allocation calibration creates arrays and objects with an expected total of 79,200,048 bytes. No live-heap-size estimate is used. Allocated bytes measure allocation traffic, not peak or retained memory.
- Raw logs include times for transparency. Following the original harness guidance, do not infer speedups from them: wall-clock measurements on this machine can drift enough to reverse rankings.
- A cache cap bounds format-instance retention, not total export memory. Distinct shared strings and genuinely different serialized styles still require storage. Creating fresh formats also still allocates those caller objects even if the writer does not retain them.

## Results — 2026-09-15

Measured on Apple M4, 16 GiB RAM, macOS 26.6, .NET SDK 10.0.400 / runtime 10.0.11, Arm64, workstation GC, Release builds. The three binaries ran sequentially (master, uncapped, current); no benchmark builds overlapped measurement.

| Checkout | Commit |
| --- | --- |
| Pre-PR master | `53d886b833f56bdd5024771e10a674faad299484` |
| Tuple overload before cap | `4ba97a48ef5bfdb8e9d2479fb6f7ff0e2cafddd6` |
| Current PR with cap | `6e0e6e34c9d1183f1b3af12e9de3020ef6325328` |

Median allocated MiB, three measured passes after warm-up:

| Arm | Master | Before cap | Current PR |
| --- | ---: | ---: | ---: |
| Values, shared strings | 0.146 | 0.146 | 0.146 |
| Values, inline strings | 0.146 | 0.146 | 0.146 |
| Tuples, null formats, inline strings | — | 0.145 | 0.145 |
| Reused formats, shared strings | — | 0.149 | 0.148 |
| Reused formats, inline strings | — | 0.148 | 0.149 |
| Fresh format per cell, inline strings | — | 116.085 | 72.036 |
| Allocation calibration | 75.533 | 75.533 | 75.533 |

The fresh-format arm falls by **44.049 MiB (37.9%)** after capping the instance cache. Caller-created formats still account for substantial allocation; reusing formats remains the intended pattern. The existing value-only API shows no material allocation regression. Differences of roughly 1–2 KiB among the small-allocation arms are within their observed run variation.

**These are warm allocation figures, not export working-set estimates.** In particular, `SharedStringTable` rents arrays from `ArrayPool`, so warm-up populates the pool before the reported passes. Shared strings still retain the distinct text during a save even when the next save allocates few new bytes. Input data preparation is also outside the measurement by design.

Calibration measured 79,201,768 bytes on all three binaries versus 79,200,048 bytes of object/array arithmetic (0.0022% difference). Each fresh-format pass collected gen 0/1/2 respectively 9/5/1 before the cap and 8/1/0 after it; the current reused-format passes incurred no collections inside the measured region. These are observations from this workload, not general GC guarantees.

Raw measurements, including all elapsed times, collection counts and output byte counts:

- [Master](results/streamed-formats-2026-09-15/master.txt)
- [Before cap](results/streamed-formats-2026-09-15/uncapped.txt)
- [Current PR](results/streamed-formats-2026-09-15/current.txt)
