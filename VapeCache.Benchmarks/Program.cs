using BenchmarkDotNet.Running;
using VapeCache.Benchmarks;

// Standalone performance test mode
if (args.Length > 0 && args[0] == "standalone")
{
    var host = args.Length > 1 ? args[1] : "127.0.0.1";
    var port = args.Length > 2 && int.TryParse(args[2], out var p) ? p : 6379;
    var username = args.Length > 3 ? args[3] : null;
    var password = args.Length > 4 ? args[4] : null;

    Console.Out.WriteLine($"Running standalone performance test against {host}:{port}");
    if (!string.IsNullOrWhiteSpace(username))
        Console.Out.WriteLine($"Username: {username}");

    var test = new StandalonePerformanceTest(host, port, username, password);
    await test.RunAsync();
}
else if (args.Length > 0 && args[0] == "alloc-profile")
{
    await AllocationProfileRunner.RunAsync(args);
}
else
{
    var isDiscoveryOrHelpMode = args.Any(static arg =>
        string.Equals(arg, "--list", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(arg, "-l", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(arg, "--info", StringComparison.OrdinalIgnoreCase));

    var config = new EnterpriseBenchmarkConfig(compactConsole: !isDiscoveryOrHelpMode);

    // Normal BenchmarkDotNet mode
    BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
}

