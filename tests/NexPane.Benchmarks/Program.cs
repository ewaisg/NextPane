using BenchmarkDotNet.Running;

// Performance budget suite (ARCHITECTURE.md §17). Run: dotnet run -c Release --project tests/NexPane.Benchmarks -- --filter *
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
