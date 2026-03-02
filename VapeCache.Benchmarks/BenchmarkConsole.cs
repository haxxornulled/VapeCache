using BenchmarkDotNet.Running;

namespace VapeCache.Benchmarks;

public static class BenchmarkConsole
{
    public static async Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length > 0 && BenchmarkSuiteCatalog.IsListCommand(args[0]))
        {
            Console.Out.WriteLine(BenchmarkSuiteCatalog.FormatCatalog(args.Length > 1 ? args[1] : null));
            return 0;
        }

        if (args.Length > 0 && BenchmarkSuiteCatalog.IsSuiteCommand(args[0]))
        {
            if (!BenchmarkSuiteCatalog.TryCreateInvocationPlan(args, out var plan, out var error))
            {
                Console.Error.WriteLine(error);
                Console.Error.WriteLine();
                Console.Error.WriteLine(BenchmarkSuiteCatalog.FormatCatalog(args[0]));
                return 1;
            }

            ApplyEnvironmentDefaults(plan.EnvironmentDefaults);
            Console.Out.WriteLine($"Running benchmark suite: {plan.DisplayName}");
            RunBenchmarks(plan.Arguments);
            return 0;
        }

        if (args.Length > 0 && string.Equals(args[0], "standalone", StringComparison.OrdinalIgnoreCase))
        {
            var host = args.Length > 1 ? args[1] : "127.0.0.1";
            var port = args.Length > 2 && int.TryParse(args[2], out var parsedPort) ? parsedPort : 6379;
            var username = args.Length > 3 ? args[3] : null;
            var password = args.Length > 4 ? args[4] : null;

            Console.Out.WriteLine($"Running standalone performance test against {host}:{port}");
            if (!string.IsNullOrWhiteSpace(username))
                Console.Out.WriteLine($"Username: {username}");

            var test = new StandalonePerformanceTest(host, port, username, password);
            await test.RunAsync().ConfigureAwait(false);
            return 0;
        }

        if (args.Length > 0 && string.Equals(args[0], "alloc-profile", StringComparison.OrdinalIgnoreCase))
        {
            await AllocationProfileRunner.RunAsync(args).ConfigureAwait(false);
            return 0;
        }

        RunBenchmarks(args);
        return 0;
    }

    private static void RunBenchmarks(string[] args)
    {
        var isDiscoveryOrHelpMode = args.Any(static arg =>
            string.Equals(arg, "--list", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "-l", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--info", StringComparison.OrdinalIgnoreCase));

        var config = new EnterpriseBenchmarkConfig(compactConsole: !isDiscoveryOrHelpMode);
        BenchmarkSwitcher.FromAssembly(typeof(BenchmarkConsole).Assembly).Run(args, config);
    }

    private static void ApplyEnvironmentDefaults(IReadOnlyList<KeyValuePair<string, string>> defaults)
    {
        foreach (var entry in defaults)
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(entry.Key)))
                continue;

            Environment.SetEnvironmentVariable(entry.Key, entry.Value);
        }
    }
}
