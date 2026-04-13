using BenchmarkDotNet.Running;

namespace VapeCache.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        BenchmarkSwitcher
            .FromAssembly(typeof(Program).Assembly)
            .Run(BenchmarkCommandLine.BuildEffectiveArgs(args), VapeCacheBenchmarkConfig.Instance);
    }
}
