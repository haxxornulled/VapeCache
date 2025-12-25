using BenchmarkDotNet.Running;
using VapeCache.Benchmarks;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, new EnterpriseBenchmarkConfig());

