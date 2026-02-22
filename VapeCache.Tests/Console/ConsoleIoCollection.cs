namespace VapeCache.Tests.Console;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleIoCollection : ICollectionFixture<ConsoleIoFixture>
{
    public const string Name = "console-io";
}

public sealed class ConsoleIoFixture
{
}
