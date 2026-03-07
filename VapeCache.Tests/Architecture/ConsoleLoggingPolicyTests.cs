using System.Text.RegularExpressions;

namespace VapeCache.Tests.Architecture;

public sealed class ConsoleLoggingPolicyTests
{
    private static readonly Regex ConsoleWriteRegex = new(
        @"\bConsole\.(Write(Line)?|Out\.Write(Line)?|Error\.Write(Line)?)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void CoreLibraries_ShouldNotUseConsoleWriteApis()
    {
        var repoRoot = FindRepositoryRoot();
        var scanRoots = new[]
        {
            Path.Combine(repoRoot, "VapeCache.Abstractions"),
            Path.Combine(repoRoot, "VapeCache.Infrastructure"),
            Path.Combine(repoRoot, "VapeCache.Extensions.Aspire")
        };

        var violations = new List<string>();

        foreach (var root in scanRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                if (IsIgnored(relativePath))
                {
                    continue;
                }

                var content = File.ReadAllText(file);
                if (!ConsoleWriteRegex.IsMatch(content))
                {
                    continue;
                }

                violations.Add(relativePath);
            }
        }

        Assert.True(
            violations.Count == 0,
            $"Console write usage found in core libraries:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    private static bool IsIgnored(string relativePath)
    {
        return relativePath.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || relativePath.Contains("/Generated/", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "VapeCache.sln")) ||
                File.Exists(Path.Combine(current.FullName, "VapeCache.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root (VapeCache.sln or VapeCache.slnx).");
    }
}
