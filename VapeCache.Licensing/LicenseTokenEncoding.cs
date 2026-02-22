namespace VapeCache.Licensing;

internal static class LicenseTokenEncoding
{
    internal static string ToBase64Url(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    internal static bool TryFromBase64Url(string value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Replace('-', '+').Replace('_', '/');
        var remainder = normalized.Length % 4;

        if (remainder == 1)
            return false;

        if (remainder > 0)
            normalized = normalized.PadRight(normalized.Length + (4 - remainder), '=');

        try
        {
            bytes = Convert.FromBase64String(normalized);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
