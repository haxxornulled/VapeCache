using System;
using System.IO;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Toolchains.CsProj;

namespace VapeCache.Benchmarks;

/// <summary>
/// BenchmarkDotNet's default CsProjGenerator prefers the solution root when a .sln is present and then searches
/// the entire tree for &lt;AssemblyName&gt;.csproj. If you have two copies of the repository under the same root,
/// that search finds multiple matches and throws.
/// This generator resolves the benchmark project's csproj by walking up from the benchmark assembly location
/// and picking the first matching csproj.
/// </summary>
internal sealed class NearestProjectCsProjGenerator : CsProjGenerator
{
    public NearestProjectCsProjGenerator(string targetFrameworkMoniker, string? customDotNetCliPath = null, string? packagesPath = null, string? runtimeFrameworkVersion = null)
        : base(targetFrameworkMoniker, customDotNetCliPath, packagesPath, runtimeFrameworkVersion)
    {
    }

    protected override FileInfo GetProjectFilePath(Type benchmarkTarget, ILogger logger)
    {
        var asm = benchmarkTarget.Assembly;
        var asmName = asm.GetName().Name;

        if (string.IsNullOrWhiteSpace(asmName))
            return base.GetProjectFilePath(benchmarkTarget, logger);

        var asmLocation = asm.Location;

        // Single-file publish etc: fall back to default behavior.
        if (string.IsNullOrWhiteSpace(asmLocation))
            return base.GetProjectFilePath(benchmarkTarget, logger);

        var dirPath = Path.GetDirectoryName(asmLocation);
        if (string.IsNullOrWhiteSpace(dirPath))
            return base.GetProjectFilePath(benchmarkTarget, logger);

        var dir = new DirectoryInfo(dirPath);
        while (dir is not null)
        {
            // Prefer exact match on assembly name (BenchmarkDotNet requirement by default)
            var candidatePath = Path.Combine(dir.FullName, asmName + ".csproj");
            if (File.Exists(candidatePath))
                return new FileInfo(candidatePath);

            // Some setups rename the assembly but keep project name; try VapeCache.Benchmarks.csproj explicitly as fallback.
            var fallbackPath = Path.Combine(dir.FullName, "VapeCache.Benchmarks.csproj");
            if (File.Exists(fallbackPath))
                return new FileInfo(fallbackPath);

            dir = dir.Parent;
        }

        return base.GetProjectFilePath(benchmarkTarget, logger);
    }
}
