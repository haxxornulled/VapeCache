using System;
using System.Threading.Tasks.Sources;

public record struct TestValue(int X, string? Y);

public class Test
{
    private ManualResetValueTaskSourceCore<TestValue> _core;

    public void DoTest()
    {
        _core.Reset();
        Console.WriteLine("Reset OK");
        try
        {
            _core.SetException(new Exception("test"));
            Console.WriteLine("SetException OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"SetException FAILED: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

public class Program
{
    public static void Main()
    {
        try
        {
            new Test().DoTest();
            Console.WriteLine("SUCCESS");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED: {ex}");
        }
    }
}
