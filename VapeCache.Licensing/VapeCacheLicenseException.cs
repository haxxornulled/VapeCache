namespace VapeCache.Licensing;

/// <summary>
/// Exception thrown when VapeCache license validation fails.
/// </summary>
public sealed class VapeCacheLicenseException : Exception
{
    public VapeCacheLicenseException(string message) : base(message)
    {
    }

    public VapeCacheLicenseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
