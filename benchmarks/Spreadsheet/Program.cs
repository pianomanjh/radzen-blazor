using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Radzen.Benchmarks.Spreadsheet.SaveBenchmarks).Assembly).Run(args);
