using BenchmarkDotNet.Toolchains;
using BenchmarkDotNet.Toolchains.DotNetCli;

namespace VapeCache.Benchmarks;

internal sealed class NearestProjectToolchain : Toolchain
{
    public static readonly NearestProjectToolchain Net8 = new(
        name: "net8-nearest",
        targetFrameworkMoniker: "net8.0");

    public static readonly NearestProjectToolchain Net10 = new(
        name: "net10-nearest",
        targetFrameworkMoniker: "net10.0");

    private NearestProjectToolchain(string name, string targetFrameworkMoniker, string? customDotNetCliPath = null)
        : base(
            name,
            new NearestProjectCsProjGenerator(targetFrameworkMoniker, customDotNetCliPath),
            new DotNetCliBuilder(targetFrameworkMoniker, customDotNetCliPath),
            new DotNetCliExecutor(customDotNetCliPath))
    {
    }
}
