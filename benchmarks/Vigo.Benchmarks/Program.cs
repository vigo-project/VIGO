using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

// Examples:
//   dotnet run -c Release --project benchmarks/Vigo.Benchmarks -- --filter *LaneEngine*
//   $env:VIGO_BENCH_REPO = 'D:\repos\linux'; dotnet run -c Release --project benchmarks/Vigo.Benchmarks -- --filter *ReadLog* --iterationCount 5
// BenchmarkDotNet 0.15 does not recognize net11.0 for its out-of-process toolchain: run in process.
var config = DefaultConfig.Instance.AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance).AsDefault());
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
